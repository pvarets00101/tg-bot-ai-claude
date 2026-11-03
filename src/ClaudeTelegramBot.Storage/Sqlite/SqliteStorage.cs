using System.Globalization;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using Microsoft.Data.Sqlite;

namespace ClaudeTelegramBot.Storage.Sqlite;

// Хранилище в одном файле SQLite (ConnectionStrings:Sqlite). Бот и MCP-сервер работают с ним одновременно: включён WAL,
// а занятость базы драйвер пережидает сам (повторяет запрос до CommandTimeout).
// Типы колонок — см. Sql (время в тиках UTC, дата текстом, id — Guid "N"); теги — JSON-массивом.
public sealed class SqliteStorage : IStorage {
    // Версия схемы в schema_version. 1 — целочисленные id и общая таблица-счётчик id_seq (до базовой
    // сущности), 2 — Guid-id, external_id и мягкое удаление.
    internal const int SchemaVersion = 2;

    private const string Schema = $$"""
        CREATE TABLE IF NOT EXISTS schema_version (
            id      integer PRIMARY KEY CHECK (id = 1),
            version integer NOT NULL
        );

        CREATE TABLE IF NOT EXISTS notes (
            {{Sql.EntityColumnsDdl}},
            text       text    NOT NULL,
            tags       text    NOT NULL DEFAULT '[]',
            created_at integer NOT NULL
        );

        CREATE TABLE IF NOT EXISTS tasks (
            {{Sql.EntityColumnsDdl}},
            title        text    NOT NULL,
            due          text,
            remind_at    integer,
            reminded     integer NOT NULL DEFAULT 0,
            done         integer NOT NULL DEFAULT 0,
            created_at   integer NOT NULL,
            completed_at integer
        );
        CREATE INDEX IF NOT EXISTS tasks_pending_reminders ON tasks (remind_at)
            WHERE NOT done AND NOT reminded AND NOT soft_deleted;

        CREATE TABLE IF NOT EXISTS digest_state (
            id        integer PRIMARY KEY CHECK (id = 1),
            last_date text NOT NULL
        );

        CREATE TABLE IF NOT EXISTS sessions (
            {{Sql.EntityColumnsDdl}},
            session_id text    NOT NULL,
            updated_at integer NOT NULL
        );

        CREATE TABLE IF NOT EXISTS chat_settings (
            {{Sql.EntityColumnsDdl}},
            model text NOT NULL
        );

        CREATE TABLE IF NOT EXISTS usage (
            {{Sql.EntityColumnsDdl}},
            at            integer NOT NULL,
            model         text,
            is_error      integer NOT NULL,
            cost_usd      text    NOT NULL,
            duration_ms   integer NOT NULL,
            num_turns     integer NOT NULL,
            input_tokens  integer NOT NULL,
            output_tokens integer NOT NULL
        );
        CREATE INDEX IF NOT EXISTS usage_at ON usage (at);

        CREATE TABLE IF NOT EXISTS allowed_users (
            {{Sql.EntityColumnsDdl}},
            added_at integer NOT NULL
        );

        CREATE TABLE IF NOT EXISTS rate_limit (
            id       integer PRIMARY KEY CHECK (id = 1),
            snapshot text NOT NULL
        );
        """;

    // Версия 1 → 2: старые таблицы отодвигаются в *_v1 (их индексы удаляются, чтобы имена не мешали
    // новой схеме), после создания новой схемы данные копируются с новыми случайными id.
    // digest_state и rate_limit не изменились.
    private const string MoveV1Aside = """
        DROP INDEX IF EXISTS tasks_pending_reminders;
        DROP INDEX IF EXISTS usage_at;
        ALTER TABLE notes RENAME TO notes_v1;
        ALTER TABLE tasks RENAME TO tasks_v1;
        ALTER TABLE sessions RENAME TO sessions_v1;
        ALTER TABLE chat_settings RENAME TO chat_settings_v1;
        ALTER TABLE usage RENAME TO usage_v1;
        ALTER TABLE allowed_users RENAME TO allowed_users_v1;
        """;

    private const string NewId = "lower(hex(randomblob(16)))";

    private const string CopyV1 = $"""
        INSERT INTO notes (id, text, tags, created_at)
            SELECT {NewId}, text, tags, created_at FROM notes_v1;
        INSERT INTO tasks (id, title, due, remind_at, reminded, done, created_at, completed_at)
            SELECT {NewId}, title, due, remind_at, reminded, done, created_at, completed_at FROM tasks_v1;
        INSERT INTO sessions (id, external_id, session_id, updated_at)
            SELECT {NewId}, CAST(chat_id AS text), session_id, updated_at FROM sessions_v1;
        INSERT INTO chat_settings (id, external_id, model)
            SELECT {NewId}, CAST(chat_id AS text), model FROM chat_settings_v1;
        INSERT INTO usage (id, at, model, is_error, cost_usd, duration_ms, num_turns, input_tokens, output_tokens)
            SELECT {NewId}, at, model, is_error, cost_usd, duration_ms, num_turns, input_tokens, output_tokens
            FROM usage_v1 ORDER BY id;
        INSERT INTO allowed_users (id, external_id, added_at)
            SELECT {NewId}, CAST(user_id AS text), added_at FROM allowed_users_v1;
        DROP TABLE notes_v1;
        DROP TABLE tasks_v1;
        DROP TABLE sessions_v1;
        DROP TABLE chat_settings_v1;
        DROP TABLE usage_v1;
        DROP TABLE allowed_users_v1;
        DROP TABLE IF EXISTS id_seq;
        """;

