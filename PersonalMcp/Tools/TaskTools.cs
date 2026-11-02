using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;

namespace PersonalMcp.Tools;

[McpServerToolType]
public sealed class TaskTools(DataStore store)
{
    [McpServerTool(Name = "add_task"),
     Description("Добавляет задачу в список дел. Если указано время, бот пришлёт напоминание в Telegram " +
                 "в этот момент. Время без даты = сегодня (или завтра, если время уже прошло). Возвращает id задачи.")]
    public string AddTask(
        [Description("Что нужно сделать")] string title,
        [Description("Срок в формате yyyy-MM-dd, если есть")] string? due = null,
        [Description("Время напоминания HH:mm, если пользователь просит напомнить в конкретное время")] string? time = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Ошибка: пустое название задачи.";
        if (!TryParseSchedule(due, time, out var dueDate, out var remindAt, out var error)) return error;

        var task = store.Update(d =>
        {
            var t = new TaskItem
            {
                Id = d.NextId++,
                Title = title.Trim(),
                Due = dueDate,
                RemindAt = remindAt,
                CreatedAt = DateTimeOffset.Now,
            };
            d.Tasks.Add(t);
            return t;
        });
        return $"Задача #{task.Id} добавлена{Describe(task)}.";
    }

    [McpServerTool(Name = "reschedule_task"),
     Description("Меняет срок и/или время напоминания задачи. Пустые параметры = убрать срок/напоминание.")]
    public string RescheduleTask(
        [Description("id задачи")] int id,
        [Description("Новый срок yyyy-MM-dd")] string? due = null,
        [Description("Новое время напоминания HH:mm")] string? time = null)
    {
        if (!TryParseSchedule(due, time, out var dueDate, out var remindAt, out var error)) return error;

        return store.Update(d =>
        {
            var t = d.Tasks.FirstOrDefault(x => x.Id == id);
            if (t is null) return $"Задача #{id} не найдена.";
            t.Due = dueDate;
            t.RemindAt = remindAt;
            t.Reminded = false;
            return $"Задача #{id} «{t.Title}»: {(dueDate is null && remindAt is null ? "срок и напоминание сняты" : "теперь" + Describe(t))}.";
        });
    }

    [McpServerTool(Name = "list_tasks", ReadOnly = true),
     Description("Показывает список дел: открытые задачи по сроку, затем без срока. Сегодняшняя дата указана в первой строке.")]
    public string ListTasks([Description("Показать и выполненные задачи")] bool includeDone = false)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tasks = store.Read(d => d.Tasks
            .Where(t => includeDone || !t.Done)
            .OrderBy(t => t.Done)
            .ThenBy(t => t.Due ?? DateOnly.MaxValue)
            .ThenBy(t => t.RemindAt ?? DateTimeOffset.MaxValue)
            .ThenBy(t => t.Id)
            .ToList());

        if (tasks.Count == 0) return $"Сегодня {today:yyyy-MM-dd}. Задач нет.";
        return $"Сегодня {today:yyyy-MM-dd}.\n" + string.Join("\n", tasks.Select(t =>
        {
            var mark = t.Done ? "[x]" : "[ ]";
            var overdue = !t.Done && t.Due is { } d && d < today ? ", просрочено" : "";
            return $"{mark} #{t.Id} {t.Title}{Describe(t)}{overdue}";
        }));
    }

    [McpServerTool(Name = "complete_task"), Description("Отмечает задачу выполненной.")]
    public string CompleteTask([Description("id задачи")] int id) =>
        store.Update(d =>
        {
            var t = d.Tasks.FirstOrDefault(x => x.Id == id);
            if (t is null) return $"Задача #{id} не найдена.";
            if (t.Done) return $"Задача #{id} уже выполнена.";
            t.Done = true;
            t.CompletedAt = DateTimeOffset.Now;
            return $"Задача #{id} «{t.Title}» выполнена.";
        });

    [McpServerTool(Name = "delete_task", Destructive = true), Description("Удаляет задачу по id.")]
    public string DeleteTask([Description("id задачи")] int id) =>
        store.Update(d => d.Tasks.RemoveAll(t => t.Id == id)) > 0
            ? $"Задача #{id} удалена."
            : $"Задача #{id} не найдена.";

    private static string Describe(TaskItem t)
    {
        var parts = new List<string>();
        if (t.Due is { } d) parts.Add($"срок {d:yyyy-MM-dd}");
        if (t.RemindAt is { } r) parts.Add($"напоминание {r:yyyy-MM-dd HH:mm}{(t.Reminded ? " (отправлено)" : "")}");
        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    private static bool TryParseSchedule(string? due, string? time,
        out DateOnly? dueDate, out DateTimeOffset? remindAt, out string error)
    {
        dueDate = null;
        remindAt = null;
        error = "";

        if (!string.IsNullOrWhiteSpace(due))
        {
            if (!DateOnly.TryParseExact(due.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                error = "Ошибка: срок должен быть в формате yyyy-MM-dd.";
                return false;
            }
            dueDate = d;
        }

        if (!string.IsNullOrWhiteSpace(time))
        {
            if (!TimeOnly.TryParseExact(time.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                error = "Ошибка: время должно быть в формате HH:mm.";
                return false;
            }

            var now = DateTime.Now;
            var date = dueDate ?? DateOnly.FromDateTime(now);
            var at = date.ToDateTime(t);
            if (dueDate is null && at <= now) at = at.AddDays(1); // «в 9:00» вечером = завтра утром
            if (at <= now)
            {
                error = $"Ошибка: время напоминания {at:yyyy-MM-dd HH:mm} уже прошло.";
                return false;
            }

            dueDate ??= DateOnly.FromDateTime(at);
            remindAt = new DateTimeOffset(at);
        }

        return true;
    }
}
