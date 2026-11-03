using System.Text.Json;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using Microsoft.Data.Sqlite;

namespace ClaudeTelegramBot.Storage.Sqlite;

// Репозитории сущностей поверх SqliteRepository<T>. Методы-намерения — одним запросом
// (UPDATE ... RETURNING, ON CONFLICT) или в транзакции, чтобы бот и MCP-сервер не мешали друг другу.

internal sealed class SqliteNoteRepository(SqliteStorage s) : SqliteRepository<Note>(s), INoteRepository {
    protected override string Table => "notes";
    protected override string[] Columns => ["text", "tags", "created_at"];
    protected override string OrderBy => "created_at DESC, id DESC";

    // Теги — JSON-массивом.
    protected override object?[] Values(Note n) => [n.Text, JsonSerializer.Serialize(n.Tags), n.CreatedAt];

    protected override void Read(SqliteRow r, Note n) {
        n.Text = r.String();
        n.Tags = JsonSerializer.Deserialize<string[]>(r.String()) ?? [];
        n.CreatedAt = r.Time();
    }

    public Note Add(string text, string[] tags) => Add(new Note { Text = text, Tags = tags, CreatedAt = DateTimeOffset.Now });

    // contains_ci — функция из SqliteStorage.Open: lower() в SQLite понимает только латиницу.
    public IReadOnlyList<Note> Search(string? query, string? tag, int limit) => Query($"""
        {SelectAll}
        WHERE NOT soft_deleted
          AND contains_ci(text, @p1)
          AND (@p2 IS NULL OR EXISTS (SELECT 1 FROM json_each(notes.tags) WHERE value = @p2))
        ORDER BY {OrderBy}
        LIMIT @p3
        """, Strings.NullIfBlank(query), Strings.NullIfBlank(tag), limit);
}

internal sealed class SqliteTaskRepository(SqliteStorage s) : SqliteRepository<TaskItem>(s), ITaskRepository {
    protected override string Table => "tasks";
    protected override string[] Columns => ["title", "due", "remind_at", "reminded", "done", "created_at", "completed_at"];
    protected override string OrderBy => "done, due IS NULL, due, remind_at IS NULL, remind_at, id";

    protected override object?[] Values(TaskItem t) => [t.Title, t.Due, t.RemindAt, t.Reminded, t.Done, t.CreatedAt, t.CompletedAt];

    protected override void Read(SqliteRow r, TaskItem t) {
        t.Title = r.String();
        t.Due = r.DateOrNull();
        t.RemindAt = r.TimeOrNull();
        t.Reminded = r.Bool();
        t.Done = r.Bool();
        t.CreatedAt = r.Time();
        t.CompletedAt = r.TimeOrNull();
    }

    public TaskItem Add(string title, DateOnly? due, DateTimeOffset? remindAt) =>
        Add(new TaskItem { Title = title, Due = due, RemindAt = remindAt, CreatedAt = DateTimeOffset.Now });

    public TaskItem? Reschedule(ShortGuid id, DateOnly? due, DateTimeOffset? remindAt) => Query($"""
        UPDATE tasks SET due = @p2, remind_at = @p3, reminded = 0
        WHERE id = @p1 AND NOT soft_deleted RETURNING {AllColumns}
        """, id, due, remindAt).SingleOrDefault();

    public IReadOnlyList<TaskItem> List(bool includeDone) =>
        Query($"{SelectAll} WHERE NOT soft_deleted AND (@p1 OR NOT done) ORDER BY {OrderBy}", includeDone);

    public IReadOnlyList<TaskItem> Open(DateOnly? dueOnOrBefore = null) => Query($"""
        {SelectAll}
        WHERE NOT soft_deleted AND NOT done AND (@p1 IS NULL OR due <= @p1)
        ORDER BY {OrderBy}
        """, dueOnOrBefore);

    public (CompleteResult Result, string? Title) Complete(ShortGuid id) {
        using var conn = Storage.Open();
        using var tx = conn.BeginTransaction();
        var t = Query(conn, tx, $"{SelectAll} WHERE id = @p1 AND NOT soft_deleted", id).SingleOrDefault();
        if (t is null) return (CompleteResult.NotFound, null);
        if (t.Done) return (CompleteResult.AlreadyDone, t.Title);

        Sql.Exec(conn, tx, "UPDATE tasks SET done = 1, completed_at = @p2 WHERE id = @p1", id, DateTimeOffset.Now);
        tx.Commit();
        return (CompleteResult.Completed, t.Title);
    }

    public bool Snooze(ShortGuid id, DateTimeOffset at) => Exec("""
        UPDATE tasks SET remind_at = @p2, reminded = 0, done = 0, completed_at = NULL,
                         due = CASE WHEN due < @p3 THEN @p3 ELSE due END
        WHERE id = @p1 AND NOT soft_deleted
        """, id, at, DateOnly.FromDateTime(at.LocalDateTime)) > 0;

    public IReadOnlyList<TaskItem> TakeDueReminders(DateTimeOffset now) => Query($"""
        UPDATE tasks SET reminded = 1
        WHERE NOT soft_deleted AND NOT done AND NOT reminded AND remind_at <= @p1
        RETURNING {AllColumns}
        """, now).OrderBy(t => t.RemindAt).ToList();

