using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Keeps a definition's pins and excludes: sorts the edits made to its collection outside the plugin into pins and
/// excludes, follows moved items (the move journal, then the same path or a single strong provider-id match), and marks
/// the entries whose item is gone as detached.
/// </summary>
/// <remarks>
/// <para>Sorting an edit (Respect mode): an added item that the collection wouldn't have becomes a pin (an excluded one
/// stops being excluded, and is pinned only when the query doesn't match it); a removed item stops being pinned, and
/// becomes an exclude when the query matches it. An add the server makes on its own after a scan (the movie's library
/// adds movies to collections automatically and the movie's collection name is the collection's) is reverted instead.
/// In Overwrite mode every edit is reverted. Edits that would take the pins or the excludes over their limit are sorted
/// into nothing: the run stops, so nothing is reverted, until a manager makes room or switches to Overwrite.</para>
/// <para>The changes are worked out from what was read, then applied to the definition as it is when saved, so a change
/// saved meanwhile (a manager's, a journal relink) is kept.</para>
/// </remarks>
internal sealed class ManualEditKeeper
{
    /// <summary>How many item ids one existence check asks about.</summary>
    public const int IdsPerQuery = 500;

    private readonly ILibraryManager _library;
    private readonly IdentityResolver? _resolver;
    private readonly MoveJournalReader? _moves;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualEditKeeper"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="resolver">Finds moved items again; without, an entry whose item is gone is only detached.</param>
    /// <param name="moves">The move journal; without, pins and excludes don't follow it.</param>
    public ManualEditKeeper(ILibraryManager library, IdentityResolver? resolver = null, MoveJournalReader? moves = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
        _resolver = resolver;
        _moves = moves;
    }

    /// <summary>The ways one edit changes the pins and excludes.</summary>
    public enum Change
    {
        /// <summary>Pin the item.</summary>
        Pin,

        /// <summary>Stop pinning it.</summary>
        Unpin,

        /// <summary>Exclude it.</summary>
        Exclude,

        /// <summary>Stop excluding it.</summary>
        Unexclude
    }

    /// <summary>
    /// Sorts the edits made outside the plugin into changes of the pins and excludes.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="box">Its collection.</param>
    /// <param name="edits">The edits, oldest first.</param>
    /// <param name="desired">The members the run worked out before the edits.</param>
    /// <returns>The changes and what they count.</returns>
    public Sorted Sort(AutoCollectionDefinition definition, BoxSet box, IReadOnlyList<AutoCollectionManualEdit> edits, DesiredMembership desired)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(edits);
        ArgumentNullException.ThrowIfNull(desired);
        if (definition.ManualEdits.Mode.Value != SmartSearchManualEditsMode.Respect)
        {
            return new Sorted([], 0, 0, edits.Count, false);
        }

        var pinned = Active(definition.ManualEdits.Pinned).ToHashSet();
        var excluded = Active(definition.ManualEdits.Excluded).ToHashSet();
        var members = desired.Ids.ToHashSet();
        var matched = desired.Evaluation.OrderedIds.ToHashSet();
        var changes = new List<(Guid ItemId, Change Change)>();
        var reverted = 0;
        foreach (var edit in edits.Where(e => e is not null && !e.ItemId.Equals(Guid.Empty)))
        {
            var id = edit.ItemId;
            if (edit.Added)
            {
                if (IsServersOwnAdd(id, box))
                {
                    reverted++;
                }
                else if (excluded.Remove(id))
                {
                    changes.Add((id, Change.Unexclude));
                    if (!matched.Contains(id) && pinned.Add(id))
                    {
                        changes.Add((id, Change.Pin));
                    }
                }
                else if (!members.Contains(id) && pinned.Add(id))
                {
                    changes.Add((id, Change.Pin));
                }

                members.Add(id);
                continue;
            }

            if (pinned.Remove(id))
            {
                changes.Add((id, Change.Unpin));
            }

            if (matched.Contains(id) && excluded.Add(id))
            {
                changes.Add((id, Change.Exclude));
            }

            members.Remove(id);
        }

        var tooMany = pinned.Count > AutoCollectionManualEdits.MaxEntries || excluded.Count > AutoCollectionManualEdits.MaxEntries;
        if (tooMany)
        {
            return new Sorted([], 0, 0, 0, true);
        }

