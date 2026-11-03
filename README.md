# ClaudeTelegramBot

Личный Telegram-бот, который отвечает через `claude -p` (Claude Code в неинтерактивном режиме)
и расходует лимиты вашей подписки Claude. Только для личного использования.

## Структура

```
ClaudeTelegramBot.sln           решение; общие настройки сборки — Directory.Build.props
src/
  ClaudeTelegramBot.Domain/     модели (Models/) и интерфейсы репозиториев (Repositories/),
                                без внешних зависимостей
  ClaudeTelegramBot.Storage/    реализации репозиториев: Json/, Sqlite/, Postgres/;
                                StorageOptions, фабрика, регистрация в DI, перенос данных
  ClaudeTelegramBot/            бот (Worker):
    Chat/                         Telegram: BotWorker, форматирование, прогресс, вложения
    Claude/                       запуск claude -p (ClaudeRunner)
    Reminders/                    напоминания и утренняя сводка
    Usage/                        статистика и лимиты (/usage)
    Access/                       кому можно писать боту (кеш AllowedUsers)
    Configuration/                классы настроек (Options)
    Program.cs, Cli.cs            запуск и служебные команды
  PersonalMcp/                  MCP-сервер с инструментами агента (Tools/)
tests/
  ClaudeTelegramBot.Storage.Tests/  контрактные тесты хранилища
deploy/                         скрипт роли PostgreSQL
docs/adr/                       архитектурные решения
```

Зависимости идут в одну сторону: `Domain` ← `Storage` ← бот и `PersonalMcp`. Сервисы бота и
инструменты MCP работают только с интерфейсами из `Domain`; `Storage` нужен им лишь для
регистрации в DI (`AddBotStorage`) и служебных команд.

Сборка и запуск — из корня репозитория:
```
dotnet build
dotnet run --project src/ClaudeTelegramBot
```
Готовый бот лежит в `src\ClaudeTelegramBot\bin\Debug\net10.0` (MCP-сервер — в его подпапке `mcp`).

## Как работает

1. `BotWorker` получает сообщения из Telegram (long polling).
2. Проверяет, что автор в списке пользователей с доступом (см. «Доступ»).
3. `ClaudeRunner` запускает `claude -p --output-format stream-json --verbose` в папке
   `agent-workspace`, передаёт текст через stdin и читает события построчно: вызовы инструментов
   (для прогресса), данные о лимитах подписки и итоговый результат.
4. `session_id` из ответа сохраняется в хранилище, следующий запрос из того же чата
   идёт с `--resume`, поэтому бот помнит контекст диалога. `/new` сбрасывает контекст.
5. Пока агент работает, `ProgressReporter` показывает одно сообщение «⏳ Работаю…» со
   списком шагов (поиск, чтение файла, задачи) и удаляет его, когда приходит ответ.
6. Ответ переводится из Markdown в Telegram HTML (`TelegramFormatter`): жирный, курсив, код,
   блоки кода, ссылки, списки, цитаты. Длинный ответ режется на сообщения по блокам, теги
   не рвутся. Если Telegram не принял разметку, кусок уходит простым текстом.

## Команды

- `/new` — новый диалог.
- `/tasks` — открытые задачи (без агента).
- `/model` — модель для этого чата: кнопки из `Claude:AllowedModels` и «по умолчанию»
  (`Claude:Model`). Можно и текстом: `/model haiku`. Хранится в хранилище.
- `/usage` — лимиты подписки (5 часов и 7 дней, % и время сброса) и статистика запросов
  за 5 часов, день, 7 и 30 дней, с разбивкой по моделям. Проценты лимитов берутся из события
  `rate_limit_event`, которое присылает `claude`, то есть это реальные цифры подписки на момент
  последнего запроса. `$` — оценка по ценам API, с подписки деньги не списываются.
- `/help` — справка.

Если 5-часовой или недельный лимит израсходован больше чем на `Claude:LimitWarningPercent`
(по умолчанию 80%), под ответом появляется предупреждение.

## Запуск

1. Установите Claude Code и войдите через подписку:
   ```
   claude          # один раз интерактивно, выполнить /login
   claude -p "привет" --output-format json   # проверка
   ```
2. Создайте бота у @BotFather и получите токен.
3. Положите токен в user-secrets (или переменную окружения `Bot__Token`):
   ```
   dotnet user-secrets set "Bot:Token" "123456:ABC..." --project src/ClaudeTelegramBot
   ```
   user-secrets подключены явно в `Program.cs`, поэтому работают в любом окружении (в т.ч. из Rider).
   Не кладите токен в `appsettings.json`: папка лежит внутри git-репозитория.
