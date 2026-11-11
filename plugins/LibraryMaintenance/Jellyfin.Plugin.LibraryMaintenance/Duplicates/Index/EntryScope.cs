using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// What a caller sees of an index entry: only
/// the members in the libraries they can open, and nothing about the others. No member outside the scope shows as a
/// path, a size, a library, a count, a flag or a "≥": the representative is an in-scope member (the root when it is in
/// scope, else the in-scope member with the lowest id), the libraries are the in-scope members' within the scope, the
/// sources count the in-scope members (and the root's local alternates when the root is in scope), and a broken state
/// shows only when the members it is about are in scope. A member the index doesn't hold (<see cref="VersionEntry.ExternalIds"/>)
/// is in no scope, and neither is a member the caller's parental rating or tags hide (the hidden items:
/// every id a caller sees is one <c>GetItemById(id, user)</c> returns them; <see cref="Groups.ItemVisibility"/>).
/// </summary>
internal static class EntryScope
{
    /// <summary>The states that are the root's alone: they show when the root is in scope.</summary>
    private const VersionSetState RootStates = VersionSetState.Orphaned | VersionSetState.StaleLinks;

    /// <summary>
    /// Gets whether a member is in scope: the caller can see it, and any of its libraries is in the scope (null: every
    /// library the index read).
    /// </summary>
    /// <param name="member">The member.</param>
    /// <param name="scope">The libraries, or null.</param>
    /// <param name="hidden">The items the caller can't see, or null for none.</param>
    /// <returns>Whether the caller may see it.</returns>
    public static bool Contains(MovieRecord member, IReadOnlySet<Guid>? scope, IReadOnlySet<Guid>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (hidden is not null && hidden.Contains(member.Id))
        {
            return false;
        }

        if (scope is null)
        {
            return true;
        }

        foreach (var id in member.LibraryIds)
        {
            if (scope.Contains(id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether the caller sees every member of the set: none is outside the index, and each has a library in scope.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="scope">The libraries, or null.</param>
    /// <param name="hidden">The items the caller can't see, or null for none.</param>
    /// <returns>Whether the whole set is in scope.</returns>
    public static bool IsWhole(VersionEntry entry, IReadOnlySet<Guid>? scope, IReadOnlySet<Guid>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.ExternalIds.Count > 0)
        {
            return false;
        }

        foreach (var member in entry.Members)
        {
            if (!Contains(member, scope, hidden))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the libraries of the entry that are in scope (every one for a null scope), in the entry's order.
    /// </summary>
    /// <param name="libraryIds">The libraries.</param>
    /// <param name="scope">The scope, or null.</param>
    /// <returns>The libraries in scope; the same list when every one is.</returns>
    public static IReadOnlyList<Guid> Libraries(IReadOnlyList<Guid> libraryIds, IReadOnlySet<Guid>? scope)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        if (scope is null || libraryIds.All(scope.Contains))
        {
            return libraryIds;
        }

        return libraryIds.Where(scope.Contains).ToList();
    }

    /// <summary>
    /// Gets the caller's view of an entry, or null when no member is in scope.
    /// </summary>
    /// <param name="entry">The entry.</param>
    /// <param name="scope">The libraries, or null for every library the index read.</param>
    /// <param name="hidden">The items the caller can't see, or null for none.</param>
    /// <returns>The view.</returns>
    public static EntryView? View(VersionEntry entry, IReadOnlySet<Guid>? scope, IReadOnlySet<Guid>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (IsWhole(entry, scope, hidden))
        {
            return new EntryView(entry.Representative, entry.Members, Libraries(entry.LibraryIds, scope), entry.SourceCount, entry.State, true);
        }

        var inScope = entry.Members.Where(m => Contains(m, scope, hidden)).ToList();
        if (inScope.Count == 0)
        {
            return null;
        }

        var root = inScope.FirstOrDefault(m => m.Id == entry.RootId);
        var representative = root ?? inScope.MinBy(m => m.Id)!;
        List<MovieRecord> members = [representative, .. inScope.Where(m => m.Id != representative.Id).OrderBy(m => m.Id)];
        var ids = members.Select(m => m.Id).ToHashSet();
        var libraries = members.SelectMany(m => m.LibraryIds).Distinct().Where(id => scope is null || scope.Contains(id)).ToList();
        var sources = members.Count + (root?.LocalAlternateCount ?? 0);
        return new EntryView(representative, members, libraries, sources, State(entry, ids, root is not null), false);
    }

    // A partial set's states: the root's own (orphaned, stale links) when the root is in scope; a chained, unlisted or
    // listed-not-pointing member's when it and the root are; a cycle only for a whole set (never here).
    private static VersionSetState State(VersionEntry entry, HashSet<Guid> ids, bool rootInScope)
    {
        if (!rootInScope || entry.State == VersionSetState.None)
        {
            return VersionSetState.None;
        }

        var state = entry.State & RootStates;
        if (entry.State.HasFlag(VersionSetState.Chained) && entry.ChainedIds.Any(ids.Contains))
        {
            state |= VersionSetState.Chained;
        }

        if (entry.State.HasFlag(VersionSetState.Unlisted) && entry.UnlistedIds.Any(ids.Contains))
        {
            state |= VersionSetState.Unlisted;
        }

        if (entry.State.HasFlag(VersionSetState.ListedNotPointing) && entry.ListedNotPointingIds.Any(ids.Contains))
        {
            state |= VersionSetState.ListedNotPointing;
        }

        return state;
    }
}
