using ClaudeTelegramBot.Domain.Repositories;
using ClaudeTelegramBot.Storage;

namespace ClaudeTelegramBot;

// Служебные команды: ClaudeTelegramBot.exe migrate-storage ... | users ... (см. Program.cs).
internal static class Cli {
    // Копирует все данные из одного хранилища в другое (целевое должно быть пустым) и проверяет результат.
    // Пути и строки подключения — из тех же настроек, что у бота. Бот на время переноса остановите.
    public static int MigrateStorage(string[] args) {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length != 2
            || !Enum.TryParse<StorageProvider>(args[0], ignoreCase: true, out var from)
            || !Enum.TryParse<StorageProvider>(args[1], ignoreCase: true, out var to)
            || from == to) {
            Console.Error.WriteLine("Использование: migrate-storage <откуда> <куда>. Хранилища: Json, Sqlite, Postgres.");
            return 2;
        }

        var options = Options();
        return Run(() => {
            using var source = StorageFactory.Create(options.With(from), AppContext.BaseDirectory);
            using var target = StorageFactory.Create(options.With(to), AppContext.BaseDirectory);
            var snapshot = StorageMigrator.Copy(source, target);
            Console.WriteLine($"Перенесено {from} → {to}: {snapshot}.");
            if (options.Provider != to)
                Console.WriteLine($"Чтобы бот работал с новым хранилищем, поставьте Storage:Provider = {to} в appsettings.json.");
        });
    }

    // Список пользователей с доступом в текущем хранилище (Storage:Provider).
    public static int Users(string[] args) {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        long id = 0;
        var valid = args switch {
            ["list"] => true,
            ["add" or "remove", var s] => long.TryParse(s, out id),
            _ => false,
        };
        if (!valid) {
            Console.Error.WriteLine("Использование: users list | users add <user id> | users remove <user id>.");
            return 2;
        }

        return Run(() => {
            using var storage = StorageFactory.Create(Options(), AppContext.BaseDirectory);
            storage.EnsureCreated();
            var users = storage.AllowedUsers;
            switch (args[0]) {
                case "add":
                    Console.WriteLine(users.Add(id) ? $"Добавлен {id}." : $"{id} уже есть в списке.");
                    break;
                case "remove":
                    Console.WriteLine(users.Remove(id) ? $"Удалён {id}." : $"{id} не было в списке.");
                    break;
            }

            var list = users.UserIds();
            Console.WriteLine(list.Count == 0
                ? $"[{storage.Provider}] Список пуст: доступ закрыт для всех."
                : $"[{storage.Provider}] Доступ есть у: {string.Join(", ", list)}.");
            if (args[0] != "list") Console.WriteLine("Запущенный бот подхватит изменения после перезапуска.");
        });
    }

    private static StorageOptions Options() {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddUserSecrets<Program>(optional: true);
        return StorageOptions.FromConfiguration(builder.Configuration);
    }

    private static int Run(Action action) {
        try {
            action();
            return 0;
        } catch (InvalidOperationException ex) {
            Console.Error.WriteLine($"Не выполнено: {ex.Message}");
            return 1;
        }
    }
}