4. Поднимите PostgreSQL (хранилище по умолчанию, см. «Хранение данных»), например в Docker:
   ```
   docker run -d --name claude-bot-pg --restart unless-stopped -e POSTGRES_USER=claude_bot -e POSTGRES_PASSWORD=<пароль администратора> -e POSTGRES_DB=claude_bot -p 5432:5432 -v claude-bot-pg:/var/lib/postgresql/data postgres:17
   ```
   `claude_bot` здесь — суперпользователь, только для администрирования. Бот работает под ролью
   приложения без лишних прав (скрипт можно запускать повторно, например чтобы сменить пароль):
   ```
   docker exec -i claude-bot-pg psql -U claude_bot -d claude_bot -v app_password=<пароль приложения> < deploy/postgres-app-role.sql
   ```
   В `ConnectionStrings:Postgres` в `appsettings.json` — `Username=claude_bot_app;Password=<пароль приложения>`.
5. Запустите `dotnet run --project src/ClaudeTelegramBot`, напишите боту любое сообщение — он ответит вашим user id.
6. Дайте себе доступ и перезапустите бота (из `src\ClaudeTelegramBot\bin\Debug\net10.0`):
   ```
   ClaudeTelegramBot.exe users add <user id>
   ```

## Доступ

Кому можно писать боту, хранится в хранилище (таблица `allowed_users`, у Json — `allowed-users.json`)
и переносится вместе с остальными данными. При старте бот загружает список в память (`AllowedUsers`,
неизменяемый `FrozenSet`), и проверка доступа на каждое сообщение в базу не ходит. Те же
пользователи получают напоминания.

```
ClaudeTelegramBot.exe users list
ClaudeTelegramBot.exe users add 123456789
ClaudeTelegramBot.exe users remove 123456789
```
Запущенный бот подхватывает изменения после перезапуска. Пустой список — доступ закрыт для всех.

## Хранение данных

Пользователи, заметки и задачи, сессии, модель чата, статистика запросов и лимиты лежат
в хранилище, которое выбирается в `appsettings.json`; строки подключения — там же:

```json
"Storage": { "Provider": "Postgres", "JsonDirectory": "." },
"ConnectionStrings": {
  "Postgres": "Host=localhost;Port=5432;Database=claude_bot;Username=claude_bot_app;Password=...",
  "Sqlite": "Data Source=bot.db"
}
```

| `Provider` | Где данные | Что нужно |
|---|---|---|
| `Postgres` (по умолчанию) | база из `ConnectionStrings:Postgres` | сервер PostgreSQL |
| `Sqlite` | файл из `ConnectionStrings:Sqlite` (`Data Source`) | ничего |
| `Json` | `notes.json`, `tasks.json`, `sessions.json`, `chat-settings.json`, `allowed-users.json`, `usage.jsonl`, `digest.json`, `rate-limit.json`, `schema.json` в `JsonDirectory` | ничего |

Пути — относительно папки бота (`bin\...`). Таблицы и папки бот создаёт сам при старте.

Версия схемы хранится в самом хранилище (`schema_version`, у Json — `schema.json`). Данные старого
формата (целочисленные id, до базовой сущности) бот при старте обновляет сам, в одной транзакции:
записи получают новые id, Telegram id переходят в `ExternalId`. Старые Json-файлы складываются в
`v1-backup/`. Перед первым запуском новой версии на PostgreSQL сделайте `pg_dump` на всякий случай.

`appsettings.json` лежит в git-репозитории вместе с паролем роли приложения. База доступна только
с этой машины (`localhost`), а у роли нет прав суперпользователя; если репозиторий куда-то
публикуется, вынесите строку в user-secrets (`dotnet user-secrets set "ConnectionStrings:Postgres" "..." --project src/ClaudeTelegramBot`):
они перекрывают `appsettings.json`.

Почему хранилище устроено так и что предстоит пересмотреть (миграции схемы, async) —
[ADR-0001](docs/adr/0001-podmenyaemoe-hranilishche.md), базовая сущность и репозиторий — [ADR-0002](docs/adr/0002-bazovaya-sushchnost-i-repozitoriy.md). Скиллы Claude Code для работы с архитектурой
и базами лежат в `.claude/skills` (откуда взяты — `.claude/skills/SOURCES.md`).

