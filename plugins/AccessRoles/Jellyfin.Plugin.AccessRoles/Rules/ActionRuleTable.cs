using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AccessRoles.Model;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// The <c>ActionRuleTable</c>: what the global filter does with an action, read from the
/// <see cref="ActionManifest"/> by controller full name and action name (stable across route aliases and BaseUrl),
/// plus the one row of another plugin the manifest can't hold: Jellyfin Enhanced's <c>user-settings</c> saves,
/// matched at runtime by controller and route prefix. Resolved once per action descriptor and cached, so a
/// request costs one dictionary lookup (the ~95 % of requests without a rule return at once).
/// </summary>
/// <remarks>
/// No route string of a Jellyfin.Api action is written here: the manifest is the mapping. Each rule has an owner
/// (<see cref="Owner"/>): the settings rules, the library sync's hooks, and the admin surfaces, the server-wide
/// rules and the hardening are applied, and the <c>Strict</c> rules are classified and passed through. Since 0.9.7 an action outside the
/// manifest that binds <c>parentId</c>, <c>seriesId</c> or <c>seasonId</c> gets the generic backstop at run time (any
/// other action, Jellyfin's or a plugin's).
/// </remarks>
internal sealed partial class ActionRuleTable
{
    /// <summary>Jellyfin Enhanced's controller (<c>je:…/Controllers/JellyfinEnhancedController.cs:43,45</c>).</summary>
    public const string JellyfinEnhancedController = "Jellyfin.Plugin.JellyfinEnhanced.Controllers.JellyfinEnhancedController";

    /// <summary>Jellyfin Enhanced's assembly, whose other controllers (12.14's <c>CatchUpController</c>) the self-check covers too.</summary>
    public const string JellyfinEnhancedAssembly = "Jellyfin.Plugin.JellyfinEnhanced";

    /// <summary>The assembly of the manifest's actions.</summary>
    public const string JellyfinApiAssembly = "Jellyfin.Api";

    // The HTTP methods that write nothing: every other one is a write, as is an action that takes any method.
    private static readonly IReadOnlySet<string> _readMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS" };

    private readonly ActionManifest _manifest;
    private readonly ConcurrentDictionary<string, ActionRules> _byDescriptor = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ActionRuleTable"/> class.
    /// </summary>
    /// <param name="manifest">The action manifest of this build's Jellyfin line.</param>
    public ActionRuleTable(ActionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _manifest = manifest;
    }

    /// <summary>Gets the parameters the generic backstop checks, in any letter case.</summary>
    public static IReadOnlyList<string> BackstopParameterNames { get; } = ["parentId", "seriesId", "seasonId"];

    /// <summary>
    /// Gets Jellyfin Enhanced's settings saves that were reviewed: the five <c>user-settings</c> saves (JE 12.8,
    /// the same five in 12.12), and from JE 12.14 <c>SaveUserSettingsDdd</c>
    /// (<c>ddd-triggers.json</c>) and <c>SetSpoilerBlurUserPrefs</c> (<c>POST spoiler-blur/user-prefs</c>: a
    /// read-modify-write of the caller's own <c>spoilerblur.json</c>, its <c>Prefs</c>, the file <c>SaveUserSpoilerBlur</c>
    /// writes; read from the 12.14.0.0 DLL's IL). The self-check lists any other locked one as unreviewed (it is locked all
    /// the same, by its route).
    /// </summary>
    public static IReadOnlySet<string> ReviewedJellyfinEnhancedSaves { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "SaveUserSettingsSettings", "SaveUserSettingsShortcuts", "SaveUserSettingsElsewhere", "SaveUserHiddenContent", "SaveUserSpoilerBlur",
        "SaveUserSettingsDdd", "SetSpoilerBlurUserPrefs"
    };

    /// <summary>
    /// Gets Jellyfin Enhanced's settings saves outside <c>user-settings/{userId}/</c> that the <c>EnhancedPanel</c> lock
    /// covers, by their route after the controller's prefix: JE 12.14's <c>spoiler-blur/user-prefs</c>,
    /// which writes the caller's Spoiler Guard preferences into the same <c>spoilerblur.json</c> as the locked
    /// <c>SaveUserSpoilerBlur</c>.
    /// </summary>
    public static IReadOnlySet<string> JellyfinEnhancedLockedRoutes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spoiler-blur/user-prefs" };

    /// <summary>Gets Jellyfin Enhanced's bookmark writes under <c>user-settings</c>, which no lock covers.</summary>
    public static IReadOnlySet<string> ReviewedJellyfinEnhancedBookmarkSaves { get; } = new HashSet<string>(StringComparer.Ordinal) { "SaveUserBookmark", "AddUserBookmark", "RemoveUserBookmark" };

