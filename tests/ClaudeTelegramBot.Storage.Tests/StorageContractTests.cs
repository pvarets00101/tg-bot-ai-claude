using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;

namespace ClaudeTelegramBot.Storage.Tests;

// Поведение, одинаковое для всех провайдеров. Наследник создаёт пустое хранилище своего типа.
public abstract class StorageContractTests : IDisposable {
    private readonly List<IStorage> _opened = [];

    // Новое пустое хранилище для теста.
    protected abstract IStorage CreateEmpty();

    // Ещё один экземпляр того же хранилища (как второй процесс: бот и MCP-сервер).
    protected abstract IStorage OpenSame();

    private IStorage? _storage;

    protected IStorage S => _storage ??= Track(CreateEmpty());

    protected IStorage Track(IStorage s) {
        s.EnsureCreated();
        _opened.Add(s);
        return s;
    }

    public virtual void Dispose() {
        foreach (var s in _opened) s.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.Now;
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.LocalDateTime);

    // --- Базовый репозиторий (IRepository<T>) ---

    [Fact]
    public void Add_AssignsNewIds_KeepsGivenId() {
        var a = S.Notes.Add("a", []);
        var b = S.Tasks.Add("b", null, null);
        Assert.False(a.Id.IsEmpty);
        Assert.NotEqual(a.Id, b.Id);

        var given = ShortGuid.NewId();
        var n = S.Notes.Add(new Note { Id = given, Text = "свой id", CreatedAt = Now });
        Assert.Equal(given, n.Id);
        Assert.Equal("свой id", S.Notes.Get(given)!.Text);
    }

    [Fact]
    public void Add_DuplicateIdOrExternalId_Throws() {
        var n = S.Notes.Add("a", []);
        Assert.Throws<InvalidOperationException>(() => S.Notes.Add(new Note { Id = n.Id, Text = "b", CreatedAt = Now }));

        S.Notes.Add(new Note { ExternalId = "ext-1", Text = "c", CreatedAt = Now });
        Assert.Throws<InvalidOperationException>(() => S.Notes.Add(new Note { ExternalId = "ext-1", Text = "d", CreatedAt = Now }));
        Assert.Equal(2, S.Notes.GetAll().Count);
    }

    [Fact]
    public void Get_ByIdAndExternalId() {
        var n = S.Notes.Add(new Note { ExternalId = "outer-42", Text = "с внешним id", Tags = ["x"], CreatedAt = Now });

        var byId = S.Notes.Get(n.Id)!;
        Assert.Equal("outer-42", byId.ExternalId);
        Assert.Equal(["x"], byId.Tags);
        Assert.False(byId.SoftDeleted);
        Assert.Null(byId.SoftDeletedDate);

        Assert.Equal(n.Id, S.Notes.GetByExternalId("outer-42")!.Id);
        Assert.Null(S.Notes.GetByExternalId("нет такого"));
        Assert.Null(S.Notes.Get(ShortGuid.NewId()));
    }

    [Fact]
    public void Update_SavesFields_NotDeleted() {
        var t = S.Tasks.Add("было", null, null);
        t.Title = "стало";
        t.Due = Today;
        t.ExternalId = "ext";
        Assert.True(S.Tasks.Update(t));

        var read = S.Tasks.Get(t.Id)!;
        Assert.Equal("стало", read.Title);
        Assert.Equal(Today, read.Due);
        Assert.Equal("ext", read.ExternalId);

        S.Tasks.Delete(t.Id);
        t.Title = "после удаления";
        Assert.False(S.Tasks.Update(t));
        Assert.False(S.Tasks.Update(new TaskItem { Id = ShortGuid.NewId(), Title = "нет такой" }));
        Assert.Equal("стало", S.Tasks.Get(t.Id, includeDeleted: true)!.Title);
    }

    [Fact]
    public void Delete_IsSoft_RestoreBringsBack() {
        var n = S.Notes.Add("x", ["тег"]);
        Assert.True(S.Notes.Delete(n.Id));
        Assert.False(S.Notes.Delete(n.Id));
        Assert.False(S.Notes.Delete(ShortGuid.NewId()));

        Assert.Null(S.Notes.Get(n.Id));
        Assert.Empty(S.Notes.GetAll());
        Assert.Empty(S.Notes.Search(null, null, 10));

        var deleted = S.Notes.Get(n.Id, includeDeleted: true)!;
        Assert.True(deleted.SoftDeleted);
        AssertTime(DateTimeOffset.Now, deleted.SoftDeletedDate!.Value, TimeSpan.FromSeconds(30));
        Assert.Single(S.Notes.GetAll(includeDeleted: true));

        Assert.True(S.Notes.Restore(n.Id));
        Assert.False(S.Notes.Restore(n.Id));
        var restored = S.Notes.Get(n.Id)!;
        Assert.False(restored.SoftDeleted);
        Assert.Null(restored.SoftDeletedDate);
        Assert.Single(S.Notes.Search(null, "тег", 10));
    }

