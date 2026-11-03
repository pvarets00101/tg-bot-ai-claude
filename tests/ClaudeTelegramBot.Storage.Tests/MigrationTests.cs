using ClaudeTelegramBot.Domain.Repositories;
using ClaudeTelegramBot.Storage.Json;
using ClaudeTelegramBot.Storage.Postgres;
using ClaudeTelegramBot.Storage.Sqlite;

namespace ClaudeTelegramBot.Storage.Tests;

// Перенос по цепочке через все провайдеры: данные на выходе те же, что на входе (с теми же id).
public sealed class MigrationTests : IDisposable {
    private readonly string _dir = TempDir.Create();
    private readonly PostgresDatabases _pg = new();
    private readonly List<IStorage> _opened = [];

    [Fact]
    public void Json_Sqlite_Json_KeepsData() {
        var json = Open(new JsonStorage(Path.Combine(_dir, "a")));
        StorageContractTests.Populate(json);
        var expected = SnapshotText.Of(json.Export());

        var sqlite = Open(new SqliteStorage(SqliteStorageTests.SqliteAt(Path.Combine(_dir, "b.db"))));
        StorageMigrator.Copy(json, sqlite);
        var back = Open(new JsonStorage(Path.Combine(_dir, "c")));
        StorageMigrator.Copy(sqlite, back);

        Assert.Equal(expected, SnapshotText.Of(sqlite.Export()));
        Assert.Equal(expected, SnapshotText.Of(back.Export()));
    }

    [Fact]
    public void Json_Postgres_Sqlite_KeepsData() {
        var json = Open(new JsonStorage(Path.Combine(_dir, "a")));
        StorageContractTests.Populate(json);
        var expected = SnapshotText.Of(json.Export());

        var pg = Open(new PostgresStorage(_pg.Create()));
        StorageMigrator.Copy(json, pg);
        var sqlite = Open(new SqliteStorage(SqliteStorageTests.SqliteAt(Path.Combine(_dir, "b.db"))));
        StorageMigrator.Copy(pg, sqlite);

        Assert.Equal(expected, SnapshotText.Of(pg.Export()));
        Assert.Equal(expected, SnapshotText.Of(sqlite.Export()));
    }

    [Fact]
    public void Copy_IntoNonEmpty_Throws_AndKeepsTarget() {
        var from = Open(new JsonStorage(Path.Combine(_dir, "a")));
        StorageContractTests.Populate(from);
        var to = Open(new SqliteStorage(SqliteStorageTests.SqliteAt(Path.Combine(_dir, "b.db"))));
        to.Notes.Add("уже есть", []);

        Assert.Throws<InvalidOperationException>(() => StorageMigrator.Copy(from, to));
        Assert.Single(to.Notes.Search(null, null, 10));
    }

    private IStorage Open(IStorage s) {
        s.EnsureCreated();
        _opened.Add(s);
        return s;
    }

    public void Dispose() {
        foreach (var s in _opened) s.Dispose();
        _pg.Dispose();
        TempDir.Delete(_dir);
    }
}
