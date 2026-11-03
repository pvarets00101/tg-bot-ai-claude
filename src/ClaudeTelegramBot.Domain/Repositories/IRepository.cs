using ClaudeTelegramBot.Domain.Models;

namespace ClaudeTelegramBot.Domain.Repositories;

// Общие операции над сущностями одного вида. Каждый провайдер реализует их в своём базовом
// репозитории (PostgresRepository<T>, SqliteRepository<T>, JsonRepository<T>) виртуальными
// методами; репозиторий конкретной сущности наследует базовый, при необходимости переопределяет
// эти методы и добавляет свои методы-намерения (ITaskRepository.TakeDueReminders и т.п.).
//
// Мягко удалённые записи (Entity.SoftDeleted) не видны, пока не попросить includeDeleted.
public interface IRepository<T> where T : Entity {
    T? Get(ShortGuid id, bool includeDeleted = false);

    T? GetByExternalId(string externalId, bool includeDeleted = false);

    // В порядке, естественном для сущности (у задач — по сроку, у статистики — по времени).
    IReadOnlyList<T> GetAll(bool includeDeleted = false);

    // Пустой Id заменяется новым. Занятый Id или ExternalId — InvalidOperationException.
    T Add(T entity);

    // Сохраняет все поля, кроме Id и отметок удаления. false — записи нет или она удалена.
    bool Update(T entity);

    // Мягкое удаление. false — записи нет или она уже удалена.
    bool Delete(ShortGuid id);

    // Снимает отметку удаления. false — записи нет или она не удалена.
    bool Restore(ShortGuid id);
}
