using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Interop;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// Registers a user page with the Plugin Pages plugin at run time, through reflection (verified in a sandbox): Plugin
/// Pages 3.x (Jellyfin 12) through <c>PluginInterface.RegisterPage(JObject)</c>, Plugin Pages 2.4.x (Jellyfin 10.11)
/// through its DI singleton <c>IPluginPagesManager.RegisterPluginPage(PluginPage)</c>. It never writes Plugin Pages'
/// <c>config.json</c>, and it has no reference to Plugin Pages or Newtonsoft.
/// </summary>
/// <remarks>
/// The assembly is matched by its exact name, <see cref="AssemblyName"/>, in every load context (each plugin has its own).
/// Plugin Pages 2.4.x can't remove a page, so a registration lasts until the server restarts.
/// </remarks>
internal static class PluginPagesBridge
{
    /// <summary>The name of Plugin Pages' assembly.</summary>
    public const string AssemblyName = "Jellyfin.Plugin.PluginPages";

    /// <summary>The API name of Plugin Pages 3.x's static registration method.</summary>
    public const string Api3x = "PluginInterface.RegisterPage (3.x)";

    /// <summary>The API name of Plugin Pages 2.4.x's DI manager.</summary>
    public const string Api24 = "IPluginPagesManager.RegisterPluginPage (2.4.x, DI)";

    /// <summary>The outcome when no Plugin Pages assembly is loaded.</summary>
    public const string NotInstalled = "NotInstalled";

    /// <summary>The outcome when the registration fails <see cref="PluginPageRegistration.Validate"/>.</summary>
    public const string InvalidPage = "InvalidPage";

    /// <summary>The outcome when the assembly has neither known API (or its manager isn't in DI).</summary>
    public const string UnexpectedInterface = "UnexpectedInterface";

    /// <summary>The outcome when Plugin Pages threw from its registration method.</summary>
    public const string Rejected = "Rejected";

    /// <summary>The outcome when reflection itself failed.</summary>
    public const string Failed = "Failed";

    private const string PluginInterfaceType = "Jellyfin.Plugin.PluginPages.PluginInterface";
    private const string ManagerType = "Jellyfin.Plugin.PluginPages.Library.IPluginPagesManager";
    private const string PageType = "Jellyfin.Plugin.PluginPages.Library.PluginPage";

    private static readonly JsonSerializerOptions _camelCase = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Gets the assemblies loaded into any load context, where Plugin Pages is looked for by default.
    /// </summary>
    public static IEnumerable<Assembly> LoadedAssemblies => AssemblyLoadContext.All.SelectMany(context => context.Assemblies);

