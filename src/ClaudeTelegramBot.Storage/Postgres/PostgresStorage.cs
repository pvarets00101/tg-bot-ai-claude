using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaudeTelegramBot.Storage.Postgres;

// Хранилище в PostgreSQL. Данные читаются и пишутся через EF Core (BotDbContext, запросы LINQ в
// репозиториях); на SQL остались схема и её обновление, блокировки и COPY журнала.
// Согласованность между ботом и MCP-сервером дают условные UPDATE и транзакции. Время — timestamptz (UTC), наружу отдаётся в локальном поясе.
// Id сущностей — uuid (ShortGuid), их выдаёт приложение.
public sealed class PostgresStorage : IStorage {
    // Версия схемы в schema_version. 1 — целочисленные id и общая последовательность (до базовой
    // сущности), 2 — uuid, external_id и мягкое удаление.
    internal const int SchemaVersion = 2;

    private const string Schema = $$"""
        CREATE TABLE IF NOT EXISTS schema_version (
            id      integer PRIMARY KEY CHECK (id = 1),
            version integer NOT NULL
        );

        CREATE TABLE IF NOT EXISTS notes (
            {{Pg.EntityColumnsDdl}},
            text       text        NOT NULL,
            tags       text[]      NOT NULL DEFAULT '{}',
            created_at timestamptz NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tasks (
            {{Pg.EntityColumnsDdl}},
            title        text        NOT NULL,
            due          date,
            remind_at    timestamptz,
            reminded     boolean     NOT NULL DEFAULT false,
            done         boolean     NOT NULL DEFAULT false,
            created_at   timestamptz NOT NULL,
            completed_at timestamptz
        );
        CREATE INDEX IF NOT EXISTS tasks_pending_reminders ON tasks (remind_at)
            WHERE NOT done AND NOT reminded AND NOT soft_deleted;

        -- Дата последней утренней сводки, чтобы не слать её дважды. Всегда одна строка.
        CREATE TABLE IF NOT EXISTS digest_state (
            id        integer PRIMARY KEY CHECK (id = 1),
            last_date date NOT NULL
        );

        -- external_id — Telegram chat id.
        CREATE TABLE IF NOT EXISTS sessions (
            {{Pg.EntityColumnsDdl}},
            session_id text        NOT NULL,
            updated_at timestamptz NOT NULL
        );

        -- external_id — Telegram chat id.
        CREATE TABLE IF NOT EXISTS chat_settings (
            {{Pg.EntityColumnsDdl}},
            model text NOT NULL
        );

        CREATE TABLE IF NOT EXISTS usage (
            {{Pg.EntityColumnsDdl}},
            at            timestamptz NOT NULL,
            model         text,
            is_error      boolean     NOT NULL,
            cost_usd      numeric     NOT NULL,
            duration_ms   integer     NOT NULL,
            num_turns     integer     NOT NULL,
            input_tokens  bigint      NOT NULL,
            output_tokens bigint      NOT NULL
        );
        CREATE INDEX IF NOT EXISTS usage_at ON usage (at);

        -- Кому разрешено писать боту. external_id — Telegram user id.
        CREATE TABLE IF NOT EXISTS allowed_users (
            {{Pg.EntityColumnsDdl}},
            added_at timestamptz NOT NULL
        );

        -- Последние данные о лимитах подписки. Всегда одна строка.
        CREATE TABLE IF NOT EXISTS rate_limit (
            id       integer PRIMARY KEY CHECK (id = 1),
            snapshot jsonb NOT NULL
        );
        """;

    // Версия 1 → 2: старые таблицы отодвигаются в *_v1 (с их индексами и ключами, чтобы имена
    // не мешали новой схеме), после создания новой схемы данные копируются с новыми id.
    // digest_state и rate_limit не изменились.
    private static readonly string[] V1Tables = ["notes", "tasks", "sessions", "chat_settings", "usage", "allowed_users"];

    private static readonly string MoveV1Aside =
        "DROP INDEX IF EXISTS tasks_pending_reminders;\nDROP INDEX IF EXISTS usage_at;\n" +
        string.Concat(V1Tables.Select(t =>
            $"ALTER TABLE {t} RENAME TO {t}_v1;\nALTER TABLE {t}_v1 RENAME CONSTRAINT {t}_pkey TO {t}_v1_pkey;\n"));

    private const string CopyV1 = """
        INSERT INTO notes (id, text, tags, created_at)
            SELECT gen_random_uuid(), text, tags, created_at FROM notes_v1;
        INSERT INTO tasks (id, title, due, remind_at, reminded, done, created_at, completed_at)
            SELECT gen_random_uuid(), title, due, remind_at, reminded, done, created_at, completed_at FROM tasks_v1;
        INSERT INTO sessions (id, external_id, session_id, updated_at)
            SELECT gen_random_uuid(), chat_id::text, session_id, updated_at FROM sessions_v1;
        INSERT INTO chat_settings (id, external_id, model)
            SELECT gen_random_uuid(), chat_id::text, model FROM chat_settings_v1;
        INSERT INTO usage (id, at, model, is_error, cost_usd, duration_ms, num_turns, input_tokens, output_tokens)
            SELECT gen_random_uuid(), at, model, is_error, cost_usd, duration_ms, num_turns, input_tokens, output_tokens
            FROM usage_v1;
        INSERT INTO allowed_users (id, external_id, added_at)
            SELECT gen_random_uuid(), user_id::text, added_at FROM allowed_users_v1;
        DROP TABLE notes_v1, tasks_v1, sessions_v1, chat_settings_v1, usage_v1, allowed_users_v1;
        DROP SEQUENCE IF EXISTS personal_id_seq;
        """;

