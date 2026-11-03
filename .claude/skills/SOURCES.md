# Скиллы проекта

Взяты с https://skills.sh, прочитаны перед установкой и скопированы как есть (без `npx skills add`,
чтобы в проект попала именно проверенная версия). Обновлять — так же: скачать, прочитать, заменить.

| Скилл | Источник | Коммит | Зачем |
|---|---|---|---|
| `supabase-postgres-best-practices` | supabase/agent-skills | c9be0e9 | Схема, индексы, блокировки, права в PostgreSQL |
| `sql-code-review` | github/awesome-copilot | 8178d19 | Ревью SQL: инъекции, права, анти-паттерны |
| `csharp-async` | github/awesome-copilot | 8178d19 | async/await в C# |
| `codebase-design` | mattpocock/skills | 4ae2317 | Словарь модулей/швов/адаптеров, deletion test |
| `architecture` | anthropics/knowledge-work-plugins | 2574e29 | Формат ADR (`docs/adr/`) |
| `architecture-patterns` | wshobson/agents | 7e2fb55 | Hexagonal / Clean Architecture, in-memory адаптеры |
| `dotnet-backend-patterns` | wshobson/agents | 7e2fb55 | DI, IOptions, async, тесты в .NET |

Отклонены: `dotnet-best-practices`, `dotnet-design-pattern-review` (правила чужого проекта: Semantic
Kernel, MSTest, ResourceManager), `postgres` от PlanetScale (реклама хостинга, справка подгружается
по ссылкам из интернета), `improve-codebase-architecture` (зависит от других скиллов автора),
`csharp-xunit` (слишком базовый).
