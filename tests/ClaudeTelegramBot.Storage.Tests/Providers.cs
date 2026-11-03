using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using ClaudeTelegramBot.Storage;
using ClaudeTelegramBot.Storage.Json;
using ClaudeTelegramBot.Storage.Postgres;
using ClaudeTelegramBot.Storage.Sqlite;
using Npgsql;

namespace ClaudeTelegramBot.Storage.Tests;

public sealed class JsonStorageTests : StorageContractTests {
    private readonly string _dir = TempDir.Create();

    protected override IStorage CreateEmpty() => new JsonStorage(Path.Combine(_dir, Guid.NewGuid().ToString("N")));

    protected override IStorage OpenSame() => new JsonStorage(((JsonStorage)S).Directory);

    public override void Dispose() {
        base.Dispose();
        TempDir.Delete(_dir);
    }
}

public sealed class SqliteStorageTests : StorageContractTests {
    private readonly string _dir = TempDir.Create();

    protected override IStorage CreateEmpty() => new SqliteStorage(SqliteAt(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db")));

    protected override IStorage OpenSame() => new SqliteStorage(SqliteAt(((SqliteStorage)S).Path));

    internal static string SqliteAt(string path) => $"Data Source={path}";

    public override void Dispose() {
        base.Dispose();
        TempDir.Delete(_dir);
    }
}

public sealed class PostgresStorageTests : StorageContractTests {
    private readonly PostgresDatabases _dbs = new();
    private string? _current;

    protected override IStorage CreateEmpty() {
        var cs = _dbs.Create();
        _current ??= cs;
        return new PostgresStorage(cs);
    }

    protected override IStorage OpenSame() {
        _ = S;
        return new PostgresStorage(_current!);
    }

    public override void Dispose() {
        base.Dispose();
        _dbs.Dispose();
    }
}

// Временные базы PostgreSQL. Строка подключения администратора — в BOTSTORAGE_TEST_POSTGRES,
// например Host=localhost;Port=55432;Username=postgres;Password=test. Без неё тесты пропускаются.
internal sealed class PostgresDatabases : IDisposable {
    public static readonly string? Admin = Environment.GetEnvironmentVariable("BOTSTORAGE_TEST_POSTGRES");
    private readonly List<string> _names = [];

    public string Create() {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Admin), "BOTSTORAGE_TEST_POSTGRES не задана — тесты PostgreSQL пропущены.");
        var name = "botstorage_test_" + Guid.NewGuid().ToString("N");
        Exec($"CREATE DATABASE {name}");
        _names.Add(name);
        return new NpgsqlConnectionStringBuilder(Admin) { Database = name }.ToString();
    }

    public void Dispose() {
        NpgsqlConnection.ClearAllPools();
        foreach (var name in _names) Exec($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
    }

    private static void Exec(string sql) {
        using var conn = new NpgsqlConnection(Admin);
        conn.Open();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }
}

internal static class TempDir {
    public static string Create() {
        var dir = Path.Combine(Path.GetTempPath(), "botstorage-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Delete(string dir) {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try {
            Directory.Delete(dir, recursive: true);
        } catch (IOException) {
            // файл ещё держит пул соединений — временную папку уберёт система
        }
    }
}
