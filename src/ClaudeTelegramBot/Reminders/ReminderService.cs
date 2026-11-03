using System.Globalization;
using System.Text;
using ClaudeTelegramBot.Access;
using ClaudeTelegramBot.Configuration;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClaudeTelegramBot.Reminders;

// Следит за задачами в хранилище и сам пишет пользователю:
// напоминания по RemindAt (с кнопками) и утреннюю сводку на сегодня.
public sealed class ReminderService(
    ITelegramBotClient bot,
    ITaskRepository tasks,
    IOptions<ReminderOptions> options,
    AllowedUsers allowedUsers,
    ILogger<ReminderService> log) : BackgroundService {
    private readonly ReminderOptions _o = options.Value;
    private readonly TimeOnly? _digestTime = TimeOnly.TryParseExact(
        options.Value.DigestTime, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    protected override async Task ExecuteAsync(CancellationToken ct) {
        if (!_o.Enabled || allowedUsers.Ids.Count == 0) {
            log.LogInformation("Напоминания выключены (Enabled=false или нет пользователей с доступом).");
            return;
        }

        log.LogInformation("Напоминания включены, сводка: {Digest}", _digestTime?.ToString("HH:mm") ?? "выкл");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _o.CheckIntervalSeconds)));
        do {
            try {
                await SendDueRemindersAsync(ct);
                await SendDigestIfTimeAsync(ct);
            } catch (Exception ex) when (!ct.IsCancellationRequested) {
                log.LogError(ex, "Ошибка в цикле напоминаний");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task SendDueRemindersAsync(CancellationToken ct) {
        var now = DateTimeOffset.Now;

        // Помечаем отправленными сразу, в том же запросе: даже если отправка упадёт,
        // напоминание не будет приходить каждые 30 секунд.
        var due = tasks.TakeDueReminders(now);

        foreach (var task in due) {
            log.LogInformation("Напоминание по задаче #{Id}", task.Id);
            await Broadcast($"⏰ Напоминание: {task.Title}", Buttons(task.Id), ct);
        }
    }

    private async Task SendDigestIfTimeAsync(CancellationToken ct) {
        if (_digestTime is not { } digestTime) return;

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        if (TimeOnly.FromDateTime(now) < digestTime) return;

        if (!tasks.TryMarkDigest(today)) return;
        var digest = tasks.Open(dueOnOrBefore: today);
        if (digest.Count == 0) return; // сводку шлём, только если есть что сказать

        var sb = new StringBuilder($"☀️ Доброе утро! На сегодня ({today:dd.MM}):\n");
        foreach (var t in digest) {
            var mark = t.Due < today ? " — просрочено" : t.RemindAt is { } r ? $" — в {r:HH:mm}" : "";
            sb.Append($"\n• {t.Title}{mark}");
        }
        await Broadcast(sb.ToString(), null, ct);
    }

    // --- Кнопки под напоминанием ---

    // id — 22 символа: данные кнопки укладываются в лимит Telegram (64 байта).
    private static InlineKeyboardMarkup Buttons(ShortGuid taskId) => new(
    [
        [InlineKeyboardButton.WithCallbackData("✅ Выполнено", $"task:done:{taskId}")],
        [
            InlineKeyboardButton.WithCallbackData("⏰ +1 час", $"task:snooze:{taskId}:60"),
            InlineKeyboardButton.WithCallbackData("🌅 Завтра", $"task:snooze:{taskId}:tomorrow"),
        ],
    ]);

    public static bool IsTaskCallback(string? data) => data?.StartsWith("task:") == true;

    public async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct) {
        var parts = query.Data!.Split(':');
        if (parts.Length < 3 || !ShortGuid.TryParse(parts[2], out var id)) {
            await bot.AnswerCallbackQuery(query.Id, "Непонятная кнопка", cancellationToken: ct);
            return;
        }

        var result = parts[1] switch {
            "done" => Complete(id),
            "snooze" when parts.Length == 4 => Snooze(id, parts[3]),
            _ => null,
        };
        if (result is null) {
            await bot.AnswerCallbackQuery(query.Id, "Непонятная кнопка", cancellationToken: ct);
            return;
        }

        await bot.AnswerCallbackQuery(query.Id, result, cancellationToken: ct);
        if (query.Message is { } msg)
            await bot.EditMessageText(msg.Chat.Id, msg.Id, $"{msg.Text}\n\n→ {result}", replyMarkup: null, cancellationToken: ct);
    }

    private string Complete(ShortGuid id) =>
        tasks.Complete(id).Result == CompleteResult.NotFound ? "Задача уже удалена" : "Выполнено ✅";

    private string? Snooze(ShortGuid id, string arg) {
        DateTime at;
        if (arg == "tomorrow")
            at = DateTime.Today.AddDays(1).Add((_digestTime ?? new TimeOnly(9, 0)).ToTimeSpan());
        else if (int.TryParse(arg, out var minutes))
            at = DateTime.Now.AddMinutes(minutes);
        else
            return null;

        return tasks.Snooze(id, new DateTimeOffset(at)) ? $"Напомню {at:dd.MM в HH:mm}" : "Задача уже удалена";
    }

    // --- /tasks без вызова claude ---

    public string FormatOpenTasks() {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var open = tasks.Open();
        if (open.Count == 0) return "Открытых задач нет.";

        var sb = new StringBuilder("Открытые задачи:\n");
        foreach (var t in open) {
            var when = t.RemindAt is { } r ? $" — {r:dd.MM HH:mm}" : t.Due is { } d ? $" — {d:dd.MM}" : "";
            var overdue = t.Due < today ? " ⚠️" : "";
            sb.Append($"\n• {t.Title}{when}{overdue}");
        }
        return sb.ToString();
    }

    private async Task Broadcast(string text, InlineKeyboardMarkup? markup, CancellationToken ct) {
        foreach (var chatId in allowedUsers.Ids) {
            try {
                await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
            } catch (Exception ex) when (!ct.IsCancellationRequested) {
                log.LogWarning(ex, "Не удалось отправить напоминание в чат {ChatId}", chatId);
            }
        }
    }
}