### Смена хранилища

Остановите бота, перенесите данные и поменяйте `Storage:Provider`:
```
cd src\ClaudeTelegramBot\bin\Debug\net10.0
ClaudeTelegramBot.exe migrate-storage Json Postgres
```
Переносится всё, включая мягко удалённые записи; id сохраняются. Целевое хранилище должно быть пустым (иначе
команда откажется), после переноса данные перечитываются и сверяются. Исходное хранилище не
меняется, так что вернуться можно той же командой в обратную сторону (в пустое).

### Как устроено

Два проекта, общие у бота и MCP-сервера:

- `ClaudeTelegramBot.Domain/Models` — сущности. Все наследуют `Entity`: `Id` (`ShortGuid` — Guid v7,
  в тексте 22 символа base64url), `ExternalId` (id во внешней системе: Telegram user/chat id; уникален,
  по нему ищет `GetByExternalId`), `SoftDeleted` и `SoftDeletedDate`. Удаление везде мягкое:
  запись пропадает из выборок, но остаётся в базе и переносится; `Restore` возвращает её.
- `ClaudeTelegramBot.Domain/Repositories` — `IRepository<T>` с общими операциями (`Get`, `GetByExternalId`,
  `GetAll`, `Add`, `Update`, `Delete`, `Restore`) и репозиторий на сущность (`INoteRepository`, `ITaskRepository`,
  `ISessionRepository`, `IChatSettingsRepository`, `IUsageRepository`, `IAllowedUserRepository`), который
  добавляет методы-намерения (`TakeDueReminders`, `Snooze`): каждая такая операция атомарна даже при
  двух процессах — у SQL это `UPDATE ... RETURNING`, у файлов межпроцессный мьютекс. `IStorage`
  объединяет репозитории и умеет `Export`/`Import` полного снимка.
- `ClaudeTelegramBot.Storage`: `Json/`, `Sqlite/`, `Postgres/` — реализации. В каждом провайдере базовый
  репозиторий (`PostgresRepository<T>`, `SqliteRepository<T>`, `JsonRepository<T>`) реализует
  `IRepository<T>` виртуальными методами; репозиторий сущности наследует его, описывает свою таблицу
  (колонки, чтение, запись) и при необходимости переопределяет методы — например, журнал запросов
  в Json дописывает `Add` в конец файла, а в Postgres импортирует его через `COPY`.
  Postgres работает через EF Core (`BotDbContext`, запросы LINQ, условные `ExecuteUpdate`); на SQL
  остались схема и её обновление, блокировки, `COPY` журнала и атомарное «забрать напоминания»
  (`UPDATE ... RETURNING`) — см. [ADR-0003](docs/adr/0003-postgres-ef-core.md).
  `StorageFactory` выбирает провайдер по конфигу, `AddBotStorage` регистрирует в DI сами
  репозитории — потребители зависят только от нужного.
- `StorageMigrator` — перенос между любыми двумя провайдерами.

MCP-серверу настройки бот передаёт переменными окружения в `mcp-config.json` (только то, что
нужно выбранному провайдеру; файл лежит рядом с ботом, вне папки агента). Переменные `Bot__*`,
`ConnectionStrings__*`, `Storage__*` из окружения `claude` убираются.

Новая сущность: класс-наследник `Entity` в `Domain/Models`, интерфейс `I...Repository : IRepository<T>`,
по репозиторию-наследнику базового в каждом провайдере, таблица в схеме, поле в `StorageSnapshot`.
Изменение существующей таблицы — новая версия схемы (`SchemaVersion`) с шагом обновления.

Новый провайдер: базовый репозиторий и репозитории сущностей, `IStorage`, значение в
`StorageProvider` и ветка в `StorageFactory`, подключить к контрактным тестам.

### Тесты

`tests/ClaudeTelegramBot.Storage.Tests` — один набор тестов, который прогоняется на каждом провайдере (и перенос
по цепочке Json → Postgres → Sqlite), так что поведение у всех одинаковое:
```
dotnet test
```
Отдельно проверяется обновление данных старого формата на каждом провайдере (`SchemaUpgradeTests`).
PostgreSQL-тесты запускаются, если задана строка администратора, иначе пропускаются;
каждый тест создаёт и удаляет свою временную базу:
```
$env:BOTSTORAGE_TEST_POSTGRES = "Host=localhost;Port=5432;Username=postgres;Password=<пароль>"
```

## Настройка агента