    /// <summary>
    /// Gets Jellyfin Enhanced's writes that were reviewed as the user's own state and are left unlocked (a
    /// deliberate decision): EnhancedPanel is JE's settings panel, and these are what a user marks item
    /// by item, undoable by the user, so a locked per-item toggle would only fail with an unexplained 403. JE 12.14's
    /// bookmarks (<c>user-settings/{userId}/bookmark.json</c>, <c>…/add</c>, <c>DELETE …/{bookmarkId}</c>), its Hidden Content
    /// (<c>continue-watching/hide/{itemId}</c>, <c>next-up/hide/{itemId}</c>, <c>POST</c> and <c>DELETE</c>; the
    /// <c>DELETE</c>s also undo an administrator's <c>AdminHideForUser</c>), its Spoiler Guard marks
    /// (<c>spoiler-blur/series|movies|collections/{id}</c> and <c>spoiler-blur/pending/{mediaType}/{tmdbId}</c>, <c>POST</c>
    /// and <c>DELETE</c>, read-modify-writes of the caller's own <c>spoilerblur.json</c> lists, and
    /// <c>DELETE spoiler-blur/health/{targetUserId}</c>, the caller's own corruption notice or an administrator's) and its
    /// reviews (<c>reviews/{mediaType}/{tmdbId}</c>, <c>POST</c> and <c>DELETE</c>; the rating mirrored to the caller's user
    /// data). So Access Roles doesn't lock JE's Hidden Content or Spoiler Guard lists.
    /// </summary>
    public static IReadOnlySet<string> ReviewedJellyfinEnhancedUnlocked { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "SaveUserBookmark", "AddUserBookmark", "RemoveUserBookmark",
        "HideFromContinueWatching", "HideFromNextUp", "UnhideFromContinueWatching", "UnhideFromNextUp",
        "EnableSpoilerBlurForSeries", "DisableSpoilerBlurForSeries", "EnableSpoilerBlurForMovie", "DisableSpoilerBlurForMovie",
        "EnableSpoilerBlurForCollection", "DisableSpoilerBlurForCollection", "EnableSpoilerBlurPending", "DisableSpoilerBlurPending",
        "AckSpoilerBlurCorruption",
        "UpsertReview", "DeleteReview"
    };

    /// <summary>
    /// Gets Jellyfin Enhanced's writes that were reviewed as no settings at all (read from JE 12.14.0.0's IL):
    /// its Seerr requests, issues and approvals (<c>jellyseerr/request</c>, <c>jellyseerr/request/tv/{tmdbId}/seasons</c>,
    /// <c>jellyseerr/issue</c>, <c>arr/requests/{requestId}/approve|decline</c>, under JE's own Seerr permissions) and the
    /// calendar's user data (<c>arr/calendar/user-data</c>, a read sent as a <c>POST</c>), which stay outside the
    /// rules; the tag strip's read (<c>tag-data/{userId}</c>, also a <c>POST</c>); a review's translation (<c>translate</c>,
    /// nothing stored); the usage counters (<c>usage/track</c>, server-wide counts); and Catch Up's event log
    /// (<c>CatchUpController.PostEvent</c>, the caller's own swipes).
    /// </summary>
    public static IReadOnlySet<string> ReviewedJellyfinEnhancedOutsideSettings { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "JellyseerrRequest", "RequestTvSeasons", "ReportJellyseerrIssue", "ActOnRequest", "GetCalendarUserDataForEvents",
        "GetTagData", "TranslateReview", "TrackUsage", "CatchUpController.PostEvent"
    };

    /// <summary>
    /// Gets Jellyfin Enhanced's writes that answer a non-administrator 403 before anything else (JE checks it in the action,
    /// <c>IsAdminUser</c> then <c>Forbid</c>, under a plain <c>[Authorize]</c>; read from JE 12.14.0.0's IL):
    /// no lock applies to an administrator, so they need no rule. A JE action whose own policy requires an administrator
    /// (<c>RequiresElevation</c>) needs none either; the self-check finds those by their policy.
    /// </summary>
    public static IReadOnlySet<string> ReviewedJellyfinEnhancedAdminOnly { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "TriggerJellyseerrRecentlyAddedScan", "SyncJellyseerrWatchlist", "ImportJellyseerrUsers", "PreviewUsageReport",
        "AdminHideForUser", "AdminUnhideForUser", "StartSpoilerApplyExisting", "AdminUpsertReview", "AdminDeleteReview",
        "ResetAllUsersSettings", "RebuildTagCache", "DismissWhatsNew", "UploadBrandingImage", "DeleteBrandingImage",
        "BroadcastMessage", "EnableMaintenanceMode", "DisableMaintenanceMode", "BroadcastMaintenanceMessage"
    };

    /// <summary>Gets the manifest the table reads.</summary>
    public ActionManifest Manifest => _manifest;