    // --- Заметки ---

    [Fact]
    public void Notes_SearchByTextIgnoringCase_ByTag_NewestFirst() {
        var milk = S.Notes.Add("Купить Молоко", ["покупки"]);
        var idea = S.Notes.Add("Идея для бота", ["идеи", "бот"]);

        Assert.Equal([milk.Id], S.Notes.Search("молоко", null, 10).Select(n => n.Id));
        Assert.Equal([idea.Id], S.Notes.Search(null, "идеи", 10).Select(n => n.Id));
        Assert.Empty(S.Notes.Search("молоко", "идеи", 10));
        Assert.Equal([idea.Id, milk.Id], S.Notes.Search(null, null, 10).Select(n => n.Id));
        Assert.Equal([idea.Id, milk.Id], S.Notes.GetAll().Select(n => n.Id));
        Assert.Equal([idea.Id], S.Notes.Search("  ", null, 1).Select(n => n.Id));

        var found = S.Notes.Search("идея", null, 10).Single();
        Assert.Equal("Идея для бота", found.Text);
        Assert.Equal(["идеи", "бот"], found.Tags);
        AssertTime(idea.CreatedAt, found.CreatedAt);
    }

    // --- Задачи ---

    [Fact]
    public void Tasks_ListOrder_ByDueThenReminder_DoneLast() {
        var later = S.Tasks.Add("позже", Today.AddDays(5), null);
        var noDue = S.Tasks.Add("без срока", null, null);
        var withRemind = S.Tasks.Add("с напоминанием", Today, Now.AddHours(1));
        var plain = S.Tasks.Add("без напоминания", Today, null);

        Assert.Equal([withRemind.Id, plain.Id, later.Id, noDue.Id], S.Tasks.List(false).Select(t => t.Id));

        S.Tasks.Complete(plain.Id);
        Assert.Equal([withRemind.Id, later.Id, noDue.Id], S.Tasks.List(false).Select(t => t.Id));
        Assert.Equal([withRemind.Id, later.Id, noDue.Id, plain.Id], S.Tasks.List(true).Select(t => t.Id));
        Assert.Equal([withRemind.Id, later.Id, noDue.Id, plain.Id], S.Tasks.GetAll().Select(t => t.Id));
    }

    [Fact]
    public void Tasks_Open_FiltersByDue() {
        var today = S.Tasks.Add("сегодня", Today, null);
        var overdue = S.Tasks.Add("просрочено", Today.AddDays(-1), null);
        S.Tasks.Add("завтра", Today.AddDays(1), null);
        var noDue = S.Tasks.Add("без срока", null, null);
        var done = S.Tasks.Add("сделано", Today, null);
        S.Tasks.Complete(done.Id);

        Assert.Equal([overdue.Id, today.Id], S.Tasks.Open(Today).Select(t => t.Id));
        Assert.Equal(4, S.Tasks.Open().Count);
        Assert.Contains(S.Tasks.Open(), t => t.Id == noDue.Id);
    }

    [Fact]
    public void Tasks_AddRoundtrip() {
        var remind = new DateTimeOffset(2026, 11, 4, 9, 30, 0, TimeSpan.FromHours(3));
        var added = S.Tasks.Add("Тест", new DateOnly(2026, 11, 4), remind);
        var t = S.Tasks.List(true).Single();

        Assert.Equal(added.Id, t.Id);
        Assert.Null(t.ExternalId);
        Assert.Equal("Тест", t.Title);
        Assert.Equal(new DateOnly(2026, 11, 4), t.Due);
        Assert.Equal(remind, t.RemindAt);
        Assert.False(t.Reminded);
        Assert.False(t.Done);
        Assert.Null(t.CompletedAt);
        AssertTime(added.CreatedAt, t.CreatedAt);
    }

