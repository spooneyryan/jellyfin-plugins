using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kit.Api;

/// <summary>
/// The caller of a request, from the claims Jellyfin's authentication handler sets.
/// </summary>
/// <remarks>
/// <para>User endpoints call <see cref="RequireUser"/>, which refuses API keys: plugin data always belongs to a user,
/// and own-data operations never take a <c>userId</c> parameter.</para>
/// <para>Admin decisions inside a user endpoint call <see cref="IsElevatedAsync"/>, which runs Jellyfin's own
/// <c>RequiresElevation</c> policy exactly as an <c>[Authorize(Policy = Policies.RequiresElevation)]</c> endpoint does,
/// so a demotion or a vetoing authorisation handler applies from the next request. <see cref="IsAdmin"/> reads the
/// stored permission and is for display and for background work without a caller only.</para>
/// </remarks>
internal static class CurrentUser
{
    /// <summary>
    /// The claim holding the user id (<c>Jellyfin.Api.Constants.InternalClaimTypes.UserId</c>, v10.11.5
    /// Jellyfin.Api/Constants/InternalClaimTypes.cs:11). Plugins can't reference Jellyfin.Api, so the literal is copied
    /// here and pinned by a test.
    /// </summary>
    public const string UserIdClaimType = "Jellyfin-UserId";

    /// <summary>
    /// The claim telling API-key requests apart (<c>InternalClaimTypes.IsApiKey</c>, same file, line 41).
    /// </summary>
    public const string IsApiKeyClaimType = "Jellyfin-IsApiKey";

    /// <summary>
    /// The role claim's value for administrators and API keys (<c>Jellyfin.Api.Constants.UserRoles.Administrator</c>,
    /// v10.11.5 Jellyfin.Api/Constants/UserRoles.cs), copied as <see cref="UserIdClaimType"/> is.
    /// </summary>
    public const string AdministratorRole = "Administrator";

    /// <summary>The message of <see cref="RequireUser"/> for API keys and requests without a user.</summary>
    public const string NoUserMessage = "This requires a signed-in user; API keys can't be used.";

    /// <summary>The message of <see cref="RequireUser"/> for a user id that no longer exists.</summary>
    public const string UnknownUserMessage = "The signed-in user no longer exists.";

    /// <summary>
    /// Gets the user id of the caller.
    /// </summary>
    /// <param name="principal">The request's user.</param>
    /// <returns>The id, or <see cref="Guid.Empty"/> for API keys and anonymous or malformed requests.</returns>
    public static Guid GetUserId(this ClaimsPrincipal principal)
        => Guid.TryParse(FindClaim(principal, UserIdClaimType), out var userId) ? userId : Guid.Empty;

    /// <summary>
    /// Gets a value indicating whether the request authenticated with an API key rather than a user's token.
    /// </summary>
    /// <param name="principal">The request's user.</param>
    /// <returns><c>true</c> for API keys.</returns>
    public static bool IsApiKey(this ClaimsPrincipal principal)
        => bool.TryParse(FindClaim(principal, IsApiKeyClaimType), out var isApiKey) && isApiKey;

    /// <summary>
    /// Gets the calling user, or refuses the request with 403.
    /// </summary>
    /// <param name="principal">The request's user.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="noUserMessage">The message for API keys and requests without a user; defaults to <see cref="NoUserMessage"/>.</param>
    /// <param name="unknownUserMessage">The message for a user that no longer exists; defaults to <see cref="UnknownUserMessage"/>.</param>
    /// <returns>The user.</returns>
    /// <exception cref="PluginException">Kind <see cref="PluginErrorKind.Forbidden"/> for API keys and unknown users.</exception>
    public static User RequireUser(
        this ClaimsPrincipal principal,
        IUserManager userManager,
        string? noUserMessage = null,
        string? unknownUserMessage = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(userManager);

        var userId = principal.GetUserId();
        if (principal.IsApiKey() || userId.Equals(Guid.Empty))
        {
            throw PluginException.Forbidden(noUserMessage ?? NoUserMessage);
        }

        return userManager.GetUserById(userId)
            ?? throw PluginException.Forbidden(unknownUserMessage ?? UnknownUserMessage);
    }

    /// <summary>
    /// Evaluates Jellyfin's <c>RequiresElevation</c> policy for the request, with the <see cref="HttpContext"/> as the
    /// resource, exactly as an <c>[Authorize(Policy = Policies.RequiresElevation)]</c> endpoint is evaluated. This is the
    /// only admin check for an authorisation decision. API keys pass it, as they pass admin endpoints.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns><c>true</c> when the caller may use admin endpoints.</returns>
    public static async Task<bool> IsElevatedAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(context.User, context, Policies.RequiresElevation).ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <summary>
    /// Gets the stored administrator flag. For display (badges, labels) and for background work without a caller only;
    /// never for an authorisation decision (use <see cref="IsElevatedAsync"/>).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns><c>true</c> when the user's policy says administrator.</returns>
    public static bool IsAdmin(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.HasPermission(PermissionKind.IsAdministrator);
    }

    /// <summary>
    /// Gets a value indicating whether the request's role claim is Jellyfin's administrator role, which its authentication
    /// handler sets for administrators and API keys at every request (v10.11.5 Jellyfin.Api/Auth/CustomAuthenticationHandler.cs).
    /// For wording only, such as which sentence an error shows (<see cref="PluginException.NonAdminMessage"/>); never for an
    /// authorisation decision (use <see cref="IsElevatedAsync"/>).
    /// </summary>
    /// <param name="principal">The request's user, if any.</param>
    /// <returns><c>true</c> for administrators and API keys.</returns>
    public static bool HasAdministratorRole(this ClaimsPrincipal? principal)
        => principal?.Claims.Any(c => c.Type == ClaimTypes.Role && c.Value == AdministratorRole) == true;

    /// <summary>
    /// Gets a value indicating whether the user has at least one of the permissions.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="kinds">The permissions.</param>
    /// <returns><c>true</c> when any is set.</returns>
    public static bool HasAny(User user, params PermissionKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(kinds);
        return kinds.Any(user.HasPermission);
    }

    // Jellyfin compares claim types case-insensitively (Jellyfin.Api/Extensions/ClaimsPrincipalExtensions.cs).
    private static string? FindClaim(ClaimsPrincipal principal, string type)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Claims.FirstOrDefault(claim => claim.Type.Equals(type, StringComparison.OrdinalIgnoreCase))?.Value;
    }
}
