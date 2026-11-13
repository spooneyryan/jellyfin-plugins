using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;

/// <summary>
/// The plugin identity checks. Each check returns a list of problems, empty when
/// the plugin is consistent, so the checks themselves can be tested against broken inputs.
/// </summary>
internal static partial class PluginIdentityChecks
{
    /// <summary>The solution file that marks the repository root.</summary>
    public const string SolutionFileName = "jellyfin-plugins.slnx";

    /// <summary>
    /// Gets the GUIDs assigned to the plugins, by build.yaml name. They are authoritative; never change a GUID
    /// after a plugin is first installed.
    /// </summary>
    public static IReadOnlyDictionary<string, Guid> AssignedGuids { get; } = new Dictionary<string, Guid>(StringComparer.Ordinal)
    {
        ["Bookmarks"] = new("d0ee1b2a-7f71-4654-8ff2-163a81441b03"),
        ["User Ratings"] = new("f53c7c34-8cba-4846-b43b-956816b2f6ae"),
        ["Smart Search"] = new("d92488d5-409f-4ad0-af1d-3615db9d0513"),
        ["Library Maintenance"] = new("fd47a1d3-dd8c-4e34-aeea-4f2edd973800"),
        ["Access Roles"] = new("7cbc1019-49a5-4b97-8aa2-279de946ef88"),
        ["Tag Cloud"] = new("e36415cf-46f1-4de6-bd61-d1fe7175c7a9"),
    };

    /// <summary>Gets GUIDs copied from templates or samples, which other plugins may also carry.</summary>
    public static IReadOnlyDictionary<Guid, string> DeniedGuids { get; } = new Dictionary<Guid, string>
    {
        [Guid.Empty] = "the empty GUID",
        [new("eb5d7894-8eef-4b36-aa6f-5d124e828ce1")] = "the jellyfin-plugin-template GUID",
        [new("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d")] = "the placeholder GUID K3ntas's Ratings plugin shipped",
    };

    /// <summary>Gets GUID prefixes that only a hand-typed placeholder would have.</summary>
    public static IReadOnlyList<string> DeniedPrefixes { get; } = ["a1b2c3d4", "12345678", "00000000", "abcdef01", "deadbeef"];

