using System.Text.Json;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ClaudeTelegramBot.Storage.Postgres;

// Репозитории сущностей поверх PostgresRepository<T>. Запросы — LINQ (EF Core). Операции, которые
// должны быть атомарными для двух процессов, — условным ExecuteUpdate (один UPDATE ... WHERE) или
// вставкой, которую отклонит уникальный индекс. На SQL остались только TakeDueReminders
// (UPDATE ... RETURNING: забрать и пометить одним оператором в LINQ не выразить) и COPY журнала.

internal sealed class PostgresNoteRepository(PostgresDb db) : PostgresRepository<Note>(db), INoteRepository {
    protected override IQueryable<Note> Sort(IQueryable<Note> q) => q.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id);

    public Note Add(string text, string[] tags) => Add(new Note { Text = text, Tags = tags, CreatedAt = DateTimeOffset.Now });

    public IReadOnlyList<Note> Search(string? query, string? tag, int limit) {
        query = Strings.NullIfBlank(query)?.ToLower();
        tag = Strings.NullIfBlank(tag);
        using var ctx = Context();
        var notes = Visible(ctx.Set<Note>());
        if (query is not null) notes = notes.Where(n => n.Text.ToLower().Contains(query));
        if (tag is not null) notes = notes.Where(n => n.Tags.Contains(tag));
        return Sort(notes).Take(limit).ToList();
    }
}

internal sealed class PostgresTaskRepository(PostgresDb db) : PostgresRepository<TaskItem>(db), ITaskRepository {
    protected override IQueryable<TaskItem> Sort(IQueryable<TaskItem> q) => q
        .OrderBy(t => t.Done)
        .ThenBy(t => t.Due == null).ThenBy(t => t.Due)
        .ThenBy(t => t.RemindAt == null).ThenBy(t => t.RemindAt)
        .ThenBy(t => t.Id);

    public TaskItem Add(string title, DateOnly? due, DateTimeOffset? remindAt) =>
        Add(new TaskItem { Title = title, Due = due, RemindAt = remindAt, CreatedAt = DateTimeOffset.Now });

    public TaskItem? Reschedule(ShortGuid id, DateOnly? due, DateTimeOffset? remindAt) {
        using var ctx = Context();
        var changed = Visible(ctx.Set<TaskItem>())
            .Where(t => t.Id == id)
            .ExecuteUpdate(s => s
                .SetProperty(t => t.Due, due)
                .SetProperty(t => t.RemindAt, remindAt)
                .SetProperty(t => t.Reminded, false));
        return changed == 0 ? null : Visible(ctx.Set<TaskItem>()).FirstOrDefault(t => t.Id == id);
    }

    public IReadOnlyList<TaskItem> List(bool includeDone) {
        using var ctx = Context();
        var tasks = Visible(ctx.Set<TaskItem>());
        if (!includeDone) tasks = tasks.Where(t => !t.Done);
        return Sort(tasks).ToList();
    }

    public IReadOnlyList<TaskItem> Open(DateOnly? dueOnOrBefore = null) {
        using var ctx = Context();
        var tasks = Visible(ctx.Set<TaskItem>()).Where(t => !t.Done);
        if (dueOnOrBefore is { } day) tasks = tasks.Where(t => t.Due <= day);
        return Sort(tasks).ToList();
    }

    // Отметка «выполнено» — условным UPDATE (только если ещё не выполнена): из двух одновременных
    // вызовов Completed получит ровно один.
    public (CompleteResult Result, string? Title) Complete(ShortGuid id) {
        using var ctx = Context();
        var completed = Visible(ctx.Set<TaskItem>())
            .Where(t => t.Id == id && !t.Done)
            .ExecuteUpdate(s => s
                .SetProperty(t => t.Done, true)
                .SetProperty(t => t.CompletedAt, (DateTimeOffset?)DateTimeOffset.Now));
        var title = Visible(ctx.Set<TaskItem>()).Where(t => t.Id == id).Select(t => t.Title).FirstOrDefault();
        if (completed > 0) return (CompleteResult.Completed, title);
        return title is null ? (CompleteResult.NotFound, null) : (CompleteResult.AlreadyDone, title);
    }

