using System.Text.Json;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;

namespace ClaudeTelegramBot.Storage.Json;

// Хранилище в JSON-файлах одной папки, по файлу на вид сущностей:
//   notes.json, tasks.json, allowed-users.json, sessions.json, chat-settings.json — JSON-массивы;
//   usage.jsonl — по строке на запрос; digest.json — дата последней сводки;
//   rate-limit.json — последние лимиты; schema.json — версия формата.
// Файлы общие у бота и MCP-сервера, поэтому каждое чтение-изменение-запись идёт под именованным
// мьютексом файла (работает между процессами), а запись — через временный файл.
public sealed class JsonStorage : IStorage {
    // Версия формата в schema.json. 1 — файлы до базовой сущности (personal-data.json с
    // целочисленными id, словари chatId -> значение), 2 — списки сущностей с ShortGuid-id.
    internal const int SchemaVersion = 2;

    private sealed class SchemaInfo {
        public int Version { get; set; }
    }

    private readonly JsonFile<SchemaInfo> _schema;
    private readonly JsonFile<DigestState> _digest;
    private readonly string _rateLimitPath;
    private readonly JsonNoteRepository _notes;
    private readonly JsonTaskRepository _tasks;
    private readonly JsonAllowedUserRepository _users;
    private readonly JsonSessionRepository _sessions;
    private readonly JsonChatSettingsRepository _chatSettings;
    private readonly JsonUsageRepository _usage;

    public JsonStorage(string directory) {
        Directory = directory;
        _schema = new(File("schema.json"));
        _digest = new(File("digest.json"));
        _rateLimitPath = File("rate-limit.json");

        _notes = new(new JsonArrayFile<Note>(File("notes.json")));
        _tasks = new(new JsonArrayFile<TaskItem>(File("tasks.json")), _digest);
        _users = new(new JsonArrayFile<AllowedUser>(File("allowed-users.json")));
        _sessions = new(new JsonArrayFile<ChatSession>(File("sessions.json")));
        _chatSettings = new(new JsonArrayFile<ChatSettings>(File("chat-settings.json")));
        _usage = new(new JsonLinesFile<UsageRecord>(File("usage.jsonl")), _rateLimitPath);
    }

    public string Directory { get; }
    public StorageProvider Provider => StorageProvider.Json;
    public IAllowedUserRepository AllowedUsers => _users;
    public INoteRepository Notes => _notes;
    public ITaskRepository Tasks => _tasks;
    public ISessionRepository Sessions => _sessions;
    public IChatSettingsRepository ChatSettings => _chatSettings;
    public IUsageRepository Usage => _usage;

    private string File(string name) => Path.Combine(Directory, name);

    // Мьютекс schema.json держится на всё обновление: второй процесс подождёт и увидит новую версию.
    public void EnsureCreated() {
        System.IO.Directory.CreateDirectory(Directory);
        _schema.Update(info => {
            var version = info.Version != 0 ? info.Version : JsonLegacyV1.Exists(Directory) ? 1 : 0;
            if (version > SchemaVersion)
                throw new InvalidOperationException(
                    $"Формат файлов версии {version} новее, чем понимает программа ({SchemaVersion}). Обновите бота.");
            if (version == 1) JsonLegacyV1.Upgrade(Directory, Load);
            info.Version = SchemaVersion;
            return 0;
        });
    }

    public bool IsEmpty() =>
        !_notes.Any() && !_tasks.Any() && !_sessions.Any() && !_chatSettings.Any() && !_usage.Any() && !_users.Any()
        && _usage.GetRateLimit() is null;

    public StorageSnapshot Export() => new(
        _notes.All(),
        _tasks.All(),
        _digest.Read(d => d.LastDigestDate),
        _sessions.All(),
        _chatSettings.All(),
        _usage.All(),
        _usage.GetRateLimit(),
        _users.All());

    // Мьютекс schema.json держим на всё время импорта: другой экземпляр не начнёт ни импорт, ни обновление.
    public void Import(StorageSnapshot s) {
        EnsureCreated();
        _schema.Read(_ => {
            if (!IsEmpty())
                throw new InvalidOperationException("Хранилище Json не пустое: импорт возможен только в пустое.");
            Load(s);
            return 0;
        });
    }

