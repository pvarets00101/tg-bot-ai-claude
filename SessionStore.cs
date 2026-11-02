using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeTelegramBot;

// chatId -> session_id Claude Code. Хранится в JSON-файле, чтобы диалоги переживали перезапуск.
public sealed class SessionStore
{
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "sessions.json");
    private readonly ConcurrentDictionary<long, string> _sessions;
    private readonly Lock _fileLock = new();

    public SessionStore()
    {
        _sessions = File.Exists(_path)
            ? new(JsonSerializer.Deserialize<Dictionary<long, string>>(File.ReadAllText(_path))!)
            : new();
    }

    public string? Get(long chatId) => _sessions.GetValueOrDefault(chatId);

    public void Set(long chatId, string sessionId)
    {
        _sessions[chatId] = sessionId;
        Save();
    }

    public void Reset(long chatId)
    {
        _sessions.TryRemove(chatId, out _);
        Save();
    }

    private void Save()
    {
        lock (_fileLock)
            File.WriteAllText(_path, JsonSerializer.Serialize(_sessions));
    }
}