    /// <summary>
    /// Gets the task that applies a rule.
    /// </summary>
    /// <param name="rule">The rule.</param>
    /// <returns>Its owner.</returns>
    public static RuleOwner Owner(ActionManifestRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Kind switch
        {
            RuleKind.LockedReject => rule.Lock is AccessRolesLock.AdminSurfaces or AccessRolesLock.UserDirectory ? RuleOwner.ServerHardening : RuleOwner.Settings,
            RuleKind.UserUpdateMerge or RuleKind.UserConfigurationMerge or RuleKind.DisplayPreferencesMerge => RuleOwner.Settings,
            RuleKind.UserPolicyPreMerge or RuleKind.PerUserLock or RuleKind.SignInHook or RuleKind.LibraryStructureHook => RuleOwner.LibraryHooks,
            RuleKind.StrictStream => RuleOwner.Strict,
            _ => RuleOwner.ServerHardening
        };
    }

    /// <summary>
    /// Gets whether an action is one of Jellyfin Enhanced's settings saves the <c>EnhancedPanel</c> lock covers: a write
    /// (<see cref="IsJellyfinEnhancedWrite"/>: any method but <c>GET</c>, <c>HEAD</c> and <c>OPTIONS</c>,
    /// since 0.9.6) of JE whose route, after the controller's prefix, starts with <c>user-settings/{userId}/</c>, except the
    /// bookmark routes, or is one of <see cref="JellyfinEnhancedLockedRoutes"/>.
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns><c>true</c> for a save the lock covers.</returns>
    public static bool IsJellyfinEnhancedSettingsSave(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!IsJellyfinEnhancedWrite(descriptor))
        {
            return false;
        }

        var template = descriptor.AttributeRouteInfo?.Template ?? string.Empty;
        var match = UserSettingsRoute().Match(template);
        if (match.Success)
        {
            return !match.Groups["rest"].Value.StartsWith("bookmark", StringComparison.OrdinalIgnoreCase);
        }

