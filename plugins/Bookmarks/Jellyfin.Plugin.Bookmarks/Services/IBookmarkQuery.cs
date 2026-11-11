using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The lists of the My Bookmarks page: the caller's live bookmarks, filtered and ordered in
/// memory, flat (<see cref="Query"/>) or one group per video (<see cref="Groups"/>). Tombstones and other users' bookmarks
/// are never in them. The filters and the counts work on the stored records only; the library is asked just once per
/// distinct video on the returned page, as the caller, for <c>ItemAvailable</c>, and never across
/// requests.
/// </summary>
/// <remarks>
/// Public because the public controller takes it: DTOs, Jellyfin's public types and BCL types only.
/// </remarks>
public interface IBookmarkQuery
{
    /// <summary>
    /// Lists the caller's bookmarks that match the filters, one row each, a page at a time (<c>GET /Bookmarks/Query</c>).
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="filter">The filters, order and page.</param>
    /// <param name="sortBy">The order; <see cref="BookmarkSortBy.UpdatedAt"/>, newest first, by default.</param>
    /// <returns>The page, with the number of matching bookmarks.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for a search term over 100 characters, a negative start or a limit
    /// outside 1 to 200.</exception>
    QueryResult<BookmarkDto> Query(User user, BookmarkQueryFilter filter, BookmarkSortBy? sortBy);

    /// <summary>
    /// Lists the videos with the caller's bookmarks that match the filters, one group each with its bookmarks by position
    /// (at most 100), a page of groups at a time (<c>GET /Bookmarks/Groups</c>).
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="filter">The filters, order and page (25 groups by default).</param>
    /// <param name="sortBy">The order of the groups; <see cref="BookmarkGroupSortBy.LatestUpdated"/>, newest first, by
    /// default.</param>
    /// <returns>The page, with the number of matching groups.</returns>
    /// <exception cref="Kit.Api.PluginException">400 as for <see cref="Query"/>.</exception>
    QueryResult<BookmarkGroupDto> Groups(User user, BookmarkQueryFilter filter, BookmarkGroupSortBy? sortBy);
}
