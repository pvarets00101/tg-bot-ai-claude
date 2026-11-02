using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ClaudeTelegramBot;

public sealed record ClaudeReply(string Text, string? SessionId, bool IsError, decimal? CostUsd);

// Обёртка над `claude -p`: один вызов = один полный агентный цикл Claude Code.
public sealed class ClaudeRunner(
    IOptions<ClaudeOptions> options,
    IOptions<PersonalMcpOptions> mcpOptions,
    ILogger<ClaudeRunner> log) {
    private readonly ClaudeOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(options.Value.MaxConcurrency);
    private readonly string? _mcpConfigPath = WriteMcpConfig(mcpOptions.Value, log);

    public string WorkingDirectory { get; } = Path.GetFullPath(
        options.Value.WorkingDirectory, AppContext.BaseDirectory);

    // Генерирует mcp-config.json с абсолютными путями, чтобы не зависеть от того, откуда запущен бот.
    private static string? WriteMcpConfig(PersonalMcpOptions o, ILogger log)
    {
        if (!o.Enabled) return null;

        var serverPath = Path.GetFullPath(o.ServerPath, AppContext.BaseDirectory);
        if (!File.Exists(serverPath)) {
            log.LogWarning("PersonalMcp не найден: {Path}. Агент будет работать без своих инструментов.", serverPath);
            return null;
        }

        var config = new {
            mcpServers = new Dictionary<string, object> {
                ["personal"] = new {
                    type = "stdio",
                    command = serverPath,
                    args = Array.Empty<string>(),
                    env = new Dictionary<string, string> {
                        // exe сервера ищет рантайм через DOTNET_ROOT. Отдаём ему тот же .NET, на котором
                        // работает бот: иначе из Rider (~/.dotnet) или из консоли (Program Files) он может
                        // не найти нужную версию.
                        ["DOTNET_ROOT"] = DotnetRoot(),
                        ["PERSONAL_MCP_DATA"] = Path.GetFullPath(o.DataPath, AppContext.BaseDirectory),
                        ["PERSONAL_MCP_REPOS"] = o.ReposRoot,
                    },
                },
            },
        };

        var path = Path.Combine(AppContext.BaseDirectory, "mcp-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        log.LogInformation("PersonalMcp подключён: {Server}", serverPath);
        return path;
    }

    public async Task<ClaudeReply> AskAsync(string prompt, string? sessionId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try {
            return await RunAsync(prompt, sessionId, ct);
        } finally {
            _gate.Release();
        }
    }

    private async Task<ClaudeReply> RunAsync(string prompt, string? sessionId, CancellationToken ct)
    {
        Directory.CreateDirectory(WorkingDirectory);

        var psi = new ProcessStartInfo(_o.ExecutablePath) {
            WorkingDirectory = WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };

        // Аргументы через ArgumentList, а промпт через stdin: никаких проблем с экранированием.
        psi.ArgumentList.Add("-p");
        Add(psi, "--output-format", "json");
        Add(psi, "--max-turns", _o.MaxTurns.ToString());
        if (!string.IsNullOrWhiteSpace(_o.Model)) Add(psi, "--model", _o.Model);
        if (_o.Restricted) psi.ArgumentList.Add("--restricted");
        if (_o.Tools.Length > 0) Add(psi, "--tools", string.Join(",", _o.Tools));
        if (!string.IsNullOrWhiteSpace(_o.PermissionMode)) Add(psi, "--permission-mode", _o.PermissionMode);
        if (WriteSystemPromptFile() is { } promptFile) Add(psi, "--append-system-prompt-file", promptFile);
        if (_o.AllowedTools.Length > 0) Add(psi, "--allowedTools", string.Join(",", _o.AllowedTools));
        if (_mcpConfigPath is not null) {
            // --strict-mcp-config: только наш сервер, без MCP из глобальных настроек Claude Code.
            Add(psi, "--mcp-config", _mcpConfigPath);
            psi.ArgumentList.Add("--strict-mcp-config");
        }
        if (sessionId is not null) Add(psi, "--resume", sessionId);

        using var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteAsync(prompt);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
        try {
            await proc.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            proc.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            return new ClaudeReply($"Превышено время ожидания ({_o.TimeoutSeconds} с).", sessionId, true, null);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (string.IsNullOrWhiteSpace(stdout)) {
            log.LogError("claude exited with {Code}: {Stderr}", proc.ExitCode, stderr);
            return new ClaudeReply($"claude завершился с кодом {proc.ExitCode}: {stderr.Trim()}", sessionId, true, null);
        }

        using var json = JsonDocument.Parse(stdout);
        var root = json.RootElement;

        var reply = new ClaudeReply(
            Text: root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "",
            SessionId: root.TryGetProperty("session_id", out var s) ? s.GetString() : sessionId,
            IsError: root.TryGetProperty("is_error", out var e) && e.GetBoolean(),
            CostUsd: root.TryGetProperty("total_cost_usd", out var c) ? c.GetDecimal() : null);

        log.LogInformation("claude done: session={Session} error={Error} cost=${Cost}",
            reply.SessionId, reply.IsError, reply.CostUsd);
        return reply;
    }

    // SystemPrompt + файл инструкций -> один файл для --append-system-prompt-file.
    // Через файл, а не аргументом: многострочный текст ломается при передаче через claude.cmd.
    // Читаем при каждом запуске, чтобы правки инструкций подхватывались без перезапуска бота.
    private string? WriteSystemPromptFile()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_o.SystemPrompt)) parts.Add(_o.SystemPrompt.Trim());

        var instructions = Path.GetFullPath(_o.InstructionsFile, AppContext.BaseDirectory);
        if (File.Exists(instructions)) parts.Add(File.ReadAllText(instructions).Trim());
        else log.LogWarning("Файл инструкций агента не найден: {Path}", instructions);

        if (parts.Count == 0) return null;

        // Вызовы claude идут под семафором, но при MaxConcurrency > 1 пишем атомарно.
        var path = Path.Combine(AppContext.BaseDirectory, "system-prompt.generated.md");
        var tmp = $"{path}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllText(tmp, string.Join("\n\n", parts), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    // <root>\shared\Microsoft.NETCore.App\<версия>\ -> <root>
    private static string DotnetRoot() => Path.GetFullPath(Path.Combine(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    private static void Add(ProcessStartInfo psi, string name, string value)
    {
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add(value);
    }
}
