using System.Globalization;

namespace ClaudeTelegramBot.Domain.Models;

// Базовая сущность: всё, что хранится записями в хранилище.
//
// Удаление мягкое: запись остаётся, помечается SoftDeleted и пропадает из всех выборок
// (кроме явного includeDeleted и полного снимка для переноса). Её можно восстановить (Restore).
public abstract class Entity {
    // Свой id записи. Пустой при создании — репозиторий выдаёт новый в Add.
    public ShortGuid Id { get; set; }

    // Id во внешней системе, если запись к ней привязана (Telegram user id, chat id).
    // Уникален в пределах вида сущностей, по нему ищет GetByExternalId. null — внешнего id нет.
    public string? ExternalId { get; set; }

    public bool SoftDeleted { get; set; }

    // Когда удалена. null, пока не удалена.
    public DateTimeOffset? SoftDeletedDate { get; set; }

    // Числовые внешние id (Telegram) записываются в ExternalId всегда одинаково.
    public static string External(long id) => id.ToString(CultureInfo.InvariantCulture);
}
