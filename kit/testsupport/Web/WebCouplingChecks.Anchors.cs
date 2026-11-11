using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// The anchor-list half of the coupling checks.
/// </summary>
public static partial class WebCouplingChecks
{
    /// <summary>The resource name of a plugin's <c>Web/anchors.json</c> (Kit.props).</summary>
    public const string AnchorsResource = "Web.anchors.json";

    /// <summary>The resource name of the kit's <c>kit/web/anchors.json</c> (Kit.props).</summary>
    public const string KitAnchorsResource = "Kit.anchors.json";

    /// <summary>
    /// Checks a plugin's anchor list: a plugin with scripts embeds <c>Web/anchors.json</c> (<see cref="AnchorsResource"/>),
    /// every entry is <c>{ name, css, required, views, note }</c> with a camelCase name that doesn't start with
    /// <c>kit.</c>, and every declared anchor is named by a string literal in one of the plugin's scripts, so the list
    /// holds nothing the code no longer uses. The kit's own parser (<c>WebAnchorCatalog</c>) checks the finer rules;
    /// plugin tests assert that it reports no problems either.
    /// </summary>
    /// <param name="pluginAssembly">The plugin's assembly.</param>
    /// <returns>The problems; empty when the list follows the policy.</returns>
    public static IReadOnlyList<string> CheckAnchors(Assembly pluginAssembly)
    {
        ArgumentNullException.ThrowIfNull(pluginAssembly);
        var problems = new List<string>();
        var scripts = Resources(pluginAssembly, "Web.js.", ".js").Concat(Resources(pluginAssembly, "Web.admin.js.", ".js")).ToList();
        if (!pluginAssembly.GetManifestResourceNames().Contains(AnchorsResource, StringComparer.Ordinal))
        {
            if (scripts.Count > 0)
            {
                problems.Add($"{pluginAssembly.GetName().Name} has scripts but no Web/anchors.json; declare its jellyfin-web anchors there (an empty list if it has none).");
            }

            return problems;
        }

        var anchors = ReadAnchors(pluginAssembly, problems);
        var literals = scripts
            .SelectMany(r => JsSource.Parse(ReadResource(pluginAssembly, r.Resource)).Strings)
            .Select(s => s.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var anchor in anchors.Where(a => !literals.Contains(a.Name)))
        {
            problems.Add($"Web/anchors.json: '{anchor.Name}' isn't used by name in any Web/js file; remove it, or find it through kit.anchors.");
        }

        return problems;
    }

    /// <summary>
    /// Reads the anchors a plugin embeds in <c>Web/anchors.json</c> (<see cref="AnchorsResource"/>).
    /// </summary>
    /// <param name="pluginAssembly">The plugin's assembly.</param>
    /// <param name="problems">Receives what is wrong with the file.</param>
    /// <returns>The well-formed anchors; empty without the file.</returns>
    public static IReadOnlyList<DeclaredAnchor> ReadAnchors(Assembly pluginAssembly, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(pluginAssembly);
        ArgumentNullException.ThrowIfNull(problems);
        return pluginAssembly.GetManifestResourceNames().Contains(AnchorsResource, StringComparer.Ordinal)
            ? ReadAnchors("Web/anchors.json", ReadResource(pluginAssembly, AnchorsResource), kit: false, problems)
            : [];
    }

    /// <summary>
    /// Reads an anchor list: <c>{ "comment": "...", "anchors": [ { "name", "css", "required", "views", "note" } ] }</c>.
    /// </summary>
    /// <param name="label">The file, for messages.</param>
    /// <param name="json">The file's text.</param>
    /// <param name="kit">Whether it is the kit's list (names start with <c>kit.</c>) or a plugin's (they don't).</param>
    /// <param name="problems">Receives what is wrong with it.</param>
    /// <returns>The well-formed anchors.</returns>
    public static IReadOnlyList<DeclaredAnchor> ReadAnchors(string label, string json, bool kit, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(problems);
        var result = new List<DeclaredAnchor>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("anchors", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"{label} must be an object with an \"anchors\" array.");
                return result;
            }

            foreach (var entry in list.EnumerateArray())
            {
                var name = Text(entry, "name");
                var css = StringArray(entry, "css");
                var views = StringArray(entry, "views");
                var required = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("required", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? r.GetBoolean()
                    : (bool?)null;
                if (name is null || !(kit ? KitAnchorName() : PluginAnchorName()).IsMatch(name) || css is null || css.Count == 0
                    || views is null || views.Count == 0 || required is null || string.IsNullOrWhiteSpace(Text(entry, "note")))
                {
                    problems.Add($"{label}: the entry '{name}' needs a {(kit ? "'kit.' " : string.Empty)}camelCase name, css and views arrays, required and a note.");
                    continue;
                }

                if (result.Any(a => a.Name == name))
                {
                    problems.Add($"{label}: '{name}' is declared twice.");
                    continue;
                }

                result.Add(new DeclaredAnchor(name, css, required.Value, views));
            }
        }
        catch (JsonException ex)
        {
            problems.Add($"{label} isn't valid JSON: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Gets the classes and ids the anchors' selectors name that are distinctive enough to grep for in scripts: those with
    /// a capital letter, a digit or a dash, or of at least 10 characters (<c>videoOsdBottom</c>, <c>htmlvideoplayer</c>;
    /// not <c>buttons</c> or <c>hide</c>, which are plain words). They join <see cref="AnchorDenylist"/> for the plugin's
    /// scripts.
    /// </summary>
    /// <param name="anchors">The anchors.</param>
    /// <returns>The tokens.</returns>
    public static IReadOnlyList<string> AnchorTokens(IEnumerable<DeclaredAnchor> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        return anchors
            .SelectMany(a => a.Css)
            .SelectMany(css => SelectorName().Matches(css).Select(m => m.Groups["name"].Value))
            .Where(token => token.Length >= 10 || token.Any(c => char.IsUpper(c) || char.IsDigit(c) || c == '-'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? Text(JsonElement entry, string property)
        => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string>? StringArray(JsonElement entry, string property)
    {
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
            {
                return null;
            }

            list.Add(item.GetString()!);
        }

        return list;
    }

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex PluginAnchorName();

    [GeneratedRegex(@"^kit\.[a-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex KitAnchorName();

    // A class or id in a selector.
    [GeneratedRegex(@"[.#](?<name>-?[A-Za-z_][\w-]*)")]
    private static partial Regex SelectorName();
}

/// <summary>
/// One entry of an anchor list, as the coupling checks read it.
/// </summary>
/// <param name="Name">The anchor's name.</param>
/// <param name="Css">The primary selector, then the fallbacks.</param>
/// <param name="Required">Whether the feature can't work without it.</param>
/// <param name="Views">The views it is expected on.</param>
public sealed record DeclaredAnchor(string Name, IReadOnlyList<string> Css, bool Required, IReadOnlyList<string> Views);
