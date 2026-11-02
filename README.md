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

- `agent-workspace/CLAUDE.md` — постоянные инструкции агента, правятся без перекомпиляции.
- `Claude:SystemPrompt` — дополнение к системному промпту.
- `Claude:AllowedTools` — какие инструменты агенту можно использовать без подтверждения.
  Всё остальное в режиме `-p` будет отклонено. Не добавляйте `Bash` и `Write`,
  если не понимаете, что агент сможет сделать на вашей машине.
- `Claude:Model` — например `haiku` для экономии лимитов. Пусто = модель по умолчанию.
- `Claude:MaxTurns`, `TimeoutSeconds` — ограничения на один запрос.

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
описано в `agent-workspace/CLAUDE.md`.

Новый инструмент: метод с `[McpServerTool]` в классе с `[McpServerToolType]` в `PersonalMcp/Tools`.
Описания (`[Description]`) важны: по ним модель решает, когда вызывать инструмент.

Проверка без Telegram (из `bin\Debug\net10.0\agent-workspace`):
```
echo "покажи мои задачи" | claude -p --output-format json --model haiku --allowedTools mcp__personal --mcp-config ..\mcp-config.json --strict-mcp-config
```

Флаги CLI периодически меняются — сверяйтесь с `claude --help`.