        // Net counts: a pin later undone by a removal counts as nothing.
        var pins = changes.Count(c => c.Change == Change.Pin) - changes.Count(c => c.Change == Change.Unpin);
        var excludes = changes.Count(c => c.Change == Change.Exclude) - changes.Count(c => c.Change == Change.Unexclude);
        return new Sorted(changes, Math.Max(pins, 0), Math.Max(excludes, 0), reverted, false);
    }

    /// <summary>Applies sorted changes to the pins and excludes as they are now.</summary>
    /// <param name="current">The current pins and excludes.</param>
    /// <param name="changes">The changes, in order.</param>
    /// <param name="now">When (a new entry's time).</param>
    /// <returns>The new pins and excludes.</returns>
    public AutoCollectionManualEdits Apply(AutoCollectionManualEdits current, IReadOnlyList<(Guid ItemId, Change Change)> changes, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(changes);
        var pinned = (current.Pinned ?? []).Where(e => e is not null).ToList();
        var excluded = (current.Excluded ?? []).Where(e => e is not null).ToList();
        foreach (var (id, change) in changes)
        {
            switch (change)
            {
                case Change.Pin when !pinned.Any(e => e.ItemId.Equals(id) && e.DetachedUtc is null):
                    pinned.RemoveAll(e => e.ItemId.Equals(id));
                    pinned.Add(NewEntry(id, now));
                    break;
                case Change.Unpin:
                    pinned.RemoveAll(e => e.ItemId.Equals(id));
                    break;
                case Change.Exclude when !excluded.Any(e => e.ItemId.Equals(id) && e.DetachedUtc is null):
                    excluded.RemoveAll(e => e.ItemId.Equals(id));
                    excluded.Add(NewEntry(id, now));
                    break;
                case Change.Unexclude:
                    excluded.RemoveAll(e => e.ItemId.Equals(id));
                    break;
            }
        }

        return current with { Pinned = pinned, Excluded = excluded };
    }

    /// <summary>
    /// Works out what the entries need: a moved item's new id, a gone item's detachment, a returned item's reattachment,
    /// and the identity of an item first seen (to find it again after a move).
    /// </summary>
    /// <param name="edits">The pins and excludes.</param>
    /// <param name="now">When (a detachment's time).</param>
    /// <returns>The fixes by the entry's current item id; empty when none is needed.</returns>
    public IReadOnlyDictionary<Guid, EntryFix> Maintain(AutoCollectionManualEdits edits, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var entries = (edits.Pinned ?? []).Concat(edits.Excluded ?? []).Where(e => e is not null && !e.ItemId.Equals(Guid.Empty)).ToList();
        var fixes = new Dictionary<Guid, EntryFix>();
        if (entries.Count == 0)
        {
            return fixes;
        }

        var existing = Existing(entries.Select(e => e.ItemId).Distinct().ToList());
        MoveJournal? journal = null;
        foreach (var entry in entries)
        {
            if (fixes.ContainsKey(entry.ItemId))
            {
                continue;
            }

            if (existing.Contains(entry.ItemId))
            {
                var stale = entry.Identity is null || !entry.Identity.ItemId.Equals(entry.ItemId);
                if (entry.DetachedUtc is not null || stale)
                {
                    fixes[entry.ItemId] = new EntryFix(entry.ItemId, stale ? Capture(entry.ItemId) ?? entry.Identity : entry.Identity, null);
                }

                continue;
            }

            // Gone: found again where it moved, else detached (an entry already detached stays as it is).
            if (_resolver is not null && entry.Identity is { } identity)
            {
                journal ??= _moves?.Read();
                var result = _resolver.Resolve(identity, RelinkPolicy.Default, journal, entry.AddedUtc);
                if (result.IsPending)
                {
                    continue;
                }

                if (result.ShouldRelink && result.Item is { } moved)
                {
                    fixes[entry.ItemId] = new EntryFix(moved.Id, ItemIdentity.Capture(moved, _library), null);
                    continue;
                }
            }

            if (entry.DetachedUtc is null)
            {
                fixes[entry.ItemId] = new EntryFix(entry.ItemId, entry.Identity, now);
            }
        }

        return fixes;
    }

    /// <summary>Applies the fixes of <see cref="Maintain"/> to the pins and excludes as they are now.</summary>
    /// <param name="current">The current pins and excludes.</param>
    /// <param name="fixes">The fixes.</param>
    /// <returns>The fixed pins and excludes.</returns>
    public static AutoCollectionManualEdits ApplyFixes(AutoCollectionManualEdits current, IReadOnlyDictionary<Guid, EntryFix> fixes)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fixes);
        return current with { Pinned = Fix(current.Pinned, fixes), Excluded = Fix(current.Excluded, fixes) };
    }

    /// <summary>
    /// Follows the move journal from the definitions' stored position: every pin and exclude on a moved item takes the
    /// item's new id, and the position is stored with them. Idempotent: a line only moves entries still on its old id.
    /// </summary>
    /// <param name="store">The definitions.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The definitions whose entries moved.</returns>
    public async Task<IReadOnlyList<Guid>> CatchUpAsync(AutoCollectionStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (_moves is null || store.IsReadOnly)
        {
            return [];
        }

        var current = store.Current;
        var catchUp = _moves.CatchUp(current.MoveJournalId, current.LastMoveSeq);
        if (!catchUp.HasChanges(current.MoveJournalId, current.LastMoveSeq))
        {
            return [];
        }

        var moved = new HashSet<Guid>();
        await store.UpdateAsync(
            d =>
            {
                moved.Clear();
                var definitions = d.Definitions.Select(definition =>
                {
                    if (definition?.ManualEdits is not { } edits)
                    {
                        return definition!;
                    }

                    var pinned = Follow(edits.Pinned, catchUp.Lines);
                    var excluded = Follow(edits.Excluded, catchUp.Lines);
                    if (ReferenceEquals(pinned, edits.Pinned) && ReferenceEquals(excluded, edits.Excluded))
                    {
                        return definition;
                    }

                    moved.Add(definition.Id);
                    return definition with { ManualEdits = edits with { Pinned = pinned, Excluded = excluded } };
                }).ToList();
                return AutoCollectionStore.WithDefinitions(d, definitions) with { MoveJournalId = catchUp.JournalId, LastMoveSeq = catchUp.LastMoveSeq };
            },
            cancellationToken).ConfigureAwait(false);
        return [.. moved];
    }

    // The entries on a moved item take its new id (the lines in order, so a chain of moves ends on the last id); the same
    // list when no line touches it.
    private static IReadOnlyList<AutoCollectionItemEntry> Follow(IReadOnlyList<AutoCollectionItemEntry>? entries, IReadOnlyList<MoveJournalLine> lines)
    {
        if (entries is null || entries.Count == 0 || lines.Count == 0)
        {
            return entries ?? [];
        }

        var result = entries.ToList();
        var changed = false;
        foreach (var line in lines)
        {
            for (var i = 0; i < result.Count; i++)
            {
                if (result[i] is { } entry && entry.ItemId.Equals(line.OldId) && !line.NewId.Equals(Guid.Empty))
                {
                    result[i] = entry with { ItemId = line.NewId, DetachedUtc = null };
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return entries;
        }

        // Two entries that moved onto one item are one.
        return result.Where(e => e is not null).GroupBy(e => e.ItemId).Select(g => g.First()).ToList();
    }

    private static List<AutoCollectionItemEntry> Fix(IReadOnlyList<AutoCollectionItemEntry>? entries, IReadOnlyDictionary<Guid, EntryFix> fixes)
    {
        var fixedEntries = (entries ?? []).Where(e => e is not null).Select(e => fixes.TryGetValue(e.ItemId, out var fix)
            ? e with { ItemId = fix.ItemId, Identity = fix.Identity ?? e.Identity, DetachedUtc = fix.DetachedUtc }
            : e);
        return fixedEntries.GroupBy(e => e.ItemId).Select(g => g.OrderBy(e => e.DetachedUtc is null ? 0 : 1).First()).ToList();
    }

    private static IEnumerable<Guid> Active(IEnumerable<AutoCollectionItemEntry>? entries)
        => (entries ?? []).Where(e => e is not null && e.DetachedUtc is null && !e.ItemId.Equals(Guid.Empty)).Select(e => e.ItemId);

    private AutoCollectionItemEntry NewEntry(Guid id, DateTime now) => new() { ItemId = id, Identity = Capture(id), AddedUtc = now };

    /// <summary>What an item is now, to find it again after a move.</summary>
    /// <param name="id">The item's id.</param>
    /// <returns>Its identity, or <c>null</c> when it can't be read.</returns>
    public IdentitySnapshot? Capture(Guid id)
    {
        try
        {
            return _library.GetItemById(id) is { } item ? ItemIdentity.Capture(item, _library) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Whether the server added the item on its own after a scan: its library adds movies to collections automatically
    // and the movie's collection name is this collection's (what the server's own task matches on).
    private bool IsServersOwnAdd(Guid id, BoxSet box)
    {
        try
        {
            return _library.GetItemById(id) is Movie movie
                && !string.IsNullOrEmpty(movie.CollectionName)
                && string.Equals(movie.CollectionName, box.Name, StringComparison.Ordinal)
                && _library.GetLibraryOptions(movie)?.AutomaticallyAddToCollection == true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private HashSet<Guid> Existing(IReadOnlyList<Guid> ids)
    {
        var existing = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(IdsPerQuery))
        {
            existing.UnionWith(_library.GetItemIds(new InternalItemsQuery
            {
                ItemIds = chunk,
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false }
            }));
        }

        return existing;
    }

    /// <summary>What a run's sorting of the edits came to.</summary>
    /// <param name="Changes">The changes to the pins and excludes, in order.</param>
    /// <param name="Pinned">The items kept as new pins.</param>
    /// <param name="Excluded">The items kept out as new excludes.</param>
    /// <param name="Reverted">The edits reverted.</param>
    /// <param name="TooMany">Whether the edits would take the pins or excludes over their limit (nothing is changed).</param>
    internal sealed record Sorted(IReadOnlyList<(Guid ItemId, Change Change)> Changes, int Pinned, int Excluded, int Reverted, bool TooMany);

    /// <summary>A fix to an entry.</summary>
    /// <param name="ItemId">The item's id now.</param>
    /// <param name="Identity">The item's identity.</param>
    /// <param name="DetachedUtc">When the item was found gone, or <c>null</c> while it resolves.</param>
    internal sealed record EntryFix(Guid ItemId, IdentitySnapshot? Identity, DateTime? DetachedUtc);
}