    /// <summary>Walks up from <paramref name="start"/> (default: the test binaries) to the folder holding the solution.</summary>
    public static string FindRepositoryRoot(string? start = null)
    {
        for (var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException($"No {SolutionFileName} above {start ?? AppContext.BaseDirectory}.");
    }

    /// <summary>Lists the plugin folders under <c>plugins/</c> that have a build.yaml, by folder name.</summary>
    public static IReadOnlyList<string> FindPluginFolders(string repositoryRoot)
    {
        var plugins = Path.Combine(repositoryRoot, "plugins");
        return Directory.Exists(plugins)
            ? Directory.GetDirectories(plugins)
                .Where(dir => File.Exists(Path.Combine(dir, "build.yaml")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
    }

    public static IReadOnlyList<string> CheckRequiredFields(BuildYaml yaml)
        => BuildYaml.RequiredFields
            .Where(field => !yaml.Has(field))
            .Select(field => $"build.yaml has no '{field}'.")
            .ToList();

    public static IReadOnlyList<string> CheckGuid(BuildYaml yaml)
    {
        var problems = new List<string>();
        var text = yaml.Guid;
        if (!Guid.TryParseExact(text, "D", out var guid))
        {
            problems.Add($"build.yaml guid '{text}' isn't a GUID in the 8-4-4-4-12 form.");
            return problems;
        }

        if (!string.Equals(text, guid.ToString("D"), StringComparison.Ordinal))
        {
            problems.Add($"build.yaml guid '{text}' should be lower case.");
        }

        if (GetDenialReason(guid) is { } reason)
        {
            problems.Add($"build.yaml guid {guid} is {reason}; generate a fresh GUID.");
        }

        if (AssignedGuids.TryGetValue(yaml.Name, out var assigned) && assigned != guid)
        {
            problems.Add($"'{yaml.Name}' is assigned GUID {assigned}, but build.yaml says {guid}.");
        }

        return problems;
    }

    public static string? GetDenialReason(Guid guid)
    {
        if (DeniedGuids.TryGetValue(guid, out var reason))
        {
            return reason;
        }

        var text = guid.ToString("N");
        return DeniedPrefixes.FirstOrDefault(prefix => text.StartsWith(prefix, StringComparison.Ordinal)) is { } denied
            ? $"a placeholder GUID (starts with {denied})"
            : null;
    }

    /// <summary>
    /// Checks a version's form: four numeric parts, none with a leading zero (<c>0</c> alone is fine), and nothing
    /// after the last, not even a newline. A release ends in <c>.0</c>; a released plugin's development
    /// builds toward <c>X.Y.Z.0</c> are <c>X.Y.Z.&lt;n&gt;</c>, n ≥ 1, for sandboxes only; before 1.0 a plugin is always
    /// <c>0.Minor.Patch.0</c>.
    /// </summary>
    public static IReadOnlyList<string> CheckVersionFormat(string version)
    {
        var match = FourPartVersion().Match(version);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var major) || !int.TryParse(match.Groups[2].Value, out var build))
        {
            return [$"Version '{version}' must have four numeric parts, Major.Minor.Patch.Build."];
        }

        return build == 0 || major >= 1
            ? []
            : [$"Version '{version}': before 1.0 a plugin is 0.Minor.Patch.0; development builds X.Y.Z.<n> are for released plugins only."];
    }

    /// <summary>
    /// Checks that the csproj sets Version, AssemblyVersion and FileVersion, each equal to <paramref name="expected"/>, for
    /// the build's Jellyfin line: once without a condition, or once for each line (<c>'$(JellyfinTarget)' != '12'</c> and
    /// <c>== '12'</c>), the one for this build counted.
    /// </summary>
    public static IReadOnlyList<string> CheckProjectVersions(XDocument project, string expected)
    {
        var problems = new List<string>();
        foreach (var property in new[] { "Version", "AssemblyVersion", "FileVersion" })
        {
            var values = project.Descendants()
                .Where(e => e.Name.LocalName == property && e.Parent?.Name.LocalName == "PropertyGroup" && IsForThisLine((string?)e.Attribute("Condition")))
                .ToList();
            if (values.Count != 1)
            {
                problems.Add($"The csproj must set <{property}> exactly once (found {values.Count}).");
            }
            else if (!string.Equals(values[0].Value.Trim(), expected, StringComparison.Ordinal))
            {
                problems.Add($"The csproj's <{property}> is '{values[0].Value.Trim()}', but build.yaml's version is '{expected}'.");
            }
        }

        return problems;
    }

    /// <summary>Checks the compiled assembly's version, file version and target framework against build.yaml.</summary>
    public static IReadOnlyList<string> CheckAssembly(Assembly assembly, BuildYaml yaml)
    {
        var problems = new List<string>();
        var version = assembly.GetName().Version?.ToString();
        if (!string.Equals(version, yaml.Version, StringComparison.Ordinal))
        {
            problems.Add($"The assembly version is '{version}', but build.yaml's version is '{yaml.Version}'.");
        }

        var fileVersion = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        if (!string.Equals(fileVersion, yaml.Version, StringComparison.Ordinal))
        {
            problems.Add($"The assembly file version is '{fileVersion}', but build.yaml's version is '{yaml.Version}'.");
        }

        var framework = ToShortFrameworkName(assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        if (!string.Equals(framework, yaml.Framework, StringComparison.Ordinal))
        {
            problems.Add($"The assembly targets '{framework}', but build.yaml's framework is '{yaml.Framework}'.");
        }

        var dll = assembly.GetName().Name + ".dll";
        if (!yaml.Artifacts.Contains(dll, StringComparer.Ordinal))
        {
            problems.Add($"build.yaml's artifacts don't list {dll}.");
        }

        return problems;
    }

    /// <summary>
    /// Checks targetAbi against the Jellyfin.Controller build the plugin compiles against (the
    /// 10.11 line uses 10.11.5.0, and the loader accepts any server at least that version).
    /// </summary>
    public static IReadOnlyList<string> CheckTargetAbi(BuildYaml yaml)
    {
        var controller = typeof(BasePlugin).Assembly.GetName().Version?.ToString();
        return string.Equals(yaml.TargetAbi, controller, StringComparison.Ordinal)
            ? []
            : [$"build.yaml's targetAbi is '{yaml.TargetAbi}', but the plugin compiles against Jellyfin {controller}."];
    }

    /// <summary>Checks the plugin instance's Id and Name against build.yaml.</summary>
    public static IReadOnlyList<string> CheckPluginInstance(BasePlugin plugin, BuildYaml yaml)
    {
        var problems = new List<string>();
        if (!Guid.TryParse(yaml.Guid, out var guid) || plugin.Id != guid)
        {
            problems.Add($"{plugin.GetType().FullName}.Id is {plugin.Id}, but build.yaml's guid is '{yaml.Guid}'.");
        }

        if (!string.Equals(plugin.Name, yaml.Name, StringComparison.Ordinal))
        {
            problems.Add($"{plugin.GetType().FullName}.Name is '{plugin.Name}', but build.yaml's name is '{yaml.Name}'.");
        }

        return problems;
    }

    /// <summary>
    /// Checks that the plugin class derives from <c>BasePlugin&lt;TConfiguration&gt;</c>. The non-generic
    /// <see cref="BasePlugin"/> leaves <c>Version</c> null, and Jellyfin 10.11.5's <c>CreatePluginInstance</c> then marks the
    /// plugin Malfunctioned (<c>Emby.Server.Implementations/Plugins/PluginManager.cs:581</c>; seen in a sandbox).
    /// </summary>
    /// <param name="pluginType">The plugin class.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckPluginBaseType(Type pluginType)
    {
        ArgumentNullException.ThrowIfNull(pluginType);
        for (var type = pluginType.BaseType; type is not null; type = type.BaseType)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(BasePlugin<>))
            {
                return [];
            }
        }

        return [$"{pluginType.FullName} must derive from BasePlugin<TConfiguration>; the non-generic BasePlugin leaves Version null and "
            + "Jellyfin 10.11.5 marks the plugin Malfunctioned."];
    }

