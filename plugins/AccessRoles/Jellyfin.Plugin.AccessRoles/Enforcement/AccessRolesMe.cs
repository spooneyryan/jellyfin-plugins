using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// <c>GET /AccessRoles/Me</c>: the caller's own locks from the same snapshot the settings filter
/// uses, so the web guard hides exactly what the server refuses or merges, and whether a managed caller can open no
/// library. Nothing about anyone else.
/// </summary>
internal sealed class AccessRolesMe : IAccessRolesMe
{
    /// <summary>The message for a managed user who can open no library.</summary>
    public const string NoLibrariesMessage = "No libraries have been shared with you yet. Ask your server administrator.";

    /// <summary>The message when every Settings section is locked (the note above Sign out).</summary>
    public const string AllSettingsLockedMessage = "Settings are managed by your administrator";

    private readonly EffectiveRulesCache _cache;
    private readonly IServerUsers _users;
    private readonly ILibraryCatalogSource _catalog;
    private readonly Func<bool> _jellyfinEnhancedInstalled;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesMe"/> class.
    /// </summary>
    /// <param name="cache">The lock snapshots.</param>
    /// <param name="users">The server's users.</param>
    /// <param name="catalog">The libraries the server lists.</param>
    /// <param name="jellyfinEnhancedInstalled">Whether Jellyfin Enhanced's controller is routed (its section then counts).</param>
    /// <param name="logger">The logger.</param>
    public AccessRolesMe(EffectiveRulesCache cache, IServerUsers users, ILibraryCatalogSource catalog, Func<bool> jellyfinEnhancedInstalled, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(jellyfinEnhancedInstalled);
        ArgumentNullException.ThrowIfNull(logger);
        _cache = cache;
        _users = users;
        _catalog = catalog;
        _jellyfinEnhancedInstalled = jellyfinEnhancedInstalled;
        _logger = logger;
    }

    /// <inheritdoc />
    public AccessRolesMeDto GetMe(ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var userId = caller.GetUserId();
        if (caller.IsApiKey() || userId.Equals(Guid.Empty))
        {
            throw PluginException.Forbidden(CurrentUser.NoUserMessage);
        }

        LockSnapshot snapshot;
        try
        {
            snapshot = _cache.Current();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Access Roles couldn't read its data file for /Me");
            throw PluginException.Unavailable("Access Roles can't read its data just now. Try again in a moment.");
        }

        var administrator = caller.IsInRole(UserPolicyFilter.AdministratorRole);
        var managed = snapshot.IsManaged(userId);
        List<AccessRolesLock> locked = administrator ? [] : [.. AccessPresets.AllLocks.Where(snapshot.Reported(userId).Contains)];
        var sections = AccessPresets.SettingsSections.Where(s => s != AccessRolesLock.EnhancedPanel || _jellyfinEnhancedInstalled()).ToList();
        var allLocked = !administrator && sections.All(locked.Contains);
        var noLibraries = managed && OpensNoLibrary(userId);
        var answer = new AccessRolesMeDto
        {
            UserId = userId,
            Managed = managed,
            Locked = locked,
            AllSettingsLocked = allLocked,
            NoLibraries = noLibraries,
            Message = noLibraries ? NoLibrariesMessage : allLocked ? AllSettingsLockedMessage : null,
            NotEnforced = SettingsSections.NotEnforcedYet
        };
        answer.RulesVersion = Version(answer);
        return answer;
    }

    // A short hash of the answer: it changes exactly when the answer does.
    private static string Version(AccessRolesMeDto answer)
    {
        var text = string.Join(
            "|",
            answer.Managed.ToString(CultureInfo.InvariantCulture),
            string.Join(",", answer.Locked),
            answer.AllSettingsLocked.ToString(CultureInfo.InvariantCulture),
            answer.NoLibraries.ToString(CultureInfo.InvariantCulture),
            string.Join(",", answer.NotEnforced));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    // What the user sees: their native access opens none of the libraries the server lists.
    private bool OpensNoLibrary(Guid userId)
    {
        if (_users.Find(userId) is not { } user)
        {
            return false;
        }

        var listed = _catalog.Read();
        return !user.Native.EnableAllFolders && !user.Native.EnabledFolders.Any(listed.Contains);
    }
}
