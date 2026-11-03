using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using ClaudeTelegramBot.Storage.Json;
using ClaudeTelegramBot.Storage.Postgres;
using ClaudeTelegramBot.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace ClaudeTelegramBot.Storage.Tests;

// Данные версии 1 (целочисленные id, до базовой сущности) после EnsureCreated доступны в новом
// формате: с новыми id, chat/user id в ExternalId, ничего не удалено.
public sealed class SchemaUpgradeTests : IDisposable {
    private readonly string _dir = TempDir.Create();
    private readonly PostgresDatabases _pg = new();
    private readonly List<IStorage> _opened = [];

    [Fact]
    public void Json_V1Files_Upgraded_OldFilesBackedUp() {
        var dir = Path.Combine(_dir, "legacy");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "personal-data.json"), """
            {
              "NextId": 6,
              "Notes": [ { "Id": 1, "Text": "встреча с Ромой", "Tags": [ "встречи" ], "CreatedAt": "2026-11-02T18:47:00.1234567+03:00" } ],
              "Tasks": [ { "Id": 3, "Title": "Позвонить", "Due": "2026-11-04", "RemindAt": "2026-11-04T09:30:00+03:00",
                           "Reminded": false, "Done": false, "CreatedAt": "2026-11-02T18:50:00+03:00", "CompletedAt": null } ],
              "LastDigestDate": "2026-11-02"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "sessions.json"), """{"631248686":"8424f831"}""");
        File.WriteAllText(Path.Combine(dir, "chat-settings.json"), """{"631248686":"sonnet"}""");
        File.WriteAllText(Path.Combine(dir, "allowed-users.json"), "[631248686, 7]");
        File.WriteAllText(Path.Combine(dir, "rate-limit.json"),
            """{"ObservedAt":"2026-11-02T20:38:35+03:00","FiveHour":null,"SevenDay":null,"Status":"allowed"}""");
        File.WriteAllText(Path.Combine(dir, "usage.jsonl"),
            """{"At":"2026-11-02T20:38:35+03:00","Model":"haiku-5-5","IsError":false,"CostUsd":0.01,"DurationMs":1,"NumTurns":1,"InputTokens":1,"OutputTokens":1}""" + "\nне json\n");

        var s = Open(new JsonStorage(dir));
        AssertUpgraded(s, usage: 1);
        Assert.Equal("allowed", s.Usage.GetRateLimit()?.Status);
        Assert.True(File.Exists(Path.Combine(dir, "v1-backup", "personal-data.json")));
        Assert.False(File.Exists(Path.Combine(dir, "personal-data.json")));

        // Второй запуск ничего не меняет.
        var ids = s.Notes.GetAll().Select(n => n.Id).ToList();
        Assert.Equal(ids, Open(new JsonStorage(dir)).Notes.GetAll().Select(n => n.Id));
    }

    [Fact]
    public void Sqlite_V1Schema_Upgraded() {
        var path = Path.Combine(_dir, "legacy.db");
        using (var conn = new SqliteConnection(SqliteStorageTests.SqliteAt(path))) {
            conn.Open();
            using var cmd = conn.CreateCommand();
            var created = new DateTimeOffset(2026, 11, 2, 18, 47, 0, TimeSpan.FromHours(3)).UtcTicks;
            var remind = new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.FromHours(3)).UtcTicks;
            cmd.CommandText = SqliteV1Schema + $"""
                INSERT INTO notes (id, text, tags, created_at) VALUES (1, 'встреча с Ромой', '["встречи"]', {created});
                INSERT INTO tasks (id, title, due, remind_at, created_at) VALUES (3, 'Позвонить', '2026-11-04', {remind}, {created});
                UPDATE id_seq SET next = 6;
                INSERT INTO digest_state (id, last_date) VALUES (1, '2026-11-02');
                INSERT INTO sessions (chat_id, session_id, updated_at) VALUES (631248686, '8424f831', {created});
                INSERT INTO chat_settings (chat_id, model) VALUES (631248686, 'sonnet');
                INSERT INTO usage (at, model, is_error, cost_usd, duration_ms, num_turns, input_tokens, output_tokens)
                    VALUES ({created}, 'haiku-5-5', 0, '0.01', 1, 1, 1, 1), ({created}, NULL, 1, '0', 1, 1, 1, 1);
                INSERT INTO allowed_users (user_id, added_at) VALUES (631248686, {created}), (7, {created});
                """;
            cmd.ExecuteNonQuery();
        }

        var s = Open(new SqliteStorage(SqliteStorageTests.SqliteAt(path)));
        AssertUpgraded(s, usage: 2);
        AssertUpgraded(Open(new SqliteStorage(SqliteStorageTests.SqliteAt(path))), usage: 2);
    }

    [Fact]
    public void Postgres_V1Schema_Upgraded() {
        var cs = _pg.Create();
        using (var conn = new NpgsqlConnection(cs)) {
            conn.Open();
            using var cmd = new NpgsqlCommand(PostgresV1Schema + """
                INSERT INTO notes (text, tags, created_at) VALUES ('встреча с Ромой', '{встречи}', '2026-11-02 18:47+03');
                INSERT INTO tasks (title, due, remind_at, created_at) VALUES ('Позвонить', '2026-11-04', '2026-11-04 09:30+03', '2026-11-02 18:50+03');
                INSERT INTO digest_state (id, last_date) VALUES (1, '2026-11-02');
                INSERT INTO sessions (chat_id, session_id) VALUES (631248686, '8424f831');
                INSERT INTO chat_settings (chat_id, model) VALUES (631248686, 'sonnet');
                INSERT INTO usage (at, model, is_error, cost_usd, duration_ms, num_turns, input_tokens, output_tokens)
                    VALUES (now(), 'haiku-5-5', false, 0.01, 1, 1, 1, 1), (now(), NULL, true, 0, 1, 1, 1, 1);
                INSERT INTO allowed_users (user_id) VALUES (631248686), (7);
                """, conn);
            cmd.ExecuteNonQuery();
        }

        var s = Open(new PostgresStorage(cs));
        AssertUpgraded(s, usage: 2);
        AssertUpgraded(Open(new PostgresStorage(cs)), usage: 2);
    }

    private static void AssertUpgraded(IStorage s, int usage) {
        var note = s.Notes.Search("ромой", "встречи", 10).Single();
        Assert.False(note.Id.IsEmpty);
        Assert.Equal(note.Id, s.Notes.Get(note.Id)!.Id);

        var task = s.Tasks.Open().Single();
        Assert.Equal("Позвонить", task.Title);
        Assert.Equal(new DateOnly(2026, 11, 4), task.Due);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.FromHours(3)), task.RemindAt);
        Assert.NotEqual(note.Id, task.Id);
        Assert.False(s.Tasks.TryMarkDigest(new DateOnly(2026, 11, 2)));

        Assert.Equal("8424f831", s.Sessions.GetSessionId(631248686));
        Assert.Equal("631248686", s.Sessions.GetAll().Single().ExternalId);
        Assert.Equal("sonnet", s.ChatSettings.GetModel(631248686));
        Assert.Equal([7L, 631248686L], s.AllowedUsers.UserIds());
        Assert.Equal(usage, s.Usage.ReadSince(DateTimeOffset.MinValue).Count);
        var all = s.Export();
        Assert.DoesNotContain(all.Notes.Cast<Entity>().Concat(all.Tasks).Concat(all.Usage).Concat(all.AllowedUsers), e => e.SoftDeleted);

        // Новые записи после обновления работают как обычно.
        Assert.True(s.Tasks.Delete(task.Id));
        Assert.True(s.Tasks.Restore(task.Id));
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

    // Схемы версии 1 — как их создавали прежние SqliteStorage/PostgresStorage.
    private const string SqliteV1Schema = """
        CREATE TABLE id_seq (id integer PRIMARY KEY CHECK (id = 1), next integer NOT NULL);
        INSERT INTO id_seq (id, next) VALUES (1, 1);
        CREATE TABLE notes (id integer PRIMARY KEY, text text NOT NULL, tags text NOT NULL DEFAULT '[]', created_at integer NOT NULL);
        CREATE TABLE tasks (id integer PRIMARY KEY, title text NOT NULL, due text, remind_at integer,
            reminded integer NOT NULL DEFAULT 0, done integer NOT NULL DEFAULT 0, created_at integer NOT NULL, completed_at integer);
        CREATE INDEX tasks_pending_reminders ON tasks (remind_at) WHERE NOT done AND NOT reminded;
        CREATE TABLE digest_state (id integer PRIMARY KEY CHECK (id = 1), last_date text NOT NULL);
        CREATE TABLE sessions (chat_id integer PRIMARY KEY, session_id text NOT NULL, updated_at integer NOT NULL);
        CREATE TABLE chat_settings (chat_id integer PRIMARY KEY, model text NOT NULL);
        CREATE TABLE usage (id integer PRIMARY KEY AUTOINCREMENT, at integer NOT NULL, model text, is_error integer NOT NULL,
            cost_usd text NOT NULL, duration_ms integer NOT NULL, num_turns integer NOT NULL,
            input_tokens integer NOT NULL, output_tokens integer NOT NULL);
        CREATE INDEX usage_at ON usage (at);
        CREATE TABLE allowed_users (user_id integer PRIMARY KEY, added_at integer NOT NULL);
        CREATE TABLE rate_limit (id integer PRIMARY KEY CHECK (id = 1), snapshot text NOT NULL);
        """;

    private const string PostgresV1Schema = """
        CREATE SEQUENCE personal_id_seq;
        CREATE TABLE notes (id integer PRIMARY KEY DEFAULT nextval('personal_id_seq'), text text NOT NULL,
            tags text[] NOT NULL DEFAULT '{}', created_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE tasks (id integer PRIMARY KEY DEFAULT nextval('personal_id_seq'), title text NOT NULL, due date,
            remind_at timestamptz, reminded boolean NOT NULL DEFAULT false, done boolean NOT NULL DEFAULT false,
            created_at timestamptz NOT NULL DEFAULT now(), completed_at timestamptz);
        CREATE INDEX tasks_pending_reminders ON tasks (remind_at) WHERE NOT done AND NOT reminded;
        CREATE TABLE digest_state (id integer PRIMARY KEY CHECK (id = 1), last_date date NOT NULL);
        CREATE TABLE sessions (chat_id bigint PRIMARY KEY, session_id text NOT NULL, updated_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE chat_settings (chat_id bigint PRIMARY KEY, model text NOT NULL);
        CREATE TABLE usage (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, at timestamptz NOT NULL, model text,
            is_error boolean NOT NULL, cost_usd numeric NOT NULL, duration_ms integer NOT NULL, num_turns integer NOT NULL,
            input_tokens bigint NOT NULL, output_tokens bigint NOT NULL);
        CREATE INDEX usage_at ON usage (at);
        CREATE TABLE allowed_users (user_id bigint PRIMARY KEY, added_at timestamptz NOT NULL DEFAULT now());
        CREATE TABLE rate_limit (id integer PRIMARY KEY CHECK (id = 1), snapshot jsonb NOT NULL);
        """;
}

public sealed class ShortGuidTests {
    [Fact]
    public void ToString_Is22UrlSafeChars_AndParsesBack() {
        for (var i = 0; i < 100; i++) {
            var id = ShortGuid.NewId();
            var text = id.ToString();
            Assert.Equal(ShortGuid.Length, text.Length);
            Assert.DoesNotContain(text, c => c is '+' or '/' or '=' or ':');
            Assert.Equal(id, ShortGuid.Parse(text));
            Assert.Equal(id, ShortGuid.Parse($" #{text} "));
            Assert.Equal(id, ShortGuid.Parse(id.Value.ToString()));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("слишком-короткий")]
    [InlineData("!!!!!!!!!!!!!!!!!!!!!!")]
    public void TryParse_RejectsGarbage(string? text) => Assert.False(ShortGuid.TryParse(text, out _));

    [Fact]
    public void NewIds_GrowOverTime() {
        var ids = Enumerable.Range(0, 50).Select(_ => {
            Thread.Sleep(1);
            return ShortGuid.NewId();
        }).ToList();
        Assert.Equal(ids, ids.Order());
    }

    [Fact]
    public void Json_WritesShortForm() {
        var id = ShortGuid.NewId();
        var json = System.Text.Json.JsonSerializer.Serialize(new Note { Id = id });
        Assert.Contains($"\"Id\":\"{id}\"", json);
        Assert.Equal(id, System.Text.Json.JsonSerializer.Deserialize<Note>(json)!.Id);
    }
}
