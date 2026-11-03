using ClaudeTelegramBot;
using ClaudeTelegramBot.Access;
using ClaudeTelegramBot.Chat;
using ClaudeTelegramBot.Claude;
using ClaudeTelegramBot.Configuration;
using ClaudeTelegramBot.Domain.Repositories;
using ClaudeTelegramBot.Reminders;
using ClaudeTelegramBot.Storage;
using ClaudeTelegramBot.Usage;
using Microsoft.Extensions.Options;
using Telegram.Bot;

// Служебные команды (бот при этом не запускается):
//   migrate-storage <откуда> <куда>   — перенос данных между хранилищами;
//   users list | add <id> | remove <id> — кому можно писать боту.
switch (args) {
    case ["migrate-storage", .. var rest]:
        return Cli.MigrateStorage(rest);
    case ["users", .. var rest]:
        return Cli.Users(rest);
}

var builder = Host.CreateApplicationBuilder(args);

// Токен хранится в user-secrets. Подключаем их в любом окружении (в т.ч. запуск из Rider
// без DOTNET_ENVIRONMENT), чтобы не класть токен в appsettings.json.
builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<ClaudeOptions>(builder.Configuration.GetSection("Claude"));
builder.Services.Configure<PersonalMcpOptions>(builder.Configuration.GetSection("PersonalMcp"));
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection("Reminders"));
builder.Services.Configure<AttachmentOptions>(builder.Configuration.GetSection("Attachments"));
builder.Services.AddSingleton<AttachmentService>();

// Хранилище (Postgres, Sqlite или Json) — секции Storage и ConnectionStrings; общее с MCP-сервером,
// ему настройки передаются через mcp-config.json (см. ClaudeRunner).
builder.Services.AddBotStorage(builder.Configuration);
builder.Services.AddSingleton<AllowedUsers>();
builder.Services.AddSingleton<UsageService>();

// Один экземпляр и как фоновая служба, и как обработчик кнопок для BotWorker.
builder.Services.AddSingleton<ReminderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReminderService>());

builder.Services.AddSingleton<ITelegramBotClient>(sp =>
    new TelegramBotClient(sp.GetRequiredService<IOptions<BotOptions>>().Value.Token));
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddHostedService<BotWorker>();

var host = builder.Build();

// До запуска служб: таблицы хранилища и список пользователей в кеш.
var storage = host.Services.GetRequiredService<IStorage>();
storage.EnsureCreated();
host.Services.GetRequiredService<ILogger<Program>>().LogInformation("Хранилище: {Provider}", storage.Provider);
host.Services.GetRequiredService<AllowedUsers>().Load();

host.Run();
return 0;
