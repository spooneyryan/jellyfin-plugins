using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Resolves a collection's links to items, as the server would: the cached item id first, then the path (a folder's,
/// such as a series', resolves too), then the library item id of an item without a path. A link the server cached as the
/// empty id (after a failed lookup) is looked up again by its path; the empty id is never looked up.
/// </summary>
/// <remarks>
/// The cached ids are checked in batches (one id-only query per <see cref="IdsPerQuery"/> links): looking each link's item
/// up on its own loads the whole item, about half a millisecond each, which made an unchanged run of a collection of a
/// thousand members cost about half a second. Only a link whose cached id names no item is looked up one by one.
/// </remarks>
internal sealed class LinkResolver
{
    /// <summary>The cached ids checked per query.</summary>
    public const int IdsPerQuery = 500;

    private readonly ILibraryManager _library;
    private readonly LibraryAccess _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkResolver"/> class.
    /// </summary>
    /// <param name="library">The library manager (paths).</param>
    /// <param name="access">The kit's library access (ids).</param>
    public LinkResolver(ILibraryManager library, LibraryAccess access)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(access);
        _library = library;
        _access = access;
    }

    /// <summary>Resolves a collection's live links.</summary>
    /// <param name="box">The collection.</param>
    /// <returns>The resolved links.</returns>
    public ResolvedLinks Resolve(BoxSet box)
    {
        ArgumentNullException.ThrowIfNull(box);
        var members = new List<Guid>();
        var links = new Dictionary<Guid, LinkedChild>();
        var dead = new List<LinkedChild>();
        var duplicates = 0;
        var hash = new StringBuilder();
        var all = box.LinkedChildren ?? [];
        var existing = Existing(all);
        foreach (var link in all)
        {
            if (link is null)
            {
                continue;
            }

            hash.Append(link.Path).Append('|').Append(link.ItemId?.ToString("N")).Append('|').Append(link.LibraryItemId).Append('\n');
            var id = link.ItemId is { } cached && existing.Contains(cached) ? cached : Find(link)?.Id;
            if (id is not { } member)
            {
                dead.Add(link);
            }
            else if (links.TryAdd(member, link))
            {
                members.Add(member);
            }
            else
            {
                duplicates++;
            }
        }

        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString())));
        return new ResolvedLinks(members, links, dead.Count, duplicates, digest, dead);
    }

    /// <summary>Finds the item a link names.</summary>
    /// <param name="link">The link.</param>
    /// <returns>The item, or <c>null</c> for a dead link.</returns>
    public BaseItem? Find(LinkedChild link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.ItemId is { } cached && !cached.Equals(Guid.Empty) && _access.GetItem<BaseItem>(cached, null) is { } byId)
        {
            return byId;
        }

        if (!string.IsNullOrEmpty(link.Path) && _library.FindByPath(link.Path, null) is { } byPath)
        {
            return byPath;
        }

        return !string.IsNullOrEmpty(link.LibraryItemId) && Guid.TryParse(link.LibraryItemId, out var libraryId) && !libraryId.Equals(Guid.Empty)
            ? _access.GetItem<BaseItem>(libraryId, null)
            : null;
    }

    // The cached ids that name an item, asked in batches of ids only.
    private HashSet<Guid> Existing(LinkedChild[] links)
    {
        var existing = new HashSet<Guid>();
        var ids = links.Where(l => l?.ItemId is { } id && !id.Equals(Guid.Empty)).Select(l => l.ItemId!.Value).Distinct();
        foreach (var chunk in ids.Chunk(IdsPerQuery))
        {
            var query = new InternalItemsQuery
            {
                ItemIds = chunk,
                GroupByPresentationUniqueKey = false,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false }
            };
            existing.UnionWith(_library.GetItemIds(query) ?? []);
        }

        return existing;
    }
}
