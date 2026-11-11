using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// One auto-collection of <c>auto-collections.json</c>: a Jellyfin collection whose members the plugin keeps equal to a
/// saved query's result.
/// </summary>
/// <remarks>
/// <para>Every part keeps the fields this version doesn't know (<c>Extra</c>), and every enum keeps a value it doesn't
/// know (<see cref="StoredEnum{TEnum}"/>), so a newer Smart Search's definition survives this build's writes. Such a
/// definition is never run or written (<see cref="FromNewerVersion"/>): a newer mode run as this version understands it
/// could remove members it shouldn't.</para>
/// </remarks>
internal sealed record AutoCollectionDefinition
{
    /// <summary>The provider-id key of the mark a collection carries: the definition it belongs to.</summary>
    public const string TetherKey = "SmartSearch";

    /// <summary>Gets the definition's id, also the collection's marker (the <c>SmartSearch</c> provider id, in <c>N</c> format).</summary>
    public Guid Id { get; init; }

    /// <summary>Gets the collection's name: 1 to 100 characters, trimmed.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the collection is kept up to date.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Gets where the query comes from.</summary>
    public AutoCollectionSource Source { get; init; } = new();

    /// <summary>Gets whose watch state per-user rules read; set exactly when the query has such rules.</summary>
    public Guid? EvaluationUserId { get; init; }

    /// <summary>Gets the collection's item id; <c>null</c> until it is created, or after an import onto another server.</summary>
    public Guid? CollectionId { get; init; }

    /// <summary>Gets the collection's name, sort, lock and overview handling.</summary>
    public AutoCollectionPresentation Presentation { get; init; } = new();

    /// <summary>Gets the manual adds and removes kept as pins and excludes.</summary>
    public AutoCollectionManualEdits ManualEdits { get; init; } = new();

    /// <summary>Gets the removal safeguards.</summary>
    public AutoCollectionSafety Safety { get; init; } = new();

    /// <summary>Gets who made it: their libraries are its scope, and its saved query must be theirs.</summary>
    public Guid CreatedByUserId { get; init; }

    /// <summary>Gets when it was made.</summary>
    public DateTime CreatedUtc { get; init; }

    /// <summary>Gets who changed it last.</summary>
    public Guid UpdatedByUserId { get; init; }

    /// <summary>Gets when it was last changed.</summary>
    public DateTime UpdatedUtc { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Gets the marker value of the collection: the id in <c>N</c> format, the only form the marker is written and found in.</summary>
    [JsonIgnore]
    public string Tether => FormatTether(Id);

    /// <summary>Formats a definition id as the marker value (<c>N</c> format; the server's lookup is exact and case-sensitive).</summary>
    /// <param name="id">The definition's id.</param>
    /// <returns>The marker value.</returns>
    public static string FormatTether(Guid id) => id.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Tells whether a newer Smart Search wrote something in the definition this version doesn't know: a field of its own
    /// or of a part (an item entry's included), or an enum value. Such a definition is never evaluated or written.
    /// </summary>
    /// <returns>The first such thing, as a JSON path, or <c>null</c> for none.</returns>
    public string? FromNewerVersion()
    {
        static bool Has(Dictionary<string, JsonElement>? extra) => extra is { Count: > 0 };

        if (Has(Extra))
        {
            return Extra!.Keys.First();
        }

        if (Source is { } source)
        {
            if (Has(source.Extra))
            {
                return "Source." + source.Extra!.Keys.First();
            }

            if (source.Mode.IsUnknown)
            {
                return "Source.Mode";
            }
        }

        if (Presentation is { } presentation)
        {
            if (Has(presentation.Extra))
            {
                return "Presentation." + presentation.Extra!.Keys.First();
            }

            if (presentation.SortMode.IsUnknown)
            {
                return "Presentation.SortMode";
            }

            if (presentation.OverviewMode.IsUnknown)
            {
                return "Presentation.OverviewMode";
            }
        }

        if (ManualEdits is { } edits)
        {
            if (Has(edits.Extra))
            {
                return "ManualEdits." + edits.Extra!.Keys.First();
            }

            if (edits.Mode.IsUnknown)
            {
                return "ManualEdits.Mode";
            }

            foreach (var (list, entries) in new[] { ("Pinned", edits.Pinned), ("Excluded", edits.Excluded) })
            {
                if ((entries ?? []).FirstOrDefault(e => e is not null && Has(e.Extra)) is { } entry)
                {
                    return "ManualEdits." + list + "." + entry.Extra!.Keys.First();
                }
            }
        }

        return Safety is { } safety && Has(safety.Extra) ? "Safety." + safety.Extra!.Keys.First() : null;
    }
}
