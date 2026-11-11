using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Whether the library still has an item: <c>GetItemById</c> finds it, and every parent up its
/// chain too, up to the library's own folders.
/// </summary>
/// <remarks>
/// <c>GetItemById</c> is cache-backed, and on 10.11.5 deleting a music artist folder leaves its albums and their tracks
/// in that cache (not in the database: a query no longer lists them), with their parent links to the deleted artist, so
/// the item alone would look there until the server restarts (seen in a sandbox). The verify sweep
/// (<see cref="AttachedRecordVerifier"/>), the resolver's steps 1 and 2 (<see cref="IdentityResolver"/>), its
/// <see cref="IdentityResolver.VisibleTo"/> and a consumer's own "is the item still there" checks all ask here, so a
/// record detached from such a ghost is never taken back to it. The walk stops at an <see cref="AggregateFolder"/>, a
/// <see cref="UserRootFolder"/> or an <see cref="ICollectionFolder"/> (a library, or the collections and playlists
/// folders), at an empty parent id (an item core keeps outside the folder tree, such as an artist by name), or after
/// <see cref="MaxParentDepth"/> links, taking the item as there.
/// </remarks>
internal static class LibraryPresence
{
    /// <summary>How far up an item's parents the walk looks before it takes the item as there.</summary>
    public const int MaxParentDepth = 32;

    /// <summary>
    /// Finds an item by id, only when the library still has it and every parent up its chain.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="id">The item id; an empty id finds nothing (<c>GetItemById(Guid.Empty)</c> throws in 10.11.5).</param>
    /// <param name="known">Answers already found, by id, for a batch of lookups; filled as the walk goes.</param>
    /// <returns>The item, or null.</returns>
    public static BaseItem? Find(ILibraryManager library, Guid id, IDictionary<Guid, bool>? known = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (id == Guid.Empty)
        {
            return null;
        }

        var item = library.GetItemById(id);
        return item is not null && IsInLibrary(library, item, known) ? item : null;
    }

    /// <summary>
    /// Whether the library still has the item with this id, and every parent up its chain.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="id">The item id; an empty id is never there.</param>
    /// <param name="known">Answers already found, by id, for a batch of lookups; filled as the walk goes.</param>
    /// <returns>Whether it has.</returns>
    public static bool IsInLibrary(ILibraryManager library, Guid id, IDictionary<Guid, bool>? known = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        return id != Guid.Empty && Walk(library, id, null, known);
    }

    /// <summary>
    /// Whether the library still has every parent up an item's chain (the item itself is in hand: from
    /// <c>GetItemById</c>, a query, or an event).
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="item">The item.</param>
    /// <param name="known">Answers already found, by id, for a batch of lookups; filled as the walk goes.</param>
    /// <returns>Whether it has.</returns>
    public static bool IsInLibrary(ILibraryManager library, BaseItem item, IDictionary<Guid, bool>? known = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(item);
        return Walk(library, item.Id, item, known);
    }

    private static bool Walk(ILibraryManager library, Guid id, BaseItem? first, IDictionary<Guid, bool>? known)
    {
        var chain = new List<Guid>();
        var found = true;
        var current = id;
        var item = first;
        while (current != Guid.Empty && chain.Count < MaxParentDepth)
        {
            if (known is not null && known.TryGetValue(current, out var answer))
            {
                found = answer;
                break;
            }

            chain.Add(current);
            item ??= library.GetItemById(current);
            if (item is null)
            {
                found = false;
                break;
            }

            if (item is AggregateFolder or UserRootFolder or ICollectionFolder)
            {
                break;
            }

            current = item.ParentId;
            item = null;
        }

        if (known is not null)
        {
            foreach (var link in chain)
            {
                known[link] = found;
            }
        }

        return found;
    }
}
