using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PersonalMcp;

public sealed class Note {
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TaskItem {
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public DateOnly? Due { get; set; }

    // Когда прислать напоминание в Telegram. null = без напоминания.
    public DateTimeOffset? RemindAt { get; set; }
    public bool Reminded { get; set; }

    public bool Done { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class PersonalData {
    public int NextId { get; set; } = 1;
    public List<Note> Notes { get; set; } = [];
    public List<TaskItem> Tasks { get; set; } = [];

    // Дата последней утренней сводки, чтобы не слать её дважды.
    public DateOnly? LastDigestDate { get; set; }
}

// Заметки и задачи в одном JSON-файле. Файл общий для MCP-сервера (процесс, который запускает claude)
// и бота (напоминания), поэтому доступ защищён именованным мьютексом — он работает между процессами.
// Этот файл подключён в проект бота ссылкой (см. ClaudeTelegramBot.csproj).
public sealed class DataStore {
    private static readonly JsonSerializerOptions Json = new() {
        WriteIndented = true,
        // Relaxed-экранирование, чтобы кириллица в файле оставалась читаемой.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly string _mutexName;

    public DataStore(string path) {
        _path = Path.GetFullPath(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_path.ToLowerInvariant())))[..16];
        _mutexName = $"Local\\PersonalMcpData_{hash}";
    }

    public T Read<T>(Func<PersonalData, T> query) => Locked(() => query(Load()));

    public T Update<T>(Func<PersonalData, T> change) => Locked(() => {
        var data = Load();
        var result = change(data);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
        File.Move(tmp, _path, overwrite: true);
        return result;
    });

    private T Locked<T>(Func<T> action) {
        using var mutex = new Mutex(false, _mutexName);
        try {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Файл данных занят другим процессом.");
        } catch (AbandonedMutexException) {
            // Другой процесс упал, держа мьютекс. Владение перешло к нам, данные на диске целые
            // (запись идёт через временный файл), можно продолжать.
        }

        try {
            return action();
        } finally {
            mutex.ReleaseMutex();
        }
    }

    private PersonalData Load() => File.Exists(_path)
        ? JsonSerializer.Deserialize<PersonalData>(File.ReadAllText(_path), Json) ?? new()
        : new();
}
