using ClaudeTelegramBot.Domain.Models;

namespace ClaudeTelegramBot.Domain.Repositories;

public interface ITaskRepository : IRepository<TaskItem> {
    TaskItem Add(string title, DateOnly? due, DateTimeOffset? remindAt);

    // Новый срок и время напоминания; напоминание снова «не отправлено». null — задачи нет.
    TaskItem? Reschedule(ShortGuid id, DateOnly? due, DateTimeOffset? remindAt);

    // Открытые по сроку, затем без срока; includeDone — выполненные в конце.
    IReadOnlyList<TaskItem> List(bool includeDone);

    // Открытые задачи со сроком не позже dueOnOrBefore (или все открытые, если null).
    IReadOnlyList<TaskItem> Open(DateOnly? dueOnOrBefore = null);

    (CompleteResult Result, string? Title) Complete(ShortGuid id);

    // Отложить напоминание: задача снова открыта, срок сдвигается, если он раньше нового времени.
    bool Snooze(ShortGuid id, DateTimeOffset at);

    // Атомарно забирает напоминания, время которых пришло, и помечает их отправленными:
    // каждое напоминание возвращается ровно один раз. По возрастанию времени.
    IReadOnlyList<TaskItem> TakeDueReminders(DateTimeOffset now);

    // true — сводку за этот день ещё не отправляли (и теперь она отмечена отправленной).
    bool TryMarkDigest(DateOnly day);
}
