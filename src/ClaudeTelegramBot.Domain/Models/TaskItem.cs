namespace ClaudeTelegramBot.Domain.Models;

public sealed class TaskItem : Entity {
    public string Title { get; set; } = "";
    public DateOnly? Due { get; set; }

    // Когда прислать напоминание в Telegram. null = без напоминания.
    public DateTimeOffset? RemindAt { get; set; }
    public bool Reminded { get; set; }

    public bool Done { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum CompleteResult { NotFound, AlreadyDone, Completed }
