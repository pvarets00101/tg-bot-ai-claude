using System.Text.Json;

namespace PersonalMcp;

public sealed class Note
{
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public string[] Tags { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public DateOnly? Due { get; set; }
    public bool Done { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class PersonalData
{
    public int NextId { get; set; } = 1;
    public List<Note> Notes { get; set; } = [];
    public List<TaskItem> Tasks { get; set; } = [];
}

// Заметки и задачи в одном JSON-файле. Сервер живёт один вызов `claude -p`,
// поэтому файл читается при каждой операции, а пишется атомарно через временный файл.
public sealed class DataStore(string path)
{
    // Relaxed-экранирование, чтобы кириллица в файле оставалась читаемой.
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly Lock _lock = new();

    public T Read<T>(Func<PersonalData, T> query)
    {
        lock (_lock) return query(Load());
    }

    public T Update<T>(Func<PersonalData, T> change)
    {
        lock (_lock)
        {
            var data = Load();
            var result = change(data);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json));
            File.Move(tmp, path, overwrite: true);
            return result;
        }
    }

    private PersonalData Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<PersonalData>(File.ReadAllText(path)) ?? new()
        : new();
}
