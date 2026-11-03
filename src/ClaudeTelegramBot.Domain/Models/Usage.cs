namespace ClaudeTelegramBot.Domain.Models;

// Один запрос к claude.
public sealed class UsageRecord : Entity {
    public DateTimeOffset At { get; set; }
    public string? Model { get; set; }
    public bool IsError { get; set; }
    public decimal CostUsd { get; set; }
    public int DurationMs { get; set; }
    public int NumTurns { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
}

// Окно лимита подписки: доля использования (0..1) и время сброса.
public sealed record LimitWindow(double Utilization, DateTimeOffset? ResetsAt, string? Status);

// Последние данные о лимитах — одно значение на хранилище, не сущность.
public sealed record RateLimitSnapshot(DateTimeOffset ObservedAt, LimitWindow? FiveHour, LimitWindow? SevenDay, string? Status);