    public bool TryMarkDigest(DateOnly day) => Scalar("""
        INSERT INTO digest_state (id, last_date) VALUES (1, @p1)
        ON CONFLICT (id) DO UPDATE SET last_date = excluded.last_date
        WHERE digest_state.last_date <> excluded.last_date
        RETURNING 1
        """, day) is not null;
}

internal sealed class SqliteAllowedUserRepository(SqliteStorage s) : SqliteRepository<AllowedUser>(s), IAllowedUserRepository {
    protected override string Table => "allowed_users";
    protected override string[] Columns => ["added_at"];
    protected override string OrderBy => "added_at, id";

    protected override object?[] Values(AllowedUser u) => [u.AddedAt];

    protected override void Read(SqliteRow r, AllowedUser u) => u.AddedAt = r.Time();

    public IReadOnlyList<long> UserIds() => GetAll().Select(u => u.UserId).Order().ToList();

    // Новая строка или восстановление удалённой; уже активный пользователь не трогается (WHERE в DO UPDATE).
    public bool Add(long userId) => Scalar("""
        INSERT INTO allowed_users (id, external_id, added_at) VALUES (@p1, @p2, @p3)
        ON CONFLICT (external_id) DO UPDATE SET soft_deleted = 0, soft_deleted_date = NULL, added_at = excluded.added_at
        WHERE allowed_users.soft_deleted
        RETURNING 1
        """, ShortGuid.NewId(), Entity.External(userId), DateTimeOffset.Now) is not null;

    public bool Remove(long userId) => DeleteByExternalId(Entity.External(userId));
}

internal sealed class SqliteSessionRepository(SqliteStorage s) : SqliteRepository<ChatSession>(s), ISessionRepository {
    protected override string Table => "sessions";
    protected override string[] Columns => ["session_id", "updated_at"];

    protected override object?[] Values(ChatSession c) => [c.SessionId, c.UpdatedAt];

    protected override void Read(SqliteRow r, ChatSession c) {
        c.SessionId = r.String();
        c.UpdatedAt = r.Time();
    }

    public string? GetSessionId(long chatId) => GetByExternalId(Entity.External(chatId))?.SessionId;

    public void SetSessionId(long chatId, string sessionId) =>
        Upsert(new ChatSession { ExternalId = Entity.External(chatId), SessionId = sessionId, UpdatedAt = DateTimeOffset.Now });

    public void Reset(long chatId) => DeleteByExternalId(Entity.External(chatId));
}

internal sealed class SqliteChatSettingsRepository(SqliteStorage s) : SqliteRepository<ChatSettings>(s), IChatSettingsRepository {
    protected override string Table => "chat_settings";
    protected override string[] Columns => ["model"];

    protected override object?[] Values(ChatSettings c) => [c.Model];

    protected override void Read(SqliteRow r, ChatSettings c) => c.Model = r.String();

    public string? GetModel(long chatId) => GetByExternalId(Entity.External(chatId))?.Model;

    public void SetModel(long chatId, string? model) {
        if (string.IsNullOrWhiteSpace(model)) DeleteByExternalId(Entity.External(chatId));
        else Upsert(new ChatSettings { ExternalId = Entity.External(chatId), Model = model });
    }
}

internal sealed class SqliteUsageRepository(SqliteStorage s) : SqliteRepository<UsageRecord>(s), IUsageRepository {
    protected override string Table => "usage";

    protected override string[] Columns =>
        ["at", "model", "is_error", "cost_usd", "duration_ms", "num_turns", "input_tokens", "output_tokens"];

    protected override string OrderBy => "at, id";

    // decimal — текстом: у SQLite нет точного десятичного типа.
    protected override object?[] Values(UsageRecord u) =>
        [u.At, u.Model, u.IsError, u.CostUsd, u.DurationMs, u.NumTurns, u.InputTokens, u.OutputTokens];

    protected override void Read(SqliteRow r, UsageRecord u) {
        u.At = r.Time();
        u.Model = r.StringOrNull();
        u.IsError = r.Bool();
        u.CostUsd = r.Decimal();
        u.DurationMs = r.Int();
        u.NumTurns = r.Int();
        u.InputTokens = r.Long();
        u.OutputTokens = r.Long();
    }

    public IReadOnlyList<UsageRecord> ReadSince(DateTimeOffset since) =>
        Query($"{SelectAll} WHERE NOT soft_deleted AND at >= @p1 ORDER BY {OrderBy}", since);

    public RateLimitSnapshot? GetRateLimit() {
        using var conn = Storage.Open();
        return ReadRateLimit(conn, null);
    }

    public void SetRateLimit(RateLimitSnapshot snapshot) {
        using var conn = Storage.Open();
        WriteRateLimit(conn, null, snapshot);
    }

    internal static RateLimitSnapshot? ReadRateLimit(SqliteConnection conn, SqliteTransaction? tx) {
        if (Sql.Scalar(conn, tx, "SELECT snapshot FROM rate_limit WHERE id = 1") is not string json) return null;
        try {
            return JsonSerializer.Deserialize<RateLimitSnapshot>(json);
        } catch (JsonException) {
            return null; // несовместимый формат — просто начнём заново
        }
    }

    internal static void WriteRateLimit(SqliteConnection conn, SqliteTransaction? tx, RateLimitSnapshot snapshot) =>
        Sql.Exec(conn, tx, "INSERT OR REPLACE INTO rate_limit (id, snapshot) VALUES (1, @p1)", JsonSerializer.Serialize(snapshot));
}
