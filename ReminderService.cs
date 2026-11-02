using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using PersonalMcp;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClaudeTelegramBot;

// Следит за задачами из personal-data.json и сам пишет пользователю:
// напоминания по RemindAt (с кнопками) и утреннюю сводку на сегодня.
public sealed class ReminderService(
    ITelegramBotClient bot,
    DataStore store,
    IOptions<ReminderOptions> options,
    IOptions<BotOptions> botOptions,
    ILogger<ReminderService> log) : BackgroundService
{
    private readonly ReminderOptions _o = options.Value;
    private readonly long[] _recipients = botOptions.Value.AllowedUserIds;
    private readonly TimeOnly? _digestTime = TimeOnly.TryParseExact(
        options.Value.DigestTime, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_o.Enabled || _recipients.Length == 0)
        {
            log.LogInformation("Напоминания выключены (Enabled=false или пустой AllowedUserIds).");
            return;
        }

        log.LogInformation("Напоминания включены, сводка: {Digest}", _digestTime?.ToString("HH:mm") ?? "выкл");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _o.CheckIntervalSeconds)));
        do
        {
            try
            {
                await SendDueRemindersAsync(ct);
                await SendDigestIfTimeAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Ошибка в цикле напоминаний");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task SendDueRemindersAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;

        // Помечаем отправленными сразу под блокировкой: даже если отправка упадёт,
        // напоминание не будет приходить каждые 30 секунд.
        var due = store.Update(d =>
        {
            var list = d.Tasks.Where(t => !t.Done && !t.Reminded && t.RemindAt <= now).ToList();
            foreach (var t in list) t.Reminded = true;
            return list;
        });

        foreach (var task in due)
        {
            log.LogInformation("Напоминание по задаче #{Id}", task.Id);
            await Broadcast($"⏰ Напоминание: {task.Title}\n(задача #{task.Id})", Buttons(task.Id), ct);
        }
    }

    private async Task SendDigestIfTimeAsync(CancellationToken ct)
    {
        if (_digestTime is not { } digestTime) return;

        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);
        if (TimeOnly.FromDateTime(now) < digestTime) return;

        var tasks = store.Update(d =>
        {
            if (d.LastDigestDate == today) return null;
            d.LastDigestDate = today;
            return d.Tasks.Where(t => !t.Done && t.Due <= today).OrderBy(t => t.Due).ThenBy(t => t.RemindAt).ToList();
        });
        if (tasks is null || tasks.Count == 0) return; // сводку шлём, только если есть что сказать

        var sb = new StringBuilder($"☀️ Доброе утро! На сегодня ({today:dd.MM}):\n");
        foreach (var t in tasks)
        {
            var mark = t.Due < today ? " — просрочено" : t.RemindAt is { } r ? $" — в {r:HH:mm}" : "";
            sb.Append($"\n• #{t.Id} {t.Title}{mark}");
        }
        await Broadcast(sb.ToString(), null, ct);
    }

    // --- Кнопки под напоминанием ---

    private static InlineKeyboardMarkup Buttons(int taskId) => new(
    [
        [InlineKeyboardButton.WithCallbackData("✅ Выполнено", $"task:done:{taskId}")],
        [
            InlineKeyboardButton.WithCallbackData("⏰ +1 час", $"task:snooze:{taskId}:60"),
            InlineKeyboardButton.WithCallbackData("🌅 Завтра", $"task:snooze:{taskId}:tomorrow"),
        ],
    ]);

    public static bool IsTaskCallback(string? data) => data?.StartsWith("task:") == true;

    public async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        var parts = query.Data!.Split(':');
        if (parts.Length < 3 || !int.TryParse(parts[2], out var id))
        {
            await bot.AnswerCallbackQuery(query.Id, "Непонятная кнопка", cancellationToken: ct);
            return;
        }

        var result = parts[1] switch
        {
            "done" => Complete(id),
            "snooze" when parts.Length == 4 => Snooze(id, parts[3]),
            _ => null,
        };
        if (result is null)
        {
            await bot.AnswerCallbackQuery(query.Id, "Непонятная кнопка", cancellationToken: ct);
            return;
        }

        await bot.AnswerCallbackQuery(query.Id, result, cancellationToken: ct);
        if (query.Message is { } msg)
            await bot.EditMessageText(msg.Chat.Id, msg.Id, $"{msg.Text}\n\n→ {result}", replyMarkup: null, cancellationToken: ct);
    }

    private string Complete(int id) => store.Update(d =>
    {
        var t = d.Tasks.FirstOrDefault(x => x.Id == id);
        if (t is null) return "Задача уже удалена";
        if (!t.Done)
        {
            t.Done = true;
            t.CompletedAt = DateTimeOffset.Now;
        }
        return "Выполнено ✅";
    });

    private string? Snooze(int id, string arg)
    {
        DateTime at;
        if (arg == "tomorrow")
            at = DateTime.Today.AddDays(1).Add((_digestTime ?? new TimeOnly(9, 0)).ToTimeSpan());
        else if (int.TryParse(arg, out var minutes))
            at = DateTime.Now.AddMinutes(minutes);
        else
            return null;

        return store.Update(d =>
        {
            var t = d.Tasks.FirstOrDefault(x => x.Id == id);
            if (t is null) return "Задача уже удалена";
            t.RemindAt = new DateTimeOffset(at);
            t.Reminded = false;
            t.Done = false;
            if (t.Due < DateOnly.FromDateTime(at)) t.Due = DateOnly.FromDateTime(at);
            return $"Напомню {at:dd.MM в HH:mm}";
        });
    }

    // --- /tasks без вызова claude ---

    public string FormatOpenTasks()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tasks = store.Read(d => d.Tasks
            .Where(t => !t.Done)
            .OrderBy(t => t.Due ?? DateOnly.MaxValue)
            .ThenBy(t => t.RemindAt ?? DateTimeOffset.MaxValue)
            .ToList());
        if (tasks.Count == 0) return "Открытых задач нет.";

        var sb = new StringBuilder("Открытые задачи:\n");
        foreach (var t in tasks)
        {
            var when = t.RemindAt is { } r ? $" — {r:dd.MM HH:mm}" : t.Due is { } d ? $" — {d:dd.MM}" : "";
            var overdue = t.Due < today ? " ⚠️" : "";
            sb.Append($"\n• #{t.Id} {t.Title}{when}{overdue}");
        }
        return sb.ToString();
    }

    private async Task Broadcast(string text, InlineKeyboardMarkup? markup, CancellationToken ct)
    {
        foreach (var chatId in _recipients)
        {
            try
            {
                await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Не удалось отправить напоминание в чат {ChatId}", chatId);
            }
        }
    }
}
