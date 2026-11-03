using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ClaudeTelegramBot.Storage.Json;

// Список записей одного вида в файле. Каждое чтение и чтение-изменение-запись — под
// межпроцессным мьютексом файла, поэтому бот и MCP-сервер видят согласованные данные.
internal interface IJsonCollection<T> {
    TResult Read<TResult>(Func<List<T>, TResult> query);
    TResult Update<TResult>(Func<List<T>, TResult> change);
}

// Файл с одним JSON-документом.
internal sealed class JsonFile<T>(string path) where T : new() {
    public string Path => path;

    public TResult Read<TResult>(Func<T, TResult> query) => JsonFile.Locked(path, () => query(Load()));

    public TResult Update<TResult>(Func<T, TResult> change) => JsonFile.Locked(path, () => {
        var data = Load();
        var result = change(data);
        JsonFile.WriteAtomic(path, JsonSerializer.Serialize(data, JsonFile.Indented));
        return result;
    });

    private T Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonFile.Indented) ?? new()
        : new();
}

// Записи JSON-массивом в одном файле (заметки, задачи, пользователи...).
internal sealed class JsonArrayFile<T>(string path) : IJsonCollection<T> {
    private readonly JsonFile<List<T>> _file = new(path);

    public TResult Read<TResult>(Func<List<T>, TResult> query) => _file.Read(query);
    public TResult Update<TResult>(Func<List<T>, TResult> change) => _file.Update(change);
}

// Записи по одной на строку (JSON Lines): новую можно дописать в конец, не переписывая файл.
// Битые строки пропускаются.
internal sealed class JsonLinesFile<T>(string path) : IJsonCollection<T> {
    public TResult Read<TResult>(Func<List<T>, TResult> query) => JsonFile.Locked(path, () => query(Load()));

    public TResult Update<TResult>(Func<List<T>, TResult> change) => JsonFile.Locked(path, () => {
        var items = Load();
        var result = change(items);
        JsonFile.WriteAtomic(path, string.Concat(items.Select(i => JsonSerializer.Serialize(i, JsonFile.Compact) + "\n")));
        return result;
    });

    public void Append(T item) => JsonFile.Locked(path, () => {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(item, JsonFile.Compact) + "\n", new UTF8Encoding(false));
        return 0;
    });

    private List<T> Load() {
        var list = new List<T>();
        if (!File.Exists(path)) return list;
        foreach (var line in File.ReadLines(path)) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try {
                if (JsonSerializer.Deserialize<T>(line, JsonFile.Compact) is { } item) list.Add(item);
            } catch (JsonException) {
                // битая строка — пропускаем
            }
        }
        return list;
    }
}

internal static class JsonFile {
    public static readonly JsonSerializerOptions Indented = new() {
        WriteIndented = true,
        // Relaxed-экранирование, чтобы кириллица в файлах оставалась читаемой.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Compact = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Именованный мьютекс на каждый файл (имя — хеш полного пути). Повторный захват тем же потоком
    // разрешён, поэтому вложенные блокировки (импорт, обновление схемы) не зависают.
    public static T Locked<T>(string path, Func<T> action) {
        var full = Path.GetFullPath(path).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
        using var mutex = new Mutex(false, $"Local\\BotStorage_{hash}");
        try {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new TimeoutException($"Файл {Path.GetFileName(path)} занят другим процессом.");
        } catch (AbandonedMutexException) {
            // Другой процесс упал, держа мьютекс. Владение перешло к нам, файл целый
            // (запись идёт через временный файл), можно продолжать.
        }

        try {
            return action();
        } finally {
            mutex.ReleaseMutex();
        }
    }

    public static void WriteAtomic(string path, string content) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
