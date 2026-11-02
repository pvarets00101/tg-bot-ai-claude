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

builder.Services.AddSingleton<ITelegramBotClient>(sp =>
    new TelegramBotClient(sp.GetRequiredService<IOptions<BotOptions>>().Value.Token));
builder.Services.AddSingleton<ClaudeRunner>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddHostedService<BotWorker>();

builder.Build().Run();
