namespace ClaudeTelegramBot;

public sealed class BotOptions {
    public string Token { get; set; } = "";

    // Telegram user id, которым разрешено писать боту. Пустой список = доступ закрыт для всех.
    public long[] AllowedUserIds { get; set; } = [];
}

public sealed class ClaudeOptions {
    public string ExecutablePath { get; set; } = "claude";

    // Папка, в которой запускается claude. Здесь лежит CLAUDE.md с инструкциями агента
    // и сюда же Claude Code привязывает сессии (--resume работает только из той же папки).
    public string WorkingDirectory { get; set; } = "agent-workspace";

    public string? Model { get; set; }
    public string? SystemPrompt { get; set; }

    // Файл с постоянными инструкциями агента (относительно папки бота). Склеивается с SystemPrompt.
    public string InstructionsFile { get; set; } = "agent-instructions.md";

    // --restricted: без инструментов, выполняющих команды; игнорируются ~/.claude/settings.json
    // (в т.ч. defaultMode=auto); файловые инструменты ограничены рабочей папкой.
    public bool Restricted { get; set; } = true;

    // --tools: какие встроенные инструменты вообще существуют для агента. MCP-инструменты не затрагивает.
    public string[] Tools { get; set; } = ["Read", "WebSearch", "WebFetch"];

    // --permission-mode: dontAsk = всё, что не в AllowedTools, молча отклоняется.
    public string PermissionMode { get; set; } = "dontAsk";

    public string[] AllowedTools { get; set; } = [];
    public int MaxTurns { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 300;

    // Сколько запросов к claude выполняется одновременно. На Pro лучше 1.
    public int MaxConcurrency { get; set; } = 1;
}

// Свой MCP-сервер (проект PersonalMcp). Бот генерирует для него mcp-config.json
// и передаёт в claude через --mcp-config. Инструменты видны агенту как mcp__personal__*.
public sealed class PersonalMcpOptions {
    public bool Enabled { get; set; } = true;

    // Относительно папки бота; сборка бота кладёт сервер в bin\...\mcp.
    public string ServerPath { get; set; } = "mcp/PersonalMcp.exe";

    // Файл с заметками и задачами.
    public string DataPath { get; set; } = "personal-data.json";

    // Папка с git-репозиториями для list_repos / repo_status. Пусто = инструменты отключены.
    public string ReposRoot { get; set; } = "";
}

// Фото и файлы из Telegram. Бот кладёт их в <WorkingDirectory>/inbox/<chatId>/, агенту
// разрешено читать только эту папку (правило "Read(./inbox/**)" в Claude:AllowedTools).
public sealed class AttachmentOptions {
    public bool Enabled { get; set; } = true;

    // Bot API не отдаёт файлы больше 20 МБ.
    public int MaxFileSizeMb { get; set; } = 20;

    // Через сколько дней удалять скачанные файлы.
    public int RetentionDays { get; set; } = 7;
}

// Напоминания и утренняя сводка. Работают без вызова claude, лимиты не тратят.
// Получатели — все пользователи из Bot:AllowedUserIds (личный чат с ботом = user id).
public sealed class ReminderOptions {
    public bool Enabled { get; set; } = true;
    public int CheckIntervalSeconds { get; set; } = 30;

    // Время утренней сводки HH:mm. Пусто = сводка отключена.
    public string DigestTime { get; set; } = "09:00";
}
