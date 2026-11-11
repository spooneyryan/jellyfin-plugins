using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The file format of <see cref="JsonDocumentStore{TDoc}"/>: PascalCase property names (no naming policy),
/// indented, enums as strings, nulls omitted, UTF-8 without a byte order mark. This is the format Bookmarks 1.0 wrote.
/// </summary>
internal static class JsonStoreFormat
{
    /// <summary>The name of the schema version property at the root of every store file.</summary>
    public const string VersionProperty = "Version";

    private static readonly JsonSerializerOptions _options = CreateSharedOptions();

    /// <summary>
    /// Gets the shared serializer options of the format. They are read-only; a store that needs another converter
    /// passes its own options, built with <see cref="CreateOptions"/>.
    /// </summary>
    public static JsonSerializerOptions Options => _options;

    /// <summary>
    /// Creates a new, still writable copy of the format's serializer options.
    /// </summary>
    /// <returns>The options.</returns>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
        return options;
    }

    private static JsonSerializerOptions CreateSharedOptions()
    {
        var options = CreateOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>
    /// Skips a UTF-8 byte order mark, which a file edited by hand may carry.
    /// </summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <returns>The bytes after the mark.</returns>
    public static ReadOnlySpan<byte> SkipByteOrderMark(ReadOnlySpan<byte> utf8)
        => utf8 is [0xEF, 0xBB, 0xBF, ..] ? utf8[3..] : utf8;

    /// <summary>
    /// Reads the root <see cref="VersionProperty"/> of a store file without deserializing the rest, and checks that the
    /// file is one well-formed JSON object. The property name is matched case-sensitively, as the serializer does, and
    /// the last one wins if it repeats.
    /// </summary>
    /// <param name="utf8">The file's bytes, without a byte order mark.</param>
    /// <returns>The version, or <c>null</c> when the file has none (which means version 1).</returns>
    /// <exception cref="JsonException">The file isn't one JSON object, or its version isn't a whole number of 1 or more.</exception>
    public static int? ReadVersion(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The file is not a JSON object.");
        }

        int? version = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isVersion = reader.ValueTextEquals(VersionProperty);
            reader.Read();
            if (!isVersion)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var value) || value < 1)
            {
                throw new JsonException("The file's Version is not a whole number of 1 or more.");
            }

            version = value;
        }

        if (reader.TokenType != JsonTokenType.EndObject)
        {
            throw new JsonException("The file ends inside its JSON object.");
        }

        // Anything but white space after the root object makes the reader throw here.
        if (reader.Read())
        {
            throw new JsonException("The file holds more than one JSON value.");
        }

        return version;
    }
}
