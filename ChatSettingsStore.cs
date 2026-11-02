using System.Collections.Concurrent;
using System.Text.Json;

namespace ClaudeTelegramBot;

// Настройки чата, которые пользователь меняет командами. Пока только модель (/model).
public sealed class ChatSettingsStore {
    private readonly string _path = Path.Combine(AppContext.BaseDirectory, "chat-settings.json");
    private readonly ConcurrentDictionary<long, string> _models;
    private readonly Lock _fileLock = new();

    public ChatSettingsStore() {
        _models = File.Exists(_path)
            ? new(JsonSerializer.Deserialize<Dictionary<long, string>>(File.ReadAllText(_path)) ?? [])
            : new();
    }

    // null = модель по умолчанию (Claude:Model).
    public string? GetModel(long chatId) => _models.GetValueOrDefault(chatId);

    public void SetModel(long chatId, string? model) {
        if (string.IsNullOrWhiteSpace(model)) _models.TryRemove(chatId, out _);
        else _models[chatId] = model;

        lock (_fileLock)
            File.WriteAllText(_path, JsonSerializer.Serialize(_models));
    }
}
