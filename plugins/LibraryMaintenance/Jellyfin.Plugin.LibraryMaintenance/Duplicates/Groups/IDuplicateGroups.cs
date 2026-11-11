using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;

/// <summary>
/// The duplicate groups as the Duplicates controller sees them (<c>GET /Groups</c>): the index read for a
/// request, grouped with the options for the libraries the caller can see.
/// </summary>
public interface IDuplicateGroups
{
    /// <summary>
    /// Gets a page of groups.
    /// </summary>
    /// <param name="mode"><c>TitleAndYear</c> or <c>Title</c>; null for the settings' default.</param>
    /// <param name="unknownYear"><c>MatchUnknownOnly</c> or <c>MatchAny</c>; null for the settings' default.</param>
    /// <param name="libraryIds">Library ids, comma-separated; null for every library the caller can see.</param>
    /// <param name="confidence">Confidences to keep, comma-separated; null for all.</param>
    /// <param name="search">A text filter on the title.</param>
    /// <param name="sort"><c>Confidence</c>, <c>Title</c>, <c>Reclaimable</c>, <c>Size</c> or <c>Newest</c>.</param>
    /// <param name="startIndex">The page's first group.</param>
    /// <param name="limit">The page size, 1 to 200 (50).</param>
    /// <param name="user">The caller, or null for an API key (every library).</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>The page, or null while the server has no index yet (<see cref="GetPending"/>).</returns>
    Task<LibraryMaintenanceDuplicateGroupsResultDto?> GetGroupsAsync(
        string? mode,
        string? unknownYear,
        string? libraryIds,
        string? confidence,
        string? search,
        string? sort,
        int? startIndex,
        int? limit,
        User? user,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets one group by its id, grouped with the same options (the id depends on them); 404 when no such group is left.
    /// </summary>
    /// <param name="groupId">The group id.</param>
    /// <param name="mode">The match mode, as for <see cref="GetGroupsAsync"/>.</param>
    /// <param name="unknownYear">The unknown-year policy.</param>
    /// <param name="libraryIds">The libraries.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>The group, or null while the server has no index yet.</returns>
    Task<LibraryMaintenanceDuplicateGroupDto?> GetGroupAsync(
        string groupId,
        string? mode,
        string? unknownYear,
        string? libraryIds,
        User? user,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets what the first build is doing, for the 202 while there is no index.
    /// </summary>
    /// <returns>The build's state.</returns>
    LibraryMaintenanceDuplicateGroupsPendingDto GetPending();
}
