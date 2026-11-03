using ClaudeTelegramBot.Domain.Repositories;
using ClaudeTelegramBot.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMcp;
using PersonalMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

// stdout занят протоколом MCP, поэтому все логи только в stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// Хранилище то же, что у бота: бот передаёт его настройки переменными окружения в mcp-config.json
// (Storage__Provider и путь или ConnectionStrings__Postgres, см. StorageOptions.ToEnvironment).
// Без них сервер молча завёл бы своё отдельное хранилище, поэтому это ошибка.
if (string.IsNullOrWhiteSpace(builder.Configuration[$"{StorageOptions.Section}:{nameof(StorageOptions.Provider)}"])) {
    Console.Error.WriteLine("Storage__Provider не задана: настройки хранилища передаёт бот через mcp-config.json.");
    return 1;
}
builder.Services.AddBotStorage(builder.Configuration);

var reposRoot = Environment.GetEnvironmentVariable("PERSONAL_MCP_REPOS") ?? "";
builder.Services.AddSingleton(new RepoSettings(reposRoot));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
app.Services.GetRequiredService<IStorage>().EnsureCreated();
await app.RunAsync();
return 0;
