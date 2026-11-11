using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Migration;
using Jellyfin.Plugin.SmartSearch.Query.Validation;

namespace Jellyfin.Plugin.SmartSearch.Query;

/// <summary>
/// Reads and writes a query's JSON: PascalCase, enums as names, nulls omitted, and text as it is (only
/// what JSON must escape, and characters outside the BMP, which the encoder always writes as two <c>\u</c> escapes).
/// Reading checks the size, runs the migrator, and sorts out the names this build doesn't know before deserializing,
/// so a newer query is reported unsupported rather than invalid; a query it accepts can be written and read
/// again (the 64 KB limit holds for the written form too).
/// </summary>
internal static class QueryJson
{
    private static readonly FrozenSet<string> _matchModes = Names<SmartSearchMatchMode>();
    private static readonly FrozenSet<string> _operators = Names<SmartSearchQueryOperator>();
    private static readonly FrozenSet<string> _units = Names<SmartSearchRelativeUnit>();
    private static readonly FrozenSet<string> _itemKinds = Names<BaseItemKind>();
    private static readonly FrozenSet<string> _orders = Names<SortOrder>();

    /// <summary>
    /// Gets the serializer options: no naming policy, names matched ignoring case, enums as names, ids in any GUID
    /// format (written as 32 hex digits), nulls omitted, and text written as it is
    /// (<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>: a query is data, never put into HTML unescaped), so
    /// that a query in Chinese is not six bytes a character when written, and a query under the limit stays under it.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Reads a query.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="migrator">The migrator; <see cref="QueryMigrator.Default"/> when <c>null</c>.</param>
    /// <returns>The query, or why it couldn't be read.</returns>
    public static QueryReadResult Read(string json, QueryMigrator? migrator = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Encoding.UTF8.GetByteCount(json) > QueryLimits.MaxBytes
            ? Failed(QueryErrorCodes.TooLarge, string.Empty, "The query is over 64 KB.")
            : Read(Encoding.UTF8.GetBytes(json), migrator);
    }

    /// <summary>Reads a query.</summary>
    /// <param name="utf8">The JSON, UTF-8.</param>
    /// <param name="migrator">The migrator; <see cref="QueryMigrator.Default"/> when <c>null</c>.</param>
    /// <returns>The query, or why it couldn't be read.</returns>
    public static QueryReadResult Read(ReadOnlySpan<byte> utf8, QueryMigrator? migrator = null)
    {
        if (utf8 is [0xEF, 0xBB, 0xBF, ..])
        {
            utf8 = utf8[3..]; // a byte order mark, which a file saved by hand may carry
        }

        if (utf8.Length > QueryLimits.MaxBytes)
        {
            return Failed(QueryErrorCodes.TooLarge, string.Empty, "The query is over 64 KB.");
        }

        JsonObject root;
        try
        {
            var node = JsonNode.Parse(utf8, new JsonNodeOptions { PropertyNameCaseInsensitive = true });
            if (node is not JsonObject obj)
            {
                return Failed(QueryErrorCodes.NotAnObject, string.Empty, "The query is not a JSON object.");
            }

            root = obj;
            Materialize(root); // a repeated property name (ignoring case), anywhere, throws here, not later
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return Failed(QueryErrorCodes.InvalidJson, string.Empty, "The query is not valid JSON.");
        }

        var migration = (migrator ?? QueryMigrator.Default).Migrate(root);
        switch (migration.Status)
        {
            case QueryMigrationStatus.Invalid:
                return new QueryReadResult(null, [new("Version", QueryErrorCodes.InvalidVersion, "Version must be a whole number of 1 or more, written without a decimal point or exponent (1, not 1.0).")], migration.Status);
            case QueryMigrationStatus.Newer:
                return new QueryReadResult(null, [new("Version", QueryErrorCodes.UnsupportedVersion, "The query was saved by a newer version of Smart Search.")], migration.Status);
        }

        var errors = new List<QueryValidationError>();
        try
        {
            CheckNames(root, errors);
        }
        catch (ArgumentException)
        {
            return new QueryReadResult(null, [new(string.Empty, QueryErrorCodes.InvalidJson, "The query is not valid JSON.")], migration.Status);
        }

        if (errors.Count > 0)
        {
            return new QueryReadResult(null, errors.Take(QueryLimits.MaxErrors).ToList(), migration.Status);
        }

        try
        {
            var definition = root.Deserialize<SmartSearchQueryDefinition>(Options)
                ?? throw new JsonException("The query is null.");
            if (JsonSerializer.SerializeToUtf8Bytes(definition, Options).Length > QueryLimits.MaxBytes)
            {
                // Defaults written out, or an emoji's 4 bytes written as a 12-byte escape, can take a query near the
                // limit over it; refused here, a query is never accepted and then unreadable once saved.
                return new QueryReadResult(null, [new(string.Empty, QueryErrorCodes.TooLarge, "The query is over 64 KB as Smart Search writes it (an emoji takes 12 bytes there).")], migration.Status);
            }

            return new QueryReadResult(definition, [], migration.Status);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            var path = ex is JsonException { Path: { } p } ? ToPath(p) : string.Empty;
            return new QueryReadResult(null, [new(path, QueryErrorCodes.InvalidJson, "A property has the wrong type.")], migration.Status);
        }
    }

