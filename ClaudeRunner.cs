using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ClaudeTelegramBot;

public sealed record ClaudeReply(
    string Text,
    string? SessionId,
    bool IsError,
    decimal? CostUsd,
    string? Model = null,
    int DurationMs = 0,
    int NumTurns = 0,
    long InputTokens = 0,
    long OutputTokens = 0);

// Агент вызвал инструмент. Input — аргументы вызова (клон, можно хранить после завершения).
public sealed record ToolCall(string Name, JsonElement Input);

// Обёртка над `claude -p`: один вызов = один полный агентный цикл Claude Code.
// Вывод читается потоком (stream-json), чтобы показывать прогресс и снимать данные о лимитах.
public sealed class ClaudeRunner(
    IOptions<ClaudeOptions> options,
    IOptions<PersonalMcpOptions> mcpOptions,
    UsageStore usage,
    ILogger<ClaudeRunner> log) {
    private readonly ClaudeOptions _o = options.Value;
    private readonly SemaphoreSlim _gate = new(options.Value.MaxConcurrency);
    private readonly string? _mcpConfigPath = WriteMcpConfig(mcpOptions.Value, log);

    public string WorkingDirectory { get; } = Path.GetFullPath(
        options.Value.WorkingDirectory, AppContext.BaseDirectory);

    // Генерирует mcp-config.json с абсолютными путями, чтобы не зависеть от того, откуда запущен бот.
    private static string? WriteMcpConfig(PersonalMcpOptions o, ILogger log) {
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

    /// <param name="model">Модель для этого запроса; null = Claude:Model.</param>
    /// <param name="onToolCall">Вызывается на каждый вызов инструмента агентом (для прогресса).</param>
    public async Task<ClaudeReply> AskAsync(string prompt, string? sessionId, string? model,
        Action<ToolCall>? onToolCall, CancellationToken ct) {
        await _gate.WaitAsync(ct);
        try {
            return await RunAsync(prompt, sessionId, model, onToolCall, ct);
        } finally {
            _gate.Release();
        }
    }

    private async Task<ClaudeReply> RunAsync(string prompt, string? sessionId, string? model,
        Action<ToolCall>? onToolCall, CancellationToken ct) {
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

        // claude и MCP-сервер наследуют окружение бота. Убираем из него настройки бота
        // (в т.ч. токен, если он задан через Bot__Token), чтобы они не попали к агенту.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("Bot__", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);

        // Аргументы через ArgumentList, а промпт через stdin: никаких проблем с экранированием.
        psi.ArgumentList.Add("-p");
        Add(psi, "--output-format", "stream-json");
        psi.ArgumentList.Add("--verbose"); // без него stream-json в режиме -p не работает
        Add(psi, "--max-turns", _o.MaxTurns.ToString());
        var effectiveModel = string.IsNullOrWhiteSpace(model) ? _o.Model : model;
        if (!string.IsNullOrWhiteSpace(effectiveModel)) Add(psi, "--model", effectiveModel);
        if (_o.Restricted) psi.ArgumentList.Add("--restricted");
        // Всегда передаём --tools: без него агент получил бы весь набор встроенных инструментов.
        var tools = _o.Tools is { Length: > 0 } t ? t : ClaudeOptions.DefaultTools;
        Add(psi, "--tools", string.Join(",", tools.Distinct(StringComparer.OrdinalIgnoreCase)));
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

        var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));

        ClaudeReply? result = null;
        string? initModel = null;
        try {
            while (await proc.StandardOutput.ReadLineAsync(timeout.Token) is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                result = HandleEvent(line, sessionId, onToolCall, ref initModel) ?? result;
            }
            await proc.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            proc.Kill(entireProcessTree: true);
            ct.ThrowIfCancellationRequested();
            return new ClaudeReply($"Превышено время ожидания ({_o.TimeoutSeconds} с).", sessionId, true, null);
        }

        var stderr = await stderrTask;
        if (result is null) {
            log.LogError("claude exited with {Code} without result: {Stderr}", proc.ExitCode, stderr);
            return new ClaudeReply($"claude завершился с кодом {proc.ExitCode}: {stderr.Trim()}", sessionId, true, null);
        }

        result = result with { Model = result.Model ?? initModel };
        usage.Record(result);
        log.LogInformation("claude done: session={Session} model={Model} error={Error} turns={Turns} {Ms} ms cost=${Cost}",
            result.SessionId, result.Model, result.IsError, result.NumTurns, result.DurationMs, result.CostUsd);
        return result;
    }

    // Разбирает одно событие stream-json. Возвращает итог, если это событие "result".
    private ClaudeReply? HandleEvent(string line, string? sessionId, Action<ToolCall>? onToolCall, ref string? initModel) {
        JsonDocument doc;
        try {
            doc = JsonDocument.Parse(line);
        } catch (JsonException) {
            log.LogDebug("Не JSON в выводе claude: {Line}", line);
            return null;
        }

        using (doc) {
            var e = doc.RootElement;
            switch (Str(e, "type")) {
                case "system" when Str(e, "subtype") == "init":
                    initModel = Str(e, "model");
                    return null;

                case "assistant" when onToolCall is not null
                                      && e.TryGetProperty("message", out var msg)
                                      && msg.TryGetProperty("content", out var content)
                                      && content.ValueKind == JsonValueKind.Array:
                    foreach (var c in content.EnumerateArray()) {
                        if (Str(c, "type") != "tool_use") continue;
                        var input = c.TryGetProperty("input", out var i) ? i.Clone() : default;
                        try {
                            onToolCall(new ToolCall(Str(c, "name") ?? "?", input));
                        } catch (Exception ex) {
                            log.LogWarning(ex, "Ошибка в обработчике прогресса");
                        }
                    }
                    return null;

                case "rate_limit_event" when e.TryGetProperty("rate_limit_info", out var info):
                    usage.SetRateLimit(info);
                    return null;

                case "result":
                    var u = e.TryGetProperty("usage", out var uu) ? uu : default;
                    return new ClaudeReply(
                        Text: Str(e, "result") ?? "",
                        SessionId: Str(e, "session_id") ?? sessionId,
                        IsError: e.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True,
                        CostUsd: e.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number ? cost.GetDecimal() : null,
                        Model: e.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object
                            ? mu.EnumerateObject().Select(p => p.Name).FirstOrDefault()
                            : null,
                        DurationMs: Int(e, "duration_ms"),
                        NumTurns: Int(e, "num_turns"),
                        InputTokens: Long(u, "input_tokens") + Long(u, "cache_read_input_tokens") + Long(u, "cache_creation_input_tokens"),
                        OutputTokens: Long(u, "output_tokens"));

                default:
                    return null;
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : 0;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;

    // SystemPrompt + файл инструкций -> один файл для --append-system-prompt-file.
    // Через файл, а не аргументом: многострочный текст ломается при передаче через claude.cmd.
    // Читаем при каждом запуске, чтобы правки инструкций подхватывались без перезапуска бота.
    private string? WriteSystemPromptFile() {
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

    private static void Add(ProcessStartInfo psi, string name, string value) {
        psi.ArgumentList.Add(name);
        psi.ArgumentList.Add(value);
    }
}
