using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;

namespace Jellyfin.Plugin.Kit.TestSupport.Api;

/// <summary>
/// The anonymous-action test. Jellyfin sets a default policy but no fallback
/// policy, so an action without <c>[Authorize]</c> (on the action, its controller or a base class) is open to anyone,
/// <c>[AllowAnonymous]</c> anywhere opens an action even under a class-level <c>[Authorize]</c>, and some Jellyfin policies
/// (<see cref="AnonymousPolicies"/>) let callers without a token through. A plugin's anonymous surface is exactly
/// <c>GET /&lt;Route&gt;/ClientScript</c> and the optional <c>GET /&lt;Route&gt;/AdminScript</c>.
/// </summary>
internal static class AnonymousActionChecks
{
    /// <summary>The last route segments allowed to be anonymous, for GET (and HEAD) only.</summary>
    public static readonly IReadOnlyList<string> AllowedAnonymousEndpoints = ["ClientScript", "AdminScript"];

    /// <summary>
    /// The Jellyfin 10.11.5 policies that admit a caller without a token. <c>AnonymousLanAccessPolicy</c> and
    /// <c>LocalAccessOrRequiresElevation</c> succeed for any LAN or loopback address without looking at the user (v10.11.5
    /// Jellyfin.Api/Auth/AnonymousLanAccessPolicy/AnonymousLanAccessHandler.cs:36-38,
    /// Jellyfin.Api/Auth/LocalAccessOrRequiresElevationPolicy/LocalAccessOrRequiresElevationHandler.cs:37-39). The three
    /// <c>FirstTimeSetup*</c> policies succeed for everyone until the startup wizard is completed
    /// (Jellyfin.Api/Auth/FirstTimeSetupPolicy/FirstTimeSetupHandler.cs:29-31); their base requirement's handler skips a
    /// caller without a user id (DefaultAuthorizationPolicy/DefaultAuthorizationHandler.cs:46). Every other 10.11.5 policy,
    /// and the default policy, needs a signed-in user or an API key.
    /// </summary>
    public static readonly IReadOnlyList<string> AnonymousPolicies =
    [
        Policies.AnonymousLanAccessPolicy,
        Policies.LocalAccessOrRequiresElevation,
        Policies.FirstTimeSetupOrDefault,
        Policies.FirstTimeSetupOrElevated,
        Policies.FirstTimeSetupOrIgnoreParentalControl,
    ];

    /// <summary>
    /// Checks every action of the plugin assembly.
    /// </summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <returns>The problems; empty when only the allowed endpoints are anonymous.</returns>
    public static IReadOnlyList<string> Check(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return Check(ControllerDiscovery.FindControllers(assembly));
    }

    /// <summary>
    /// Checks the given controllers' actions.
    /// </summary>
    /// <param name="controllers">The controller types.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> Check(IEnumerable<Type> controllers)
    {
        ArgumentNullException.ThrowIfNull(controllers);

        var problems = new List<string>();
        foreach (var controller in controllers)
        {
            foreach (var action in ControllerDiscovery.FindActions(controller))
            {
                var reason = GetAnonymousReason(controller, action);
                if (reason is null || IsAllowedAnonymous(controller, action))
                {
                    continue;
                }

                problems.Add(
                    $"{ControllerDiscovery.Describe(controller, action)} ({string.Join(", ", ControllerDiscovery.GetRouteTemplates(controller, action).Select(t => "/" + t))}) "
                    + $"can be called without signing in: {reason}. Only GET ClientScript and AdminScript may be anonymous; add "
                    + "[Authorize] with the default policy or another policy that needs a user.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Gets a value indicating whether an action runs without authentication: it or its controller (including base
    /// classes) carries <c>[AllowAnonymous]</c>, or every <c>[Authorize]</c> on them (there may be none) names one of the
    /// <see cref="AnonymousPolicies"/> and no roles.
    /// </summary>
    /// <param name="controller">The controller type.</param>
    /// <param name="action">The action.</param>
    /// <returns><c>true</c> when anonymous callers reach the action.</returns>
    public static bool IsAnonymous(Type controller, MethodInfo action)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(action);

        return GetAnonymousReason(controller, action) is not null;
    }

    private static string? GetAnonymousReason(Type controller, MethodInfo action)
    {
        var metadata = controller.GetCustomAttributes(inherit: true).Concat(action.GetCustomAttributes(inherit: true)).ToList();
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return "it carries [AllowAnonymous]";
        }

        var authorize = metadata.OfType<IAuthorizeData>().ToList();
        if (authorize.Count == 0)
        {
            return "neither it nor its controller carries [Authorize]";
        }

        // ASP.NET Core requires every [Authorize] at once (AuthorizationPolicy.CombineAsync), so one that needs a user is enough.
        return authorize.All(AdmitsAnonymous)
            ? $"its only [Authorize] policies admit callers without a token ({string.Join(", ", authorize.Select(a => a.Policy).Distinct(StringComparer.OrdinalIgnoreCase))})"
            : null;
    }

    /// <summary>
    /// Gets a value indicating whether one <c>[Authorize]</c> lets a caller without a token through. Without a policy or
    /// roles it adds the default policy, which needs a user; roles need a signed-in user. Policy names are matched
    /// case-insensitively, as <see cref="AuthorizationOptions"/> stores them.
    /// </summary>
    private static bool AdmitsAnonymous(IAuthorizeData authorize)
        => string.IsNullOrWhiteSpace(authorize.Roles)
            && authorize.Policy is { } policy
            && AnonymousPolicies.Contains(policy, StringComparer.OrdinalIgnoreCase);

    private static bool IsAllowedAnonymous(Type controller, MethodInfo action)
    {
        var methods = ControllerDiscovery.GetHttpMethods(action);
        if (methods.Count == 0 || methods.Any(method => method is not ("GET" or "HEAD")))
        {
            return false;
        }

        var templates = ControllerDiscovery.GetRouteTemplates(controller, action);
        return templates.Count > 0
            && templates.All(template => AllowedAnonymousEndpoints.Contains(template.Split('/')[^1], StringComparer.Ordinal));
    }
}