    /// <summary>
    /// Finds Plugin Pages' assembly by its exact name (ordinal).
    /// </summary>
    /// <param name="assemblies">The loaded assemblies.</param>
    /// <returns>The assembly, or <c>null</c> when Plugin Pages isn't loaded.</returns>
    public static Assembly? Find(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        return assemblies.FirstOrDefault(a => string.Equals(a.GetName().Name, AssemblyName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Lists the pages Plugin Pages 2.4.x holds in memory, through its DI manager's <c>GetPages()</c>: the list
    /// <c>GET /PluginPages/User</c> returns and the drawer shows (the Jellyfin Enhanced switch-off checklist).
    /// Plugin Pages 2.4.x reads its <c>config.json</c> only when it starts, so this, not the file, says whether a link is
    /// still shown. Never throws for anything Plugin Pages does.
    /// </summary>
    /// <param name="services">The server's service provider (for 2.4.x's DI manager).</param>
    /// <param name="assemblies">Where to look for Plugin Pages; <see cref="LoadedAssemblies"/> when <c>null</c>.</param>
    /// <returns>The pages; not installed when Plugin Pages isn't loaded; a problem when its interface is unexpected.</returns>
    public static PluginPagesState TryListPages(IServiceProvider services, IEnumerable<Assembly>? assemblies = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var assembly = Find(assemblies ?? LoadedAssemblies);
        if (assembly is null)
        {
            return PluginPagesState.NotInstalled;
        }

        var name = assembly.GetName();
        var detail = name.Name + " " + name.Version;
        try
        {
            var managerType = assembly.GetType(ManagerType);
            var getPages = managerType?.GetMethod("GetPages", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            var manager = managerType is null ? null : services.GetService(managerType);
            if (manager is null || getPages is null)
            {
                return new(true, [], "Plugin Pages (" + detail + ") has an unexpected interface, so its list can't be read.");
            }

            // The manager's own List<PluginPage>, copied at once; a registration at the same moment means another try.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return new(true, CopyPages(getPages.Invoke(manager, null)), null);
                }
                catch (InvalidOperationException) when (attempt < 2)
                {
                    // The list changed while it was copied.
                }
            }
        }
        catch (TargetInvocationException ex)
        {
            return new(true, [], "Plugin Pages (" + detail + ") failed to list its pages: " + (ex.InnerException ?? ex).Message);
        }
        catch (Exception ex) when (ex is AmbiguousMatchException or TypeLoadException or MissingMethodException or MemberAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException or BadImageFormatException or System.IO.FileLoadException)
        {
            return new(true, [], "Reading Plugin Pages' list (" + detail + ") failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Copies what <c>GetPages()</c> returned (its own <c>List&lt;PluginPage&gt;</c>, or any enumerable of objects with
    /// <c>Id</c>, <c>Url</c> and <c>DisplayText</c> string properties) into the kit's entries, at once, so a registration
    /// meanwhile throws here rather than later.
    /// </summary>
    /// <param name="pages">The manager's result.</param>
    /// <returns>The entries, in the manager's order; none for a result that isn't a list.</returns>
    private static IReadOnlyList<PluginPagesEntry> CopyPages(object? pages)
    {
        if (pages is not System.Collections.IEnumerable list)
        {
            return [];
        }

        var entries = new List<PluginPagesEntry>();
        foreach (var page in list)
        {
            if (page is null)
            {
                continue;
            }

            var type = page.GetType();
            entries.Add(new PluginPagesEntry(
                ReadString(type, page, "Id") ?? string.Empty,
                ReadString(type, page, "Url"),
                ReadString(type, page, "DisplayText")));
        }

        return entries;
    }

    private static string? ReadString(Type type, object page, string name)
        => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(page) as string;

    /// <summary>
    /// Registers a page with Plugin Pages, if it is installed. Never throws for anything Plugin Pages does.
    /// </summary>
    /// <param name="services">The server's service provider (for 2.4.x's DI manager).</param>
    /// <param name="page">The page.</param>
    /// <param name="logger">The logger; problems are logged as warnings.</param>
    /// <param name="assemblies">Where to look for Plugin Pages; <see cref="LoadedAssemblies"/> when <c>null</c>.</param>
    /// <returns>The outcome.</returns>
    public static PluginPagesRegistrationResult TryRegister(
        IServiceProvider services,
        PluginPageRegistration page,
        ILogger logger,
        IEnumerable<Assembly>? assemblies = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(logger);

        var problems = page.Validate();
        if (problems.Count > 0)
        {
            return new(false, InvalidPage, string.Join(" ", problems));
        }

        var assembly = Find(assemblies ?? LoadedAssemblies);
        if (assembly is null)
        {
            return new(false, NotInstalled, "No assembly named " + AssemblyName + " is loaded.");
        }

        var name = assembly.GetName();
        var detail = name.Name + " " + name.Version;
        try
        {
            // Plugin Pages 3.x (Jellyfin 12): public static PluginInterface.RegisterPage(JObject). Its PluginPage is read
            // case-insensitively; JObject.Parse(string) builds the argument without a Newtonsoft reference.
            var register = assembly.GetType(PluginInterfaceType)?.GetMethod("RegisterPage", BindingFlags.Public | BindingFlags.Static);
            if (register?.GetParameters() is [var parameter]
                && parameter.ParameterType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]) is { } parse)
            {
                register.Invoke(null, [parse.Invoke(null, [JsonSerializer.Serialize(page, _camelCase)])]);
                return new(true, Api3x, detail);
            }

            // Plugin Pages 2.4.x (Jellyfin 10.11): the DI singleton IPluginPagesManager and its PluginPage POCO.
            var managerType = assembly.GetType(ManagerType);
            var pageType = assembly.GetType(PageType);
            var registerPage = managerType?.GetMethod("RegisterPluginPage", [pageType ?? typeof(object)]);
            var manager = managerType is null ? null : services.GetService(managerType);
            var properties = pageType is null
                ? []
                : new[] { "Id", "Url", "DisplayText", "Icon" }.Select(p => pageType.GetProperty(p, BindingFlags.Public | BindingFlags.Instance)).ToArray();
            if (manager is null || pageType is null || registerPage is null || properties.Any(p => p is null || !p.CanWrite || p.PropertyType != typeof(string)))
            {
                logger.LogWarning(
                    "Plugin Pages ({Assembly}) has an unexpected interface, so page {Id} uses its fallback route",
                    detail,
                    page.Id);
                return new(false, UnexpectedInterface, detail);
            }

            var dto = Activator.CreateInstance(pageType)!;
            properties[0]!.SetValue(dto, page.Id);
            properties[1]!.SetValue(dto, page.Url);
            properties[2]!.SetValue(dto, page.DisplayText);
            properties[3]!.SetValue(dto, page.Icon);
            registerPage.Invoke(manager, [dto]);
            return new(true, Api24, detail);
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException ?? ex;
            logger.LogWarning(inner, "Plugin Pages ({Assembly}) rejected page {Id}, so it uses its fallback route", detail, page.Id);
            return new(false, Rejected, detail + ": " + inner.Message);
        }
        catch (Exception ex) when (ex is AmbiguousMatchException or TypeLoadException or MissingMethodException or MemberAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException or BadImageFormatException or System.IO.FileLoadException)
        {
            logger.LogWarning(ex, "Registering page {Id} with Plugin Pages ({Assembly}) failed, so it uses its fallback route", page.Id, detail);
            return new(false, Failed, detail + ": " + ex.Message);
        }
    }
}
