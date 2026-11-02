# ClaudeTelegramBot

Личный Telegram-бот, который отвечает через `claude -p` (Claude Code в неинтерактивном режиме)
и расходует лимиты вашей подписки Claude. Только для личного использования.

## Как работает

1. `BotWorker` получает сообщения из Telegram (long polling).
2. Проверяет, что автор в белом списке `AllowedUserIds`.
3. `ClaudeRunner` запускает `claude -p --output-format json` в папке `agent-workspace`,
   передаёт текст через stdin и разбирает JSON-ответ.
4. `session_id` из ответа сохраняется в `sessions.json`, следующий запрос из того же чата
   идёт с `--resume`, поэтому бот помнит контекст диалога. `/new` сбрасывает контекст.

## Запуск

1. Установите Claude Code и войдите через подписку:
   ```
   claude          # один раз интерактивно, выполнить /login
   claude -p "привет" --output-format json   # проверка
   ```
2. Создайте бота у @BotFather и получите токен.
3. Положите токен в user-secrets (или переменную окружения `Bot__Token`):
   ```
   dotnet user-secrets set "Bot:Token" "123456:ABC..."
   ```
   user-secrets подключены явно в `Program.cs`, поэтому работают в любом окружении (в т.ч. из Rider).
   Не кладите токен в `appsettings.json`: папка лежит внутри git-репозитория.
4. Запустите `dotnet run`, напишите боту любое сообщение — он ответит вашим user id.
5. Добавьте этот id в `Bot:AllowedUserIds` в `appsettings.json` и перезапустите.

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

`ReminderService` раз в 30 с проверяет `personal-data.json` и сам пишет пользователям из
`AllowedUserIds`. Сам он `claude` не вызывает и лимиты не тратит.

- Задача с временем (`add_task(..., time: "18:00")`) → напоминание с кнопками
  «Выполнено» / «+1 час» / «Завтра».
- Утренняя сводка (`Reminders:DigestTime`, по умолчанию 09:00): задачи на сегодня и просроченные.
  Если задач нет, сводка не приходит.
- `/tasks` — открытые задачи без вызова агента.

Файл данных общий у бота и MCP-сервера, доступ к нему защищён именованным мьютексом.

## Свой MCP-сервер (PersonalMcp)

`PersonalMcp/` — отдельный проект (.NET 9, пакет `ModelContextProtocol`). Он собирается вместе
с ботом и копируется в `bin\...\mcp`. При старте бот пишет `mcp-config.json` и передаёт его в
`claude -p` через `--mcp-config ... --strict-mcp-config`. Claude Code сам запускает сервер
(stdio) на время каждого запроса.

Инструменты (`mcp__personal__*`, разрешены целиком через `"mcp__personal"` в `AllowedTools`):

- заметки: `add_note`, `search_notes`, `delete_note`;
- дела: `add_task`, `list_tasks`, `complete_task`, `delete_task`;
- репозитории (только чтение): `list_repos`, `repo_status`.

Данные лежат в `personal-data.json` рядом с ботом. Настройки — секция `PersonalMcp`
(`ReposRoot` — папка с git-репозиториями). Как агенту пользоваться инструментами,
описано в `agent-instructions.md`.

Новый инструмент: метод с `[McpServerTool]` в классе с `[McpServerToolType]` в `PersonalMcp/Tools`.
Описания (`[Description]`) важны: по ним модель решает, когда вызывать инструмент.

Проверка без Telegram (из `bin\Debug\net10.0\agent-workspace`):
```
echo "покажи мои задачи" | claude -p --output-format json --model haiku --allowedTools mcp__personal --mcp-config ..\mcp-config.json --strict-mcp-config
```

Флаги CLI периодически меняются — сверяйтесь с `claude --help`.