    [Fact]
    public void Tasks_Complete_States() {
        var t = S.Tasks.Add("задача", null, null);

        Assert.Equal((CompleteResult.Completed, "задача"), S.Tasks.Complete(t.Id));
        Assert.Equal(CompleteResult.AlreadyDone, S.Tasks.Complete(t.Id).Result);
        Assert.Equal((CompleteResult.NotFound, (string?)null), S.Tasks.Complete(ShortGuid.NewId()));

        var done = S.Tasks.List(true).Single();
        Assert.True(done.Done);
        Assert.NotNull(done.CompletedAt);
    }

    // Удалённая задача не видна методам-намерениям: её нельзя выполнить, перенести, напомнить о ней.
    [Fact]
    public void Tasks_Deleted_InvisibleToIntentMethods() {
        var t = S.Tasks.Add("x", Today, Now.AddMinutes(-5));
        Assert.True(S.Tasks.Delete(t.Id));

        Assert.Empty(S.Tasks.List(true));
        Assert.Empty(S.Tasks.Open());
        Assert.Empty(S.Tasks.TakeDueReminders(Now));
        Assert.Equal(CompleteResult.NotFound, S.Tasks.Complete(t.Id).Result);
        Assert.Null(S.Tasks.Reschedule(t.Id, null, null));
        Assert.False(S.Tasks.Snooze(t.Id, Now.AddHours(1)));

        Assert.True(S.Tasks.Restore(t.Id));
        Assert.Single(S.Tasks.TakeDueReminders(Now));
    }

    [Fact]
    public void Tasks_Reschedule_ResetsReminder() {
        var t = S.Tasks.Add("x", Today, Now.AddMinutes(-5));
        Assert.Single(S.Tasks.TakeDueReminders(Now));

        var r = S.Tasks.Reschedule(t.Id, Today.AddDays(1), Now.AddMinutes(-1));
        Assert.NotNull(r);
        Assert.Equal(Today.AddDays(1), r.Due);
        Assert.False(r.Reminded);
        Assert.Single(S.Tasks.TakeDueReminders(Now));

        var cleared = S.Tasks.Reschedule(t.Id, null, null)!;
        Assert.Null(cleared.Due);
        Assert.Null(cleared.RemindAt);
        Assert.Null(S.Tasks.Reschedule(ShortGuid.NewId(), null, null));
    }

    [Fact]
    public void Reminders_TakenOnce_OnlyDueAndOpen() {
        var second = S.Tasks.Add("второе", null, Now.AddMinutes(-1));
        var first = S.Tasks.Add("первое", null, Now.AddMinutes(-10));
        S.Tasks.Add("будущее", null, Now.AddHours(1));
        S.Tasks.Add("без напоминания", null, null);
        var done = S.Tasks.Add("сделано", null, Now.AddMinutes(-3));
        S.Tasks.Complete(done.Id);

        var due = S.Tasks.TakeDueReminders(Now);
        Assert.Equal([first.Id, second.Id], due.Select(t => t.Id));
        Assert.All(due, t => Assert.True(t.Reminded));
        Assert.Empty(S.Tasks.TakeDueReminders(Now));
    }

    [Fact]
    public void Reminders_ConcurrentTakers_EachReminderOnce() {
        for (var i = 0; i < 40; i++) S.Tasks.Add($"t{i}", null, Now.AddMinutes(-1));
        var other = Track(OpenSame());

        var taken = new System.Collections.Concurrent.ConcurrentBag<ShortGuid>();
        Parallel.For(0, 8, i => {
            var store = i % 2 == 0 ? S : other;
            for (var k = 0; k < 5; k++)
                foreach (var t in store.Tasks.TakeDueReminders(Now)) taken.Add(t.Id);
        });

        Assert.Equal(40, taken.Count);
        Assert.Equal(40, taken.Distinct().Count());
    }

    [Fact]
    public void Snooze_ReopensAndMovesDue() {
        var t = S.Tasks.Add("x", Today, Now.AddMinutes(-1));
        S.Tasks.TakeDueReminders(Now);
        S.Tasks.Complete(t.Id);

        var at = new DateTimeOffset(Now.LocalDateTime.Date.AddDays(2).AddHours(9));
        Assert.True(S.Tasks.Snooze(t.Id, at));

        var s = S.Tasks.List(true).Single();
        Assert.False(s.Done);
        Assert.Null(s.CompletedAt);
        Assert.False(s.Reminded);
        Assert.Equal(at, s.RemindAt);
        Assert.Equal(Today.AddDays(2), s.Due);

        Assert.False(S.Tasks.Snooze(ShortGuid.NewId(), at));
    }

