using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Jellyfin.Plugin.Kit.TestSupport.Api;

/// <summary>
/// The controller-name test and its route companion. Jellyfin Enhanced 12.8's global
/// response filters key on the MVC <c>controller</c> and <c>action</c> route values, so a plugin controller named like a
/// core one gets JE's Hidden Content filtering and Spoiler Guard stripping. And a route outside the plugin's prefix can
/// collide with another plugin's, which makes that endpoint and <c>/api-docs</c> fail (seen in a sandbox run).
/// </summary>
internal static class ControllerNameChecks
{
    /// <summary>The pinned list of reserved controller names in <c>kit/testdata</c>.</summary>
    public const string ReservedNamesFile = "reserved-controller-names.txt";

    private static readonly Lazy<IReadOnlySet<string>> _reservedNames =
        new(() => ControllerDiscovery.ReadTestDataList(ReservedNamesFile).ToHashSet(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the controller names a plugin controller must never use: every core controller of Jellyfin 10.11.5 plus the
    /// names JE's filters key on (<c>kit/testdata/reserved-controller-names.txt</c>).
    /// </summary>
    public static IReadOnlySet<string> ReservedNames => _reservedNames.Value;

    /// <summary>
    /// Checks every controller of the plugin assembly: its name starts with the route prefix and isn't reserved.
    /// </summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <param name="routePrefix">The plugin's route prefix (<c>KitOptions.RoutePrefix</c>).</param>
    /// <returns>The problems; empty when the plugin passes.</returns>
    public static IReadOnlyList<string> CheckNames(Assembly assembly, string routePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return CheckNames(ControllerDiscovery.FindControllers(assembly), routePrefix, ReservedNames);
    }

    /// <summary>
    /// Checks the given controllers' names.
    /// </summary>
    /// <param name="controllers">The controller types.</param>
    /// <param name="routePrefix">The plugin's route prefix.</param>
    /// <param name="reservedNames">Names that must never be used (compared case-insensitively).</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckNames(IEnumerable<Type> controllers, string routePrefix, IReadOnlySet<string> reservedNames)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        ArgumentException.ThrowIfNullOrEmpty(routePrefix);
        ArgumentNullException.ThrowIfNull(reservedNames);

        var problems = new List<string>();
        foreach (var controller in controllers)
        {
            var name = ControllerDiscovery.GetControllerName(controller);
            if (reservedNames.Any(reserved => string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add(
                    $"{controller.FullName} has the controller name '{name}', which Jellyfin or Jellyfin Enhanced's filters use; "
                    + "JE's global response filters match on the controller name, so they would filter and strip its responses.");
            }

            if (!name.StartsWith(routePrefix, StringComparison.Ordinal))
            {
                problems.Add($"{controller.FullName}: controller class names start with the route prefix '{routePrefix}'.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Checks that every action's attribute route lies under <c>/&lt;routePrefix&gt;</c> and that every action
    /// has an attribute route at all.
    /// </summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <param name="routePrefix">The plugin's route prefix.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckRoutes(Assembly assembly, string routePrefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return CheckRoutes(ControllerDiscovery.FindControllers(assembly), routePrefix);
    }

    /// <summary>
    /// Checks the given controllers' routes.
    /// </summary>
    /// <param name="controllers">The controller types.</param>
    /// <param name="routePrefix">The plugin's route prefix.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckRoutes(IEnumerable<Type> controllers, string routePrefix)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        ArgumentException.ThrowIfNullOrEmpty(routePrefix);

        var problems = new List<string>();
        foreach (var controller in controllers)
        {
            foreach (var action in ControllerDiscovery.FindActions(controller))
            {
                var templates = ControllerDiscovery.GetRouteTemplates(controller, action);
                if (templates.Count == 0 || templates.All(string.IsNullOrEmpty))
                {
                    problems.Add($"{ControllerDiscovery.Describe(controller, action)} has no attribute route; routes are /{routePrefix}/...");
                    continue;
                }

                problems.AddRange(templates
                    .Where(template => !string.Equals(template, routePrefix, StringComparison.Ordinal)
                        && !template.StartsWith(routePrefix + "/", StringComparison.Ordinal))
                    .Select(template => $"{ControllerDiscovery.Describe(controller, action)} answers '/{template}', outside '/{routePrefix}'."));
            }
        }

        return problems;
    }
}
