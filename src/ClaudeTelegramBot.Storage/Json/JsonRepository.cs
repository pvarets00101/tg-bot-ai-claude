using ClaudeTelegramBot.Domain.Models;
using ClaudeTelegramBot.Domain.Repositories;

namespace ClaudeTelegramBot.Storage.Json;

// Базовый репозиторий Json-провайдера: общие операции IRepository<T> над списком записей в файле.
//
// Наследник задаёт порядок (Sort) и получает Get/GetAll/Add/Update/Delete/Restore готовыми;
// любой из них можно переопределить (так журнал запросов дописывает Add в конец файла, а не
// переписывает его). Методы-намерения пишутся через Items.Read / Items.Update: всё, что внутри
// Update, выполняется под мьютексом файла и потому атомарно для бота и MCP-сервера.
internal abstract class JsonRepository<T>(IJsonCollection<T> items) : IRepository<T> where T : Entity {
    protected IJsonCollection<T> Items { get; } = items;

    // Порядок GetAll и выгрузки — такой же, как ORDER BY у SQL-провайдеров.
    protected virtual IEnumerable<T> Sort(IEnumerable<T> items) => items.OrderBy(e => e.Id);

    // --- IRepository<T> ---

    public virtual T? Get(ShortGuid id, bool includeDeleted = false) =>
        Items.Read(l => l.FirstOrDefault(e => e.Id == id && Visible(e, includeDeleted)));

    public virtual T? GetByExternalId(string externalId, bool includeDeleted = false) =>
        Items.Read(l => l.FirstOrDefault(e => e.ExternalId == externalId && Visible(e, includeDeleted)));

    public virtual IReadOnlyList<T> GetAll(bool includeDeleted = false) =>
        Items.Read(l => Sort(l.Where(e => Visible(e, includeDeleted))).ToList());

    public virtual T Add(T entity) => Items.Update(l => {
        Insert(l, entity);
        return entity;
    });

    public virtual bool Update(T entity) => Items.Update(l => {
        var i = l.FindIndex(e => e.Id == entity.Id && !e.SoftDeleted);
        if (i < 0) return false;
        EnsureUnique(l, entity, except: l[i]);
        entity.SoftDeleted = false;
        entity.SoftDeletedDate = null;
        l[i] = entity;
        return true;
    });

    public virtual bool Delete(ShortGuid id) => Items.Update(l => MarkDeleted(l.FirstOrDefault(e => e.Id == id)));

    public virtual bool Restore(ShortGuid id) => Items.Update(l => {
        var e = l.FirstOrDefault(x => x.Id == id && x.SoftDeleted);
        if (e is null) return false;
        e.SoftDeleted = false;
        e.SoftDeletedDate = null;
        return true;
    });

    // --- Для наследников ---

    protected static bool Visible(T e, bool includeDeleted = false) => includeDeleted || !e.SoftDeleted;

    // Вставка или обновление по ExternalId (удалённая запись восстанавливается). Id существующей
    // записи сохраняется.
    protected virtual T Upsert(T entity) {
        if (entity.ExternalId is null) throw new ArgumentException("Для Upsert нужен ExternalId.", nameof(entity));
        return Items.Update(l => {
            var i = l.FindIndex(e => e.ExternalId == entity.ExternalId);
            if (i < 0) {
                Insert(l, entity);
            } else {
                entity.Id = l[i].Id;
                entity.SoftDeleted = false;
                entity.SoftDeletedDate = null;
                l[i] = entity;
            }
            return entity;
        });
    }

    protected bool DeleteByExternalId(string externalId) =>
        Items.Update(l => MarkDeleted(l.FirstOrDefault(e => e.ExternalId == externalId)));

    protected static void Insert(List<T> list, T entity) {
        if (entity.Id.IsEmpty) entity.Id = ShortGuid.NewId();
        EnsureUnique(list, entity, except: null);
        list.Add(entity);
    }

    // --- Снимок (JsonStorage.Export/Import) ---

    internal List<T> All() => Items.Read(l => Sort(l).ToList());

    internal bool Any() => Items.Read(l => l.Count > 0);

    internal virtual void ReplaceAll(IReadOnlyList<T> entities) => Items.Update(l => {
        l.Clear();
        foreach (var e in entities) Insert(l, e);
        return 0;
    });

    private static bool MarkDeleted(T? e) {
        if (e is null || e.SoftDeleted) return false;
        e.SoftDeleted = true;
        e.SoftDeletedDate = DateTimeOffset.Now;
        return true;
    }

    private static void EnsureUnique(List<T> list, T entity, T? except) {
        if (list.Any(e => !ReferenceEquals(e, except)
                          && (e.Id == entity.Id || (entity.ExternalId is not null && e.ExternalId == entity.ExternalId))))
            throw new InvalidOperationException(
                $"Уже есть {typeof(T).Name} с id {entity.Id} или внешним id {entity.ExternalId}.");
    }
}
