using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// Reads and writes <see cref="KnownUsers"/> (see its remarks).
/// </summary>
internal sealed class KnownUsersJsonConverter : JsonConverter<KnownUsers>
{
    /// <summary>
    /// Gets a value indicating whether a JSON <c>null</c> reaches the converter: it reads as <see cref="KnownUsers.Empty"/>.
    /// </summary>
    public override bool HandleNull => true;

    /// <inheritdoc />
    public override KnownUsers Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return KnownUsers.Empty;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("KnownUsers must be a JSON object of user id to name.");
        }

        var pairs = new List<KeyValuePair<Guid, string>>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString();
            reader.Read();
            if (reader.TokenType == JsonTokenType.String && Guid.TryParse(key, out var id))
            {
                pairs.Add(new KeyValuePair<Guid, string>(id, reader.GetString() ?? string.Empty));
            }
            else
            {
                reader.Skip();
            }
        }

        return KnownUsers.From(pairs);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, KnownUsers value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        foreach (var (id, name) in value ?? KnownUsers.Empty)
        {
            writer.WriteString(KnownUsers.FormatId(id), name);
        }

        writer.WriteEndObject();
    }
}
