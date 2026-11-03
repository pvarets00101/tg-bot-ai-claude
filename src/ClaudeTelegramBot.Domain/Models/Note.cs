namespace ClaudeTelegramBot.Domain.Models;

public sealed class Note : Entity {
    public string Text { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}
