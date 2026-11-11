using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The merge helpers of the kit's import, for a plugin's <see cref="JsonImportAdapter{TDoc}"/>:
/// records matched by id, compared by content.
/// </summary>
internal static class ImportMerge
{
    /// <summary>
    /// <c>mode=Merge</c>: an incoming record with a new id is added; one whose id is stored replaces the stored copy only
    /// when it was changed more recently (the stored copy is kept otherwise, as <see cref="ImportCounts.KeptNewer"/>);
    /// nothing is removed. The stored order is kept and added records follow in the export's order; an id the export
    /// repeats counts once, its last copy.
    /// </summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="existing">The stored records.</param>
    /// <param name="incoming">The export's records, their user ids already mapped.</param>
    /// <param name="id">A record's id.</param>
    /// <param name="updatedAt">When a record was last changed.</param>
    /// <param name="sameContent">Whether two records are the same (<see cref="JsonContent{T}"/>).</param>
    /// <returns>The merged records and the counts.</returns>
    public static MergeResult<T> Merge<T>(IEnumerable<T> existing, IEnumerable<T> incoming, Func<T, Guid> id, Func<T, DateTime> updatedAt, IEqualityComparer<T> sameContent)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(updatedAt);
        ArgumentNullException.ThrowIfNull(sameContent);

        var records = existing.ToList();
        var index = new Dictionary<Guid, int>();
        for (var i = 0; i < records.Count; i++)
        {
            index[id(records[i])] = i;
        }

        var unique = Unique(incoming, id);
        int added = 0, updated = 0, unchanged = 0, keptNewer = 0;
        foreach (var record in unique)
        {
            if (!index.TryGetValue(id(record), out var at))
            {
                index[id(record)] = records.Count;
                records.Add(record);
                added++;
            }
            else if (sameContent.Equals(records[at], record))
            {
                unchanged++;
            }
            else if (updatedAt(record) > updatedAt(records[at]))
            {
                records[at] = record;
                updated++;
            }
            else
            {
                keptNewer++;
            }
        }

        return new MergeResult<T>(records, new ImportCounts { Incoming = unique.Count, Added = added, Updated = updated, Unchanged = unchanged, KeptNewer = keptNewer });
    }

    /// <summary>
    /// <c>mode=Replace</c>: the export's records become the stored ones. Counts compare them with the stored ones by id:
    /// added, updated (different content), unchanged, removed (stored, not in the export).
    /// </summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="existing">The stored records.</param>
    /// <param name="incoming">The export's records, their user ids already mapped.</param>
    /// <param name="id">A record's id.</param>
    /// <param name="sameContent">Whether two records are the same.</param>
    /// <returns>The export's records (an id it repeats once) and the counts.</returns>
    public static MergeResult<T> Replace<T>(IEnumerable<T> existing, IEnumerable<T> incoming, Func<T, Guid> id, IEqualityComparer<T> sameContent)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sameContent);

        var stored = new Dictionary<Guid, T>();
        foreach (var record in existing)
        {
            stored[id(record)] = record;
        }

        var records = Unique(incoming, id);
        int added = 0, updated = 0, unchanged = 0;
        foreach (var record in records)
        {
            if (!stored.Remove(id(record), out var current))
            {
                added++;
            }
            else if (sameContent.Equals(current, record))
            {
                unchanged++;
            }
            else
            {
                updated++;
            }
        }

        return new MergeResult<T>(records, new ImportCounts { Incoming = records.Count, Added = added, Updated = updated, Unchanged = unchanged, Removed = stored.Count });
    }

    /// <summary>
    /// Compares records by their JSON in the store's format, which covers nested lists and extension data that record
    /// equality compares by reference.
    /// </summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="options">The serializer options; <see cref="JsonStoreFormat.Options"/> by default.</param>
    /// <returns>The comparer.</returns>
    public static IEqualityComparer<T> JsonContent<T>(JsonSerializerOptions? options = null)
        where T : class
        => new JsonContentComparer<T>(options ?? JsonStoreFormat.Options);

    private static List<T> Unique<T>(IEnumerable<T> records, Func<T, Guid> id)
    {
        var list = new List<T>();
        var at = new Dictionary<Guid, int>();
        foreach (var record in records)
        {
            if (at.TryGetValue(id(record), out var i))
            {
                list[i] = record;
            }
            else
            {
                at[id(record)] = list.Count;
                list.Add(record);
            }
        }

        return list;
    }

    private sealed class JsonContentComparer<T>(JsonSerializerOptions options) : IEqualityComparer<T>
        where T : class
    {
        public bool Equals(T? x, T? y)
            => ReferenceEquals(x, y)
                || (x is not null && y is not null && JsonNode.DeepEquals(JsonSerializer.SerializeToNode(x, options), JsonSerializer.SerializeToNode(y, options)));

        public int GetHashCode(T obj) => 0;
    }
}
