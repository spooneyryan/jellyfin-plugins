using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Jellyfin.Plugin.Kit.TestSupport.Api;

/// <summary>
/// Finds controllers and actions the way ASP.NET Core MVC does (<c>ControllerFeatureProvider.IsController</c> and
/// <c>DefaultApplicationModelProvider.IsAction</c>), so the API checks see exactly what Jellyfin will route.
/// </summary>
internal static class ControllerDiscovery
{
    private const string ControllerSuffix = "Controller";

    /// <summary>
    /// Lists the controllers MVC finds in an assembly: public, concrete, non-generic classes that derive from
    /// <see cref="ControllerBase"/> (or are named <c>*Controller</c> or carry <see cref="ControllerAttribute"/>), without
    /// <see cref="NonControllerAttribute"/>.
    /// </summary>
    /// <param name="assembly">The plugin assembly.</param>
    /// <returns>The controller types, by full name.</returns>
    public static IReadOnlyList<Type> FindControllers(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetTypes().Where(IsController).OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Gets a value indicating whether MVC treats the type as a controller.
    /// </summary>
    /// <param name="type">The type.</param>
    /// <returns><c>true</c> for a controller.</returns>
    public static bool IsController(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!type.IsClass || type.IsAbstract || !type.IsPublic || type.ContainsGenericParameters
            || type.IsDefined(typeof(NonControllerAttribute), inherit: true))
        {
            return false;
        }

        return type.Name.EndsWith(ControllerSuffix, StringComparison.OrdinalIgnoreCase)
            || type.IsDefined(typeof(ControllerAttribute), inherit: true);
    }

    /// <summary>
    /// Gets the MVC controller name (the route value <c>controller</c>): the class name without the <c>Controller</c> suffix.
    /// </summary>
    /// <param name="controller">The controller type.</param>
    /// <returns>The name.</returns>
    public static string GetControllerName(Type controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var name = controller.Name;
        return name.EndsWith(ControllerSuffix, StringComparison.OrdinalIgnoreCase) && name.Length > ControllerSuffix.Length
            ? name[..^ControllerSuffix.Length]
            : name;
    }

    /// <summary>
    /// Lists a controller's actions: public instance methods that aren't special names, generic definitions, object's
    /// methods, <c>Dispose</c> or marked <see cref="NonActionAttribute"/> (every public method of <see cref="ControllerBase"/> is).
    /// </summary>
    /// <param name="controller">The controller type.</param>
    /// <returns>The actions.</returns>
    public static IReadOnlyList<MethodInfo> FindActions(Type controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        return controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => !method.IsSpecialName
                && !method.IsGenericMethod
                && !method.IsAbstract
                && method.GetBaseDefinition().DeclaringType != typeof(object)
                && !method.IsDefined(typeof(NonActionAttribute), inherit: true)
                && !(method.Name == nameof(IDisposable.Dispose) && method.GetParameters().Length == 0 && typeof(IDisposable).IsAssignableFrom(controller)))
            .OrderBy(method => method.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Gets the attribute route templates of an action: each method template combined with each controller template
    /// (a method template starting with <c>/</c> or <c>~/</c> replaces the controller's).
    /// </summary>
    /// <param name="controller">The controller type.</param>
    /// <param name="action">The action.</param>
    /// <returns>The templates without leading or trailing slashes; empty when the action has no attribute route.</returns>
    public static IReadOnlyList<string> GetRouteTemplates(Type controller, MethodInfo action)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(action);

        var controllerTemplates = controller.GetCustomAttributes(inherit: true).OfType<IRouteTemplateProvider>()
            .Select(provider => provider.Template).OfType<string>().DefaultIfEmpty(string.Empty).ToList();
        var actionProviders = action.GetCustomAttributes(inherit: true).OfType<IRouteTemplateProvider>().ToList();
        var actionTemplates = actionProviders.Count == 0 ? [null] : actionProviders.Select(provider => provider.Template).ToList();

        var templates = new List<string>();
        foreach (var actionTemplate in actionTemplates)
        {
            if (actionTemplate is not null && (actionTemplate.StartsWith('/') || actionTemplate.StartsWith("~/", StringComparison.Ordinal)))
            {
                templates.Add(actionTemplate.TrimStart('~').Trim('/'));
                continue;
            }

            templates.AddRange(controllerTemplates.Select(controllerTemplate =>
                string.Join('/', new[] { controllerTemplate, actionTemplate }.Where(t => !string.IsNullOrEmpty(t)).Select(t => t!.Trim('/')))));
        }

        return templates.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Gets the HTTP methods an action accepts, from its <see cref="IActionHttpMethodProvider"/> attributes; empty means any.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <returns>The upper-case methods.</returns>
    public static IReadOnlyCollection<string> GetHttpMethods(MethodInfo action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>()
            .SelectMany(provider => provider.HttpMethods)
            .Select(method => method.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets a readable name for an action, <c>Controller.Method</c>.
    /// </summary>
    /// <param name="controller">The controller type.</param>
    /// <param name="action">The action.</param>
    /// <returns>The name.</returns>
    public static string Describe(Type controller, MethodInfo action) => $"{controller?.Name}.{action?.Name}";

    /// <summary>
    /// Reads a pinned list from <c>kit/testdata</c>, embedded by <c>kit/build/TestSupport.props</c>: one entry per line,
    /// blank lines and <c>#</c> comments ignored.
    /// </summary>
    /// <param name="fileName">The file name, for example <c>openapi-schema-ids.txt</c>.</param>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<string> ReadTestDataList(string fileName)
    {
        var name = "Jellyfin.Plugin.Kit.TestData." + fileName;
        using var stream = typeof(ControllerDiscovery).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The test assembly doesn't embed {name}; kit/build/TestSupport.props embeds it.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }
}
