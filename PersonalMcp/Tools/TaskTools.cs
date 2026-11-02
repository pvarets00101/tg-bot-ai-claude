using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PersonalMcp.Tools;

[McpServerToolType]
public sealed class TaskTools(DataStore store)
{
    [McpServerTool(Name = "add_task"), Description("Добавляет задачу в список дел пользователя. Возвращает id задачи.")]
    public string AddTask(
        [Description("Что нужно сделать")] string title,
        [Description("Срок в формате yyyy-MM-dd, если есть")] string? due = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return "Ошибка: пустое название задачи.";

        DateOnly? dueDate = null;
        if (!string.IsNullOrWhiteSpace(due))
        {
            if (!DateOnly.TryParseExact(due, "yyyy-MM-dd", out var parsed))
                return "Ошибка: срок должен быть в формате yyyy-MM-dd.";
            dueDate = parsed;
        }

        var task = store.Update(d =>
        {
            var t = new TaskItem { Id = d.NextId++, Title = title.Trim(), Due = dueDate, CreatedAt = DateTimeOffset.Now };
            d.Tasks.Add(t);
            return t;
        });
        return $"Задача #{task.Id} добавлена{(task.Due is { } dd ? $", срок {dd:yyyy-MM-dd}" : "")}.";
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
            .ThenBy(t => t.Id)
            .ToList());

        if (tasks.Count == 0) return $"Сегодня {today:yyyy-MM-dd}. Задач нет.";
        return $"Сегодня {today:yyyy-MM-dd}.\n" + string.Join("\n", tasks.Select(t =>
        {
            var mark = t.Done ? "[x]" : "[ ]";
            var due = t.Due is { } d ? $" (срок {d:yyyy-MM-dd}{(!t.Done && d < today ? ", просрочено" : "")})" : "";
            return $"{mark} #{t.Id} {t.Title}{due}";
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
}