    public bool Snooze(ShortGuid id, DateTimeOffset at) {
        DateOnly? day = DateOnly.FromDateTime(at.LocalDateTime);
        using var ctx = Context();
        return Visible(ctx.Set<TaskItem>())
            .Where(t => t.Id == id)
            .ExecuteUpdate(s => s
                .SetProperty(t => t.RemindAt, (DateTimeOffset?)at)
                .SetProperty(t => t.Reminded, false)
                .SetProperty(t => t.Done, false)
                .SetProperty(t => t.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(t => t.Due, t => t.Due < day ? day : t.Due)) > 0;
    }

    // SQL: забрать и пометить отправленными нужно одним оператором, чтобы каждое напоминание
    // досталось ровно одному процессу. EF только материализует строки RETURNING.
    public IReadOnlyList<TaskItem> TakeDueReminders(DateTimeOffset now) {
        using var ctx = Context();
        return ctx.Set<TaskItem>()
            .FromSqlRaw("""
                UPDATE tasks SET reminded = true
                WHERE NOT soft_deleted AND NOT done AND NOT reminded AND remind_at <= {0}
                RETURNING *
                """, now.ToUniversalTime())
            .AsEnumerable()
            .OrderBy(t => t.RemindAt)
            .ToList();
    }

    // Обновить дату, только если она другая; строки ещё нет — вставить. Если вставку опередил
    // второй процесс, уникальный ключ её отклонит: сводку за этот день уже отметили.
    public bool TryMarkDigest(DateOnly day) {
        using var ctx = Context();
        if (ctx.Set<DigestRow>().Where(d => d.Id == 1 && d.LastDate != day)
                .ExecuteUpdate(s => s.SetProperty(d => d.LastDate, day)) > 0)
            return true;
        if (ctx.Set<DigestRow>().Any()) return false;

        ctx.Add(new DigestRow { Id = 1, LastDate = day });
        try {
            ctx.SaveChanges();
            return true;
        } catch (DbUpdateException ex) when (PostgresDb.IsUniqueViolation(ex)) {
            return false;
        }
    }
}

internal sealed class PostgresAllowedUserRepository(PostgresDb db) : PostgresRepository<AllowedUser>(db), IAllowedUserRepository {
    protected override IQueryable<AllowedUser> Sort(IQueryable<AllowedUser> q) => q.OrderBy(u => u.AddedAt).ThenBy(u => u.Id);

    public IReadOnlyList<long> UserIds() => GetAll().Select(u => u.UserId).Order().ToList();

    // Удалённого — восстановить условным UPDATE; нового — вставить. Уже активный не трогается:
    // вставку отклонит уникальный индекс external_id.
    public bool Add(long userId) {
        var externalId = Entity.External(userId);
        var now = DateTimeOffset.Now;
        using var ctx = Context();
        var restored = ctx.Set<AllowedUser>()
            .Where(u => u.ExternalId == externalId && u.SoftDeleted)
            .ExecuteUpdate(s => s
                .SetProperty(u => u.SoftDeleted, false)
                .SetProperty(u => u.SoftDeletedDate, (DateTimeOffset?)null)
                .SetProperty(u => u.AddedAt, now));
        if (restored > 0) return true;

        ctx.Add(new AllowedUser { Id = ShortGuid.NewId(), ExternalId = externalId, AddedAt = now });
        try {
            ctx.SaveChanges();
            return true;
        } catch (DbUpdateException ex) when (PostgresDb.IsUniqueViolation(ex)) {
            return false;
        }
    }

    public bool Remove(long userId) => DeleteByExternalId(Entity.External(userId));
}

internal sealed class PostgresSessionRepository(PostgresDb db) : PostgresRepository<ChatSession>(db), ISessionRepository {
    public string? GetSessionId(long chatId) => GetByExternalId(Entity.External(chatId))?.SessionId;

    public void SetSessionId(long chatId, string sessionId) =>
        Upsert(new ChatSession { ExternalId = Entity.External(chatId), SessionId = sessionId, UpdatedAt = DateTimeOffset.Now });

    public void Reset(long chatId) => DeleteByExternalId(Entity.External(chatId));
}

internal sealed class PostgresChatSettingsRepository(PostgresDb db) : PostgresRepository<ChatSettings>(db), IChatSettingsRepository {
    public string? GetModel(long chatId) => GetByExternalId(Entity.External(chatId))?.Model;