    [Fact]
    public void Snooze_KeepsLaterDueAndNoDue() {
        var later = S.Tasks.Add("позже", Today.AddDays(10), null);
        var noDue = S.Tasks.Add("без срока", null, null);
        var at = Now.AddDays(1);

        S.Tasks.Snooze(later.Id, at);
        S.Tasks.Snooze(noDue.Id, at);

        var all = S.Tasks.List(true).ToDictionary(t => t.Id);
        Assert.Equal(Today.AddDays(10), all[later.Id].Due);
        Assert.Null(all[noDue.Id].Due);
    }

    [Fact]
    public void Digest_OncePerDay() {
        Assert.True(S.Tasks.TryMarkDigest(Today));
        Assert.False(S.Tasks.TryMarkDigest(Today));
        Assert.True(S.Tasks.TryMarkDigest(Today.AddDays(1)));
        Assert.False(Track(OpenSame()).Tasks.TryMarkDigest(Today.AddDays(1)));
    }

    // --- Пользователи ---

    [Fact]
    public void AllowedUsers_AddRemoveList_ExternalIdIsTelegramId() {
        Assert.Empty(S.AllowedUsers.UserIds());
        Assert.True(S.AllowedUsers.Add(631248686));
        Assert.False(S.AllowedUsers.Add(631248686));
        Assert.True(S.AllowedUsers.Add(42));
        Assert.Equal([42L, 631248686L], S.AllowedUsers.UserIds());
        Assert.Equal([42L, 631248686L], Track(OpenSame()).AllowedUsers.UserIds());
        Assert.Equal(631248686, S.AllowedUsers.GetByExternalId("631248686")!.UserId);

        Assert.True(S.AllowedUsers.Remove(42));
        Assert.False(S.AllowedUsers.Remove(42));
        Assert.Equal([631248686L], S.AllowedUsers.UserIds());

        // Повторное добавление восстанавливает ту же запись.
        var removed = S.AllowedUsers.GetByExternalId("42", includeDeleted: true)!;
        Assert.True(removed.SoftDeleted);
        Assert.True(S.AllowedUsers.Add(42));
        Assert.Equal(removed.Id, S.AllowedUsers.GetByExternalId("42")!.Id);
        Assert.Equal(2, S.AllowedUsers.GetAll(includeDeleted: true).Count);
    }

    // --- Сессии, настройки, статистика ---

    [Fact]
    public void Sessions_SetOverwriteReset() {
        Assert.Null(S.Sessions.GetSessionId(1));
        S.Sessions.SetSessionId(1, "a");
        var first = S.Sessions.GetByExternalId("1")!;
        S.Sessions.SetSessionId(1, "b");
        S.Sessions.SetSessionId(2, "c");
        Assert.Equal("b", S.Sessions.GetSessionId(1));
        Assert.Equal("c", Track(OpenSame()).Sessions.GetSessionId(2));
        Assert.Equal(first.Id, S.Sessions.GetByExternalId("1")!.Id);

        S.Sessions.Reset(1);
        S.Sessions.Reset(42);
        Assert.Null(S.Sessions.GetSessionId(1));
        Assert.Equal("c", S.Sessions.GetSessionId(2));
        Assert.True(S.Sessions.GetByExternalId("1", includeDeleted: true)!.SoftDeleted);

        // Новая сессия после сброса — та же запись, восстановленная.
        S.Sessions.SetSessionId(1, "d");
        Assert.Equal("d", S.Sessions.GetSessionId(1));
        Assert.Equal(first.Id, S.Sessions.GetByExternalId("1")!.Id);
    }

    [Fact]
    public void ChatSettings_SetAndClear() {
        Assert.Null(S.ChatSettings.GetModel(5));
        S.ChatSettings.SetModel(5, "sonnet");
        S.ChatSettings.SetModel(5, "haiku");
        Assert.Equal("haiku", S.ChatSettings.GetModel(5));

        S.ChatSettings.SetModel(5, " ");
        Assert.Null(S.ChatSettings.GetModel(5));
        S.ChatSettings.SetModel(6, "opus");
        S.ChatSettings.SetModel(6, null);
        Assert.Null(S.ChatSettings.GetModel(6));
        S.ChatSettings.SetModel(5, "opus");
        Assert.Equal("opus", S.ChatSettings.GetModel(5));
        Assert.Equal(2, S.ChatSettings.GetAll(includeDeleted: true).Count);
    }

