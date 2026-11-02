using System.Globalization;
using System.Text;

namespace ClaudeTelegramBot;

// Тексты для /usage и предупреждения о лимитах (HTML для Telegram).
public static class UsageReport {
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Build(UsageStore usage, string chatModel) {
        var now = DateTimeOffset.Now;
        var sb = new StringBuilder("📊 <b>Использование</b>\n");

        // Лимиты подписки — из последнего ответа claude.
        if (usage.LastRateLimit is { } rl) {
            sb.Append($"\n<b>Лимиты подписки</b> (по данным на {rl.ObservedAt:HH:mm}):\n");
            sb.Append(Window("5 часов", rl.FiveHour, now));
            sb.Append(Window("7 дней", rl.SevenDay, now));
            if (rl.Status is { } s && s != "allowed") sb.Append($"⛔ Статус: {TelegramFormatter.Escape(s)}\n");
        } else {
            sb.Append("\nЛимиты подписки появятся после первого запроса к агенту.\n");
        }

        // Наша статистика.
        var month = usage.ReadSince(now.AddDays(-30));
        var today = month.Where(r => r.At.Date == now.Date).ToList();
        var week = month.Where(r => r.At >= now.AddDays(-7)).ToList();
        var fiveHours = month.Where(r => r.At >= now.AddHours(-5)).ToList();

        sb.Append("\n<b>Запросы</b>:\n");
        sb.Append(Period("за 5 часов", fiveHours));
        sb.Append(Period("сегодня", today));
        sb.Append(Period("7 дней", week));
        sb.Append(Period("30 дней", month));

        if (month.Count > 0) {
            var byModel = month.GroupBy(r => r.Model ?? "?")
                .OrderByDescending(g => g.Count())
                .Select(g => $"{TelegramFormatter.Escape(g.Key)} — {g.Count()}");
            sb.Append($"\nМодели за 30 дней: {string.Join(", ", byModel)}\n");
        }

        sb.Append($"\nМодель этого чата: <b>{TelegramFormatter.Escape(chatModel)}</b> (сменить: /model)\n");
        sb.Append("\n<i>$ — сколько это стоило бы по ценам API. С подписки деньги не списываются, " +
                  "расходуются лимиты выше.</i>");
        return sb.ToString();
    }

    // Строка-предупреждение под ответом, если лимит почти исчерпан; иначе null.
    public static string? Warning(UsageStore usage, int thresholdPercent) {
        if (usage.LastRateLimit is not { } rl) return null;
        var threshold = thresholdPercent / 100.0;
        var now = DateTimeOffset.Now;

        var parts = new List<string>();
        if (rl.FiveHour is { } f && f.Utilization >= threshold)
            parts.Add($"5-часовой лимит: {UsageStore.Percent(f.Utilization)}{Resets(f, now)}");
        if (rl.SevenDay is { } w && w.Utilization >= threshold)
            parts.Add($"недельный лимит: {UsageStore.Percent(w.Utilization)}{Resets(w, now)}");
        return parts.Count == 0 ? null : "⚠️ <i>" + string.Join("; ", parts) + "</i>";
    }

    private static string Window(string name, LimitWindow? w, DateTimeOffset now) {
        if (w is null) return $"• {name}: нет данных\n";
        var icon = w.Utilization >= 0.9 ? "🔴" : w.Utilization >= 0.7 ? "🟡" : "🟢";
        return $"{icon} {name}: <b>{UsageStore.Percent(w.Utilization)}</b>{Resets(w, now)}\n";
    }

    private static string Resets(LimitWindow w, DateTimeOffset now) {
        if (w.ResetsAt is not { } at) return "";
        if (at <= now) return " (уже сброшен)";
        return at.Date == now.Date
            ? $", сброс в {at:HH:mm}"
            : $", сброс {at.ToString("ddd d MMM в HH:mm", Ru)}";
    }

    private static string Period(string name, IReadOnlyList<UsageRecord> records) {
        if (records.Count == 0) return $"• {name}: 0\n";
        var cost = records.Sum(r => r.CostUsd);
        var avg = records.Average(r => r.DurationMs) / 1000;
        var errors = records.Count(r => r.IsError);
        var err = errors > 0 ? $", ошибок {errors}" : "";
        return $"• {name}: <b>{records.Count}</b> (≈ ${cost.ToString("0.00", CultureInfo.InvariantCulture)}, " +
               $"в среднем {avg.ToString("0", CultureInfo.InvariantCulture)} с{err})\n";
    }
}
