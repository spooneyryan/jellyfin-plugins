using System.Security.Claims;
using Jellyfin.Plugin.AccessRoles.Api.Models;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The user endpoint <c>GET /AccessRoles/Me</c> behind <c>AccessRolesController</c>: the caller's own
/// locks as the settings filter applies them.
/// </summary>
public interface IAccessRolesMe
{
    /// <summary>
    /// Describes the caller's own locks.
    /// </summary>
    /// <param name="caller">The request's user.</param>
    /// <returns>The answer.</returns>
    /// <remarks>An API key or a request without a user is refused with 403 (the kit's <c>Forbidden</c> kind).</remarks>
    AccessRolesMeDto GetMe(ClaimsPrincipal caller);
}