    /// <summary>
    /// Checks that every GUID literal in the plugin class's source file is build.yaml's GUID, and that there is one.
    /// </summary>
    public static IReadOnlyList<string> CheckSourceGuids(string source, string fileName, Guid expected)
    {
        var literals = GuidLiteral().Matches(source).Select(m => Guid.Parse(m.Groups[1].Value)).ToList();
        if (literals.Count == 0)
        {
            return [$"{fileName} has no GUID literal; Plugin.Id should come from build.yaml's GUID written in source."];
        }

        return literals
            .Where(g => g != expected)
            .Distinct()
            .Select(g => $"{fileName} contains GUID {g}, which isn't build.yaml's guid {expected}.")
            .ToList();
    }

    /// <summary>
    /// Checks the manifest fields a script writes (e.g. Deploy-Plugin.ps1's meta.json template) against build.yaml.
    /// Only <c>guid</c>, <c>name</c> and <c>targetAbi</c> hashtable entries with single-quoted values are compared.
    /// </summary>
    public static IReadOnlyList<string> CheckScriptManifest(string script, string fileName, BuildYaml yaml)
    {
        var problems = new List<string>();
        foreach (var match in ScriptManifestEntry().Matches(script).AsEnumerable())
        {
            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value;
            var expected = key.ToUpperInvariant() switch
            {
                "GUID" => yaml.Guid,
                "NAME" => yaml.Name,
                _ => yaml.TargetAbi,
            };
            if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{fileName} writes {key} '{value}', but build.yaml says '{expected}'.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Checks the dashboard pages: the settings HTML page comes first, because jellyfin-web opens a
    /// plugin's first page when none or several are <c>EnableInMainMenu</c>; at most one page is in the main menu
    /// and it is the settings page; names carry the plugin prefix; every resource exists and is .html or .js.
    /// </summary>
    public static IReadOnlyList<string> CheckPages(IReadOnlyList<PluginPageInfo> pages, string pluginName, IReadOnlyCollection<string> resourceNames)
    {
        var problems = new List<string>();
        if (pages.Count == 0)
        {
            problems.Add("GetPages() returns no pages; every plugin has a settings page.");
            return problems;
        }

        var first = pages[0];
        if (!first.EmbeddedResourcePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(
                $"The first GetPages() entry must be the settings HTML page, but it is '{first.Name}' ({first.EmbeddedResourcePath}); "
                + "the plugin card's Settings button would open it.");
        }

        var menuPages = pages.Where(p => p.EnableInMainMenu).ToList();
        if (menuPages.Count > 1)
        {
            problems.Add($"{menuPages.Count} pages are EnableInMainMenu ({string.Join(", ", menuPages.Select(p => p.Name))}); at most one may be.");
        }
        else if (menuPages.Count == 1 && !ReferenceEquals(menuPages[0], first))
        {
            problems.Add($"The EnableInMainMenu page '{menuPages[0].Name}' must be the settings page, the first GetPages() entry.");
        }

        var prefix = pluginName.Replace(" ", string.Empty, StringComparison.Ordinal);
        foreach (var page in pages)
        {
            if (!page.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Page name '{page.Name}' must start with '{prefix}'; page names are global across plugins.");
            }

            if (!page.EmbeddedResourcePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                && !page.EmbeddedResourcePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Page '{page.Name}' serves '{page.EmbeddedResourcePath}'; page resources end in .html or .js.");
            }

            if (!resourceNames.Contains(page.EmbeddedResourcePath))
            {
                problems.Add($"Page '{page.Name}' serves embedded resource '{page.EmbeddedResourcePath}', which the assembly doesn't contain.");
            }
        }

        problems.AddRange(pages
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => $"Page name '{g.Key}' is used {g.Count()} times; names are matched case-insensitively."));
        return problems;
    }

    /// <summary>
    /// Creates a plugin instance the way the server would, with mocks for its constructor's services. Paths point at
    /// <paramref name="scratchDirectory"/>.
    /// </summary>
    public static BasePlugin CreatePlugin(Type pluginType, string scratchDirectory)
    {
        var constructor = pluginType.GetConstructors().MaxBy(c => c.GetParameters().Length)
            ?? throw new InvalidOperationException($"{pluginType.FullName} has no public constructor.");
        var arguments = constructor.GetParameters().Select(p => CreateArgument(p.ParameterType, scratchDirectory)).ToArray();
        return (BasePlugin)constructor.Invoke(arguments);
    }

    private static object CreateArgument(Type type, string scratchDirectory)
    {
        if (type == typeof(IApplicationPaths))
        {
            var paths = new Mock<IApplicationPaths>();
            paths.SetReturnsDefault(scratchDirectory);
            return paths.Object;
        }

        if (type == typeof(ILoggerFactory))
        {
            return NullLoggerFactory.Instance;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ILogger<>))
        {
            return Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(type.GetGenericArguments()))!;
        }

