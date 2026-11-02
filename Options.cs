namespace ClaudeTelegramBot;

public sealed class BotOptions
{
    public string Token { get; set; } = "";

    // Telegram user id, которым разрешено писать боту. Пустой список = доступ закрыт для всех.
    public long[] AllowedUserIds { get; set; } = [];
}

public sealed class ClaudeOptions
{
    public string ExecutablePath { get; set; } = "claude";

    // Папка, в которой запускается claude. Здесь лежит CLAUDE.md с инструкциями агента
    // и сюда же Claude Code привязывает сессии (--resume работает только из той же папки).
    public string WorkingDirectory { get; set; } = "agent-workspace";

    public string? Model { get; set; }
    public string? SystemPrompt { get; set; }
    public string[] AllowedTools { get; set; } = [];
    public int MaxTurns { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 300;

    // Сколько запросов к claude выполняется одновременно. На Pro лучше 1.
    public int MaxConcurrency { get; set; } = 1;
}

// Свой MCP-сервер (проект PersonalMcp). Бот генерирует для него mcp-config.json
// и передаёт в claude через --mcp-config. Инструменты видны агенту как mcp__personal__*.
public sealed class PersonalMcpOptions
{
    public bool Enabled { get; set; } = true;

    // Относительно папки бота; сборка бота кладёт сервер в bin\...\mcp.
    public string ServerPath { get; set; } = "mcp/PersonalMcp.exe";

    // Файл с заметками и задачами.
    public string DataPath { get; set; } = "personal-data.json";

    // Папка с git-репозиториями для list_repos / repo_status. Пусто = инструменты отключены.
    public string ReposRoot { get; set; } = "";
}

// Напоминания и утренняя сводка. Работают без вызова claude, лимиты не тратят.
// Получатели — все пользователи из Bot:AllowedUserIds (личный чат с ботом = user id).
public sealed class ReminderOptions
{
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSeconds { get; set; } = 30;

    // Время утренней сводки HH:mm. Пусто = сводка отключена.
    public string DigestTime { get; set; } = "09:00";
}
