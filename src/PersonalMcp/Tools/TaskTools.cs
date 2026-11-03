using System.ComponentModel;
using System.Globalization;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using ModelContextProtocol.Server;

namespace PersonalMcp.Tools;

[McpServerToolType]
public sealed class TaskTools(ITaskRepository tasks) {
    [McpServerTool(Name = "add_task"),
     Description("Добавляет задачу в список дел. Если указано время, бот пришлёт напоминание в Telegram " +
                 "в этот момент. Время без даты = сегодня (или завтра, если время уже прошло). Возвращает id задачи.")]
    public string AddTask(
        [Description("Что нужно сделать")] string title,
        [Description("Срок в формате yyyy-MM-dd, если есть")] string? due = null,
        [Description("Время напоминания HH:mm, если пользователь просит напомнить в конкретное время")] string? time = null) {
        if (string.IsNullOrWhiteSpace(title)) return "Ошибка: пустое название задачи.";
        if (!TryParseSchedule(due, time, out var dueDate, out var remindAt, out var error)) return error;

        var task = tasks.Add(title.Trim(), dueDate, remindAt);
        return $"Задача #{task.Id} добавлена{Describe(task)}.";
    }

    [McpServerTool(Name = "reschedule_task"),
     Description("Меняет срок и/или время напоминания задачи. Пустые параметры = убрать срок/напоминание.")]
    public string RescheduleTask(
        [Description(IdDescription)] string id,
        [Description("Новый срок yyyy-MM-dd")] string? due = null,
        [Description("Новое время напоминания HH:mm")] string? time = null) {
        if (!ShortGuid.TryParse(id, out var taskId)) return BadId(id);
        if (!TryParseSchedule(due, time, out var dueDate, out var remindAt, out var error)) return error;

        var t = tasks.Reschedule(taskId, dueDate, remindAt);
        if (t is null) return $"Задача #{taskId} не найдена.";
        return $"Задача #{taskId} «{t.Title}»: {(dueDate is null && remindAt is null ? "срок и напоминание сняты" : "теперь" + Describe(t))}.";
    }

    [McpServerTool(Name = "list_tasks", ReadOnly = true),
     Description("Показывает список дел: открытые задачи по сроку, затем без срока. Сегодняшняя дата указана в первой строке.")]
    public string ListTasks([Description("Показать и выполненные задачи")] bool includeDone = false) {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var list = tasks.List(includeDone);

        if (list.Count == 0) return $"Сегодня {today:yyyy-MM-dd}. Задач нет.";
        return $"Сегодня {today:yyyy-MM-dd}.\n" + string.Join("\n", list.Select(t => {
            var mark = t.Done ? "[x]" : "[ ]";
            var overdue = !t.Done && t.Due is { } d && d < today ? ", просрочено" : "";
            return $"{mark} #{t.Id} {t.Title}{Describe(t)}{overdue}";
        }));
    }

    [McpServerTool(Name = "complete_task"), Description("Отмечает задачу выполненной.")]
    public string CompleteTask([Description(IdDescription)] string id) {
        if (!ShortGuid.TryParse(id, out var taskId)) return BadId(id);
        return tasks.Complete(taskId) switch {
            (CompleteResult.NotFound, _) => $"Задача #{taskId} не найдена.",
            (CompleteResult.AlreadyDone, _) => $"Задача #{taskId} уже выполнена.",
            (_, var title) => $"Задача #{taskId} «{title}» выполнена.",
        };
    }

    [McpServerTool(Name = "delete_task", Destructive = true), Description("Удаляет задачу по id.")]
    public string DeleteTask([Description(IdDescription)] string id) {
        if (!ShortGuid.TryParse(id, out var taskId)) return BadId(id);
        return tasks.Delete(taskId)
            ? $"Задача #{taskId} удалена."
            : $"Задача #{taskId} не найдена.";
    }

    private const string IdDescription = "id задачи, как в выдаче list_tasks (22 символа, без #)";

    private static string BadId(string id) => $"Ошибка: «{id}» — не id задачи. Возьми id из list_tasks.";

    private static string Describe(TaskItem t) {
        var parts = new List<string>();
        if (t.Due is { } d) parts.Add($"срок {d:yyyy-MM-dd}");
        if (t.RemindAt is { } r) parts.Add($"напоминание {r:yyyy-MM-dd HH:mm}{(t.Reminded ? " (отправлено)" : "")}");
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private static bool TryParseSchedule(string? due, string? time,
        out DateOnly? dueDate, out DateTimeOffset? remindAt, out string error) {
        dueDate = null;
        remindAt = null;
        error = "";

        if (!string.IsNullOrWhiteSpace(due)) {
            if (!DateOnly.TryParseExact(due.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) {
                error = "Ошибка: срок должен быть в формате yyyy-MM-dd.";
                return false;
            }
            dueDate = d;
        }

        if (!string.IsNullOrWhiteSpace(time)) {
            if (!TimeOnly.TryParseExact(time.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) {
                error = "Ошибка: время должно быть в формате HH:mm.";
                return false;
            }

            var now = DateTime.Now;
            var date = dueDate ?? DateOnly.FromDateTime(now);
            var at = date.ToDateTime(t);
            if (dueDate is null && at <= now) at = at.AddDays(1); // «в 9:00» вечером = завтра утром
            if (at <= now) {
                error = $"Ошибка: время напоминания {at:yyyy-MM-dd HH:mm} уже прошло.";
                return false;
            }

            dueDate ??= DateOnly.FromDateTime(at);
            remindAt = new DateTimeOffset(at);
        }

        return true;
    }
}
