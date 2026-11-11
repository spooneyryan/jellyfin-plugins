using System;
using System.Security.Claims;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.AccessRoles.Audit;

/// <summary>
/// Who did what an audit line records (<c>Actor</c>): an administrator, an API key (which has no owner), or the
/// plugin itself.
/// </summary>
/// <param name="Kind"><c>Admin</c>, <c>ApiKey</c> or <c>System</c>.</param>
/// <param name="UserId">The administrator's id, or <c>null</c>.</param>
/// <param name="Name">The administrator's name, or <c>null</c>.</param>
internal sealed record AuditActor(string Kind, Guid? UserId, string? Name)
{
    /// <summary>The kind of an administrator.</summary>
    public const string AdminKind = "Admin";

    /// <summary>The kind of an API key.</summary>
    public const string ApiKeyKind = "ApiKey";

    /// <summary>The kind of the plugin's own work.</summary>
    public const string SystemKind = "System";

    /// <summary>Gets the plugin as the actor.</summary>
    public static AuditActor System { get; } = new(SystemKind, null, null);

    /// <summary>Gets an API key as the actor.</summary>
    public static AuditActor ApiKey { get; } = new(ApiKeyKind, null, null);

    /// <summary>
    /// Gets the actor of a request an admin endpoint (or a hook on one) serves: the API key, or the administrator with their
    /// id and name.
    /// </summary>
    /// <param name="caller">The request's user.</param>
    /// <param name="nameOf">Finds a user's name by id, or <c>null</c>.</param>
    /// <returns>The actor.</returns>
    public static AuditActor Of(ClaimsPrincipal caller, Func<Guid, string?> nameOf)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(nameOf);
        if (caller.IsApiKey())
        {
            return ApiKey;
        }

        var id = caller.GetUserId();
        return new AuditActor(AdminKind, id.Equals(Guid.Empty) ? null : id, id.Equals(Guid.Empty) ? null : nameOf(id));
    }
}
