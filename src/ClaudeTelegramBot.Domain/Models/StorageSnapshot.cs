namespace ClaudeTelegramBot.Domain.Models;

// Все данные хранилища целиком, включая мягко удалённые записи: для переноса между провайдерами
// (StorageMigrator). Id, ExternalId и отметки удаления переносятся как есть.
public sealed record StorageSnapshot(
    IReadOnlyList<Note> Notes,
    IReadOnlyList<TaskItem> Tasks,
    DateOnly? LastDigestDate,
    IReadOnlyList<ChatSession> Sessions,
    IReadOnlyList<ChatSettings> ChatSettings,
    IReadOnlyList<UsageRecord> Usage,
    RateLimitSnapshot? RateLimit,
    IReadOnlyList<AllowedUser> AllowedUsers) {
    public int RecordCount => Notes.Count + Tasks.Count + Sessions.Count + ChatSettings.Count + Usage.Count
                              + (RateLimit is null ? 0 : 1) + AllowedUsers.Count;

    public override string ToString() =>
        $"пользователей {AllowedUsers.Count}, заметок {Notes.Count}, задач {Tasks.Count}, сессий {Sessions.Count}, " +
        $"настроек чатов {ChatSettings.Count}, запросов {Usage.Count}, лимиты: {(RateLimit is null ? "нет" : "есть")}";
}
