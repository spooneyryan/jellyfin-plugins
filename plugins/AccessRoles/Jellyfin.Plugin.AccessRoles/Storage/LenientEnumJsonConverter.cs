using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// Reads and writes a <see cref="LenientEnum{TEnum}"/> as its name, also as a dictionary key. A string is the name; a number
/// (which this store never writes) is kept as its text, unknown; <c>null</c> reads as an unknown empty name, so the value
/// fails closed where unknown values do. Any other token is malformed data.
/// </summary>
/// <typeparam name="TEnum">The enum.</typeparam>
internal sealed class LenientEnumJsonConverter<TEnum> : JsonConverter<LenientEnum<TEnum>>
    where TEnum : struct, Enum
{
    /// <inheritdoc />
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override LenientEnum<TEnum> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => LenientEnum<TEnum>.Parse(reader.GetString() ?? string.Empty),
            JsonTokenType.Number => LenientEnum<TEnum>.Parse(Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray())),
            JsonTokenType.Null => LenientEnum<TEnum>.Parse(string.Empty),
            _ => throw new JsonException($"A {typeof(TEnum).Name} must be a name.")
        };

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, LenientEnum<TEnum> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Name);
    }

    /// <inheritdoc />
    public override LenientEnum<TEnum> ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => LenientEnum<TEnum>.Parse(reader.GetString() ?? string.Empty);

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, LenientEnum<TEnum> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.Name);
    }
}
