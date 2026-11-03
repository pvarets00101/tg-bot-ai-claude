using System.Text.RegularExpressions;
using ClaudeTelegramBot.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;

namespace ClaudeTelegramBot.Storage.Postgres;

// Модель EF Core поверх схемы из PostgresStorage.Schema. Схему создаёт и обновляет не EF
// (миграций нет), а PostgresStorage.EnsureCreated, поэтому здесь только сопоставление:
// таблицы и колонки в snake_case, ShortGuid -> uuid, время пишется в UTC и читается в локальном поясе.
internal sealed class BotDbContext(DbContextOptions<BotDbContext> options) : DbContext(options) {
    protected override void ConfigureConventions(ModelConfigurationBuilder b) {
        // Применяется и к Nullable<T>.
        b.Properties<ShortGuid>().HaveConversion<ShortGuidConverter>();
        b.Properties<DateTimeOffset>().HaveConversion<UtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b) {
        EntityTable<Note>(b, "notes");
        EntityTable<TaskItem>(b, "tasks");
        EntityTable<AllowedUser>(b, "allowed_users").Ignore(u => u.UserId);
        EntityTable<ChatSession>(b, "sessions");
        EntityTable<ChatSettings>(b, "chat_settings");
        EntityTable<UsageRecord>(b, "usage");

        b.Entity<DigestRow>(e => {
            e.ToTable("digest_state");
            e.Property(r => r.Id).ValueGeneratedNever();
        });
        b.Entity<RateLimitRow>(e => {
            e.ToTable("rate_limit");
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Snapshot).HasColumnType("jsonb");
        });

        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var p in entity.GetProperties())
                p.SetColumnName(SnakeCase(p.Name));
    }

    // Id выдаёт приложение (ShortGuid.NewId), а не база.
    private static Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> EntityTable<T>(ModelBuilder b, string table)
        where T : Entity {
        var e = b.Entity<T>();
        e.ToTable(table);
        e.HasKey(x => x.Id);
        e.Property(x => x.Id).ValueGeneratedNever();
        e.HasIndex(x => x.ExternalId).IsUnique();
        return e;
    }

    private static string SnakeCase(string name) => Regex.Replace(name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant();

    private sealed class ShortGuidConverter() : ValueConverter<ShortGuid, Guid>(v => v.Value, v => new ShortGuid(v));

    // Npgsql пишет в timestamptz только DateTimeOffset с нулевым смещением; наружу время отдаётся в локальном поясе.
    private sealed class UtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v.ToLocalTime());
}

// Дата последней утренней сводки. Всегда одна строка (id = 1).
internal sealed class DigestRow {
    public int Id { get; set; }
    public DateOnly LastDate { get; set; }
}

// Последние данные о лимитах подписки (JSON). Всегда одна строка (id = 1).
internal sealed class RateLimitRow {
    public int Id { get; set; }
    public string Snapshot { get; set; } = "";
}

// Источник соединений и фабрика контекстов. Контекст короткоживущий — на одну операцию:
// репозитории — синглтоны и вызываются из разных потоков.
internal sealed class PostgresDb : IDisposable {
    private readonly PooledDbContextFactory<BotDbContext> _contexts;

    public PostgresDb(string connectionString) {
        DataSource = NpgsqlDataSource.Create(connectionString);
        _contexts = new(new DbContextOptionsBuilder<BotDbContext>()
            .UseNpgsql(DataSource)
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);
    }

    // Для того, что остаётся на SQL: схема, блокировки, COPY.
    public NpgsqlDataSource DataSource { get; }

    public BotDbContext Context() => _contexts.CreateDbContext();

    public static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public void Dispose() => DataSource.Dispose();
}