    [Fact]
    public void Usage_ReadSince_FiltersAndOrders() {
        var old = Record(Now.AddDays(-10), "haiku-5-5", 0.01m);
        var recent = Record(Now.AddHours(-1), null, 0.123456m);
        var newest = Record(Now.AddMinutes(-1), "opus-5-5", 1.5m);
        S.Usage.Add(newest);
        S.Usage.Add(old);
        S.Usage.Add(recent);

        var since = S.Usage.ReadSince(Now.AddDays(-1));
        Assert.Equal(2, since.Count);
        AssertUsage(recent, since[0]);
        AssertUsage(newest, since[1]);
        Assert.Equal(3, S.Usage.ReadSince(DateTimeOffset.MinValue).Count);

        Assert.True(S.Usage.Delete(recent.Id));
        Assert.Equal([newest.Id], S.Usage.ReadSince(Now.AddDays(-1)).Select(u => u.Id));
    }

    [Fact]
    public void RateLimit_Roundtrip() {
        Assert.Null(S.Usage.GetRateLimit());
        var snap = RateLimit();
        S.Usage.SetRateLimit(snap);
        S.Usage.SetRateLimit(snap with { Status = "allowed_warning" });

        var read = S.Usage.GetRateLimit()!;
        Assert.Equal("allowed_warning", read.Status);
        Assert.Equal(snap.FiveHour, read.FiveHour);
        Assert.Equal(snap.SevenDay, read.SevenDay);
        AssertTime(snap.ObservedAt, read.ObservedAt);
    }

    // --- Снимок и перенос ---

    [Fact]
    public void IsEmpty_CountsSoftDeletedToo() {
        Assert.True(S.IsEmpty());
        S.Sessions.SetSessionId(1, "s");
        Assert.False(S.IsEmpty());
        S.Sessions.Reset(1);
        Assert.False(S.IsEmpty());

        var other = Track(CreateEmpty());
        other.Usage.SetRateLimit(RateLimit());
        Assert.False(other.IsEmpty());
    }

    [Fact]
    public void Snapshot_ExportImport_PreservesEverything() {
        Populate(S);
        var exported = S.Export();
        Assert.Contains(exported.Notes, n => n.SoftDeleted);

        var copy = Track(CreateEmpty());
        copy.Import(exported);

        Assert.Equal(SnapshotText.Of(exported), SnapshotText.Of(copy.Export()));
        var deleted = exported.Notes.Single(n => n.SoftDeleted);
        Assert.Null(copy.Notes.Get(deleted.Id));
        Assert.True(copy.Notes.Restore(deleted.Id));
    }

    [Fact]
    public void Import_LargeUsageLog_KeepsAllRecordsInOrder() {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var usage = Enumerable.Range(0, 2000)
            .Select(i => Record(start.AddMinutes(i), i % 3 == 0 ? null : "haiku-5-5", i / 1000m))
            .ToList();
        var snapshot = new StorageSnapshot([], [], null, [], [], usage, null, []);

        S.Import(snapshot);

        var read = S.Usage.ReadSince(DateTimeOffset.MinValue);
        Assert.Equal(2000, read.Count);
        AssertUsage(usage[0], read[0]);
        AssertUsage(usage[1999], read[1999]);
        Assert.Equal(usage.Count(u => u.Model is null), read.Count(u => u.Model is null));
    }

    [Fact]
    public void Import_IntoNonEmpty_Throws() {
        S.Notes.Add("есть данные", []);
        Assert.Throws<InvalidOperationException>(() => S.Import(S.Export()));
    }

    [Fact]
    public void EnsureCreated_Twice_KeepsData() {
        S.Notes.Add("x", []);
        var again = Track(OpenSame());
        Assert.Single(again.Notes.GetAll());
    }

    // --- Данные для тестов ---

