using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Reads a <see cref="StoredEnum{TEnum}"/>: a member's name, ignoring case (as the store's enum converter reads); any other
/// string, a number or another JSON value is <c>Unknown</c> with its text kept. Writes the name, or the kept text.
/// </summary>
/// <typeparam name="TEnum">The enum.</typeparam>
internal sealed class StoredEnumConverter<TEnum> : JsonConverter<StoredEnum<TEnum>>
    where TEnum : struct, Enum
{
    /// <inheritdoc />
    public override StoredEnum<TEnum> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString() ?? string.Empty;

            // Names only: Enum.TryParse would also take "3" or "1, 2", which no writer of this file produces.
            foreach (var name in Enum.GetNames<TEnum>())
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    var value = Enum.Parse<TEnum>(name);
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture) == 0 ? new StoredEnum<TEnum>(value, text) : new StoredEnum<TEnum>(value);
                }
            }

            return new StoredEnum<TEnum>(default, text);
        }

        // Anything else (a number, an object) is kept as its JSON text and written back as a string of it.
        using var document = JsonDocument.ParseValue(ref reader);
        return new StoredEnum<TEnum>(default, document.RootElement.GetRawText());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StoredEnum<TEnum> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.IsUnknown && value.Raw is not null ? value.Raw : value.Value.ToString());
    }
}
