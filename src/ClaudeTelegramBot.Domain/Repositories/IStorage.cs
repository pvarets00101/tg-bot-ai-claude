using ClaudeTelegramBot.Domain.Models;

namespace ClaudeTelegramBot.Domain.Repositories;

// Репозитории — по одному на сущность. Общие операции (Get/Add/Update/Delete/Restore) — в
// IRepository<T> и базовом репозитории провайдера; операции, которые должны быть атомарными даже
// при двух процессах (бот и MCP-сервер), — методы-намерения конкретного репозитория, например
// «забрать напоминания и пометить отправленными». Каждый провайдер делает их своими средствами:
// UPDATE ... RETURNING в PostgreSQL и SQLite, межпроцессный мьютекс для файлов.
//
// Все времена возвращаются в локальном поясе; сравниваются как моменты времени.
// Одинаковое поведение всех провайдеров проверяют контрактные тесты (ClaudeTelegramBot.Storage.Tests).

// Одно хранилище (провайдер) со всеми репозиториями. Создаётся StorageFactory по конфигу.
public interface IStorage : IDisposable {
    StorageProvider Provider { get; }

    IAllowedUserRepository AllowedUsers { get; }
    INoteRepository Notes { get; }
    ITaskRepository Tasks { get; }
    ISessionRepository Sessions { get; }
    IChatSettingsRepository ChatSettings { get; }
    IUsageRepository Usage { get; }

    // Создаёт таблицы / папку, если их нет, и обновляет схему старой версии (с переносом данных).
    // Безопасно вызывать из нескольких процессов.
    void EnsureCreated();

    // Нет ни одной записи, в том числе мягко удалённой (пользователи, заметки, задачи, сессии,
    // настройки, статистика, лимиты).
    bool IsEmpty();

    StorageSnapshot Export();

    // Загружает снимок в пустое хранилище, сохраняя id. Непустое — InvalidOperationException.
    void Import(StorageSnapshot snapshot);
}
