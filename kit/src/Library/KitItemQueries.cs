using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The differences between the Jellyfin lines in how library items are queried, in one place.
/// </summary>
/// <remarks>
/// <para><b>Jellyfin 12</b> hides alternate versions (<c>PrimaryVersionId</c> set) and owned items that aren't extras from
/// every <c>InternalItemsQuery</c> unless it sets <c>IncludeAlternateVersions</c> / <c>IncludeOwnedItems</c>
/// (<c>BaseItemRepository.TranslateQuery.cs:798-812</c> at v12.4), <c>ILibraryManager.FindByPath</c> included. On 10.11
/// nothing was hidden. The kit's lookups keep 10.11's answers on both lines: <see cref="IncludeEveryVersion"/> on its
/// queries, <see cref="FindAnyByPath"/> instead of <c>FindByPath</c>.</para>
/// </remarks>
internal static class KitItemQueries
{
    /// <summary>
    /// Lets the query return alternate versions as separate items, as every query did on Jellyfin 10.11. Owned items stay
    /// as they were (callers keep them out by <c>TopParentIds</c> or <see cref="LibraryAccess.IsOwned"/>).
    /// </summary>
    /// <param name="query">The query.</param>
    /// <returns>The same query.</returns>
    public static InternalItemsQuery IncludeEveryVersion(InternalItemsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
#if JELLYFIN_12
        query.IncludeAlternateVersions = true;
#endif
        return query;
    }

    /// <summary>
    /// Lets the query return alternate versions and owned items (parts, local alternate versions), as every query did on
    /// Jellyfin 10.11.
    /// </summary>
    /// <param name="query">The query.</param>
    /// <returns>The same query.</returns>
    public static InternalItemsQuery IncludeEverything(InternalItemsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
#if JELLYFIN_12
        query.IncludeAlternateVersions = true;
        query.IncludeOwnedItems = true;
#endif
        return query;
    }

    /// <summary>
    /// The newest item at a path, of any kind, versions and owned items included: what <c>ILibraryManager.FindByPath</c>
    /// answers on 10.11. On 12 core's <c>FindByPath</c> never returns an alternate version or an owned item, so the
    /// same query is run with both flags. A blank path finds nothing (10.11.5's <c>FindByPath</c> has no path filter for
    /// one and answers with the newest item of the library).
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="path">The path.</param>
    /// <param name="isFolder">Folders only, files only, or either (null).</param>
    /// <returns>The item, or null.</returns>
    public static BaseItem? FindAnyByPath(ILibraryManager library, string? path, bool? isFolder)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

#if JELLYFIN_12
        var items = library.GetItemList(IncludeEverything(new InternalItemsQuery
        {
            Path = path,
            IsFolder = isFolder,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)],
            Limit = 1,
            DtoOptions = new DtoOptions(true)
        }));
        return items.Count > 0 ? items[0] : null;
#else
        return library.FindByPath(path, isFolder);
#endif
    }

    /// <summary>
    /// The video's primary version id as 10.11 stores it (the "N" form of a GUID), or null for a primary or a video that
    /// isn't a version. Jellyfin 12 keeps it as a <c>Guid?</c>.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns>The id text, or null.</returns>
    public static string? PrimaryVersionText(Video video)
    {
        ArgumentNullException.ThrowIfNull(video);
#if JELLYFIN_12
        return video.PrimaryVersionId is { } id && id != Guid.Empty ? id.ToString("N", System.Globalization.CultureInfo.InvariantCulture) : null;
#else
        return video.PrimaryVersionId;
#endif
    }

    /// <summary>
    /// The video's primary version id, or null for a primary or a video that isn't a version; <see cref="Guid.Empty"/>
    /// when 10.11 stores text that names no item.
    /// </summary>
    /// <param name="video">The video.</param>
    /// <returns>The id, or null.</returns>
    public static Guid? PrimaryVersionGuid(Video video)
    {
        ArgumentNullException.ThrowIfNull(video);
#if JELLYFIN_12
        return video.PrimaryVersionId is { } id && id != Guid.Empty ? id : null;
#else
        return string.IsNullOrWhiteSpace(video.PrimaryVersionId) ? null : Guid.TryParse(video.PrimaryVersionId, out var id) ? id : Guid.Empty;
#endif
    }

    /// <summary>
    /// A linked child's path: on 10.11 the path it was saved with; on 12 links carry only the item id, so null.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <returns>The path, or null.</returns>
    public static string? LinkPath(LinkedChild link)
    {
        ArgumentNullException.ThrowIfNull(link);
#if JELLYFIN_12
        return null;
#else
        return link.Path;
#endif
    }

    /// <summary>
    /// A linked child's cached library item id text: on 10.11 the id it was saved with; on 12 null (links carry only
    /// <c>ItemId</c>).
    /// </summary>
    /// <param name="link">The link.</param>
    /// <returns>The id text, or null.</returns>
    public static string? LinkLibraryItemId(LinkedChild link)
    {
        ArgumentNullException.ThrowIfNull(link);
#if JELLYFIN_12
        return null;
#else
        return link.LibraryItemId;
#endif
    }
}
