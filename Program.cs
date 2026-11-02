using ClaudeTelegramBot;
using Microsoft.Extensions.Options;
using Telegram.Bot;

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

// Тот же файл, что и у MCP-сервера (путь передаётся ему в mcp-config.json).
builder.Services.AddSingleton(sp => new PersonalMcp.DataStore(Path.GetFullPath(
    sp.GetRequiredService<IOptions<PersonalMcpOptions>>().Value.DataPath, AppContext.BaseDirectory)));

// Один экземпляр и как фоновая служба, и как обработчик кнопок для BotWorker.
builder.Services.AddSingleton<ReminderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReminderService>());

builder.Services.AddSingleton<ITelegramBotClient>(sp =>
    new TelegramBotClient(sp.GetRequiredService<IOptions<BotOptions>>().Value.Token));
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<ChatSettingsStore>();
builder.Services.AddSingleton<UsageStore>();
builder.Services.AddHostedService<BotWorker>();

builder.Build().Run();
