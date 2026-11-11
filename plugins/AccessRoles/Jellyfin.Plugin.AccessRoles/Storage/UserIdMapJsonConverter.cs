using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// Reads and writes a map keyed by user id the way the store writes user ids in keys, like the kit's
/// <see cref="KnownUsers"/> (and Bookmarks' converter of the same name): Jellyfin's "N" format, lower case, in
/// ordinal key order, so the file changes only when a value does. Keys in any <see cref="Guid"/> format are read; an
/// entry whose key isn't a GUID (or is the empty GUID) or whose value is <c>null</c> is dropped, and a JSON <c>null</c>
/// reads as an empty map.
/// </summary>
/// <typeparam name="TValue">The value type.</typeparam>
internal sealed class UserIdMapJsonConverter<TValue> : JsonConverter<IReadOnlyDictionary<Guid, TValue>>
    where TValue : class
{
    /// <summary>
    /// Gets a value indicating whether a JSON <c>null</c> reaches the converter.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override IReadOnlyDictionary<Guid, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var builder = ImmutableDictionary.CreateBuilder<Guid, TValue>();
        if (reader.TokenType == JsonTokenType.Null)
        {
            return builder.ToImmutable();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A map of user ids must be a JSON object.");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString();
            reader.Read();
            if (Guid.TryParse(key, out var id) && !id.Equals(Guid.Empty) && JsonSerializer.Deserialize<TValue>(ref reader, options) is { } value)
            {
                builder[id] = value;
            }
            else
            {
                reader.Skip();
            }
        }

        return builder.ToImmutable();
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, IReadOnlyDictionary<Guid, TValue> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        foreach (var (key, entry) in (value ?? ImmutableDictionary<Guid, TValue>.Empty)
                     .Select(p => (Key: KnownUsers.FormatId(p.Key), p.Value))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            JsonSerializer.Serialize(writer, entry, options);
        }

        writer.WriteEndObject();
    }
}
