using System.Text;
using System.Text.Json;
using Telegram.Bot;

namespace ClaudeTelegramBot;

// Одно сообщение «⏳ Работаю…» со списком шагов агента, которое обновляется по ходу запроса
// и удаляется, когда приходит ответ. Появляется при первом вызове инструмента или если ответ
// думается дольше ShowAfter. Правки не чаще EditInterval — у Telegram лимиты на редактирование.
public sealed class ProgressReporter : IAsyncDisposable {
    private static readonly TimeSpan ShowAfter = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan EditInterval = TimeSpan.FromSeconds(1.5);
    private const int MaxSteps = 8;

    private readonly ITelegramBotClient _bot;
    private readonly long _chatId;
    private readonly ILogger _log;
    private readonly List<string> _steps = [];
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private int? _messageId;
    private string? _lastText;

    public ProgressReporter(ITelegramBotClient bot, long chatId, ILogger log, CancellationToken ct) {
        _bot = bot;
        _chatId = chatId;
        _log = log;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = LoopAsync(_cts.Token);
    }

    public void OnToolCall(ToolCall call) {
        lock (_lock) _steps.Add(Describe(call));
    }

    private async Task LoopAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                await Task.Delay(EditInterval, ct);
                await RenderAsync(ct);
            }
        } catch (OperationCanceledException) {
            // запрос завершён
        }
    }

    private async Task RenderAsync(CancellationToken _) {
        string text;
        lock (_lock) {
            var elapsed = DateTime.UtcNow - _startedAt;
            if (_steps.Count == 0 && elapsed < ShowAfter) return;
            text = Render(_steps, elapsed);
        }
        if (text == _lastText) return;

        // Сами вызовы API не отменяем: если отменить отправку на полпути, сообщение может
        // появиться в чате, а его id мы не узнаем и не сможем удалить.
        try {
            if (_messageId is null) {
                var msg = await _bot.SendMessage(_chatId, text, cancellationToken: CancellationToken.None);
                _messageId = msg.Id;
            } else {
                await _bot.EditMessageText(_chatId, _messageId.Value, text, cancellationToken: CancellationToken.None);
            }
            _lastText = text;
        } catch (Exception ex) {
            _log.LogDebug(ex, "Не удалось обновить прогресс");
        }
    }

    private static string Render(List<string> steps, TimeSpan elapsed) {
        var sb = new StringBuilder($"⏳ Работаю… {elapsed.TotalSeconds:0} с");
        var skip = Math.Max(0, steps.Count - MaxSteps);
        if (skip > 0) sb.Append($"\n… ещё {skip} шаг(ов)");
        for (var i = skip; i < steps.Count; i++)
            sb.Append('\n').Append(i == steps.Count - 1 ? "▸ " : "✓ ").Append(steps[i]);
        return sb.ToString();
    }

    // Человеческое описание вызова инструмента.
    private static string Describe(ToolCall call) {
        var name = call.Name.StartsWith("mcp__personal__", StringComparison.Ordinal)
            ? call.Name["mcp__personal__".Length..]
            : call.Name;

        return name switch {
            "WebSearch" => $"🔍 Ищу: {Arg(call, "query")}",
            "Read" => $"📄 Читаю {Path.GetFileName(Arg(call, "file_path"))}",
            "add_note" => "📝 Сохраняю заметку",
            "search_notes" => "🔎 Ищу в заметках",
            "delete_note" => "🗑 Удаляю заметку",
            "add_task" => $"➕ Добавляю задачу: {Arg(call, "title")}",
            "reschedule_task" => "🕒 Переношу задачу",
            "list_tasks" => "📋 Смотрю задачи",
            "complete_task" => "✅ Отмечаю задачу",
            "delete_task" => "🗑 Удаляю задачу",
            "list_repos" => "📁 Смотрю репозитории",
            "repo_status" => $"📁 Смотрю {Arg(call, "name")}",
            _ => $"🛠 {name}",
        };
    }

    private static string Arg(ToolCall call, string name) {
        if (call.Input.ValueKind != JsonValueKind.Object || !call.Input.TryGetProperty(name, out var v)) return "";
        var s = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
        return s.Length <= 60 ? s : s[..57] + "…";
    }

    // Останавливает обновления и удаляет сообщение о прогрессе.
    public async ValueTask DisposeAsync() {
        await _cts.CancelAsync();
        await _loop;
        _cts.Dispose();
        if (_messageId is { } id) {
            try {
                await _bot.DeleteMessage(_chatId, id);
            } catch (Exception ex) {
                _log.LogDebug(ex, "Не удалось удалить сообщение прогресса");
            }
        }
    }
}