        if (type.IsInterface || type.IsAbstract)
        {
            var mock = (Mock)Activator.CreateInstance(typeof(Mock<>).MakeGenericType(type))!;
            mock.DefaultValue = DefaultValue.Mock;
            return mock.Object;
        }

        throw new NotSupportedException($"Can't create a {type.FullName} for the plugin constructor.");
    }

    // Whether a csproj property's Condition applies to the build's line: none, or one on JellyfinTarget and 12.
    private static bool IsForThisLine(string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }

        var compact = condition.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Contains("'$(JellyfinTarget)'=='12'", StringComparison.Ordinal))
        {
            return Jellyfin.Plugin.Kit.TestSupport.JellyfinLine.Is12;
        }

        if (compact.Contains("'$(JellyfinTarget)'!='12'", StringComparison.Ordinal))
        {
            return !Jellyfin.Plugin.Kit.TestSupport.JellyfinLine.Is12;
        }

        return true;
    }

    private static string? ToShortFrameworkName(string? frameworkName)
    {
        if (frameworkName is null)
        {
            return null;
        }

        var framework = new FrameworkName(frameworkName);
        return framework.Identifier == ".NETCoreApp"
            ? string.Create(CultureInfo.InvariantCulture, $"net{framework.Version.Major}.{framework.Version.Minor}")
            : frameworkName;
    }

    // \z, not $: $ also matches before a final newline, so "1.1.0.2\n" passed.
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")]
    private static partial Regex FourPartVersion();

    [GeneratedRegex("\"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\"")]
    private static partial Regex GuidLiteral();

    [GeneratedRegex(@"^\s*(?<key>guid|name|targetAbi)\s*=\s*'(?<value>[^']*)'", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptManifestEntry();
}
