using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;
using ClaudeTelegramBot.Storage.Json;
using ClaudeTelegramBot.Storage.Postgres;
using ClaudeTelegramBot.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeTelegramBot.Storage;

// Какое хранилище использовать — секция Storage в appsettings.json.
// Строки подключения — секция ConnectionStrings (Postgres, Sqlite); пути — относительно папки программы.
public sealed class StorageOptions {
    public const string Section = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.Postgres;

    // Папка с файлами Json-провайдера (personal-data.json, sessions.json и т.д.).
    public string JsonDirectory { get; set; } = ".";

    // ConnectionStrings:Postgres.
    public string? PostgresConnectionString { get; set; }

    // ConnectionStrings:Sqlite. Data Source — путь к файлу базы, относительный — от папки программы.
    public string SqliteConnectionString { get; set; } = "Data Source=bot.db";

    public static StorageOptions FromConfiguration(IConfiguration config) {
        var o = new StorageOptions();
        config.GetSection(Section).Bind(o);
        o.PostgresConnectionString = config.GetConnectionString("Postgres") ?? o.PostgresConnectionString;
        o.SqliteConnectionString = config.GetConnectionString("Sqlite") ?? o.SqliteConnectionString;
        return o;
    }

    public StorageOptions With(StorageProvider provider) => new() {
        Provider = provider,
        JsonDirectory = JsonDirectory,
        PostgresConnectionString = PostgresConnectionString,
        SqliteConnectionString = SqliteConnectionString,
    };

    // Переменные окружения для дочернего процесса (MCP-сервера), из которых FromConfiguration
    // восстановит те же настройки. Только то, что нужно выбранному провайдеру; пути абсолютные.
    public Dictionary<string, string> ToEnvironment(string baseDirectory) {
        var env = new Dictionary<string, string> { ["Storage__Provider"] = Provider.ToString() };
        switch (Provider) {
            case StorageProvider.Json:
                env["Storage__JsonDirectory"] = Path.GetFullPath(JsonDirectory, baseDirectory);
                break;
            case StorageProvider.Sqlite:
                env["ConnectionStrings__Sqlite"] = ResolveSqlite(baseDirectory);
                break;
            case StorageProvider.Postgres:
                env["ConnectionStrings__Postgres"] = RequirePostgres();
                break;
        }
        return env;
    }

    internal string RequirePostgres() => string.IsNullOrWhiteSpace(PostgresConnectionString)
        ? throw new InvalidOperationException(
            "Storage:Provider = Postgres, но не задана строка подключения ConnectionStrings:Postgres.")
        : PostgresConnectionString;

    // Строка SQLite с абсолютным путём к файлу.
    internal string ResolveSqlite(string baseDirectory) {
        var b = new SqliteConnectionStringBuilder(SqliteConnectionString);
        if (string.IsNullOrWhiteSpace(b.DataSource))
            throw new InvalidOperationException("В ConnectionStrings:Sqlite не задан Data Source (путь к файлу базы).");
        b.DataSource = Path.GetFullPath(b.DataSource, baseDirectory);
        return b.ToString();
    }
}

public static class StorageFactory {
    public static IStorage Create(StorageOptions o, string baseDirectory) => o.Provider switch {
        StorageProvider.Json => new JsonStorage(Path.GetFullPath(o.JsonDirectory, baseDirectory)),
        StorageProvider.Sqlite => new SqliteStorage(o.ResolveSqlite(baseDirectory)),
        StorageProvider.Postgres => new PostgresStorage(o.RequirePostgres()),
        _ => throw new ArgumentOutOfRangeException(nameof(o), o.Provider, "Неизвестный провайдер хранилища."),
    };
}

public static class StorageServiceCollectionExtensions {
    // Регистрирует IStorage выбранного провайдера и его репозитории по отдельности:
    // потребители зависят только от нужного им интерфейса.
    public static IServiceCollection AddBotStorage(this IServiceCollection services, IConfiguration config) {
        var options = StorageOptions.FromConfiguration(config);
        services.AddSingleton(options);
        services.AddSingleton(_ => StorageFactory.Create(options, AppContext.BaseDirectory));
        services.AddSingleton(sp => sp.GetRequiredService<IStorage>().AllowedUsers);
        services.AddSingleton(sp => sp.GetRequiredService<IStorage>().Notes);
        services.AddSingleton(sp => sp.GetRequiredService<IStorage>().Tasks);
        services.AddSingleton(sp => sp.GetRequiredService<IStorage>().Sessions);
        services.AddSingleton(sp => sp.GetRequiredService<IStorage>().ChatSettings);
        services.AddSingleton(sp => sp.GetRequiredService<IStorage>().Usage);
        return services;
    }
}
