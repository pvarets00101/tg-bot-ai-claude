using System.ComponentModel;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using ModelContextProtocol.Server;

namespace PersonalMcp.Tools;

[McpServerToolType]
public sealed class NoteTools(INoteRepository notes) {
    [McpServerTool(Name = "add_note"), Description("Сохраняет заметку пользователя. Возвращает id заметки.")]
    public string AddNote(
        [Description("Текст заметки")] string text,
        [Description("Теги без #, например [\"работа\", \"идеи\"]")] string[]? tags = null) {
        if (string.IsNullOrWhiteSpace(text)) return "Ошибка: пустой текст заметки.";

        var normTags = tags?.Select(t => t.Trim().TrimStart('#').ToLowerInvariant()).Where(t => t != "").Distinct().ToArray() ?? [];
        var note = notes.Add(text.Trim(), normTags);
        return $"Заметка #{note.Id} сохранена.";
    }

    [McpServerTool(Name = "search_notes", ReadOnly = true),
     Description("Ищет заметки по подстроке и/или тегу. Без параметров возвращает последние заметки.")]
    public string SearchNotes(
        [Description("Подстрока для поиска в тексте (без учёта регистра)")] string? query = null,
        [Description("Тег без #")] string? tag = null,
        [Description("Максимум результатов")] int limit = 20) {
        var tagNorm = tag?.Trim().TrimStart('#').ToLowerInvariant();
        var found = notes.Search(query?.Trim(), tagNorm, Math.Clamp(limit, 1, 100));

        if (found.Count == 0) return "Заметок не найдено.";
        return string.Join("\n", found.Select(n =>
            $"#{n.Id} [{n.CreatedAt:yyyy-MM-dd HH:mm}]{(n.Tags.Length > 0 ? " " + string.Join(" ", n.Tags.Select(t => "#" + t)) : "")}: {n.Text}"));
    }

    [McpServerTool(Name = "delete_note", Destructive = true), Description("Удаляет заметку по id.")]
    public string DeleteNote([Description("id заметки, как в выдаче search_notes (22 символа, без #)")] string id) {
        if (!ShortGuid.TryParse(id, out var noteId)) return $"Ошибка: «{id}» — не id заметки.";
        return notes.Delete(noteId)
            ? $"Заметка #{noteId} удалена."
            : $"Заметка #{noteId} не найдена.";
    }
}
