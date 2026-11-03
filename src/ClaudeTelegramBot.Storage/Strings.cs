namespace ClaudeTelegramBot.Storage;

internal static class Strings {
    public static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
