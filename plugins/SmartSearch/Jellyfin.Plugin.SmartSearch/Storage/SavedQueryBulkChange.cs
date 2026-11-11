using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// What a bulk change does to the saved queries that are there now, for
/// <see cref="ISavedQueryReferenceGuard.FindBrokenReferences"/>: the ids it removes, and the ids whose query or owner it
/// changes (a name, a description or sharing alone isn't a change here). Worked out by <see cref="Between"/>, it also
/// carries both documents and, for a remap of owners, the map, so the references can be checked against what the change
/// would store.
/// </summary>
/// <param name="Removed">The ids of the queries it removes.</param>
/// <param name="Changed">The ids of the queries it changes.</param>
internal sealed record SavedQueryBulkChange(IReadOnlyCollection<Guid> Removed, IReadOnlyCollection<Guid> Changed)
{
    /// <summary>Gets the document stored now, when known.</summary>
    public SavedQueriesDocument? Before { get; init; }

    /// <summary>Gets the document the change would store, when known; without it a changed query counts as broken.</summary>
    public SavedQueriesDocument? After { get; init; }

    /// <summary>
    /// Gets the owners a remap moves (old id to new id), whose other records (what references the queries) are remapped
    /// the same way; <c>null</c> for any other change.
    /// </summary>
    public IReadOnlyDictionary<Guid, Guid>? UserMap { get; init; }

    /// <summary>Gets a value indicating whether the change removes or changes nothing.</summary>
    public bool IsEmpty => Removed.Count == 0 && Changed.Count == 0;

    /// <summary>Works out what turning one document into another removes and changes.</summary>
    /// <param name="before">The document stored now.</param>
    /// <param name="after">The document the change would store.</param>
    /// <param name="userMap">For a remap of owners, the map (old id to new id); <c>null</c> otherwise.</param>
    /// <returns>The change.</returns>
    public static SavedQueryBulkChange Between(SavedQueriesDocument before, SavedQueriesDocument after, IReadOnlyDictionary<Guid, Guid>? userMap = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var next = (after.Queries ?? []).Where(q => q is not null).GroupBy(q => q.Id).ToDictionary(g => g.Key, g => g.First());
        var removed = new List<Guid>();
        var changed = new List<Guid>();
        foreach (var query in (before.Queries ?? []).Where(q => q is not null))
        {
            if (!next.TryGetValue(query.Id, out var other))
            {
                removed.Add(query.Id);
            }
            else if (!other.OwnerUserId.Equals(query.OwnerUserId) || !SameJson(query.Query, other.Query))
            {
                changed.Add(query.Id);
            }
        }

        return new SavedQueryBulkChange(removed, changed) { Before = before, After = after, UserMap = userMap };
    }

    private static bool SameJson(JsonElement a, JsonElement b)
        => a.ValueKind == JsonValueKind.Undefined || b.ValueKind == JsonValueKind.Undefined
            ? a.ValueKind == b.ValueKind
            : JsonElement.DeepEquals(a, b);
}
