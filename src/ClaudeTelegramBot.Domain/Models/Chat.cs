using System.Globalization;
using System.Text.Json.Serialization;

namespace ClaudeTelegramBot.Domain.Models;

// Пользователь, которому разрешено писать боту. ExternalId — Telegram user id.
public sealed class AllowedUser : Entity {
    public DateTimeOffset AddedAt { get; set; }

    [JsonIgnore]
    public long UserId => long.Parse(ExternalId!, CultureInfo.InvariantCulture);
}

// Сессия Claude Code, которую продолжает чат. ExternalId — Telegram chat id.
public sealed class ChatSession : Entity {
    public string SessionId { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

// Настройки чата, которые пользователь меняет командами (/model). ExternalId — Telegram chat id.
public sealed class ChatSettings : Entity {
    public string Model { get; set; } = "";
}
