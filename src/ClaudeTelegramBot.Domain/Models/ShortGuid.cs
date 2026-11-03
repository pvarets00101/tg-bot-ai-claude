using System.Buffers;
using System.Buffers.Text;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeTelegramBot.Domain.Models;

// Идентификатор сущности: Guid, записанный 22 символами base64url (без '+', '/', '=') — короче
// обычного Guid и удобен в тексте, командах и данных кнопок Telegram (лимит 64 байта).
//
// Новые id — Guid версии 7: начинаются с метки времени, поэтому растут со временем и не
// разбрасывают вставки по индексу. Порядок сравнения — как у Guid (и у uuid в PostgreSQL).
[JsonConverter(typeof(ShortGuidJsonConverter))]
[TypeConverter(typeof(ShortGuidTypeConverter))]
public readonly record struct ShortGuid(Guid Value) : IComparable<ShortGuid>, IParsable<ShortGuid> {
    public const int Length = 22;

    public static readonly ShortGuid Empty = default;

    public static ShortGuid NewId() => new(Guid.CreateVersion7());

    public bool IsEmpty => Value == Guid.Empty;

    public override string ToString() {
        Span<byte> bytes = stackalloc byte[16];
        Value.TryWriteBytes(bytes, bigEndian: true, out _);
        return Base64Url.EncodeToString(bytes);
    }

    public int CompareTo(ShortGuid other) => Value.CompareTo(other.Value);

    // Принимает короткую форму и обычный Guid; пробелы по краям и '#' в начале («#AbC…») игнорируются.
    public static bool TryParse([NotNullWhen(true)] string? s, out ShortGuid result) {
        result = default;
        if (s is null) return false;
        var text = s.AsSpan().Trim().TrimStart('#');

        if (text.Length == Length) {
            Span<byte> bytes = stackalloc byte[16];
            if (Base64Url.DecodeFromChars(text, bytes, out _, out var written) != OperationStatus.Done || written != 16)
                return false;
            result = new ShortGuid(new Guid(bytes, bigEndian: true));
            return true;
        }

        if (Guid.TryParse(text, out var guid)) {
            result = new ShortGuid(guid);
            return true;
        }
        return false;
    }

    public static ShortGuid Parse(string s) =>
        TryParse(s, out var id) ? id : throw new FormatException($"«{s}» — не id (ожидается {Length} символа base64url).");

    static ShortGuid IParsable<ShortGuid>.Parse(string s, IFormatProvider? provider) => Parse(s);

    static bool IParsable<ShortGuid>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ShortGuid result) =>
        TryParse(s, out result);
}

internal sealed class ShortGuidJsonConverter : JsonConverter<ShortGuid> {
    public override ShortGuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ShortGuid.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("Неверный id.");

    public override void Write(Utf8JsonWriter writer, ShortGuid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal sealed class ShortGuidTypeConverter : TypeConverter {
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
        value is string s ? ShortGuid.Parse(s) : base.ConvertFrom(context, culture, value);
}
