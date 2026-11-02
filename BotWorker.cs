using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace ClaudeTelegramBot;

public sealed class BotWorker(
    ITelegramBotClient bot,
    ClaudeRunner claude,
    SessionStore sessions,
    ReminderService reminders,
    AttachmentService attachments,
    IOptions<BotOptions> options,
    ILogger<BotWorker> log) : BackgroundService {
    private const int TelegramMaxLength = 4000;

    // Альбом приходит отдельными сообщениями с общим MediaGroupId. Ждём остальные части столько,
    // а потом отправляем агенту всё одним запросом.
    private static readonly TimeSpan AlbumWait = TimeSpan.FromSeconds(1.5);

    private readonly HashSet<long> _allowed = [.. options.Value.AllowedUserIds];

    // Сообщения одного чата обрабатываются строго по очереди, разные чаты параллельно.
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _chatLocks = new();
    private readonly ConcurrentDictionary<string, List<Message>> _albums = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var me = await bot.GetMe(ct);
        log.LogInformation("Bot @{Username} started, agent workspace: {Dir}", me.Username, claude.WorkingDirectory);

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

    private async Task HandleAsync(long chatId, long userId, string text, CancellationToken ct)
    {
        try {
            if (!_allowed.Contains(userId)) {
                await Send(chatId, $"Нет доступа. Ваш user id: {userId}", ct);
                return;
            }

            switch (text.Trim()) {
                case "/start":
                    await Send(chatId, "Привет! Пишите задачу. /new — начать новый диалог, /tasks — открытые задачи.", ct);
                    return;
                case "/tasks":
                    await Send(chatId, reminders.FormatOpenTasks(), ct);
                    return;
                case "/new":
                    sessions.Reset(chatId);
                    await Send(chatId, "Контекст сброшен, начинаем новый диалог.", ct);
                    return;
            }

            await InChatQueue(chatId, () => AskClaudeAsync(chatId, text, ct), ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            log.LogError(ex, "Failed to handle message in chat {ChatId}", chatId);
            await Send(chatId, "Что-то пошло не так, подробности в логах.", ct);
        }
    }

    // Фото, документы и всё остальное, что не текст.
    private async Task HandleMediaAsync(Message msg, CancellationToken ct)
    {
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
    private async Task<IReadOnlyList<Message>?> CollectAlbumAsync(Message msg, CancellationToken ct)
    {
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

    private async Task InChatQueue(long chatId, Func<Task> action, CancellationToken ct)
    {
        var chatLock = _chatLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1));
        await chatLock.WaitAsync(ct);
        try {
            await action();
        } finally {
            chatLock.Release();
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery query, CancellationToken ct)
    {
        try {
            if (!_allowed.Contains(query.From.Id)) {
                await bot.AnswerCallbackQuery(query.Id, "Нет доступа", cancellationToken: ct);
                return;
            }

            if (ReminderService.IsTaskCallback(query.Data))
                await reminders.HandleCallbackAsync(query, ct);
            else
                await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            log.LogError(ex, "Failed to handle callback {Data}", query.Data);
        }
    }

    private async Task AskClaudeAsync(long chatId, string text, CancellationToken ct)
    {
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = KeepTypingAsync(chatId, typing.Token);

        var sessionId = sessions.Get(chatId);
        var reply = await claude.AskAsync(text, sessionId, ct);

        // Сессия могла протухнуть или удалиться — пробуем один раз с чистого листа.
        if (reply.IsError && sessionId is not null) {
            sessions.Reset(chatId);
            reply = await claude.AskAsync(text, null, ct);
        }

        typing.Cancel();

        if (reply.SessionId is not null && !reply.IsError)
            sessions.Set(chatId, reply.SessionId);

        await Send(chatId, string.IsNullOrWhiteSpace(reply.Text) ? "(пустой ответ)" : reply.Text, ct);
    }

    private async Task KeepTypingAsync(long chatId, CancellationToken ct)
    {
        try {
            while (!ct.IsCancellationRequested) {
                await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        } catch (OperationCanceledException) { }
    }

    private async Task Send(long chatId, string text, CancellationToken ct)
    {
        // Отправляем простым текстом: Markdown от модели часто ломает парсер Telegram.
        for (var i = 0; i < text.Length; i += TelegramMaxLength)
            await bot.SendMessage(chatId, text.Substring(i, Math.Min(TelegramMaxLength, text.Length - i)), cancellationToken: ct);
    }
}
