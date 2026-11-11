using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Api.Models;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The Settings tab's API: the settings read, and changed through a dry run and its token, audited
/// (<c>SettingsChanged</c>).
/// </summary>
public interface IAccessRolesSettings
{
    /// <summary>
    /// Gets the settings as this build reads them.
    /// </summary>
    /// <returns>The settings.</returns>
    AccessRolesSettingsDto Get();

    /// <summary>
    /// Changes the settings the body sets, or with <c>dryRun</c> only says what that would do.
    /// </summary>
    /// <param name="body">The values to set; a value left out stays.</param>
    /// <param name="options">The dry run, its token and the secret keys' confirmation.</param>
    /// <param name="caller">The admin, for the audit line.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>What the change does.</returns>
    Task<AccessRolesSettingsChangeDto> UpdateAsync(AccessRolesSettingsDto body, AccessRolesSettingsOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken);
}
