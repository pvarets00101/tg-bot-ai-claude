using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeTelegramBot;

// Markdown от модели -> Telegram HTML (parse_mode=HTML), разбитый на сообщения до лимита Telegram.
// Поддерживается то, что модель реально пишет: **жирный**, *курсив*, `код`, ```блоки```, ссылки,
// заголовки, списки, цитаты, ~~зачёркнутый~~. Таблицы показываются моноширинным блоком.
// Разбивка идёт по блокам исходного текста, поэтому теги никогда не рвутся между сообщениями.
public static partial class TelegramFormatter {
    // Лимит Telegram 4096 символов после разбора сущностей; HTML-теги в него не входят,
    // но держим запас на них и на &amp; и т.п.
    private const int ChunkLimit = 3800;

    public static IReadOnlyList<string> ToHtmlChunks(string markdown) {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var block in ToHtmlBlocks(markdown)) {
            foreach (var piece in SplitOversized(block)) {
                if (current.Length > 0 && current.Length + 2 + piece.Length > ChunkLimit) {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0) current.Append("\n\n");
                current.Append(piece);
            }
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks.Count > 0 ? chunks : ["(пустой ответ)"];
    }

    // Простой текст без разметки, порезанный по лимиту, — запасной вариант, если Telegram
    // не принял HTML.
    public static IReadOnlyList<string> ToPlainChunks(string text) {
        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += ChunkLimit)
            chunks.Add(text.Substring(i, Math.Min(ChunkLimit, text.Length - i)));
        return chunks.Count > 0 ? chunks : ["(пустой ответ)"];
    }

    public static string Escape(string s) => WebUtility.HtmlEncode(s);

    // --- Блоки ---

    private abstract record Block;
    private sealed record CodeBlock(string Language, string Code) : Block;
    private sealed record TextBlock(string Html) : Block;

    private static IEnumerable<Block> ToHtmlBlocks(string markdown) {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var quote = new List<string>();
        var table = new List<string>();

        IEnumerable<Block> Flush() {
            if (paragraph.Count > 0) {
                yield return new TextBlock(string.Join("\n", paragraph));
                paragraph.Clear();
            }
            if (quote.Count > 0) {
                yield return new TextBlock($"<blockquote>{string.Join("\n", quote)}</blockquote>");
                quote.Clear();
            }
            if (table.Count > 0) {
                yield return new CodeBlock("", string.Join("\n", table));
                table.Clear();
            }
        }

        for (var i = 0; i < lines.Length; i++) {
            var line = lines[i];

            // ```lang ... ```
            var fence = FenceRegex().Match(line);
            if (fence.Success) {
                foreach (var b in Flush()) yield return b;
                var code = new List<string>();
                for (i++; i < lines.Length && !FenceRegex().IsMatch(lines[i]); i++) code.Add(lines[i]);
                yield return new CodeBlock(fence.Groups[1].Value, string.Join("\n", code));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) {
                foreach (var b in Flush()) yield return b;
                continue;
            }

            // Таблица: строки вида | a | b |
            if (line.TrimStart().StartsWith('|')) {
                if (paragraph.Count > 0 || quote.Count > 0) foreach (var b in Flush()) yield return b;
                table.Add(line.Trim());
                continue;
            }
            if (table.Count > 0) foreach (var b in Flush()) yield return b;

            // > цитата
            if (line.TrimStart().StartsWith('>')) {
                if (paragraph.Count > 0) foreach (var b in Flush()) yield return b;
                quote.Add(Inline(line.TrimStart()[1..].TrimStart()));
                continue;
            }
            if (quote.Count > 0) foreach (var b in Flush()) yield return b;

            paragraph.Add(FormatLine(line));
        }
        foreach (var b in Flush()) yield return b;
    }

    private static string FormatLine(string line) {
        // # Заголовок -> жирный
        var heading = HeadingRegex().Match(line);
        if (heading.Success) return $"<b>{Inline(heading.Groups[1].Value)}</b>";

        // --- / *** -> разделитель
        if (RuleRegex().IsMatch(line)) return "──────────";

        // - пункт / * пункт / + пункт -> •, с сохранением вложенности
        var bullet = BulletRegex().Match(line);
        if (bullet.Success) {
            var indent = bullet.Groups[1].Value.Replace("\t", "    ").Length / 2;
            return new string(' ', indent * 2) + "• " + Inline(bullet.Groups[2].Value);
        }

        return Inline(line);
    }

    // --- Инлайн-разметка ---

    private static string Inline(string text) {
        // 1. `код` прячем в заглушки, чтобы внутри ничего не форматировалось.
        var codes = new List<string>();
        text = InlineCodeRegex().Replace(text, m => {
            codes.Add($"<code>{Escape(m.Groups[1].Value)}</code>");
            return $"\u0000{codes.Count - 1}\u0000";
        });

        // 2. Экранируем HTML. Символы разметки (* _ [ ] ( ) ~) не затрагиваются.
        text = Escape(text);

        // 3. Разметка.
        text = LinkRegex().Replace(text, m => {
            var url = m.Groups[2].Value;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? $"<a href=\"{url}\">{m.Groups[1].Value}</a>"
                : m.Value;
        });
        text = BoldRegex().Replace(text, "<b>$1$2</b>");
        text = StrikeRegex().Replace(text, "<s>$1</s>");
        text = ItalicStarRegex().Replace(text, "<i>$1</i>");
        text = ItalicUnderscoreRegex().Replace(text, "<i>$1</i>");

        // 4. Возвращаем код.
        return CodePlaceholderRegex().Replace(text, m => codes[int.Parse(m.Groups[1].Value)]);
    }

    // --- Разбивка слишком больших блоков ---

    private static IEnumerable<string> SplitOversized(Block block) {
        switch (block) {
            case CodeBlock code: {
                // Длинный код режем по строкам на несколько блоков <pre>.
                var open = code.Language.Length > 0
                    ? $"<pre><code class=\"language-{Escape(code.Language)}\">"
                    : "<pre>";
                var close = code.Language.Length > 0 ? "</code></pre>" : "</pre>";
                var budget = ChunkLimit - open.Length - close.Length;

                var sb = new StringBuilder();
                foreach (var rawLine in code.Code.Split('\n')) {
                    var line = Escape(rawLine);
                    while (line.Length > budget) { // одна огромная строка
                        if (sb.Length > 0) {
                            yield return open + sb + close;
                            sb.Clear();
                        }
                        var cut = SafeCut(line, budget);
                        yield return open + line[..cut] + close;
                        line = line[cut..];
                    }
                    if (sb.Length > 0 && sb.Length + 1 + line.Length > budget) {
                        yield return open + sb + close;
                        sb.Clear();
                    }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line);
                }
                yield return open + sb + close;
                break;
            }
            case TextBlock text when text.Html.Length <= ChunkLimit:
                yield return text.Html;
                break;
            case TextBlock text: {
                // Длинный абзац: режем по строкам (каждая строка форматировалась отдельно,
                // так что теги внутри строки целые). Строку длиннее лимита отдаём без тегов.
                var sb = new StringBuilder();
                foreach (var line in text.Html.Split('\n')) {
                    var piece = line.Length <= ChunkLimit ? line : Escape(StripTags(line));
                    while (piece.Length > ChunkLimit) {
                        if (sb.Length > 0) {
                            yield return sb.ToString();
                            sb.Clear();
                        }
                        var cut = SafeCut(piece, ChunkLimit);
                        yield return piece[..cut];
                        piece = piece[cut..];
                    }
                    if (sb.Length > 0 && sb.Length + 1 + piece.Length > ChunkLimit) {
                        yield return sb.ToString();
                        sb.Clear();
                    }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(piece);
                }
                if (sb.Length > 0) yield return sb.ToString();
                break;
            }
        }
    }

    // Не режем посреди HTML-сущности вроде &amp;.
    private static int SafeCut(string s, int max) {
        var amp = s.LastIndexOf('&', max - 1);
        return amp >= 0 && amp > max - 8 && s.IndexOf(';', amp) >= max ? amp : max;
    }

    public static string StripTags(string html) => WebUtility.HtmlDecode(TagRegex().Replace(html, ""));

    [GeneratedRegex(@"^\s*```\s*([\w#+.-]*)\s*$")] private static partial Regex FenceRegex();
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$")] private static partial Regex HeadingRegex();
    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")] private static partial Regex RuleRegex();
    [GeneratedRegex(@"^(\s*)[-*+]\s+(.+)$")] private static partial Regex BulletRegex();
    [GeneratedRegex(@"`([^`\n]+)`")] private static partial Regex InlineCodeRegex();
    [GeneratedRegex(@"\[([^\]\n]+)\]\(([^)\s]+)\)")] private static partial Regex LinkRegex();
    [GeneratedRegex(@"\*\*(?=\S)(.+?)(?<=\S)\*\*|__(?=\S)(.+?)(?<=\S)__")] private static partial Regex BoldRegex();
    [GeneratedRegex(@"~~(?=\S)(.+?)(?<=\S)~~")] private static partial Regex StrikeRegex();
    [GeneratedRegex(@"(?<![*\w])\*(?=\S)([^*\n]+?)(?<=\S)\*(?![*\w])")] private static partial Regex ItalicStarRegex();
    [GeneratedRegex(@"(?<![\w_])_(?=\S)([^_\n]+?)(?<=\S)_(?![\w_])")] private static partial Regex ItalicUnderscoreRegex();
    [GeneratedRegex("\u0000(\\d+)\u0000")] private static partial Regex CodePlaceholderRegex();
    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
}
