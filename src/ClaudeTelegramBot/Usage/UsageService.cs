using System.Globalization;
using System.Text.Json;
using ClaudeTelegramBot.Claude;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;

namespace ClaudeTelegramBot.Usage;

// Статистика запросов к claude и последние данные о лимитах подписки, которые claude присылает
// в событии rate_limit_event. Разбирает ответы claude; хранит их IUsageRepository.
public sealed class UsageService(IUsageRepository repo) {
    private readonly Lock _lock = new();
    private RateLimitSnapshot? _limit = repo.GetRateLimit();

    public RateLimitSnapshot? LastRateLimit {
        get { lock (_lock) return _limit; }
    }

    public void Record(ClaudeReply r) {
        repo.Add(new UsageRecord {
            At = DateTimeOffset.Now,
            Model = ShortModel(r.Model),
            IsError = r.IsError,
            CostUsd = r.CostUsd ?? 0,
            DurationMs = r.DurationMs,
            NumTurns = r.NumTurns,
            InputTokens = r.InputTokens,
            OutputTokens = r.OutputTokens,
        });
    }

    public void SetRateLimit(JsonElement info) {
        var snapshot = new RateLimitSnapshot(
            DateTimeOffset.Now,
            Window(info, "five_hour"),
            Window(info, "seven_day"),
            info.TryGetProperty("status", out var s) ? s.GetString() : null);

        lock (_lock) _limit = snapshot;
        repo.SetRateLimit(snapshot);
    }

    public IReadOnlyList<UsageRecord> ReadSince(DateTimeOffset since) => repo.ReadSince(since);

    // claude-haiku-5-5 -> haiku-5-5: короче для сообщений.
    public static string? ShortModel(string? model) =>
        model is null ? null : model.StartsWith("claude-", StringComparison.Ordinal) ? model["claude-".Length..] : model;

    private static LimitWindow? Window(JsonElement info, string name) {
        if (!info.TryGetProperty("unifiedWindows", out var windows) || !windows.TryGetProperty(name, out var w))
            return null;

        var util = w.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var d) ? d : 0;
        DateTimeOffset? resets = w.TryGetProperty("resetsAt", out var ra) && ra.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime()
            : null;
        var status = w.TryGetProperty("status", out var st) ? st.GetString() : null;
        return new LimitWindow(util, resets, status);
    }

    public static string Percent(double utilization) =>
        (utilization * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
