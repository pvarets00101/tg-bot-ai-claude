using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace ClaudeTelegramBot;

public sealed class BotWorker(
    ITelegramBotClient bot,
    ClaudeRunner claude,
    SessionStore sessions,
    ChatSettingsStore chatSettings,
    UsageStore usage,
    ReminderService reminders,
    AttachmentService attachments,
    IOptions<BotOptions> options,
    IOptions<ClaudeOptions> claudeOptions,
    ILogger<BotWorker> log) : BackgroundService {
    private const int PlainMaxLength = 4000;

    // Альбом приходит отдельными сообщениями с общим MediaGroupId. Ждём остальные части столько,
    // а потом отправляем агенту всё одним запросом.
    private static readonly TimeSpan AlbumWait = TimeSpan.FromSeconds(1.5);

    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    private static readonly BotCommand[] Commands = [
        new() { Command = "new", Description = "Начать новый диалог" },
        new() { Command = "tasks", Description = "Открытые задачи" },
        new() { Command = "model", Description = "Выбрать модель для этого чата" },
        new() { Command = "usage", Description = "Лимиты подписки и статистика" },
        new() { Command = "help", Description = "Что умеет бот" },
    ];

    private readonly HashSet<long> _allowed = [.. options.Value.AllowedUserIds];
    private readonly ClaudeOptions _claude = claudeOptions.Value;
    private readonly string[] _models = claudeOptions.Value.AllowedModels is { Length: > 0 } m
        ? m
        : ClaudeOptions.DefaultAllowedModels;

    // Сообщения одного чата обрабатываются строго по очереди, разные чаты параллельно.
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _chatLocks = new();
    private readonly ConcurrentDictionary<string, List<Message>> _albums = new();

    protected override async Task ExecuteAsync(CancellationToken ct) {
        var me = await bot.GetMe(ct);
        log.LogInformation("Bot @{Username} started, agent workspace: {Dir}", me.Username, claude.WorkingDirectory);

        try {
            await bot.SetMyCommands(Commands, cancellationToken: ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            log.LogWarning(ex, "Не удалось зарегистрировать меню команд");
        }

        var offset = 0;
        while (!ct.IsCancellationRequested) {
            Update[] updates;
            try {
                updates = await bot.GetUpdates(offset, timeout: 30,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], cancellationToken: ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch (Exception ex) {
                log.LogWarning(ex, "GetUpdates failed");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            foreach (var update in updates) {
                offset = update.Id + 1;
                if (update.Message is { Text: { } text, From: { } from } msg)
                    _ = HandleAsync(msg.Chat.Id, from.Id, text, ct);
                else if (update.Message is { From: not null } media)
                    _ = HandleMediaAsync(media, ct);
                else if (update.CallbackQuery is { } query)
                    _ = HandleCallbackAsync(query, ct);
            }
        }
    }

    // --- Текст и команды ---

    private async Task HandleAsync(long chatId, long userId, string text, CancellationToken ct) {
        try {
            if (!_allowed.Contains(userId)) {
                await Send(chatId, $"Нет доступа. Ваш user id: {userId}", ct);
                return;
            }

            if (await TryHandleCommandAsync(chatId, text.Trim(), ct)) return;

            await InChatQueue(chatId, () => AskClaudeAsync(chatId, text, ct), ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            log.LogError(ex, "Failed to handle message in chat {ChatId}", chatId);
            await Send(chatId, "Что-то пошло не так, подробности в логах.", ct);
        }
    }

    private async Task<bool> TryHandleCommandAsync(long chatId, string text, CancellationToken ct) {
        if (!text.StartsWith('/')) return false;

        // "/model@my_bot haiku" -> ("/model", "haiku")
        var space = text.IndexOf(' ');
        var command = (space < 0 ? text : text[..space]).Split('@')[0].ToLowerInvariant();
        var arg = space < 0 ? "" : text[(space + 1)..].Trim();

        switch (command) {
            case "/start":
            case "/help":
                await SendHtml(chatId, HelpText(), null, ct);
                return true;
            case "/new":
                sessions.Reset(chatId);
                await Send(chatId, "Контекст сброшен, начинаем новый диалог.", ct);
                return true;
            case "/tasks":
                await Send(chatId, reminders.FormatOpenTasks(), ct);
                return true;
            case "/usage":
                await SendHtml(chatId, UsageReport.Build(usage, ModelLabel(chatSettings.GetModel(chatId))), null, ct);
                return true;
            case "/model":
                await HandleModelCommandAsync(chatId, arg, ct);
                return true;
            default:
                return false; // неизвестную команду отдаём агенту как обычный текст
        }
    }

    private async Task HandleModelCommandAsync(long chatId, string arg, CancellationToken ct) {
        if (arg.Length == 0) {
            var current = chatSettings.GetModel(chatId);
            await SendHtml(chatId,
                $"Модель этого чата: <b>{TelegramFormatter.Escape(ModelLabel(current))}</b>\nВыберите другую:",
                ModelButtons(current), ct);
            return;
        }

        var result = SetModel(chatId, arg);
        await Send(chatId, result ?? $"Не знаю модель «{arg}». Доступны: {string.Join(", ", _models)}, default.", ct);
    }

    // Возвращает текст подтверждения или null, если модель не разрешена.
    private string? SetModel(long chatId, string model) {
        model = model.Trim().ToLowerInvariant();
        if (model is "default" or "по умолчанию") {
            chatSettings.SetModel(chatId, null);
            return $"Модель: {ModelLabel(null)}";
        }
        if (!_models.Contains(model, StringComparer.OrdinalIgnoreCase)) return null;
        chatSettings.SetModel(chatId, model);
        return $"Модель: {model}";
    }

    private InlineKeyboardMarkup ModelButtons(string? current) {
        var buttons = _models
            .Select(m => InlineKeyboardButton.WithCallbackData((m == current ? "✓ " : "") + m, $"model:{m}"))
            .Append(InlineKeyboardButton.WithCallbackData((current is null ? "✓ " : "") + "по умолчанию", "model:default"));
        return new InlineKeyboardMarkup(buttons.Chunk(2).Select(row => row.ToArray()));
    }

    private string ModelLabel(string? chatModel) =>
        chatModel ?? (string.IsNullOrWhiteSpace(_claude.Model) ? "по умолчанию" : $"{_claude.Model} (по умолчанию)");

    private static string HelpText() =>
        "Я ваш агент на Claude. Пишите задачу текстом, присылайте фото, скриншоты и файлы.\n\n" +
        "Умею искать в интернете, вести заметки и задачи с напоминаниями, смотреть ваши репозитории.\n\n" +
        "/new — начать новый диалог\n" +
        "/tasks — открытые задачи\n" +
        "/model — модель для этого чата (haiku дешевле по лимитам)\n" +
        "/usage — лимиты подписки и статистика";

    // --- Фото, документы и всё остальное, что не текст ---

    private async Task HandleMediaAsync(Message msg, CancellationToken ct) {
        var chatId = msg.Chat.Id;
        try {
            if (!_allowed.Contains(msg.From!.Id)) {
                await Send(chatId, $"Нет доступа. Ваш user id: {msg.From.Id}", ct);
                return;
            }

            if (!AttachmentService.HasAttachment(msg) || !attachments.Enabled) {
                await Send(chatId, "Пока понимаю только текст, фото и файлы.", ct);
                return;
            }

            var parts = await CollectAlbumAsync(msg, ct);
            if (parts is null) return; // эту часть альбома обработает первое сообщение

            await InChatQueue(chatId, async () => {
                var files = new List<SavedAttachment>();
                foreach (var part in parts)
                    files.Add(await attachments.DownloadAsync(part, ct));

                // Подпись у альбома обычно только на одном из сообщений.
                var caption = parts.Select(p => p.Caption).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                await AskClaudeAsync(chatId, AttachmentService.BuildPrompt(caption, files), ct);
            }, ct);
        } catch (AttachmentException ex) {
            await Send(chatId, ex.Message, ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            log.LogError(ex, "Failed to handle attachment in chat {ChatId}", chatId);
            await Send(chatId, "Не получилось обработать вложение, подробности в логах.", ct);
        }
    }

    // Одиночное сообщение возвращается сразу. Для альбома первое сообщение ждёт остальные
    // и возвращает их все, остальные получают null.
    private async Task<IReadOnlyList<Message>?> CollectAlbumAsync(Message msg, CancellationToken ct) {
        if (msg.MediaGroupId is not { } groupId) return [msg];

        var key = $"{msg.Chat.Id}:{groupId}";
        var list = _albums.GetOrAdd(key, _ => []);
        bool first;
        lock (list) {
            first = list.Count == 0;
            list.Add(msg);
        }
        if (!first) return null;

        await Task.Delay(AlbumWait, ct);
        _albums.TryRemove(key, out _);
        lock (list) return list.OrderBy(m => m.Id).ToList();
    }

    private async Task InChatQueue(long chatId, Func<Task> action, CancellationToken ct) {
        var chatLock = _chatLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1));
        await chatLock.WaitAsync(ct);
        try {
            await action();
        } finally {
            chatLock.Release();
        }
    }

    // --- Кнопки ---

    private async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct) {
        try {
            if (!_allowed.Contains(query.From.Id)) {
                await bot.AnswerCallbackQuery(query.Id, "Нет доступа", cancellationToken: ct);
                return;
            }

            if (ReminderService.IsTaskCallback(query.Data)) {
                await reminders.HandleCallbackAsync(query, ct);
            } else if (query.Data?.StartsWith("model:") == true && query.Message is { } msg) {
                var result = SetModel(msg.Chat.Id, query.Data["model:".Length..]) ?? "Модель недоступна";
                await bot.AnswerCallbackQuery(query.Id, result, cancellationToken: ct);
                await bot.EditMessageText(msg.Chat.Id, msg.Id, result, replyMarkup: null, cancellationToken: ct);
            } else {
                await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
            }
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            log.LogError(ex, "Failed to handle callback {Data}", query.Data);
        }
    }

    // --- Запрос к агенту ---

    private async Task AskClaudeAsync(long chatId, string text, CancellationToken ct) {
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = KeepTypingAsync(chatId, typing.Token);

        ClaudeReply reply;
        await using (var progress = new ProgressReporter(bot, chatId, log, ct)) {
            var model = chatSettings.GetModel(chatId);
            var sessionId = sessions.Get(chatId);
            reply = await claude.AskAsync(text, sessionId, model, progress.OnToolCall, ct);

            // Сессия могла протухнуть или удалиться — пробуем один раз с чистого листа.
            if (reply.IsError && sessionId is not null) {
                sessions.Reset(chatId);
                reply = await claude.AskAsync(text, null, model, progress.OnToolCall, ct);
            }
        }
        typing.Cancel();

        if (reply.SessionId is not null && !reply.IsError)
            sessions.Set(chatId, reply.SessionId);

        if (reply.IsError) {
            // Ошибки (лимит, таймаут, падение claude) — простым текстом, как есть.
            await Send(chatId, string.IsNullOrWhiteSpace(reply.Text) ? "Агент вернул ошибку без текста." : reply.Text, ct);
            return;
        }

        await SendMarkdown(chatId, string.IsNullOrWhiteSpace(reply.Text) ? "(пустой ответ)" : reply.Text, ct);

        if (UsageReport.Warning(usage, _claude.LimitWarningPercent) is { } warning)
            await SendHtml(chatId, warning, null, ct);
    }

    private async Task KeepTypingAsync(long chatId, CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        } catch (OperationCanceledException) { }
    }

    // --- Отправка ---

    // Ответ модели: Markdown -> Telegram HTML. Если Telegram не принял разметку куска,
    // этот кусок уходит простым текстом.
    private async Task SendMarkdown(long chatId, string markdown, CancellationToken ct) {
        foreach (var chunk in TelegramFormatter.ToHtmlChunks(markdown)) {
            try {
                await bot.SendMessage(chatId, chunk, parseMode: ParseMode.Html, linkPreviewOptions: NoPreview, cancellationToken: ct);
            } catch (ApiRequestException ex) when (ex.ErrorCode == 400) {
                log.LogWarning("Telegram не принял HTML ({Error}), отправляю текстом", ex.Message);
                foreach (var plain in TelegramFormatter.ToPlainChunks(TelegramFormatter.StripTags(chunk)))
                    await bot.SendMessage(chatId, plain, linkPreviewOptions: NoPreview, cancellationToken: ct);
            }
        }
    }

    private async Task SendHtml(long chatId, string html, InlineKeyboardMarkup? markup, CancellationToken ct) =>
        await bot.SendMessage(chatId, html, parseMode: ParseMode.Html, replyMarkup: markup,
            linkPreviewOptions: NoPreview, cancellationToken: ct);

    private async Task Send(long chatId, string text, CancellationToken ct) {
        for (var i = 0; i < text.Length; i += PlainMaxLength)
            await bot.SendMessage(chatId, text.Substring(i, Math.Min(PlainMaxLength, text.Length - i)), cancellationToken: ct);
    }
}
