using System.Globalization;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using Microsoft.Data.Sqlite;

namespace ClaudeTelegramBot.Storage.Sqlite;

// Базовый репозиторий SQLite: общие операции IRepository<T> над одной таблицей.
//
// Наследник описывает свою таблицу (Table, Columns, Values, Read) и получает Get/GetAll/Add/
// Update/Delete/Restore готовыми. Любой из них можно переопределить, а методы-намерения писать
// своим SQL через Query/Exec — время, даты, id и null в параметрах приводятся в Sql.Arg.
//
// У каждой таблицы сущности первые колонки одинаковые (Sql.EntityColumnsDdl). Id хранится текстом
// Guid "N" (32 hex): так строки сортируются в том же порядке, что uuid в PostgreSQL.
internal abstract class SqliteRepository<T>(SqliteStorage storage) : IRepository<T> where T : Entity, new() {
    private const string EntityColumns = "id, external_id, soft_deleted, soft_deleted_date";
    private const int EntityColumnCount = 4;

    protected SqliteStorage Storage { get; } = storage;

    protected abstract string Table { get; }

    // Колонки сущности после общих, в том же порядке, что Values и Read.
    protected abstract string[] Columns { get; }

    protected abstract object?[] Values(T entity);

    protected abstract void Read(SqliteRow row, T entity);

    // Порядок GetAll и выгрузки.
    protected virtual string OrderBy => "id";

    protected string AllColumns => $"{EntityColumns}, {string.Join(", ", Columns)}";

    protected string SelectAll => $"SELECT {AllColumns} FROM {Table}";

    // --- IRepository<T> ---

    public virtual T? Get(ShortGuid id, bool includeDeleted = false) =>
        Query($"{SelectAll} WHERE id = @p1 AND (@p2 OR NOT soft_deleted)", id, includeDeleted).SingleOrDefault();

    public virtual T? GetByExternalId(string externalId, bool includeDeleted = false) =>
        Query($"{SelectAll} WHERE external_id = @p1 AND (@p2 OR NOT soft_deleted)", externalId, includeDeleted).SingleOrDefault();

    public virtual IReadOnlyList<T> GetAll(bool includeDeleted = false) =>
        Query($"{SelectAll} WHERE @p1 OR NOT soft_deleted ORDER BY {OrderBy}", includeDeleted);

    public virtual T Add(T entity) {
        using var conn = Storage.Open();
        Insert(conn, null, entity);
        return entity;
    }

    public virtual bool Update(T entity) {
        var set = string.Join(", ", Columns.Select((c, i) => $"{c} = @p{i + 3}"));
        return Exec($"UPDATE {Table} SET external_id = @p2, {set} WHERE id = @p1 AND NOT soft_deleted",
            [entity.Id, entity.ExternalId, .. Values(entity)]) > 0;
    }

    public virtual bool Delete(ShortGuid id) =>
        Exec($"UPDATE {Table} SET soft_deleted = 1, soft_deleted_date = @p2 WHERE id = @p1 AND NOT soft_deleted",
            id, DateTimeOffset.Now) > 0;

    public virtual bool Restore(ShortGuid id) =>
        Exec($"UPDATE {Table} SET soft_deleted = 0, soft_deleted_date = NULL WHERE id = @p1 AND soft_deleted", id) > 0;

    // --- Для наследников ---

    // Вставка или обновление по ExternalId (удалённая запись восстанавливается). Id существующей
    // записи сохраняется. Возвращает то, что теперь лежит в базе.
    protected virtual T Upsert(T entity) {
        if (entity.ExternalId is null) throw new ArgumentException("Для Upsert нужен ExternalId.", nameof(entity));
        if (entity.Id.IsEmpty) entity.Id = ShortGuid.NewId();
        var update = string.Join(", ", Columns.Select(c => $"{c} = excluded.{c}"));
        return Query($"""
            INSERT INTO {Table} ({AllColumns}) VALUES ({Placeholders(EntityColumnCount + Columns.Length)})
            ON CONFLICT (external_id) DO UPDATE SET soft_deleted = 0, soft_deleted_date = NULL, {update}
            RETURNING {AllColumns}
            """, [entity.Id, entity.ExternalId, false, null, .. Values(entity)]).Single();
    }

    protected bool DeleteByExternalId(string externalId) =>
        Exec($"UPDATE {Table} SET soft_deleted = 1, soft_deleted_date = @p2 WHERE external_id = @p1 AND NOT soft_deleted",
            externalId, DateTimeOffset.Now) > 0;

    protected List<T> Query(string sql, params object?[] args) {
        using var conn = Storage.Open();
        return Query(conn, null, sql, args);
    }

