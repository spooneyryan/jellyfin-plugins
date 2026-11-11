using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// Reads and writes a saved query's times as UTC (<see cref="DateTimeKind.Utc"/>): a time with an offset is converted, one
/// without (a file written by hand or by another tool) is taken as UTC. So a time reads back as it is written, and an
/// <c>IfUpdatedAt</c> sent back always matches.
/// </summary>
internal sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    /// <summary>Gets a time as UTC: converted when it has a zone, taken as UTC when it has none.</summary>
    /// <param name="value">The time.</param>
    /// <returns>The time, with <see cref="DateTimeKind.Utc"/>.</returns>
    public static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <inheritdoc />
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : reader.GetDateTimeOffset().UtcDateTime;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Utc(value));
    }
}
