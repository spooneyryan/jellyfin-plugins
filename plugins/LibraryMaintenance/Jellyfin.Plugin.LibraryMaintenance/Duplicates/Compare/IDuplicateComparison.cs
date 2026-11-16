using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Compare;

/// <summary>
/// A duplicate group's copies side by side (<c>GET …/Groups/{groupId}/Compare</c>), read from Jellyfin's library when
/// asked, one group at a time, so the groups' list costs nothing more. Read-only.
/// </summary>
public interface IDuplicateComparison
{
    /// <summary>
    /// Compares the copies of one group, found as <c>GET …/Groups/{groupId}</c> finds it (the same options, libraries and
    /// visibility); 404 when no such group is left.
    /// </summary>
    /// <param name="groupId">The group id.</param>
    /// <param name="mode">The match mode the group came from.</param>
    /// <param name="unknownYear">The unknown-year policy.</param>
    /// <param name="libraryIds">The libraries.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>The comparison, or null while the server has no index yet.</returns>
    Task<LibraryMaintenanceDuplicateCompareDto?> CompareAsync(
        string groupId,
        string? mode,
        string? unknownYear,
        string? libraryIds,
        User? user,
        CancellationToken cancellationToken);
}