    /// <summary>Writes a query, compact.</summary>
    /// <param name="definition">The query.</param>
    /// <returns>The JSON text.</returns>
    public static string Write(SmartSearchQueryDefinition definition) => JsonSerializer.Serialize(definition, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false), new QueryGuidConverter() }
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    // Builds every object's dictionary, which is where JsonNode notices a repeated name; nested objects are otherwise
    // built only when read, and a reference {Id, Name} inside Values never is.
    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    Materialize(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    Materialize(child);
                }

                break;
        }
    }

    private static FrozenSet<string> Names<T>()
        where T : struct, Enum
        => Enum.GetNames<T>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static QueryReadResult Failed(string code, string path, string message) => new(null, [new(path, code, message)], null);

    private static string ToPath(string jsonPath) => jsonPath.StartsWith("$.", StringComparison.Ordinal) ? jsonPath[2..] : jsonPath.TrimStart('$');

    // The names of enum values the deserializer would reject. An unknown operator, match mode or unit is a newer
    // query's (unsupported); an unknown item kind or sort order, or a name that isn't one, is a mistake (invalid).
    private static void CheckNames(JsonObject root, List<QueryValidationError> errors)
    {
        if (root["Where"] is JsonObject where)
        {
            CheckNode(where, "Where", 1, errors);
        }

        if (root["Scope"] is JsonObject scope && scope["ItemKinds"] is JsonArray kinds)
        {
            for (var i = 0; i < kinds.Count; i++)
            {
                CheckName(kinds[i], _itemKinds, "Scope.ItemKinds[" + i + "]", false, errors);
            }
        }

        if (root["Sort"] is JsonArray sort)
        {
            for (var i = 0; i < sort.Count; i++)
            {
                if (sort[i] is JsonObject key)
                {
                    CheckName(key["Order"], _orders, "Sort[" + i + "].Order", false, errors);
                }
            }
        }
    }

    private static void CheckNode(JsonObject node, string path, int depth, List<QueryValidationError> errors)
    {
        CheckName(node["Match"], _matchModes, path + ".Match", true, errors);
        CheckName(node["Op"], _operators, path + ".Op", true, errors);
        CheckName(node["Unit"], _units, path + ".Unit", true, errors);
        if (node["Children"] is JsonArray children && depth <= QueryLimits.MaxDepth + 1)
        {
            for (var i = 0; i < children.Count; i++)
            {
                if (children[i] is JsonObject child)
                {
                    CheckNode(child, path + ".Children[" + i + "]", depth + 1, errors);
                }
            }
        }
    }

    private static void CheckName(JsonNode? node, FrozenSet<string> names, string path, bool newerIfUnknown, List<QueryValidationError> errors)
    {
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return; // null is no value; another JSON type fails in the deserializer
        }

        var name = value.GetValue<string>();
        if (names.Contains(name))
        {
            return;
        }

        if (newerIfUnknown && IsIdentifier(name))
        {
            errors.Add(new(path, QueryErrorCodes.UnknownOperator, "This operator was added by a newer version of Smart Search."));
        }
        else
        {
            errors.Add(new(path, QueryErrorCodes.InvalidValue, "This is not one of the names the property takes."));
        }
    }

    private static bool IsIdentifier(string name)
        => name.Length is > 0 and <= 64 && char.IsAsciiLetter(name[0]) && name.All(char.IsAsciiLetterOrDigit);
}
