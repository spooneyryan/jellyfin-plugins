using System;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// A user of the server as the resolver sees them (<c>EffectiveRulesResolver</c>'s input).
/// </summary>
/// <param name="Id">The user's id.</param>
/// <param name="Name">The user's name.</param>
/// <param name="IsAdministrator">The stored administrator flag: for the lock exemption (administrators are never restricted), display and the lockout
/// check, never for an authorization decision (that comes from the role claim).</param>
/// <param name="IsDisabled">Whether the account is disabled.</param>
/// <param name="IsHidden">Whether the user is hidden from the login screen.</param>
/// <param name="Native">The user's native access now.</param>
internal sealed record ServerUser(Guid Id, string Name, bool IsAdministrator, bool IsDisabled, bool IsHidden, NativeAccess Native);