    private readonly string _connectionString;
    private readonly SqliteNoteRepository _notes;
    private readonly SqliteTaskRepository _tasks;
    private readonly SqliteAllowedUserRepository _users;
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteChatSettingsRepository _chatSettings;
    private readonly SqliteUsageRepository _usage;

    // Data Source в строке — путь к файлу (StorageFactory делает его абсолютным).
    public SqliteStorage(string connectionString) {
        var b = new SqliteConnectionStringBuilder(connectionString);
        Path = b.DataSource;
        _connectionString = b.ToString();

        _notes = new(this);
        _tasks = new(this);
        _users = new(this);
        _sessions = new(this);
        _chatSettings = new(this);
        _usage = new(this);
    }

    public string Path { get; }
    public StorageProvider Provider => StorageProvider.Sqlite;
    public IAllowedUserRepository AllowedUsers => _users;
    public INoteRepository Notes => _notes;
    public ITaskRepository Tasks => _tasks;
    public ISessionRepository Sessions => _sessions;
    public IChatSettingsRepository ChatSettings => _chatSettings;
    public IUsageRepository Usage => _usage;

    // Транзакция по умолчанию — BEGIN IMMEDIATE: второй процесс ждёт, пока первый обновит схему.
    public void EnsureCreated() {
        if (System.IO.Path.GetDirectoryName(Path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        using var conn = Open();
        Sql.Exec(conn, null, "PRAGMA journal_mode = WAL");
        using var tx = conn.BeginTransaction();

        var version = CurrentVersion(conn, tx);
        if (version > SchemaVersion)
            throw new InvalidOperationException(
                $"Схема базы версии {version} новее, чем понимает программа ({SchemaVersion}). Обновите бота.");
        if (version == 1) Sql.Exec(conn, tx, MoveV1Aside);
        Sql.Exec(conn, tx, Schema);
        if (version == 1) Sql.Exec(conn, tx, CopyV1);
        Sql.Exec(conn, tx, "INSERT OR REPLACE INTO schema_version (id, version) VALUES (1, @p1)", SchemaVersion);
        tx.Commit();
    }

    // 0 — базы нет, 1 — схема до версионирования (есть notes, нет schema_version).
    private static int CurrentVersion(SqliteConnection conn, SqliteTransaction tx) {
        bool TableExists(string name) =>
            Sql.Scalar(conn, tx, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @p1", name) is not null;

        if (TableExists("schema_version"))
            return Sql.Scalar(conn, tx, "SELECT version FROM schema_version WHERE id = 1") is long v ? (int)v : 0;
        return TableExists("notes") ? 1 : 0;
    }

    public bool IsEmpty() {
        using var conn = Open();
        return IsEmpty(conn, null);
    }

    private bool IsEmpty(SqliteConnection conn, SqliteTransaction? tx) =>
        !_notes.Any(conn, tx) && !_tasks.Any(conn, tx) && !_sessions.Any(conn, tx) && !_chatSettings.Any(conn, tx)
        && !_usage.Any(conn, tx) && !_users.Any(conn, tx)
        && Sql.Scalar(conn, tx, "SELECT 1 FROM rate_limit") is null;

    public StorageSnapshot Export() {
        using var conn = Open();
        // Одна транзакция — согласованный снимок.
        using var tx = conn.BeginTransaction(deferred: true);
        var snapshot = new StorageSnapshot(
            _notes.All(conn, tx),
            _tasks.All(conn, tx),
            Sql.Scalar(conn, tx, "SELECT last_date FROM digest_state WHERE id = 1") is string d
                ? DateOnly.Parse(d, CultureInfo.InvariantCulture)
                : null,
            _sessions.All(conn, tx),
            _chatSettings.All(conn, tx),
            _usage.All(conn, tx),
            SqliteUsageRepository.ReadRateLimit(conn, tx),
            _users.All(conn, tx));
        tx.Commit();
        return snapshot;
    }

    public void Import(StorageSnapshot s) {
        EnsureCreated();
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        if (!IsEmpty(conn, tx))
            throw new InvalidOperationException("Хранилище Sqlite не пустое: импорт возможен только в пустое.");

        _notes.InsertMany(conn, tx, s.Notes);
        _tasks.InsertMany(conn, tx, s.Tasks);
        if (s.LastDigestDate is { } day)
            Sql.Exec(conn, tx, "INSERT OR REPLACE INTO digest_state (id, last_date) VALUES (1, @p1)", day);
        _sessions.InsertMany(conn, tx, s.Sessions);
        _chatSettings.InsertMany(conn, tx, s.ChatSettings);
        _usage.InsertMany(conn, tx, s.Usage);
        if (s.RateLimit is { } rl) SqliteUsageRepository.WriteRateLimit(conn, tx, rl);
        _users.InsertMany(conn, tx, s.AllowedUsers);

        tx.Commit();
    }

    public void Dispose() => SqliteConnection.ClearAllPools();

    internal SqliteConnection Open() {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // lower() в SQLite понимает только латиницу; сравнение без учёта регистра делаем на стороне .NET,
        // так же, как в Json-провайдере.
        conn.CreateFunction("contains_ci", (string? text, string? query) =>
            query is null || (text?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        return conn;
    }
}
