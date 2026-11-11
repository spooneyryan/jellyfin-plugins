using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Unicode;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Sniffs and parses Jellyfin Enhanced's data files: a user's
/// <c>bookmark.json</c> and the server's <c>reviews.json</c>. A file of another shape is
/// <see cref="JeDataFormat.Unsupported"/> ("unsupported JE format"), one that isn't JSON (partly written while JE saves) or
/// whose text isn't (bytes that aren't UTF-8, an escaped half of a surrogate pair such as <c>\uD800</c> alone, where
/// <c>GetString</c> threw and an early User Ratings build's import answered 500) <see cref="JeDataFormat.Unreadable"/>;
/// never an exception. Each entry's values must have the types JE writes (<c>BookmarkItem</c>, <c>UserReview</c>;
/// checked with 12.8 to 12.12): an entry with another type is left out and listed, with the reason.
/// </summary>
/// <remarks>
/// Field names are matched ignoring case, as JE's own reader (Newtonsoft.Json) does; an entry key that appears twice keeps
/// its last value, as JE's dictionaries do. The TMDb and TVDb ids may also be JSON numbers, which JE's reader accepts too.
/// </remarks>
internal static class JeDataParser
{
    private const string BookmarksProperty = "Bookmarks";
    private const string ReviewsProperty = "Reviews";

    /// <summary>What a file whose bytes aren't UTF-8 text is.</summary>
    public const string NotUtf8 = "Not valid text: the file has bytes that aren't UTF-8.";

    /// <summary>What a file with a name or value that isn't whole text (an escaped half of a surrogate pair) is.</summary>
    public const string NotText = "Not valid text: a name or value in the file is an escaped character that isn't a whole one (such as \\uD800 alone).";

