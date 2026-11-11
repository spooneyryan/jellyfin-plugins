using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// Ids in a query's JSON as Jellyfin writes them: read in any of the GUID formats (Jellyfin's API and web client use 32
/// hex digits), written as 32 hex digits. System.Text.Json alone reads only the hyphenated form.
/// </summary>
internal sealed class QueryGuidConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("An id must be a GUID string.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("N"));
    }
}