    protected List<T> Query(SqliteConnection conn, SqliteTransaction? tx, string sql, params object?[] args) {
        // Команда закрывается вместе с ридером: SqliteDataReader освобождает её сам.
        using var r = Sql.Command(conn, tx, sql, args).ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(Map(r));
        return list;
    }

    protected int Exec(string sql, params object?[] args) {
        using var conn = Storage.Open();
        return Sql.Exec(conn, null, sql, args);
    }

    protected object? Scalar(string sql, params object?[] args) {
        using var conn = Storage.Open();
        return Sql.Scalar(conn, null, sql, args);
    }

    // --- Снимок (SqliteStorage.Export/Import) ---

    internal List<T> All(SqliteConnection conn, SqliteTransaction tx) => Query(conn, tx, $"{SelectAll} ORDER BY {OrderBy}");

    internal bool Any(SqliteConnection conn, SqliteTransaction? tx) =>
        Convert.ToInt64(Sql.Scalar(conn, tx, $"SELECT EXISTS (SELECT 1 FROM {Table})"), CultureInfo.InvariantCulture) != 0;

    // Вставка записи со всеми полями как есть (в том числе отметками удаления).
    internal virtual void Insert(SqliteConnection conn, SqliteTransaction? tx, T entity) {
        if (entity.Id.IsEmpty) entity.Id = ShortGuid.NewId();
        try {
            Sql.Exec(conn, tx, $"INSERT INTO {Table} ({AllColumns}) VALUES ({Placeholders(EntityColumnCount + Columns.Length)})",
                [entity.Id, entity.ExternalId, entity.SoftDeleted, entity.SoftDeletedDate, .. Values(entity)]);
        } catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { // SQLITE_CONSTRAINT
            throw new InvalidOperationException($"В {Table} уже есть запись с id {entity.Id} или внешним id {entity.ExternalId}.", ex);
        }
    }

    internal virtual void InsertMany(SqliteConnection conn, SqliteTransaction tx, IReadOnlyList<T> entities) {
        foreach (var e in entities) Insert(conn, tx, e);
    }

    private T Map(SqliteDataReader r) {
        var row = new SqliteRow(r, 0);
        var e = new T {
            Id = row.Id(),
            ExternalId = row.StringOrNull(),
            SoftDeleted = row.Bool(),
            SoftDeletedDate = row.TimeOrNull(),
        };
        Read(row, e);
        return e;
    }

    private static string Placeholders(int count) => string.Join(", ", Enumerable.Range(1, count).Select(i => $"@p{i}"));
}

// Общее для SQL SQLite: колонки сущности в DDL, выполнение команд и приведение типов.
// Время — тики UTC (integer): точно и сравнимо; дата — текст yyyy-MM-dd; id — Guid "N".
internal static class Sql {
    public const string EntityColumnsDdl =
        "id text PRIMARY KEY, external_id text UNIQUE, soft_deleted integer NOT NULL DEFAULT 0, soft_deleted_date integer";

    public static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql, object?[] args) {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"@p{i + 1}", Arg(args[i]));
        return cmd;
    }

    public static int Exec(SqliteConnection conn, SqliteTransaction? tx, string sql, params object?[] args) {
        using var cmd = Command(conn, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection conn, SqliteTransaction? tx, string sql, params object?[] args) {
        using var cmd = Command(conn, tx, sql, args);
        return cmd.ExecuteScalar();
    }

    public static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object Arg(object? a) => a switch {
        null => DBNull.Value,
        DateTimeOffset d => d.UtcTicks,
        DateOnly d => Date(d),
        ShortGuid id => id.Value.ToString("N"),
        _ => a,
    };
}

// Чтение колонок подряд. Время — в локальном поясе.
internal sealed class SqliteRow(SqliteDataReader r, int start) {
    private int _i = start;

    public ShortGuid Id() => new(Guid.ParseExact(r.GetString(_i++), "N"));
    public string String() => r.GetString(_i++);
    public string? StringOrNull() => r.IsDBNull(_i) ? Skip<string>() : r.GetString(_i++);
    public bool Bool() => r.GetBoolean(_i++);
    public int Int() => r.GetInt32(_i++);
    public long Long() => r.GetInt64(_i++);
    public decimal Decimal() => r.GetDecimal(_i++);
    public DateTimeOffset Time() => new DateTimeOffset(r.GetInt64(_i++), TimeSpan.Zero).ToLocalTime();
    public DateTimeOffset? TimeOrNull() => r.IsDBNull(_i) ? Skip<DateTimeOffset?>() : Time();
    public DateOnly? DateOrNull() => r.IsDBNull(_i) ? Skip<DateOnly?>() : DateOnly.Parse(r.GetString(_i++), CultureInfo.InvariantCulture);

    private TValue? Skip<TValue>() {
        _i++;
        return default;
    }
}
