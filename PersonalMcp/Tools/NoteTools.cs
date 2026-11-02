using System.ComponentModel;
using ModelContextProtocol.Server;

namespace PersonalMcp.Tools;

[McpServerToolType]
public sealed class NoteTools(DataStore store)
{
    [McpServerTool(Name = "add_note"), Description("Сохраняет заметку пользователя. Возвращает id заметки.")]
    public string AddNote(
        [Description("Текст заметки")] string text,
        [Description("Теги без #, например [\"работа\", \"идеи\"]")] string[]? tags = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Ошибка: пустой текст заметки.";

        var note = store.Update(d =>
        {
            var n = new Note
            {
                Id = d.NextId++,
                Text = text.Trim(),
                Tags = tags?.Select(t => t.Trim().TrimStart('#').ToLowerInvariant()).Where(t => t != "").Distinct().ToArray() ?? [],
                CreatedAt = DateTimeOffset.Now,
            };
            d.Notes.Add(n);
            return n;
        });
        return $"Заметка #{note.Id} сохранена.";
    }

    [McpServerTool(Name = "search_notes", ReadOnly = true),
     Description("Ищет заметки по подстроке и/или тегу. Без параметров возвращает последние заметки.")]
    public string SearchNotes(
        [Description("Подстрока для поиска в тексте (без учёта регистра)")] string? query = null,
        [Description("Тег без #")] string? tag = null,
        [Description("Максимум результатов")] int limit = 20)
    {
        var tagNorm = tag?.Trim().TrimStart('#').ToLowerInvariant();
        var found = store.Read(d => d.Notes
            .Where(n => string.IsNullOrWhiteSpace(query) || n.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(n => string.IsNullOrWhiteSpace(tagNorm) || n.Tags.Contains(tagNorm))
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(limit, 1, 100))
            .ToList());

        if (found.Count == 0) return "Заметок не найдено.";
        return string.Join("\n", found.Select(n =>
            $"#{n.Id} [{n.CreatedAt:yyyy-MM-dd HH:mm}]{(n.Tags.Length > 0 ? " " + string.Join(" ", n.Tags.Select(t => "#" + t)) : "")}: {n.Text}"));
    }

    [McpServerTool(Name = "delete_note", Destructive = true), Description("Удаляет заметку по id.")]
    public string DeleteNote([Description("id заметки")] int id) =>
        store.Update(d => d.Notes.RemoveAll(n => n.Id == id)) > 0
            ? $"Заметка #{id} удалена."
            : $"Заметка #{id} не найдена.";
}