    private const string AllTables = "notes, tasks, digest_state, sessions, chat_settings, usage, rate_limit, allowed_users";

    private readonly PostgresDb _db;
    private readonly PostgresNoteRepository _notes;
    private readonly PostgresTaskRepository _tasks;
    private readonly PostgresAllowedUserRepository _users;
    private readonly PostgresSessionRepository _sessions;
    private readonly PostgresChatSettingsRepository _chatSettings;
    private readonly PostgresUsageRepository _usage;

    public PostgresStorage(string connectionString) {
        _db = new PostgresDb(connectionString);
        _notes = new(_db);
        _tasks = new(_db);
        _users = new(_db);
        _sessions = new(_db);
        _chatSettings = new(_db);
        _usage = new(_db);
    }

    public StorageProvider Provider => StorageProvider.Postgres;
    public IAllowedUserRepository AllowedUsers => _users;
    public INoteRepository Notes => _notes;
    public ITaskRepository Tasks => _tasks;
    public ISessionRepository Sessions => _sessions;
    public IChatSettingsRepository ChatSettings => _chatSettings;
    public IUsageRepository Usage => _usage;

    // SQL: DDL и переименования таблиц в LINQ не выражаются, а схему создаёт не EF (миграций нет).
    // Вызывают и бот, и MCP-сервер; advisory lock — чтобы два процесса не создавали и не обновляли
    // схему одновременно (параллельный CREATE ... IF NOT EXISTS может упасть).
    public void EnsureCreated() {
        try {
            using var conn = _db.DataSource.OpenConnection();
            using var tx = conn.BeginTransaction();
            Pg.Exec(conn, tx, "SELECT pg_advisory_xact_lock(7212460351)");

            var version = CurrentVersion(conn, tx);
            if (version > SchemaVersion)
                throw new InvalidOperationException(
                    $"Схема базы версии {version} новее, чем понимает программа ({SchemaVersion}). Обновите бота.");
            if (version == 1) Pg.Exec(conn, tx, MoveV1Aside);
            Pg.Exec(conn, tx, Schema);
            if (version == 1) Pg.Exec(conn, tx, CopyV1);
            Pg.Exec(conn, tx, """
                INSERT INTO schema_version (id, version) VALUES (1, $1)
                ON CONFLICT (id) DO UPDATE SET version = excluded.version
                """, SchemaVersion);
            tx.Commit();
        } catch (NpgsqlException ex) {
            throw new InvalidOperationException(
                "Не удалось подключиться к PostgreSQL. Проверьте ConnectionStrings:Postgres " +
                "(пароль — в user-secrets) и что база создана.", ex);
        }
    }

    // 0 — базы нет, 1 — схема до версионирования (есть notes, нет schema_version).
    private static int CurrentVersion(NpgsqlConnection conn, NpgsqlTransaction tx) {
        if (Pg.Scalar(conn, tx, "SELECT to_regclass('schema_version') IS NOT NULL") is true)
            return Pg.Scalar(conn, tx, "SELECT version FROM schema_version WHERE id = 1") is int v ? v : 0;
        return Pg.Scalar(conn, tx, "SELECT to_regclass('notes') IS NOT NULL") is true ? 1 : 0;
    }

    public bool IsEmpty() {
        using var ctx = _db.Context();
        return IsEmpty(ctx);
    }

    private bool IsEmpty(BotDbContext ctx) =>
        !_notes.Any(ctx) && !_tasks.Any(ctx) && !_sessions.Any(ctx) && !_chatSettings.Any(ctx)
        && !_usage.Any(ctx) && !_users.Any(ctx) && !ctx.Set<RateLimitRow>().Any();

    public StorageSnapshot Export() {
        using var ctx = _db.Context();
        // REPEATABLE READ — все запросы видят один и тот же момент.
        using var tx = ctx.Database.BeginTransaction(IsolationLevel.RepeatableRead);
        var snapshot = new StorageSnapshot(
            _notes.All(ctx),
            _tasks.All(ctx),
            ctx.Set<DigestRow>().Select(d => (DateOnly?)d.LastDate).FirstOrDefault(),
            _sessions.All(ctx),
            _chatSettings.All(ctx),
            _usage.All(ctx),
            PostgresUsageRepository.ReadRateLimit(ctx),
            _users.All(ctx));
        tx.Commit();
        return snapshot;
    }

    public void Import(StorageSnapshot s) {
        EnsureCreated();
        using var ctx = _db.Context();
        using var tx = ctx.Database.BeginTransaction();
        // SQL: явной блокировки таблиц в LINQ нет.
        ctx.Database.ExecuteSqlRaw($"LOCK TABLE {AllTables} IN EXCLUSIVE MODE");
        if (!IsEmpty(ctx))
            throw new InvalidOperationException("Хранилище Postgres не пустое: импорт возможен только в пустое.");

        _notes.InsertMany(ctx, s.Notes);
        _tasks.InsertMany(ctx, s.Tasks);
        if (s.LastDigestDate is { } day) ctx.Add(new DigestRow { Id = 1, LastDate = day });
        if (s.RateLimit is { } rl) ctx.Add(new RateLimitRow { Id = 1, Snapshot = JsonSerializer.Serialize(rl) });
        ctx.SaveChanges();
        _sessions.InsertMany(ctx, s.Sessions);
        _chatSettings.InsertMany(ctx, s.ChatSettings);
        _usage.InsertMany(ctx, s.Usage);
        _users.InsertMany(ctx, s.AllowedUsers);

        tx.Commit();
    }

    public void Dispose() => _db.Dispose();
}
