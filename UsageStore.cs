using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ClaudeTelegramBot;

public sealed record UsageRecord(
    DateTimeOffset At,
    string? Model,
    bool IsError,
    decimal CostUsd,
    int DurationMs,
    int NumTurns,
    long InputTokens,
    long OutputTokens);

// Окно лимита подписки: доля использования (0..1) и время сброса.
public sealed record LimitWindow(double Utilization, DateTimeOffset? ResetsAt, string? Status);

public sealed record RateLimitSnapshot(DateTimeOffset ObservedAt, LimitWindow? FiveHour, LimitWindow? SevenDay, string? Status);

// Статистика запросов к claude (usage.jsonl, по строке на запрос) и последние данные о лимитах
// подписки, которые claude присылает в событии rate_limit_event (rate-limit.json).
public sealed class UsageStore {
    private static readonly JsonSerializerOptions Json = new() {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _usagePath = Path.Combine(AppContext.BaseDirectory, "usage.jsonl");
    private readonly string _limitPath = Path.Combine(AppContext.BaseDirectory, "rate-limit.json");
    private readonly Lock _lock = new();
    private RateLimitSnapshot? _limit;

    public UsageStore() {
        try {
            if (File.Exists(_limitPath))
                _limit = JsonSerializer.Deserialize<RateLimitSnapshot>(File.ReadAllText(_limitPath), Json);
        } catch (Exception) {
            _limit = null; // битый файл — просто начнём заново
        }
    }

    public RateLimitSnapshot? LastRateLimit {
        get { lock (_lock) return _limit; }
    }

    public void Record(ClaudeReply r) {
        var rec = new UsageRecord(DateTimeOffset.Now, ShortModel(r.Model), r.IsError, r.CostUsd ?? 0,
            r.DurationMs, r.NumTurns, r.InputTokens, r.OutputTokens);
        lock (_lock)
            File.AppendAllText(_usagePath, JsonSerializer.Serialize(rec, Json) + "\n", new UTF8Encoding(false));
    }

    public void SetRateLimit(JsonElement info) {
        var snapshot = new RateLimitSnapshot(
            DateTimeOffset.Now,
            Window(info, "five_hour"),
            Window(info, "seven_day"),
            info.TryGetProperty("status", out var s) ? s.GetString() : null);

        lock (_lock) {
            _limit = snapshot;
            File.WriteAllText(_limitPath, JsonSerializer.Serialize(snapshot, Json));
        }
    }

    public IReadOnlyList<UsageRecord> ReadSince(DateTimeOffset since) {
        lock (_lock) {
            if (!File.Exists(_usagePath)) return [];
            var list = new List<UsageRecord>();
            foreach (var line in File.ReadLines(_usagePath)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try {
                    if (JsonSerializer.Deserialize<UsageRecord>(line, Json) is { } r && r.At >= since) list.Add(r);
                } catch (JsonException) {
                    // пропускаем битую строку
                }
            }
            return list;
        }
    }

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
