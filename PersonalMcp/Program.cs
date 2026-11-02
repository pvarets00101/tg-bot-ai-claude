using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PersonalMcp;
using PersonalMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

// stdout занят протоколом MCP, поэтому все логи только в stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var dataPath = Environment.GetEnvironmentVariable("PERSONAL_MCP_DATA")
    ?? Path.Combine(AppContext.BaseDirectory, "personal-data.json");
var reposRoot = Environment.GetEnvironmentVariable("PERSONAL_MCP_REPOS") ?? "";

builder.Services.AddSingleton(new DataStore(dataPath));
builder.Services.AddSingleton(new RepoSettings(reposRoot));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
