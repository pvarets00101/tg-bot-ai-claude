using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;

namespace PersonalMcp.Tools;

public sealed record RepoSettings(string Root);

// Только чтение: агент видит состояние репозиториев, но ничего в них не меняет.
[McpServerToolType]
public sealed class RepoTools(RepoSettings settings) {
    private const int MaxOutput = 3000;

    [McpServerTool(Name = "list_repos", ReadOnly = true),
     Description("Список git-репозиториев пользователя (папки верхнего уровня в корне репозиториев).")]
    public string ListRepos() {
        if (!TryGetRoot(out var root, out var error)) return error;

        var repos = Directory.EnumerateDirectories(root)
            .Where(d => Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git")))
            .Select(Path.GetFileName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return repos.Count == 0 ? "Репозиториев не найдено." : string.Join("\n", repos);
    }

    [McpServerTool(Name = "repo_status", ReadOnly = true),
     Description("Состояние репозитория: текущая ветка, незакоммиченные изменения и последние коммиты.")]
    public async Task<string> RepoStatus(
        [Description("Имя папки репозитория из list_repos")] string name,
        [Description("Сколько последних коммитов показать (1–30)")] int commits = 5,
        CancellationToken ct = default) {
        if (!TryGetRoot(out var root, out var error)) return error;

        // Только имя папки, без путей: агент не должен выходить за пределы корня.
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Contains(".."))
            return "Ошибка: укажите имя папки репозитория из list_repos.";
        var dir = Path.Combine(root, name);
        if (!Directory.Exists(dir)) return $"Репозиторий {name} не найден.";

        var status = await Git(dir, ct, "status", "--short", "--branch");
        var log = await Git(dir, ct, "log", "--oneline", "--date=short", "--format=%h %ad %s", "-n", Math.Clamp(commits, 1, 30).ToString());
        return Truncate($"== status\n{status}\n== последние коммиты\n{log}");
    }

    private bool TryGetRoot(out string root, out string error) {
        root = settings.Root;
        error = "";
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root)) return true;
        error = "Корень репозиториев не настроен (PersonalMcp:ReposRoot в appsettings бота).";
        return false;
    }

    private static async Task<string> Git(string dir, CancellationToken ct, params string[] args) {
        var psi = new ProcessStartInfo("git") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try {
            await proc.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            proc.Kill(entireProcessTree: true);
            return "git не ответил за 15 с.";
        }

        var output = (await stdout).TrimEnd();
        return proc.ExitCode == 0 ? (output == "" ? "(пусто)" : output) : $"git ошибка: {(await stderr).Trim()}";
    }

    private static string Truncate(string s) => s.Length <= MaxOutput ? s : s[..MaxOutput] + "\n…(обрезано)";
}
