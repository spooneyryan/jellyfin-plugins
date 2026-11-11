using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// The provider-id maps of <c>Ratings.ProviderIds</c> and <c>SeriesProviderIds</c>: written as a JSON object of
/// strings in ordinal key order, and read tolerantly. A value the store never writes (a number such as
/// <c>{"Tmdb": 603}</c> from a hand edit or another tool, an array, text that isn't JSON) used to throw a raw
/// <see cref="JsonException"/>, a 500 on every read of that row; now what can be read is kept (a number as its text, which
/// is the same id) and the rest left out, and the caller logs one Warning for the row.
/// </summary>
internal static class ProviderIdsJson
{
    /// <summary>
    /// Writes a map as the database stores it.
    /// </summary>
    /// <param name="ids">The map.</param>
    /// <returns>The JSON object, keys in ordinal order; <c>null</c> for no map or an empty one.</returns>
    public static string? Write(IReadOnlyDictionary<string, string>? ids)
        => ids is null || ids.Count == 0
            ? null
            : JsonSerializer.Serialize(ids.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));

    /// <summary>
    /// Reads a stored map, keeping what can be read.
    /// </summary>
    /// <param name="json">The stored text.</param>
    /// <param name="problem">What wasn't as the store writes it, for the Warning; <c>null</c> when nothing was wrong.</param>
    /// <returns>The map: every string value, every number as its text; empty when the text isn't a JSON object.</returns>
    public static Dictionary<string, string> Read(string json, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(json);
        problem = null;
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                problem = "is JSON but not an object (" + document.RootElement.ValueKind + ")";
                return ids;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        ids[property.Name] = property.Value.GetString()!;
                        break;
                    case JsonValueKind.Number:
                        ids[property.Name] = property.Value.GetRawText();
                        problem ??= string.Create(CultureInfo.InvariantCulture, $"has a number for {property.Name}, read as its text");
                        break;
                    default:
                        problem ??= string.Create(CultureInfo.InvariantCulture, $"has {property.Value.ValueKind} for {property.Name}, left out");
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            problem = "isn't valid JSON (" + ex.Message + ")";
            ids.Clear();
        }

        return ids;
    }
}
