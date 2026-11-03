using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ClaudeTelegramBot.Storage.Postgres;

// Базовый репозиторий PostgreSQL на EF Core: общие операции IRepository<T> запросами LINQ.
//
// Наследник задаёт порядок (Sort) и получает Get/GetAll/Add/Update/Delete/Restore готовыми;
// любой из них можно переопределить, а методы-намерения писать своими LINQ-запросами через Context().
// Изменения «по условию» (удалить, если не удалена) — ExecuteUpdate: один UPDATE ... WHERE,
// атомарный и для бота, и для MCP-сервера.
internal abstract class PostgresRepository<T>(PostgresDb db) : IRepository<T> where T : Entity {
    protected PostgresDb Db { get; } = db;

    protected BotDbContext Context() => Db.Context();

    // Порядок GetAll и выгрузки.
    protected virtual IQueryable<T> Sort(IQueryable<T> query) => query.OrderBy(e => e.Id);

    protected static IQueryable<T> Visible(IQueryable<T> query, bool includeDeleted = false) =>
        includeDeleted ? query : query.Where(e => !e.SoftDeleted);

    // --- IRepository<T> ---

    public virtual T? Get(ShortGuid id, bool includeDeleted = false) {
        using var ctx = Context();
        return Visible(ctx.Set<T>(), includeDeleted).FirstOrDefault(e => e.Id == id);
    }

    public virtual T? GetByExternalId(string externalId, bool includeDeleted = false) {
        using var ctx = Context();
        return Visible(ctx.Set<T>(), includeDeleted).FirstOrDefault(e => e.ExternalId == externalId);
    }

    public virtual IReadOnlyList<T> GetAll(bool includeDeleted = false) {
        using var ctx = Context();
        return Sort(Visible(ctx.Set<T>(), includeDeleted)).ToList();
    }

    public virtual T Add(T entity) {
        if (entity.Id.IsEmpty) entity.Id = ShortGuid.NewId();
        using var ctx = Context();
        ctx.Add(entity);
        Save(ctx, entity);
        return entity;
    }

    public virtual bool Update(T entity) {
        using var ctx = Context();
        var stored = ctx.Set<T>().AsTracking().FirstOrDefault(e => e.Id == entity.Id && !e.SoftDeleted);
        if (stored is null) return false;
        ctx.Entry(stored).CurrentValues.SetValues(entity);
        stored.SoftDeleted = false;
        stored.SoftDeletedDate = null;
        Save(ctx, entity);
        return true;
    }

    public virtual bool Delete(ShortGuid id) => MarkDeleted(e => e.Id == id);

    public virtual bool Restore(ShortGuid id) {
        using var ctx = Context();
        return ctx.Set<T>()
            .Where(e => e.Id == id && e.SoftDeleted)
            .ExecuteUpdate(s => s
                .SetProperty(e => e.SoftDeleted, false)
                .SetProperty(e => e.SoftDeletedDate, (DateTimeOffset?)null)) > 0;
    }

    // --- Для наследников ---

    // Вставка или обновление по ExternalId (удалённая запись восстанавливается), Id существующей
    // записи сохраняется. Если другой процесс вставил ту же запись между чтением и вставкой,
    // уникальный индекс external_id отклонит вставку — тогда повторяем, уже как обновление.
    protected virtual T Upsert(T entity) {
        if (entity.ExternalId is null) throw new ArgumentException("Для Upsert нужен ExternalId.", nameof(entity));
        for (var attempt = 1; ; attempt++) {
            using var ctx = Context();
            var stored = ctx.Set<T>().AsTracking().FirstOrDefault(e => e.ExternalId == entity.ExternalId);
            if (stored is null) {
                if (entity.Id.IsEmpty) entity.Id = ShortGuid.NewId();
                ctx.Add(entity);
            } else {
                entity.Id = stored.Id;
                entity.SoftDeleted = false;
                entity.SoftDeletedDate = null;
                ctx.Entry(stored).CurrentValues.SetValues(entity);
            }

            try {
                ctx.SaveChanges();
                return entity;
            } catch (DbUpdateException ex) when (PostgresDb.IsUniqueViolation(ex) && attempt < 3) {
                // гонка со вторым процессом — ещё раз
            }
        }
    }

    protected bool DeleteByExternalId(string externalId) => MarkDeleted(e => e.ExternalId == externalId);

    private bool MarkDeleted(System.Linq.Expressions.Expression<Func<T, bool>> match) {
        using var ctx = Context();
        var now = DateTimeOffset.Now;
        return ctx.Set<T>()
            .Where(match)
            .Where(e => !e.SoftDeleted)
            .ExecuteUpdate(s => s
                .SetProperty(e => e.SoftDeleted, true)
                .SetProperty(e => e.SoftDeletedDate, (DateTimeOffset?)now)) > 0;
    }

    protected static void Save(BotDbContext ctx, T entity) {
        try {
            ctx.SaveChanges();
        } catch (DbUpdateException ex) when (PostgresDb.IsUniqueViolation(ex)) {
            throw new InvalidOperationException(
                $"Уже есть {typeof(T).Name} с id {entity.Id} или внешним id {entity.ExternalId}.", ex);
        }
    }

    // --- Снимок (PostgresStorage.Export/Import): в общем контексте и транзакции ---

    internal List<T> All(BotDbContext ctx) => Sort(ctx.Set<T>()).ToList();

    internal bool Any(BotDbContext ctx) => ctx.Set<T>().Any();

    // Вставка записей со всеми полями как есть (в том числе отметками удаления).
    internal virtual void InsertMany(BotDbContext ctx, IReadOnlyList<T> entities) {
        foreach (var e in entities.Where(e => e.Id.IsEmpty)) e.Id = ShortGuid.NewId();
        ctx.Set<T>().AddRange(entities);
        ctx.SaveChanges();
        ctx.ChangeTracker.Clear();
    }
}

// То, что остаётся на SQL (схема, блокировки, COPY): выполнение команд с приведением параметров.
internal static class Pg {
    // Общие колонки каждой таблицы сущности (вставляются первыми в CREATE TABLE).
    public const string EntityColumnsDdl =
        "id uuid PRIMARY KEY, external_id text UNIQUE, soft_deleted boolean NOT NULL DEFAULT false, soft_deleted_date timestamptz";

    public static NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, object?[] args) {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.AddWithValue(Arg(a));
        return cmd;
    }

    public static int Exec(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params object?[] args) {
        using var cmd = Command(conn, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params object?[] args) {
        using var cmd = Command(conn, tx, sql, args);
        return cmd.ExecuteScalar();
    }

    // Npgsql пишет в timestamptz только DateTimeOffset с нулевым смещением; id хранится как uuid.
    public static object Arg(object? a) => a switch {
        null => DBNull.Value,
        DateTimeOffset d => d.ToUniversalTime(),
        ShortGuid id => id.Value,
        _ => a,
    };
}