- `agent-instructions.md` — постоянные инструкции агента. Вместе с `Claude:SystemPrompt` они
  передаются через `--append-system-prompt-file` и правятся без перекомпиляции. Новая версия
  действует с нового диалога (`/new`).
- `Claude:Model` — например `haiku` для экономии лимитов. Пусто = модель по умолчанию.
- `Claude:MaxTurns`, `TimeoutSeconds` — ограничения на один запрос.

### Безопасность

Агент работает на вашей машине, а пишут ему из Telegram, поэтому права зажаты:

- `Claude:Restricted` (`--restricted`) убирает инструменты, которые выполняют команды (Bash
  и т.п.). Кроме того, он игнорирует `~/.claude/settings.json`. Это важно: если там стоит
  `defaultMode: auto`, без этого флага агент сам решал бы, что ему можно, и `AllowedTools`
  ничего бы не ограничивал. Файловые инструменты ограничены папкой `agent-workspace`.
- `Claude:Tools` (`--tools`) — встроенные инструменты, которые вообще существуют: `Read`,
  `WebSearch`. Записи и правки файлов нет. `WebFetch` (загрузка произвольных URL) убран: через адрес
  запроса агент мог бы вынести данные наружу при промпт-инъекции.
- `Claude:PermissionMode` = `dontAsk`: всё, чего нет в `AllowedTools`, молча отклоняется.
- `Claude:AllowedTools` — что разрешено: веб, `mcp__personal` и `Read(./inbox/**)`.

Проверено: файлы вне `agent-workspace` агент прочитать не может.

## Фото и файлы

Фото, альбомы и документы (до 20 МБ — это лимит Bot API) бот скачивает в
`agent-workspace/inbox/<chatId>/` и передаёт агенту пути к ним. Подпись к фото становится
вопросом. Без подписи агент сам описывает, что прислано, а если это ошибка, объясняет причину.
Альбом собирается в один запрос. Файлы старше `Attachments:RetentionDays` удаляются.

Агент читает через `Read` картинки, PDF, текст, логи и код. docx, xlsx и архивы он не читает и
попросит другой формат. Скриншоты лучше слать файлом: Telegram сжимает фото.

## Напоминания

`ReminderService` раз в 30 с проверяет задачи в хранилище и сам пишет всем пользователям
с доступом. Сам он `claude` не вызывает и лимиты не тратит.

- Задача с временем (`add_task(..., time: "18:00")`) → напоминание с кнопками
  «Выполнено» / «+1 час» / «Завтра».
- Утренняя сводка (`Reminders:DigestTime`, по умолчанию 09:00): задачи на сегодня и просроченные.
  Если задач нет, сводка не приходит.
- `/tasks` — открытые задачи без вызова агента.

Хранилище общее у бота и MCP-сервера; напоминание забирается и помечается отправленным атомарно,
поэтому дублей не бывает.

## Свой MCP-сервер (PersonalMcp)

`src/PersonalMcp/` — отдельный проект (net10.0, пакет `ModelContextProtocol`). Он собирается вместе
с ботом и копируется в `bin\...\mcp`. При старте бот пишет `mcp-config.json` и передаёт его в
`claude -p` через `--mcp-config ... --strict-mcp-config`. Claude Code сам запускает сервер
(stdio) на время каждого запроса.

Инструменты (`mcp__personal__*`, разрешены целиком через `"mcp__personal"` в `AllowedTools`):

- заметки: `add_note`, `search_notes`, `delete_note`;
- дела: `add_task`, `reschedule_task`, `list_tasks`, `complete_task`, `delete_task`;
- репозитории (только чтение): `list_repos`, `repo_status`.

Данные — в общем хранилище (см. «Хранение данных»). Настройки — секция `PersonalMcp`
(`ReposRoot` — папка с git-репозиториями). Как агенту пользоваться инструментами,
описано в `agent-instructions.md`.

Новый инструмент: метод с `[McpServerTool]` в классе с `[McpServerToolType]` в `src/PersonalMcp/Tools`.
Описания (`[Description]`) важны: по ним модель решает, когда вызывать инструмент.

Проверка без Telegram (из `src\ClaudeTelegramBot\bin\Debug\net10.0\agent-workspace`):
```
echo "покажи мои задачи" | claude -p --output-format json --model haiku --allowedTools mcp__personal --mcp-config ..\mcp-config.json --strict-mcp-config
```

Флаги CLI периодически меняются — сверяйтесь с `claude --help`.