        return JellyfinEnhancedLockedRoutes.Contains(RouteAfterPrefix(template));
    }

    /// <summary>
    /// Gets whether an action is Jellyfin Enhanced's: of its controller, or of another controller in its assembly.
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns><c>true</c> for one.</returns>
    public static bool IsJellyfinEnhanced(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Equals(descriptor.ControllerTypeInfo.FullName, JellyfinEnhancedController, StringComparison.Ordinal)
            || string.Equals(descriptor.ControllerTypeInfo.Assembly.GetName().Name, JellyfinEnhancedAssembly, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets whether an action is a write of Jellyfin Enhanced's (not only its <c>POST</c>s): an action of JE
    /// that takes a method other than <c>GET</c>, <c>HEAD</c> and <c>OPTIONS</c> (<c>POST</c>, <c>PUT</c>, <c>PATCH</c>,
    /// <c>DELETE</c>), or any method at all (no method constraint).
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns><c>true</c> for one.</returns>
    public static bool IsJellyfinEnhancedWrite(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!IsJellyfinEnhanced(descriptor))
        {
            return false;
        }

        var methods = Methods(descriptor);
        return methods.Count == 0 || methods.Any(m => !_readMethods.Contains(m));
    }

    /// <summary>
    /// Gets an action's HTTP methods, upper case, as its constraints give them (none: any method).
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns>The methods.</returns>
    public static IReadOnlyList<string> Methods(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return [.. (descriptor.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods) ?? []).Select(m => m.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Gets the name a Jellyfin Enhanced action is reviewed by: its action name on JE's controller, the controller's short
    /// name and the action name on another of JE's controllers (<c>CatchUpController.PostEvent</c>).
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns>The name.</returns>
    public static string JellyfinEnhancedName(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Equals(descriptor.ControllerTypeInfo.FullName, JellyfinEnhancedController, StringComparison.Ordinal)
            ? descriptor.ActionName
            : descriptor.ControllerTypeInfo.Name + "." + descriptor.ActionName;
    }

    /// <summary>
    /// Gets an action's route after its controller's prefix (<c>JellyfinEnhanced/spoiler-blur/user-prefs</c> gives
    /// <c>spoiler-blur/user-prefs</c>): JE's controller routes under <c>JellyfinEnhanced</c>.
    /// </summary>
    /// <param name="template">The route template.</param>
    /// <returns>The rest.</returns>
    public static string RouteAfterPrefix(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var route = template.TrimStart('/');
        return route.StartsWith("JellyfinEnhanced/", StringComparison.OrdinalIgnoreCase) ? route["JellyfinEnhanced/".Length..] : route;
    }

    /// <summary>
    /// Gets what the filter does with an action, cached per descriptor.
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns>Its rules.</returns>
    public ActionRules For(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return _byDescriptor.GetOrAdd(descriptor.Id, _ => Resolve(descriptor));
    }

    /// <summary>
    /// Gets the rules of a manifest action by controller and action name (the tests' and Diagnostics' view).
    /// </summary>
    /// <param name="controller">The controller's full name.</param>
    /// <param name="action">The action name.</param>
    /// <returns>Its rules; none for an action outside the manifest.</returns>
    public ActionRules For(string controller, string action)
    {
        var entry = _manifest.Find(controller, action);
        return ActionRules.Of(controller + "." + action, entry?.Rules ?? [], isJellyfinApi: entry is not null, scopeParameters: entry is null ? [] : ScopeOf(entry.Parameters));
    }

    /// <summary>
    /// Gets the scope parameters among an action's parameters as the manifest writes them (<c>name</c>, or
    /// <c>name=boundName</c>): those bound from <c>parentId</c>, <c>seriesId</c> or <c>seasonId</c> in any letter case, by
    /// their argument names.
    /// </summary>
    /// <param name="parameters">The parameters.</param>
    /// <returns>The scope parameters' argument names.</returns>
    public static IReadOnlyList<string> ScopeOf(IEnumerable<string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var result = new List<string>();
        foreach (var parameter in parameters)
        {
            var split = parameter.IndexOf('=', StringComparison.Ordinal);
            var argument = split < 0 ? parameter : parameter[..split];
            var bound = split < 0 ? parameter : parameter[(split + 1)..];
            if (BackstopParameterNames.Contains(bound, StringComparer.OrdinalIgnoreCase) && !result.Contains(argument, StringComparer.Ordinal))
            {
                result.Add(argument);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets the scope parameters of an action as MVC describes it: its parameters bound from <c>parentId</c>,
    /// <c>seriesId</c> or <c>seasonId</c> in any letter case, by their argument names.
    /// </summary>
    /// <param name="descriptor">The action.</param>
    /// <returns>The scope parameters' argument names.</returns>
    public static IReadOnlyList<string> ScopeOf(ControllerActionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return ScopeOf((descriptor.Parameters ?? []).Select(p => p.BindingInfo?.BinderModelName is { Length: > 0 } bound && !string.Equals(bound, p.Name, StringComparison.Ordinal) ? p.Name + "=" + bound : p.Name));
    }

    /// <summary>
    /// Counts the manifest's actions and rules by owner (an action with rules of two owners counts for each), and the
    /// actions reviewed with no rule.
    /// </summary>
    /// <returns>The counts.</returns>
    public IReadOnlyDictionary<string, object?> Summary()
    {
        var rules = _manifest.Actions.SelectMany(a => a.Rules).ToList();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Actions"] = _manifest.Actions.Count,
            ["NoRule"] = _manifest.Actions.Count(a => a.Rules.Count == 0)
        };
        foreach (var owner in Enum.GetValues<RuleOwner>())
        {
            result[owner.ToString()] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Actions"] = _manifest.Actions.Count(a => a.Rules.Any(r => Owner(r) == owner)),
                ["Rules"] = rules.Count(r => Owner(r) == owner)
            };
        }

        return result;
    }

    private ActionRules Resolve(ControllerActionDescriptor descriptor)
    {
        var controller = descriptor.ControllerTypeInfo.FullName ?? descriptor.ControllerTypeInfo.Name;
        var key = controller + "." + descriptor.ActionName;
        var isJellyfinApi = string.Equals(descriptor.ControllerTypeInfo.Assembly.GetName().Name, JellyfinApiAssembly, StringComparison.Ordinal);

        // The rule identity is the controller's full name and the action name, as the hooks match them.
        var scope = ScopeOf(descriptor);
        if (_manifest.Find(controller, descriptor.ActionName) is { } entry)
        {
            return ActionRules.Of(key, entry.Rules, isJellyfinApi, scopeParameters: scope);
        }

        if (IsJellyfinEnhancedSettingsSave(descriptor))
        {
            return ActionRules.Of(key, [new ActionManifestRule { Kind = RuleKind.LockedReject, Lock = AccessRolesLock.EnhancedPanel }], isJellyfinApi, jellyfinEnhanced: true);
        }

        // Outside the manifest: another plugin's action, or a Jellyfin.Api action a later Jellyfin adds (listed as unreviewed
        // by the self-check). One that binds a scope parameter gets the generic backstop.
        return scope.Count > 0
            ? ActionRules.Of(key, [new ActionManifestRule { Kind = RuleKind.GenericBackstop }], isJellyfinApi, scopeParameters: scope, runtimeBackstop: true)
            : ActionRules.Of(key, [], isJellyfinApi);
    }

    // JE's routes: "JellyfinEnhanced/user-settings/{userId}/settings.json" (the controller's prefix, then the action's).
    [GeneratedRegex(@"^(?:[^/{}]+/)?user-settings/\{[^}/]+\}/(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserSettingsRoute();
}
