using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Audit;

/// <summary>
/// Ids in the audit log as Jellyfin writes them (<c>N</c>, lower case), read in any .NET format.
/// </summary>
internal sealed class AuditGuidConverter : JsonConverter<Guid>
{
    /// <inheritdoc />
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Guid.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("The value is not a GUID.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString("N", CultureInfo.InvariantCulture));
    }
}
