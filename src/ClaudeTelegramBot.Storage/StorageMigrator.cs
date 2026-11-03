using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;

namespace ClaudeTelegramBot.Storage;

// Перенос всех данных из одного хранилища в другое (любые провайдеры). Целевое должно быть пустым.
// После переноса данные перечитываются из целевого и сверяются по количеству.
public static class StorageMigrator {
    public static StorageSnapshot Copy(IStorage from, IStorage to) {
        from.EnsureCreated();
        to.EnsureCreated();
        if (!to.IsEmpty())
            throw new InvalidOperationException($"Хранилище {to.Provider} не пустое: перенос возможен только в пустое.");

        var snapshot = from.Export();
        to.Import(snapshot);

        // Сверка по количеству и по id: id переносятся как есть.
        var copied = to.Export();
        if (copied.RecordCount != snapshot.RecordCount || !Ids(copied).SetEquals(Ids(snapshot)))
            throw new InvalidOperationException($"После переноса данные не совпадают. Было: {snapshot}. Стало: {copied}.");
        return snapshot;
    }

    private static HashSet<ShortGuid> Ids(StorageSnapshot s) => [
        .. s.Notes.Select(e => e.Id), .. s.Tasks.Select(e => e.Id), .. s.Sessions.Select(e => e.Id),
        .. s.ChatSettings.Select(e => e.Id), .. s.Usage.Select(e => e.Id), .. s.AllowedUsers.Select(e => e.Id),
    ];
}