    // Записывает снимок поверх текущих данных, без проверок (Import, обновление формата).
    private void Load(StorageSnapshot s) {
        _notes.ReplaceAll(s.Notes);
        _tasks.ReplaceAll(s.Tasks);
        _digest.Update(d => d.LastDigestDate = s.LastDigestDate);
        _sessions.ReplaceAll(s.Sessions);
        _chatSettings.ReplaceAll(s.ChatSettings);
        _usage.ReplaceAll(s.Usage);
        if (s.RateLimit is { } rl) _usage.SetRateLimit(rl);
        _users.ReplaceAll(s.AllowedUsers);
    }

    public void Dispose() { }
}

// Файлы версии 1 (до базовой сущности): personal-data.json (заметки и задачи с общим целочисленным
// счётчиком id), sessions.json и chat-settings.json (словари chatId -> значение), usage.jsonl без id,
// allowed-users.json (массив user id). Обновление переносит их в папку v1-backup и записывает
// данные в новом формате с новыми id. rate-limit.json не изменился.
internal static class JsonLegacyV1 {
    private static readonly string[] Files = ["personal-data.json", "sessions.json", "chat-settings.json", "usage.jsonl", "allowed-users.json"];

    private sealed class PersonalData {
        public List<V1Note> Notes { get; set; } = [];
        public List<V1Task> Tasks { get; set; } = [];
        public DateOnly? LastDigestDate { get; set; }
    }

    private sealed record V1Note(int Id, string Text, string[]? Tags, DateTimeOffset CreatedAt);

    private sealed record V1Task(int Id, string Title, DateOnly? Due, DateTimeOffset? RemindAt, bool Reminded, bool Done,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    public static bool Exists(string dir) => Files.Any(f => File.Exists(Path.Combine(dir, f)));

    public static void Upgrade(string dir, Action<StorageSnapshot> load) {
        var backup = Path.Combine(dir, "v1-backup");
        System.IO.Directory.CreateDirectory(backup);
        foreach (var f in Files.Where(f => File.Exists(Path.Combine(dir, f))))
            File.Move(Path.Combine(dir, f), Path.Combine(backup, f), overwrite: true);

        var now = DateTimeOffset.Now;
        var personal = Read<PersonalData>(backup, "personal-data.json") ?? new PersonalData();

        var notes = personal.Notes.OrderBy(n => n.Id)
            .Select(n => new Note { Id = ShortGuid.NewId(), Text = n.Text, Tags = n.Tags ?? [], CreatedAt = n.CreatedAt })
            .ToList();
        var tasks = personal.Tasks.OrderBy(t => t.Id)
            .Select(t => new TaskItem {
                Id = ShortGuid.NewId(),
                Title = t.Title,
                Due = t.Due,
                RemindAt = t.RemindAt,
                Reminded = t.Reminded,
                Done = t.Done,
                CreatedAt = t.CreatedAt,
                CompletedAt = t.CompletedAt,
            })
            .ToList();
        var sessions = (Read<Dictionary<long, string>>(backup, "sessions.json") ?? [])
            .Select(p => new ChatSession { Id = ShortGuid.NewId(), ExternalId = Entity.External(p.Key), SessionId = p.Value, UpdatedAt = now })
            .ToList();
        var settings = (Read<Dictionary<long, string>>(backup, "chat-settings.json") ?? [])
            .Select(p => new ChatSettings { Id = ShortGuid.NewId(), ExternalId = Entity.External(p.Key), Model = p.Value })
            .ToList();
        var users = (Read<List<long>>(backup, "allowed-users.json") ?? [])
            .Distinct()
            .Select(id => new AllowedUser { Id = ShortGuid.NewId(), ExternalId = Entity.External(id), AddedAt = now })
            .ToList();
        // Строки журнала совпадают с UsageRecord, кроме отсутствующего id.
        var usage = new JsonLinesFile<UsageRecord>(Path.Combine(backup, "usage.jsonl")).Read(l => l);
        foreach (var r in usage) r.Id = ShortGuid.NewId();

        // rate-limit.json остаётся на месте и не перезаписывается (RateLimit = null).
        load(new StorageSnapshot(notes, tasks, personal.LastDigestDate, sessions, settings, usage, null, users));
    }

    private static T? Read<T>(string dir, string name) {
        var path = Path.Combine(dir, name);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonFile.Indented) : default;
    }
}
