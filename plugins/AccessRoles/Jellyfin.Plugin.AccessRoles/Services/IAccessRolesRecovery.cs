using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Api.Models;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// Lockout and recovery: ending Degraded mode, and releasing the plugin's native locks.
/// </summary>
public interface IAccessRolesRecovery
{
    /// <summary>
    /// Ends Degraded mode (<c>POST /AccessRoles/Admin/Degraded/Resolve</c>): the users it locks get only what the data file
    /// gives them. A dry run first; the confirmation sends its <c>PreviewToken</c> as <c>ifPreview</c>.
    /// </summary>
    /// <param name="options">The dry run and the token.</param>
    /// <param name="caller">The admin.</param>
    /// <param name="cancellationToken">Cancels the audit write.</param>
    /// <returns>What it locked, and whether it ended.</returns>
    Task<AccessRolesDegradedDto> ResolveDegradedAsync(AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);

    /// <summary>
    /// Releases native locks before the plugin is disabled or deleted (<c>POST /AccessRoles/Admin/ReleaseLocks</c>):
    /// the preference flags the optional mirror turned off go back on, with <paramref name="restoreLibraries"/> the
    /// ledger's original library access is written back (deleted libraries and ones created again since left out), and the
    /// users become <c>Excluded</c> (roles and ledger kept), so nothing writes the locks again. A dry run first; the
    /// confirmation sends its <c>PreviewToken</c> as <c>ifPreview</c> (409 <c>Changed</c> naming who changed). Audited
    /// <c>LocksReleased</c>.
    /// </summary>
    /// <param name="body">The users; none: everyone with a native lock to release.</param>
    /// <param name="restoreLibraries">Whether the original library access is restored too.</param>
    /// <param name="options">The dry run, <c>ifPreview</c> and <c>confirmNoFullAdmin</c>.</param>
    /// <param name="caller">The admin.</param>
    /// <param name="cancellationToken">Cancels before the first write.</param>
    /// <returns>Who was (or would be) released, and how.</returns>
    Task<AccessRolesReleaseLocksDto> ReleaseLocksAsync(AccessRolesReleaseLocksRequest? body, bool restoreLibraries, AccessRolesWriteOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);
}
