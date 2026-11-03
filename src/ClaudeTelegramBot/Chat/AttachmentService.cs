using System.Text;
using ClaudeTelegramBot.Claude;
using ClaudeTelegramBot.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace ClaudeTelegramBot.Chat;

public sealed record SavedAttachment(string RelativePath, string Kind, long Size);

// Пользователь не увидит стек, только этот текст.
public sealed class AttachmentException(string userMessage) : Exception(userMessage);

// Скачивает фото и документы из Telegram в папку агента и собирает для него промпт.
public sealed class AttachmentService(
    ITelegramBotClient bot,
    ClaudeRunner claude,
    IOptions<AttachmentOptions> options,
    ILogger<AttachmentService> log) {
    private const string InboxFolder = "inbox";
    private const string DefaultInstruction =
        "Посмотри, что я прислал, и коротко скажи, что там. Если это ошибка, лог или код — " +
        "объясни, в чём проблема и как исправить.";

    private readonly AttachmentOptions _o = options.Value;

    public bool Enabled => _o.Enabled;

    public static bool HasAttachment(Message m) => m.Photo is { Length: > 0 } || m.Document is not null;

    public async Task<SavedAttachment> DownloadAsync(Message m, CancellationToken ct) {
        string fileId, fileName, kind;
        long? size;

        if (m.Photo is { Length: > 0 } photos) {
            var best = photos[^1]; // последний — самый крупный вариант
            (fileId, size, kind) = (best.FileId, best.FileSize, "фото");
            fileName = $"photo_{m.Id}.jpg";
        } else if (m.Document is { } doc) {
            (fileId, size, kind) = (doc.FileId, doc.FileSize, doc.MimeType ?? "файл");
            fileName = doc.FileName ?? $"file_{m.Id}";
        } else {
            throw new AttachmentException("В сообщении нет фото или файла.");
        }

        if (size > _o.MaxFileSizeMb * 1024L * 1024)
            throw new AttachmentException($"Файл больше {_o.MaxFileSizeMb} МБ, Telegram не даёт ботам его скачать.");

        var chatDir = Path.Combine(claude.WorkingDirectory, InboxFolder, m.Chat.Id.ToString());
        Directory.CreateDirectory(chatDir);
        CleanupOldFiles();

        var name = UniqueName(chatDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{Sanitize(fileName)}");
        var fullPath = Path.Combine(chatDir, name);

        try {
            await using var stream = File.Create(fullPath);
            await bot.GetInfoAndDownloadFile(fileId, stream, ct);
        } catch {
            File.Delete(fullPath);
            throw;
        }

        var actualSize = new FileInfo(fullPath).Length;
        log.LogInformation("Сохранён файл {Path} ({Size} байт)", fullPath, actualSize);
        return new SavedAttachment($"{InboxFolder}/{m.Chat.Id}/{name}", kind, actualSize);
    }

    public static string BuildPrompt(string? caption, IReadOnlyList<SavedAttachment> files) {
        var sb = new StringBuilder(string.IsNullOrWhiteSpace(caption) ? DefaultInstruction : caption.Trim());
        sb.Append("\n\n[Вложения пользователя — прочитай их инструментом Read, пути относительно текущей папки:]");
        foreach (var f in files)
            sb.Append($"\n- {f.RelativePath} ({f.Kind}, {FormatSize(f.Size)})");
        return sb.ToString();
    }

    private void CleanupOldFiles() {
        var inbox = Path.Combine(claude.WorkingDirectory, InboxFolder);
        var threshold = DateTime.Now.AddDays(-Math.Max(1, _o.RetentionDays));
        try {
            foreach (var file in Directory.EnumerateFiles(inbox, "*", SearchOption.AllDirectories))
                if (File.GetLastWriteTime(file) < threshold)
                    File.Delete(file);
        } catch (Exception ex) {
            log.LogWarning(ex, "Не удалось почистить {Inbox}", inbox);
        }
    }

    private static string Sanitize(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length == 0) clean = "file";

        // Ограничиваем длину, сохраняя расширение.
        const int max = 80;
        if (clean.Length > max) {
            var ext = Path.GetExtension(clean);
            if (ext.Length > 10) ext = "";
            clean = clean[..(max - ext.Length)] + ext;
        }
        return clean;
    }

    private static string UniqueName(string dir, string name) {
        var candidate = name;
        for (var i = 2; File.Exists(Path.Combine(dir, candidate)); i++)
            candidate = $"{Path.GetFileNameWithoutExtension(name)}_{i}{Path.GetExtension(name)}";
        return candidate;
    }

    private static string FormatSize(long bytes) => bytes switch {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} МБ",
    };
}
