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
    IOptions<BotOptions> options,
    ILogger<BotWorker> log) : BackgroundService
{
    private const int TelegramMaxLength = 4000;

    private readonly HashSet<long> _allowed = [.. options.Value.AllowedUserIds];

    // Сообщения одного чата обрабатываются строго по очереди, разные чаты параллельно.
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _chatLocks = new();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var me = await bot.GetMe(ct);
        log.LogInformation("Bot @{Username} started, agent workspace: {Dir}", me.Username, claude.WorkingDirectory);

        var offset = 0;
        while (!ct.IsCancellationRequested)
        {
            Update[] updates;
            try
            {
                updates = await bot.GetUpdates(offset, timeout: 30, allowedUpdates: [UpdateType.Message], cancellationToken: ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "GetUpdates failed");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            foreach (var update in updates)
            {
                offset = update.Id + 1;
                if (update.Message is { Text: { } text, From: { } from } msg)
                    _ = HandleAsync(msg.Chat.Id, from.Id, text, ct);
            }
        }
    }

    private async Task HandleAsync(long chatId, long userId, string text, CancellationToken ct)
    {
        try
        {
            if (!_allowed.Contains(userId))
            {
                await Send(chatId, $"Нет доступа. Ваш user id: {userId}", ct);
                return;
            }

            switch (text.Trim())
            {
                case "/start":
                    await Send(chatId, "Привет! Пишите задачу. /new — начать новый диалог.", ct);
                    return;
                case "/new":
                    sessions.Reset(chatId);
                    await Send(chatId, "Контекст сброшен, начинаем новый диалог.", ct);
                    return;
            }

            var chatLock = _chatLocks.GetOrAdd(chatId, _ => new SemaphoreSlim(1));
            await chatLock.WaitAsync(ct);
            try
            {
                await AskClaudeAsync(chatId, text, ct);
            }
            finally
            {
                chatLock.Release();
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Failed to handle message in chat {ChatId}", chatId);
            await Send(chatId, "Что-то пошло не так, подробности в логах.", ct);
        }
    }

    private async Task AskClaudeAsync(long chatId, string text, CancellationToken ct)
    {
        using var typing = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = KeepTypingAsync(chatId, typing.Token);

        var sessionId = sessions.Get(chatId);
        var reply = await claude.AskAsync(text, sessionId, ct);

        // Сессия могла протухнуть или удалиться — пробуем один раз с чистого листа.
        if (reply.IsError && sessionId is not null)
        {
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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task Send(long chatId, string text, CancellationToken ct)
    {
        // Отправляем простым текстом: Markdown от модели часто ломает парсер Telegram.
        for (var i = 0; i < text.Length; i += TelegramMaxLength)
            await bot.SendMessage(chatId, text.Substring(i, Math.Min(TelegramMaxLength, text.Length - i)), cancellationToken: ct);
    }
}
