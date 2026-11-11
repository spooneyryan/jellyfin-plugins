using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// What a write would change in a collection: always computed against its live links, never against what the last run
/// wrote, so links the server's startup cleanup dropped, or that something else added, are seen and put right.
/// </summary>
/// <param name="Added">The members to add, in the desired order.</param>
/// <param name="Removed">The members to remove.</param>
/// <param name="Dead">The links that name nothing (pruned by the write).</param>
/// <param name="Duplicates">The further links to a member already linked (pruned by the write).</param>
/// <param name="OrderChanged">Whether the members are the same but in another order (only when the order is the query's).</param>
/// <param name="Presentation">The presentation fields that differ: <c>Name</c>, <c>IsLocked</c>, <c>DisplayOrder</c>,
/// <c>Overview</c>, <c>LockedFields</c>.</param>
internal sealed record CollectionDiff(
    IReadOnlyList<Guid> Added,
    IReadOnlyList<Guid> Removed,
    int Dead,
    int Duplicates,
    bool OrderChanged,
    IReadOnlyList<string> Presentation)
{
    /// <summary>Gets a value indicating whether nothing would change: no write, no refresh, no events.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Dead == 0 && Duplicates == 0 && !OrderChanged && Presentation.Count == 0;

    /// <summary>Gets the server's <c>DisplayOrder</c> for a definition: its sort when locked; the release date when not (the server resets it at every refresh).</summary>
    /// <param name="presentation">The definition's presentation.</param>
    /// <returns>The display order.</returns>
    public static string DisplayOrderOf(AutoCollectionPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        return presentation.EffectiveSortMode switch
        {
            SmartSearchSortMode.QueryOrder => "Default",
            SmartSearchSortMode.SortName => "SortName",
            _ => "PremiereDate"
        };
    }

    /// <summary>Compares a collection's live links and presentation with what the definition wants.</summary>
    /// <param name="box">The collection.</param>
    /// <param name="links">Its resolved live links.</param>
    /// <param name="desired">The desired members, in order.</param>
    /// <param name="definition">The definition.</param>
    /// <returns>The difference.</returns>
    public static CollectionDiff Compute(BoxSet box, ResolvedLinks links, IReadOnlyList<Guid> desired, AutoCollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(definition);
        var current = links.Members.ToHashSet();
        var wanted = desired.ToHashSet();
        var added = desired.Where(id => !current.Contains(id)).Distinct().ToList();
        var removed = links.Members.Where(id => !wanted.Contains(id)).ToList();
        var presentation = definition.Presentation;
        var orderChanged = added.Count == 0 && removed.Count == 0
            && presentation.EffectiveSortMode == SmartSearchSortMode.QueryOrder
            && !links.Members.SequenceEqual(desired.Distinct());

        var fields = new List<string>();
        if (!string.Equals(box.Name, definition.Name, StringComparison.Ordinal))
        {
            fields.Add(nameof(BoxSet.Name));
        }

        if (box.IsLocked != presentation.Lock)
        {
            fields.Add(nameof(BoxSet.IsLocked));
        }

        // An unlocked collection's display order is reset to the release date by every refresh: never compared.
        if (presentation.Lock && !string.Equals(box.DisplayOrder, DisplayOrderOf(presentation), StringComparison.Ordinal))
        {
            fields.Add(nameof(BoxSet.DisplayOrder));
        }

        if (presentation.OverviewMode.Value == SmartSearchOverviewMode.Managed
            && !string.Equals(box.Overview ?? string.Empty, presentation.Overview ?? string.Empty, StringComparison.Ordinal))
        {
            fields.Add(nameof(BoxSet.Overview));
        }

        if (!presentation.Lock && !(box.LockedFields ?? []).Contains(MetadataField.Name))
        {
            fields.Add(nameof(BoxSet.LockedFields));
        }

        return new CollectionDiff(added, removed, links.Dead, links.Duplicates, orderChanged, fields);
    }
}
