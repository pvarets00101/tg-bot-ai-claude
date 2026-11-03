using System.Text.Json;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;

namespace ClaudeTelegramBot.Storage.Json;

// Репозитории сущностей поверх JsonRepository<T>. Всё, что меняет данные, — внутри Items.Update,
// то есть под мьютексом файла.

internal sealed class JsonNoteRepository(IJsonCollection<Note> items) : JsonRepository<Note>(items), INoteRepository {
    protected override IEnumerable<Note> Sort(IEnumerable<Note> notes) =>
        notes.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id);

    public Note Add(string text, string[] tags) => Add(new Note { Text = text, Tags = tags, CreatedAt = DateTimeOffset.Now });

    public IReadOnlyList<Note> Search(string? query, string? tag, int limit) => Items.Read(l => Sort(l
            .Where(n => Visible(n))
            .Where(n => string.IsNullOrWhiteSpace(query) || n.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(n => string.IsNullOrWhiteSpace(tag) || n.Tags.Contains(tag)))
        .Take(limit)
        .ToList());
}

// Дата последней утренней сводки.
internal sealed class DigestState {
    public DateOnly? LastDigestDate { get; set; }
}

internal sealed class JsonTaskRepository(IJsonCollection<TaskItem> items, JsonFile<DigestState> digest)
    : JsonRepository<TaskItem>(items), ITaskRepository {
    protected override IEnumerable<TaskItem> Sort(IEnumerable<TaskItem> tasks) => tasks
        .OrderBy(t => t.Done)
        .ThenBy(t => t.Due ?? DateOnly.MaxValue)
        .ThenBy(t => t.RemindAt ?? DateTimeOffset.MaxValue)
        .ThenBy(t => t.Id);

    public TaskItem Add(string title, DateOnly? due, DateTimeOffset? remindAt) =>
        Add(new TaskItem { Title = title, Due = due, RemindAt = remindAt, CreatedAt = DateTimeOffset.Now });

    public TaskItem? Reschedule(ShortGuid id, DateOnly? due, DateTimeOffset? remindAt) => Items.Update(l => {
        var t = Find(l, id);
        if (t is null) return null;
        t.Due = due;
        t.RemindAt = remindAt;
        t.Reminded = false;
        return t;
    });

    public IReadOnlyList<TaskItem> List(bool includeDone) =>
        Items.Read(l => Sort(l.Where(t => Visible(t) && (includeDone || !t.Done))).ToList());

    public IReadOnlyList<TaskItem> Open(DateOnly? dueOnOrBefore = null) =>
        Items.Read(l => Sort(l.Where(t => Visible(t) && !t.Done && (dueOnOrBefore is null || t.Due <= dueOnOrBefore))).ToList());

    public (CompleteResult Result, string? Title) Complete(ShortGuid id) => Items.Update<(CompleteResult, string?)>(l => {
        var t = Find(l, id);
        if (t is null) return (CompleteResult.NotFound, null);
        if (t.Done) return (CompleteResult.AlreadyDone, t.Title);
        t.Done = true;
        t.CompletedAt = DateTimeOffset.Now;
        return (CompleteResult.Completed, t.Title);
    });

    public bool Snooze(ShortGuid id, DateTimeOffset at) => Items.Update(l => {
        var t = Find(l, id);
        if (t is null) return false;
        var day = DateOnly.FromDateTime(at.LocalDateTime);
        t.RemindAt = at;
        t.Reminded = false;
        t.Done = false;
        t.CompletedAt = null;
        if (t.Due < day) t.Due = day;
        return true;
    });

    public IReadOnlyList<TaskItem> TakeDueReminders(DateTimeOffset now) => Items.Update(l => {
        var due = l.Where(t => Visible(t) && !t.Done && !t.Reminded && t.RemindAt <= now).OrderBy(t => t.RemindAt).ToList();
        foreach (var t in due) t.Reminded = true;
        return due;
    });

    public bool TryMarkDigest(DateOnly day) => digest.Update(d => {
        if (d.LastDigestDate == day) return false;
        d.LastDigestDate = day;
        return true;
    });

    private static TaskItem? Find(List<TaskItem> l, ShortGuid id) => l.FirstOrDefault(t => t.Id == id && Visible(t));
}

