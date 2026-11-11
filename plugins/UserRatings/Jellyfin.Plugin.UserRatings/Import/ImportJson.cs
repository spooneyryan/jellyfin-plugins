using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// How an import reads the rows of an export: the store's own format, except
/// that every Guid is read in any of .NET's forms, "N" (32 digits), "D", with braces or parentheses, in either case. Another
/// tool's export, or one edited by hand, often writes "N"; read in "D" only, a tombstone's id in "N" made its row unusable,
/// and its rating could come back. Writes stay as the store's ("D").
/// </summary>
internal static class ImportJson
{
    /// <summary>Gets the options.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonStoreFormat.Options);
        options.Converters.Insert(0, new AnyFormGuidConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    // A Guid in any form Guid.TryParse reads; a Guid? is read through it too (System.Text.Json's nullable wrapper).
    private sealed class AnyFormGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var id)
                ? id
                : throw new JsonException("Not a Guid.");

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
}
