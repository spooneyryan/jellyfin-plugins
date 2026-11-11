using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.LibraryMaintenance.Settings;

/// <summary>
/// Reads a GUID in any format <see cref="Guid.TryParse(string?, out Guid)"/> takes, and writes the "D" format.
/// </summary>
internal sealed class AnyGuidConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("An id must be a GUID.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("D", CultureInfo.InvariantCulture));
    }
}
