using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// <see cref="IServerUsers"/> over the server's <see cref="IUserManager"/>: the list through <see cref="KitUsers"/> and each
/// user's fields from the user manager's object, which is what the server enforces (on 10.11.5 its cached object, the one
/// <c>GetUserDto</c> reads). It only reads.
/// </summary>
internal sealed class JellyfinServerUsers : IServerUsers
{
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinServerUsers"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    public JellyfinServerUsers(IUserManager userManager)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        _userManager = userManager;
    }

    /// <inheritdoc />
    public IReadOnlyList<ServerUser> GetAll()
        => [.. KitUsers.GetAll(_userManager).GroupBy(u => u.Id).Select(g => From(g.First())).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ThenBy(u => u.Id)];

    /// <inheritdoc />
    public ServerUser? Find(Guid id) => KitUsers.Find(_userManager, id) is { } user ? From(user) : null;

    /// <summary>
    /// Reads a user's fields.
    /// </summary>
    /// <param name="user">The user manager's object.</param>
    /// <returns>The fields.</returns>
    internal static ServerUser From(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return new ServerUser(
            user.Id,
            user.Username,
            user.HasPermission(PermissionKind.IsAdministrator),
            user.HasPermission(PermissionKind.IsDisabled),
            user.HasPermission(PermissionKind.IsHidden),
            new NativeAccess(
                user.HasPermission(PermissionKind.EnableAllFolders),
                user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders),
                user.EnableUserPreferenceAccess,
                user.HasPermission(PermissionKind.EnableLiveTvManagement),
                user.GetPreferenceValues<Guid>(PreferenceKind.BlockedMediaFolders)));
    }
}
