using ClaudeTelegramBot.Domain.Models;

namespace ClaudeTelegramBot.Domain.Repositories;

// Telegram user id, которым разрешено писать боту. Пустой список = доступ закрыт для всех.
public interface IAllowedUserRepository : IRepository<AllowedUser> {
    // Активные, по возрастанию id.
    IReadOnlyList<long> UserIds();

    // false — уже был в списке. Удалённого ранее восстанавливает.
    bool Add(long userId);

    // Мягкое удаление. false — не было в списке.
    bool Remove(long userId);
}

// chatId -> session_id Claude Code.
public interface ISessionRepository : IRepository<ChatSession> {
    string? GetSessionId(long chatId);
    void SetSessionId(long chatId, string sessionId);

    // Мягко удаляет сессию чата: следующий запрос начнёт новую.
    void Reset(long chatId);
}

public interface IChatSettingsRepository : IRepository<ChatSettings> {
    // null = модель по умолчанию.
    string? GetModel(long chatId);

    // null или пусто — сбросить на модель по умолчанию.
    void SetModel(long chatId, string? model);
}

public interface IUsageRepository : IRepository<UsageRecord> {
    // По возрастанию времени.
    IReadOnlyList<UsageRecord> ReadSince(DateTimeOffset since);

    RateLimitSnapshot? GetRateLimit();
    void SetRateLimit(RateLimitSnapshot snapshot);
}