    internal static void Populate(IStorage s) {
        s.Notes.Add("Заметка «один»", ["тег", "ещё"]);
        s.Notes.Add(new Note { ExternalId = "import:7", Text = "с внешним id", CreatedAt = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero) });
        var done = s.Tasks.Add("Выполненная", new DateOnly(2026, 11, 1), new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        s.Tasks.Complete(done.Id);
        s.Tasks.Add("Открытая", null, null);
        var deleted = s.Notes.Add("будет удалена", []);
        s.Notes.Delete(deleted.Id);
        s.Tasks.TryMarkDigest(new DateOnly(2026, 11, 2));
        s.Sessions.SetSessionId(631248686, "8424f831-e379-4039-8e71-cbc1289b5eaa");
        s.Sessions.SetSessionId(-100500, "group-session");
        s.Sessions.Reset(-100500);
        s.ChatSettings.SetModel(631248686, "sonnet");
        s.Usage.Add(Record(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.FromHours(3)), "haiku-5-5", 0.0123m));
        var error = Record(new DateTimeOffset(2026, 11, 2, 19, 0, 0, TimeSpan.FromHours(3)), null, 0m);
        error.IsError = true;
        s.Usage.Add(error);
        s.Usage.SetRateLimit(RateLimit());
        s.AllowedUsers.Add(631248686);
        s.AllowedUsers.Add(7);
    }

    internal static UsageRecord Record(DateTimeOffset at, string? model, decimal cost) => new() {
        At = at,
        Model = model,
        CostUsd = cost,
        DurationMs = 1234,
        NumTurns = 3,
        InputTokens = 100_000_000_000,
        OutputTokens = 42,
    };

    internal static RateLimitSnapshot RateLimit() => new(
        new DateTimeOffset(2026, 11, 2, 20, 38, 35, TimeSpan.FromHours(3)),
        new LimitWindow(0.38, new DateTimeOffset(2026, 11, 2, 22, 30, 0, TimeSpan.FromHours(3)), "allowed"),
        new LimitWindow(0.22, null, null),
        "allowed");

    // Провайдеры хранят время с разной точностью (PostgreSQL — микросекунды).
    internal static void AssertTime(DateTimeOffset expected, DateTimeOffset actual, TimeSpan? tolerance = null) =>
        Assert.True((expected - actual).Duration() < (tolerance ?? TimeSpan.FromMilliseconds(1)), $"{expected:O} != {actual:O}");

    private static void AssertUsage(UsageRecord expected, UsageRecord actual) {
        AssertTime(expected.At, actual.At);
        Assert.Equal(SnapshotText.Usage(expected), SnapshotText.Usage(actual));
    }
}

// Снимок в виде текста для сравнения: время в UTC с точностью до миллисекунды.
internal static class SnapshotText {
    public static string Of(StorageSnapshot s) {
        var lines = new List<string> { $"digest={s.LastDigestDate}" };
        lines.AddRange(s.Notes.OrderBy(n => n.Id).Select(n => $"note {E(n)} {n.Text} [{string.Join(",", n.Tags)}] {T(n.CreatedAt)}"));
        lines.AddRange(s.Tasks.OrderBy(t => t.Id).Select(t =>
            $"task {E(t)} {t.Title} {t.Due} {T(t.RemindAt)} {t.Reminded} {t.Done} {T(t.CreatedAt)} {T(t.CompletedAt)}"));
        lines.AddRange(s.Sessions.OrderBy(e => e.Id).Select(e => $"session {E(e)} {e.SessionId} {T(e.UpdatedAt)}"));
        lines.AddRange(s.ChatSettings.OrderBy(e => e.Id).Select(e => $"settings {E(e)} {e.Model}"));
        lines.AddRange(s.Usage.Select(Usage));
        lines.AddRange(s.AllowedUsers.OrderBy(e => e.Id).Select(e => $"user {E(e)} {T(e.AddedAt)}"));
        if (s.RateLimit is { } rl)
            lines.Add($"limit {T(rl.ObservedAt)} {rl.Status} {W(rl.FiveHour)} {W(rl.SevenDay)}");
        return string.Join("\n", lines);
    }

    // Масштаб decimal у провайдеров разный (0 и 0.0) — сравниваем значение.
    public static string Usage(UsageRecord u) =>
        $"usage {E(u)} {T(u.At)} {u.Model} {u.IsError} {u.CostUsd.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)} " +
        $"{u.DurationMs} {u.NumTurns} {u.InputTokens} {u.OutputTokens}";

    private static string E(Entity e) => $"{e.Id} ext={e.ExternalId} del={e.SoftDeleted} {T(e.SoftDeletedDate)}";

    private static string T(DateTimeOffset? d) =>
        d is { } v ? new DateTimeOffset(v.UtcTicks - v.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero).ToString("O") : "-";

    private static string W(LimitWindow? w) => w is null ? "-" : $"{w.Utilization} {T(w.ResetsAt)} {w.Status}";
}