    private static readonly JsonDocumentOptions _options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32
    };

    /// <summary>
    /// Parses a user's <c>bookmark.json</c>.
    /// </summary>
    /// <param name="json">The file's bytes.</param>
    /// <param name="userId">The user whose folder it is in.</param>
    /// <returns>The file: its bookmarks, the entries left out, or why none can be used.</returns>
    public static JeBookmarkFile ParseBookmarks(ReadOnlyMemory<byte> json, Guid userId)
    {
        if (!TryParse(json, out var document, out var problem))
        {
            return new(userId, JeDataFormat.Unreadable, [], [], problem);
        }

        using (document)
        {
            if (!TryGetMap(document!.RootElement, BookmarksProperty, out var map))
            {
                return new(userId, JeDataFormat.Unsupported, [], [], "Unsupported Jellyfin Enhanced format: no \"Bookmarks\" object.");
            }

            var entries = new List<JeBookmark>();
            var invalid = new List<JeDataProblem>();
            var recognised = 0;
            foreach (var (key, value) in LastByKey(map))
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    invalid.Add(new(key, "Not an object."));
                    continue;
                }

                if (Has(value, "ItemId") && Has(value, "Timestamp"))
                {
                    recognised++;
                }

                var errors = new List<string>();
                var bookmark = new JeBookmark(
                    key,
                    ItemId: RequiredString(value, "ItemId", errors),
                    TmdbId: Id(value, "TmdbId", errors),
                    TvdbId: Id(value, "TvdbId", errors),
                    MediaType: OptionalString(value, "MediaType", errors),
                    Name: OptionalString(value, "Name", errors),
                    Timestamp: Seconds(value, "Timestamp", errors),
                    Label: OptionalString(value, "Label", errors),
                    CreatedAt: OptionalString(value, "CreatedAt", errors),
                    UpdatedAt: OptionalString(value, "UpdatedAt", errors),
                    SyncedFrom: OptionalString(value, "SyncedFrom", errors),
                    SeasonNumber: OptionalInt(value, "SeasonNumber", errors),
                    EpisodeNumber: OptionalInt(value, "EpisodeNumber", errors));
                if (errors.Count > 0)
                {
                    invalid.Add(new(key, string.Join(" ", errors)));
                }
                else
                {
                    entries.Add(bookmark);
                }
            }

            // Entries, but none with the two fields every JE bookmark has: JE changed its format.
            if (recognised == 0 && (entries.Count > 0 || invalid.Count > 0))
            {
                return new(userId, JeDataFormat.Unsupported, [], [], "Unsupported Jellyfin Enhanced format: no bookmark has \"ItemId\" and \"Timestamp\".");
            }

            return new(userId, JeDataFormat.Bookmarks1, entries, invalid, null);
        }
    }

    /// <summary>
    /// Parses the server's <c>reviews.json</c>.
    /// </summary>
    /// <param name="json">The file's bytes.</param>
    /// <returns>The file: its reviews, the entries left out, or why none can be used.</returns>
    public static JeReviewsFile ParseReviews(ReadOnlyMemory<byte> json)
    {
        if (!TryParse(json, out var document, out var problem))
        {
            return new(JeDataFormat.Unreadable, [], [], problem);
        }

        using (document)
        {
            if (!TryGetMap(document!.RootElement, ReviewsProperty, out var map))
            {
                return new(JeDataFormat.Unsupported, [], [], "Unsupported Jellyfin Enhanced format: no \"Reviews\" object.");
            }

            var entries = new List<JeReview>();
            var invalid = new List<JeDataProblem>();
            var fields = new List<string>();
            var recognised = 0;
            foreach (var (key, value) in LastByKey(map))
            {
                if (value.ValueKind != JsonValueKind.Object)
                {
                    invalid.Add(new(key, "Not an object."));
                    continue;
                }

                foreach (var property in value.EnumerateObject())
                {
                    var name = property.Name.Length > 64 ? property.Name[..64] : property.Name;
                    if (fields.Count < JeReviewsFile.MaxFields && !fields.Contains(name))
                    {
                        fields.Add(name);
                    }
                }

                if (Has(value, "TmdbId") && (Has(value, "Rating") || Has(value, "Content")))
                {
                    recognised++;
                }

                var errors = new List<string>();
                var review = new JeReview(
                    key,
                    UserId: OptionalString(value, "UserId", errors),
                    TmdbId: Id(value, "TmdbId", errors, required: true),
                    MediaType: OptionalString(value, "MediaType", errors),
                    Content: OptionalString(value, "Content", errors),
                    Rating: OptionalNumber(value, "Rating", errors),
                    CreatedAt: OptionalString(value, "CreatedAt", errors),
                    UpdatedAt: OptionalString(value, "UpdatedAt", errors));
                if (errors.Count > 0)
                {
                    invalid.Add(new(key, string.Join(" ", errors)));
                }
                else
                {
                    entries.Add(review);
                }
            }

            if (recognised == 0 && (entries.Count > 0 || invalid.Count > 0))
            {
                return new(JeDataFormat.Unsupported, [], [], "Unsupported Jellyfin Enhanced format: no review has \"TmdbId\" and a rating or text.");
            }

            return new(JeDataFormat.Reviews1, entries, invalid, null) { Fields = fields };
        }
    }

    /// <summary>
    /// Parses a user's <c>shortcuts.json</c> (<c>{"Shortcuts": [{"Name", "Key", ...}]}</c>, JE 12.8 to 12.12
    /// <c>UserShortcuts</c>). Only entries with a name and a key override the server's: JE's script skips entries without
    /// one (<c>Key !== undefined</c>), and a key that is <c>null</c> or <c>""</c> switches the action off for that user (no
    /// key press equals it); <c>null</c> is read as <c>""</c>.
    /// </summary>
    /// <param name="json">The file's bytes.</param>
    /// <param name="shortcuts">The entries.</param>
    /// <param name="problem">Why the file can't be used.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryParseUserShortcuts(ReadOnlyMemory<byte> json, out IReadOnlyList<JeShortcut> shortcuts, out string? problem)
    {
        shortcuts = [];
        if (!TryParse(json, out var document, out problem))
        {
            return false;
        }

        using (document)
        {
            var root = document!.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, "Shortcuts", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                problem = "Unsupported Jellyfin Enhanced format: no \"Shortcuts\" list.";
                return false;
            }

            var entries = new List<JeShortcut>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && TryGetProperty(entry, "Name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() is { Length: > 0 } actionName
                    && TryGetProperty(entry, "Key", out var key) && key.ValueKind is JsonValueKind.String or JsonValueKind.Null)
                {
                    entries.Add(new JeShortcut(
                        actionName,
                        key.ValueKind == JsonValueKind.Null ? string.Empty : key.GetString(),
                        TryGetProperty(entry, "Label", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null,
                        TryGetProperty(entry, "Category", out var category) && category.ValueKind == JsonValueKind.String ? category.GetString() : null));
                }
            }

            shortcuts = entries;
            problem = null;
            return true;
        }
    }

    private static bool TryParse(ReadOnlyMemory<byte> json, out JsonDocument? document, out string? problem)
    {
        document = null;
        problem = null;
        var bytes = json;
        if (bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }

        if (!Utf8.IsValid(bytes.Span))
        {
            problem = NotUtf8;
            return false;
        }

        try
        {
            document = JsonDocument.Parse(bytes, _options);
        }
        catch (JsonException ex)
        {
            problem = "Not valid JSON (the file may be being written; try again): " + ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            problem = "Not valid JSON: " + ex.Message;
            return false;
        }

        // Every name and string value read once before the entries are: one that isn't whole text throws in GetString on
        // .NET 10 (an escaped lone surrogate; .NET 9 returns it as it is), so it is found here on both, and nothing after
        // this can throw on it.
        if (!IsWholeText(document.RootElement, 0))
        {
            document.Dispose();
            document = null;
            problem = NotText;
            return false;
        }

        return true;
    }

    private static bool IsWholeText(JsonElement element, int depth)
    {
        if (depth > _options.MaxDepth)
        {
            return true;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return IsWholeText(element);
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!IsWholeText(item, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (!IsWholeName(property) || !IsWholeText(property.Value, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private static bool IsWholeText(JsonElement value)
    {
        try
        {
            return IsWhole(value.GetString());
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsWholeName(JsonProperty property)
    {
        try
        {
            return IsWhole(property.Name);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // No half of a surrogate pair without its other half.
    private static bool IsWhole(string? text)
    {
        if (text is null)
        {
            return true;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetMap(JsonElement root, string name, out JsonElement map)
    {
        map = default;
        return root.ValueKind == JsonValueKind.Object && TryGetProperty(root, name, out map) && map.ValueKind == JsonValueKind.Object;
    }

    private static List<(string Key, JsonElement Value)> LastByKey(JsonElement map)
    {
        var list = new List<(string Key, JsonElement Value)>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in map.EnumerateObject())
        {
            if (index.TryGetValue(property.Name, out var at))
            {
                list[at] = (property.Name, property.Value);
            }
            else
            {
                index[property.Name] = list.Count;
                list.Add((property.Name, property.Value));
            }
        }

        return list;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        // The last of the fields whose names match ignoring case, as Newtonsoft.Json fills an object.
        var found = false;
        value = default;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    private static bool Has(JsonElement element, string name) => TryGetProperty(element, name, out _);

    private static string? OptionalString(JsonElement element, string name, List<string> errors)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{name} is a {Describe(value)}, not text.");
            return null;
        }

        return value.GetString();
    }

    private static string? RequiredString(JsonElement element, string name, List<string> errors)
    {
        // A null is as good as no value: a bookmark without an item id can't be placed.
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            errors.Add($"{name} is missing.");
            return null;
        }

        return OptionalString(element, name, errors);
    }

    private static string? Id(JsonElement element, string name, List<string> errors, bool required = false)
    {
        if (!TryGetProperty(element, name, out var value) || (required && value.ValueKind == JsonValueKind.Null))
        {
            if (required)
            {
                errors.Add($"{name} is missing.");
            }

            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number when value.TryGetInt64(out var number):
                return number.ToString(CultureInfo.InvariantCulture);
            default:
                errors.Add($"{name} is a {Describe(value)}, not an id.");
                return null;
        }
    }

    private static double Seconds(JsonElement element, string name, List<string> errors)
    {
        if (!TryGetProperty(element, name, out var value))
        {
            errors.Add($"{name} is missing.");
            return 0;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || !double.IsFinite(seconds))
        {
            errors.Add($"{name} is a {Describe(value)}, not a number of seconds.");
            return 0;
        }

        if (seconds < 0)
        {
            errors.Add($"{name} is negative.");
            return 0;
        }

        return seconds;
    }

    private static double? OptionalNumber(JsonElement element, string name, List<string> errors)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            errors.Add($"{name} is a {Describe(value)}, not a number.");
            return null;
        }

        return number;
    }

    private static int? OptionalInt(JsonElement element, string name, List<string> errors)
    {
        if (!TryGetProperty(element, name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            errors.Add($"{name} is a {Describe(value)}, not a whole number.");
            return null;
        }

        return number;
    }

    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => "number",
        JsonValueKind.String => "text",
        JsonValueKind.True or JsonValueKind.False => "true/false value",
        JsonValueKind.Array => "list",
        JsonValueKind.Object => "object",
        _ => value.ValueKind.ToString().ToLowerInvariant()
    };
}