internal sealed class JsonAllowedUserRepository(IJsonCollection<AllowedUser> items)
    : JsonRepository<AllowedUser>(items), IAllowedUserRepository {
    protected override IEnumerable<AllowedUser> Sort(IEnumerable<AllowedUser> users) =>
        users.OrderBy(u => u.AddedAt).ThenBy(u => u.Id);

    public IReadOnlyList<long> UserIds() => GetAll().Select(u => u.UserId).Order().ToList();

    // Новая запись или восстановление удалённой; уже активного не трогаем.
    public bool Add(long userId) => Items.Update(l => {
        var externalId = Entity.External(userId);
        var u = l.FirstOrDefault(x => x.ExternalId == externalId);
        if (u is null) {
            Insert(l, new AllowedUser { ExternalId = externalId, AddedAt = DateTimeOffset.Now });
            return true;
        }
        if (!u.SoftDeleted) return false;
        u.SoftDeleted = false;
        u.SoftDeletedDate = null;
        u.AddedAt = DateTimeOffset.Now;
        return true;
    });

    public bool Remove(long userId) => DeleteByExternalId(Entity.External(userId));
}

internal sealed class JsonSessionRepository(IJsonCollection<ChatSession> items)
    : JsonRepository<ChatSession>(items), ISessionRepository {
    public string? GetSessionId(long chatId) => GetByExternalId(Entity.External(chatId))?.SessionId;

    public void SetSessionId(long chatId, string sessionId) =>
        Upsert(new ChatSession { ExternalId = Entity.External(chatId), SessionId = sessionId, UpdatedAt = DateTimeOffset.Now });

    public void Reset(long chatId) => DeleteByExternalId(Entity.External(chatId));
}

internal sealed class JsonChatSettingsRepository(IJsonCollection<ChatSettings> items)
    : JsonRepository<ChatSettings>(items), IChatSettingsRepository {
    public string? GetModel(long chatId) => GetByExternalId(Entity.External(chatId))?.Model;

    public void SetModel(long chatId, string? model) {
        if (string.IsNullOrWhiteSpace(model)) DeleteByExternalId(Entity.External(chatId));
        else Upsert(new ChatSettings { ExternalId = Entity.External(chatId), Model = model });
    }
}

// Журнал запросов — JSON Lines: Add дописывает строку, не перечитывая и не переписывая файл.
// Поэтому уникальность id здесь не проверяется — новые id и так уникальны.
internal sealed class JsonUsageRepository(JsonLinesFile<UsageRecord> log, string rateLimitPath)
    : JsonRepository<UsageRecord>(log), IUsageRepository {
    protected override IEnumerable<UsageRecord> Sort(IEnumerable<UsageRecord> records) =>
        records.OrderBy(r => r.At).ThenBy(r => r.Id);

    public override UsageRecord Add(UsageRecord record) {
        if (record.Id.IsEmpty) record.Id = ShortGuid.NewId();
        log.Append(record);
        return record;
    }

    public IReadOnlyList<UsageRecord> ReadSince(DateTimeOffset since) =>
        Items.Read(l => Sort(l.Where(r => Visible(r) && r.At >= since)).ToList());

    public RateLimitSnapshot? GetRateLimit() => JsonFile.Locked(rateLimitPath, () => {
        try {
            return File.Exists(rateLimitPath)
                ? JsonSerializer.Deserialize<RateLimitSnapshot>(File.ReadAllText(rateLimitPath), JsonFile.Compact)
                : null;
        } catch (JsonException) {
            return null; // битый файл — просто начнём заново
        }
    });

    public void SetRateLimit(RateLimitSnapshot snapshot) => JsonFile.Locked(rateLimitPath, () => {
        JsonFile.WriteAtomic(rateLimitPath, JsonSerializer.Serialize(snapshot, JsonFile.Compact));
        return 0;
    });
}
