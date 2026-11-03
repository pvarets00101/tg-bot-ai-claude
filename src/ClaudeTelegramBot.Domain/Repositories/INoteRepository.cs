using ClaudeTelegramBot.Domain.Models;

namespace ClaudeTelegramBot.Domain.Repositories;

public interface INoteRepository : IRepository<Note> {
    Note Add(string text, string[] tags);

    // Новые сверху. query — подстрока без учёта регистра, tag — точное совпадение тега.
    IReadOnlyList<Note> Search(string? query, string? tag, int limit);
}
