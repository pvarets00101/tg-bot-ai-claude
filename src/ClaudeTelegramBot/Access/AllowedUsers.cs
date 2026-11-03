using System.Collections.Frozen;
using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;

namespace ClaudeTelegramBot.Access;

// Кому можно писать боту (Telegram user id). Список лежит в хранилище (IAllowedUserRepository),
// загружается один раз при старте (Load) и дальше читается из памяти: проверка доступа на каждое
// сообщение и кнопку не ходит в базу. FrozenSet неизменяем, поэтому читать его можно из любых
// потоков без блокировок.
//
// Список меняется командой `ClaudeTelegramBot users add|remove <id>`; бот подхватит его после перезапуска.
public sealed class AllowedUsers(IAllowedUserRepository repo, ILogger<AllowedUsers> log) {
    private volatile FrozenSet<long> _ids = FrozenSet<long>.Empty;

    // Они же получатели напоминаний: личный чат с ботом = user id.
    public IReadOnlyCollection<long> Ids => _ids;

    public bool Contains(long userId) => _ids.Contains(userId);

    public void Load() {
        _ids = repo.UserIds().ToFrozenSet();
        if (_ids.Count == 0)
            log.LogWarning("Список пользователей пуст: доступ закрыт для всех. Добавьте себя: ClaudeTelegramBot users add <user id>");
        else
            log.LogInformation("Пользователей с доступом: {Count}", _ids.Count);
    }
}
