using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// The read-only "Also rated on another copy" hint: other items of the same kind sharing a strong provider
/// key (TMDb, IMDb or TVDb; for episodes and seasons the series' key with the numbers) that have attached rows, found through
/// <c>RatingKeys</c> (their item ids only, <see cref="IRatingsStore.FindItemIdsByKeys"/>). Each copy is kept only if the
/// viewer can open it as <c>GetItemById&lt;BaseItem&gt;(id, user)</c> would, parents included (the kit's
/// <c>LibraryAccess.EligibleIds</c>), so the hint never reveals a library, or a copy, the viewer
/// can't open; its summary follows the same <see cref="VisibilityPolicy"/> as the main one. At most <see cref="MaxSiblings"/>.
/// </summary>
/// <remarks>
/// Books and AudioBooks get no strong keys (book providers set none of TMDb, IMDb and TVDb, which the kit's
/// <c>ItemIdentity.StrongKeys</c> takes for them), so they never get a hint; relinking, which shares those keys, has the same limit.
/// </remarks>
internal sealed class CopySiblings
{
    /// <summary>The most copies the hint names.</summary>
    public const int MaxSiblings = 3;

    private readonly IRatingsStore _store;
    private readonly ItemAccess _access;
    private readonly SummaryCache _cache;
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="CopySiblings"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="access">Item access.</param>
    /// <param name="cache">The summary cache.</param>
    /// <param name="library">The library manager (the copies' libraries).</param>
    public CopySiblings(IRatingsStore store, ItemAccess access, SummaryCache cache, ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(library);
        _store = store;
        _access = access;
        _cache = cache;
        _library = library;
    }

    /// <summary>
    /// Finds the copies of an item the viewer can open that have ratings the viewer may see (or the viewer's own).
    /// </summary>
    /// <param name="identity">The item's identity, as captured now.</param>
    /// <param name="viewer">The viewer.</param>
    /// <param name="policy">The request's visibility policy.</param>
    /// <returns>The copies, most rated first; none when the item has no strong key.</returns>
    public IReadOnlyList<CopySibling> Find(IdentitySnapshot identity, User viewer, VisibilityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(policy);
        var keys = ItemIdentity.StrongKeys(identity);
        if (keys.Count == 0)
        {
            return [];
        }

        // The items' ids only: the rows, with every author's review text, are never read for the hint.
        var candidates = _store.FindItemIdsByKeys(keys).Where(id => id != identity.ItemId).ToList();
        if (candidates.Count == 0)
        {
            return [];
        }

        var visible = _access.VisibleIds(viewer, candidates);
        if (visible.Count == 0)
        {
            return [];
        }

        var rows = _cache.Get(visible.ToList());
        var libraries = _access.VisibleLibraries(viewer);
        return visible
            .Select(id => (Id: id, Summary: policy.Summarize(rows[id]), Mine: policy.MyStars(rows[id], viewer.Id)))
            .Where(s => s.Summary.Count > 0 || s.Summary.ReviewCount > 0 || s.Mine is not null)
            .OrderByDescending(s => s.Summary.Count)
            .ThenByDescending(s => s.Summary.ReviewCount)
            .ThenBy(s => s.Id)
            .Take(MaxSiblings)
            .Select(s => new CopySibling(s.Id, LibraryName(s.Id, libraries), s.Summary, s.Mine))
            .ToList();
    }

    // The copy's library among the viewer's, for "Also rated on another copy (Movies 2)".
    private string? LibraryName(Guid itemId, IReadOnlyList<CollectionFolder> libraries)
    {
        if (_library.GetItemById(itemId) is not { } item)
        {
            return null;
        }

        var visible = libraries.Select(l => l.Id).ToHashSet();
        return _library.GetCollectionFolders(item).FirstOrDefault(f => visible.Contains(f.Id))?.Name;
    }
}
