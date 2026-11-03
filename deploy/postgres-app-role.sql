-- Роль приложения для PostgreSQL (принцип наименьших привилегий).
--
-- Бот и MCP-сервер не должны работать под суперпользователем: суперпользователь читает любые базы
-- и выполняет команды ОС (COPY ... TO PROGRAM), а MCP-сервером управляет агент из Telegram.
-- Роль claude_bot_app — обычный пользователь: подключается только к своей базе и владеет только
-- своими таблицами (владение нужно, потому что схему создаёт само приложение, см. EnsureCreated).
--
-- Запуск от администратора базы (суперпользователя), повторный запуск безопасен:
--   docker exec -i claude-bot-pg psql -U claude_bot -d claude_bot -v app_password=<пароль> < deploy/postgres-app-role.sql
-- Затем в ConnectionStrings:Postgres: Username=claude_bot_app;Password=<пароль>.

\set ON_ERROR_STOP on

-- Создать роль, если её нет (у CREATE ROLE нет IF NOT EXISTS), и задать пароль.
SELECT format('CREATE ROLE claude_bot_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD %L', :'app_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'claude_bot_app') \gexec
ALTER ROLE claude_bot_app PASSWORD :'app_password';

-- Подключаться к базе может только приложение (по умолчанию CONNECT есть у PUBLIC). Экземпляр
-- выделен под бота, поэтому и к остальным базам (postgres, template1) PUBLIC подключаться не может.
SELECT format('REVOKE ALL ON DATABASE %I FROM PUBLIC', datname) FROM pg_database WHERE datallowconn \gexec
SELECT format('GRANT CONNECT ON DATABASE %I TO claude_bot_app', current_database()) \gexec

-- Схема public: использовать и создавать в ней таблицы (EnsureCreated).
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO claude_bot_app;

-- Уже созданные таблицы и последовательности — во владение приложения. Последовательности
-- identity-столбцов переходят вместе с таблицей, их отдельно менять нельзя.
DO $$
DECLARE r record;
BEGIN
    FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP
        EXECUTE format('ALTER TABLE public.%I OWNER TO claude_bot_app', r.tablename);
    END LOOP;

    FOR r IN
        SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'S' AND n.nspname = 'public'
          AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = c.oid AND d.deptype IN ('a', 'i'))
    LOOP
        EXECUTE format('ALTER SEQUENCE public.%I OWNER TO claude_bot_app', r.relname);
    END LOOP;
END $$;