    public void SetModel(long chatId, string? model) {
        if (string.IsNullOrWhiteSpace(model)) DeleteByExternalId(Entity.External(chatId));
        else Upsert(new ChatSettings { ExternalId = Entity.External(chatId), Model = model });
    }
}

internal sealed class PostgresUsageRepository(PostgresDb db) : PostgresRepository<UsageRecord>(db), IUsageRepository {
    protected override IQueryable<UsageRecord> Sort(IQueryable<UsageRecord> q) => q.OrderBy(r => r.At).ThenBy(r => r.Id);

    // DateTimeOffset.MinValue Npgsql передаёт как -infinity, поэтому «всё» тоже работает.
    public IReadOnlyList<UsageRecord> ReadSince(DateTimeOffset since) {
        using var ctx = Context();
        return Sort(Visible(ctx.Set<UsageRecord>()).Where(r => r.At >= since)).ToList();
    }

    public RateLimitSnapshot? GetRateLimit() {
        using var ctx = Context();
        return ReadRateLimit(ctx);
    }

    // Строка одна: обновить, а если её нет — вставить (при гонке со вторым процессом — ещё раз обновить).
    public void SetRateLimit(RateLimitSnapshot snapshot) {
        var json = JsonSerializer.Serialize(snapshot);
        using var ctx = Context();
        for (var attempt = 1; ; attempt++) {
            if (ctx.Set<RateLimitRow>().Where(r => r.Id == 1).ExecuteUpdate(s => s.SetProperty(r => r.Snapshot, json)) > 0) return;
            ctx.Add(new RateLimitRow { Id = 1, Snapshot = json });
            try {
                ctx.SaveChanges();
                return;
            } catch (DbUpdateException ex) when (PostgresDb.IsUniqueViolation(ex) && attempt < 3) {
                ctx.ChangeTracker.Clear();
            }
        }
    }

    internal static RateLimitSnapshot? ReadRateLimit(BotDbContext ctx) {
        if (ctx.Set<RateLimitRow>().Where(r => r.Id == 1).Select(r => r.Snapshot).FirstOrDefault() is not { } json) return null;
        try {
            return JsonSerializer.Deserialize<RateLimitSnapshot>(json);
        } catch (JsonException) {
            return null; // несовместимый формат — просто начнём заново
        }
    }

    // SQL (COPY): журнал запросов растёт без ограничений, поэтому при переносе — одним потоком,
    // а не INSERT на каждую строку. COPY идёт в текущей транзакции соединения контекста.
    internal override void InsertMany(BotDbContext ctx, IReadOnlyList<UsageRecord> records) {
        if (records.Count == 0) return;
        var conn = (NpgsqlConnection)ctx.Database.GetDbConnection();
        using var w = conn.BeginBinaryImport("""
            COPY usage (id, external_id, soft_deleted, soft_deleted_date,
                        at, model, is_error, cost_usd, duration_ms, num_turns, input_tokens, output_tokens)
            FROM STDIN (FORMAT BINARY)
            """);
        foreach (var r in records) {
            if (r.Id.IsEmpty) r.Id = ShortGuid.NewId();
            w.StartRow();
            w.Write(r.Id.Value, NpgsqlDbType.Uuid);
            WriteNullable(w, r.ExternalId, NpgsqlDbType.Text);
            w.Write(r.SoftDeleted, NpgsqlDbType.Boolean);
            WriteNullable(w, r.SoftDeletedDate?.ToUniversalTime(), NpgsqlDbType.TimestampTz);
            w.Write(r.At.ToUniversalTime(), NpgsqlDbType.TimestampTz);
            WriteNullable(w, r.Model, NpgsqlDbType.Text);
            w.Write(r.IsError, NpgsqlDbType.Boolean);
            w.Write(r.CostUsd, NpgsqlDbType.Numeric);
            w.Write(r.DurationMs, NpgsqlDbType.Integer);
            w.Write(r.NumTurns, NpgsqlDbType.Integer);
            w.Write(r.InputTokens, NpgsqlDbType.Bigint);
            w.Write(r.OutputTokens, NpgsqlDbType.Bigint);
        }
        w.Complete();
    }

    private static void WriteNullable<TValue>(NpgsqlBinaryImporter w, TValue? value, NpgsqlDbType type) {
        if (value is null) w.WriteNull();
        else w.Write(value, type);
    }
}
