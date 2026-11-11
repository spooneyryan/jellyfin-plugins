using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// The jellyfin-web anchors of one plugin: the kit's own (<c>kit/web/anchors.json</c>,
/// embedded as <c>Kit.anchors.json</c>) followed by the plugin's (<c>Web/anchors.json</c>, embedded as
/// <c>Web.anchors.json</c>). The ClientScript gets them as <c>KIT_ANCHOR_DECLARATIONS</c> (<see cref="ToClientJson"/>),
/// and the ClientHealth endpoint accepts only their names and views (<see cref="Accepts"/>).
/// </summary>
/// <remarks>
/// A file is <c>{ "comment": "...", "anchors": [ { "name", "css": [primary, ...fallbacks], "required", "views", "note" } ] }</c>.
/// An entry that breaks a rule is left out and named in <see cref="Problems"/>; the plugin's tests fail on any problem
/// (<c>WebCouplingChecks.CheckAnchors</c>), so at run time a bad entry only means that anchor is unknown.
/// </remarks>
internal sealed partial class WebAnchorCatalog
{
    /// <summary>The resource name of the kit's anchors (Kit.props).</summary>
    public const string KitResource = "Kit.anchors.json";

    /// <summary>The resource name of the plugin's anchors (Kit.props).</summary>
    public const string PluginResource = "Web.anchors.json";

    /// <summary>The prefix of the kit's own anchor names.</summary>
    public const string KitNamePrefix = "kit.";

    /// <summary>The most selectors (primary plus fallbacks) one anchor may have.</summary>
    public const int MaxSelectors = 8;

    /// <summary>The longest selector.</summary>
    public const int MaxSelectorLength = 200;

    /// <summary>The suffix <see cref="ToClientJson"/> gives the classes and ids of a renamed anchor (developer builds).</summary>
    public const string RenameSuffix = "KitRenamed";

    private static readonly string[] _entryProperties = ["name", "css", "required", "views", "note"];

    private readonly Dictionary<string, WebAnchor> _byName;

    private WebAnchorCatalog(IReadOnlyList<WebAnchor> anchors, IReadOnlyList<string> problems)
    {
        Anchors = anchors;
        Problems = problems;
        _byName = anchors.ToDictionary(a => a.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the anchors, the kit's first.
    /// </summary>
    public IReadOnlyList<WebAnchor> Anchors { get; }

    /// <summary>
    /// Gets the problems found while reading the files; empty when both are valid.
    /// </summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>
    /// Reads the kit's anchors from <paramref name="kitAssembly"/> and the plugin's from <paramref name="pluginAssembly"/>
    /// (the same assembly in a plugin). Never throws: an unreadable file becomes a problem and contributes no anchors.
    /// </summary>
    /// <param name="pluginAssembly">The plugin's assembly.</param>
    /// <param name="kitAssembly">The assembly holding <see cref="KitResource"/>.</param>
    /// <returns>The catalog.</returns>
    public static WebAnchorCatalog Load(Assembly pluginAssembly, Assembly kitAssembly)
    {
        ArgumentNullException.ThrowIfNull(pluginAssembly);
        ArgumentNullException.ThrowIfNull(kitAssembly);

        var problems = new List<string>();
        var kit = ReadResource(kitAssembly, KitResource, problems);
        if (kit is null)
        {
            problems.Add($"{KitResource} is missing from {kitAssembly.GetName().Name}; kit/build/Kit.props embeds it.");
        }

        var plugin = ReadResource(pluginAssembly, PluginResource, problems);
        return Parse(kit, plugin, problems);
    }

    /// <summary>
    /// Builds a catalog from the two files' text.
    /// </summary>
    /// <param name="kitJson">The kit's <c>anchors.json</c>, or <c>null</c>.</param>
    /// <param name="pluginJson">The plugin's <c>Web/anchors.json</c>, or <c>null</c> for a plugin without one.</param>
    /// <returns>The catalog.</returns>
    public static WebAnchorCatalog Parse(string? kitJson, string? pluginJson) => Parse(kitJson, pluginJson, []);

    /// <summary>
    /// Gets an anchor by name (ordinal).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The anchor, or <c>null</c>.</returns>
    public WebAnchor? Find(string name) => name is not null && _byName.TryGetValue(name, out var anchor) ? anchor : null;

    /// <summary>
    /// Gets a value indicating whether a health report may name this anchor on this view: the anchor is declared and the
    /// view is one of its views.
    /// </summary>
    /// <param name="anchor">The anchor name.</param>
    /// <param name="view">The view name.</param>
    /// <returns><c>true</c> when both are declared together.</returns>
    public bool Accepts(string anchor, string view)
        => Find(anchor) is { } declared && declared.Views.Contains(view, StringComparer.Ordinal);

    /// <summary>
    /// Serializes the anchors for the ClientScript's <c>KIT_ANCHOR_DECLARATIONS</c>: <c>[{name, css, required, views}]</c>,
    /// without the notes. The anchors named in <paramref name="renamed"/> get every class and id of their selectors
    /// renamed (developer builds with <c>-p:KitRenameAnchors</c>), so the browser never finds them.
    /// </summary>
    /// <param name="renamed">The anchors to rename; empty in normal builds.</param>
    /// <returns>A JSON array.</returns>
    public string ToClientJson(IReadOnlyCollection<string> renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        var list = Anchors.Select(a => new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["name"] = a.Name,
            ["css"] = renamed.Contains(a.Name, StringComparer.Ordinal) ? a.Css.Select(Rename).ToList() : a.Css,
            ["required"] = a.Required,
            ["views"] = a.Views
        });
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// Renames every class and id of a selector (<c>.mainDetailButtons</c> becomes <c>.mainDetailButtonsKitRenamed</c>); a
    /// selector without either gets <c>[data-kit-renamed]</c>, which nothing carries.
    /// </summary>
    /// <param name="selector">The selector.</param>
    /// <returns>A selector that matches nothing the web client renders.</returns>
    public static string Rename(string selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var renamed = ClassOrId().Replace(selector, "$0" + RenameSuffix);
        return renamed == selector ? selector + "[data-kit-renamed]" : renamed;
    }

    /// <summary>
    /// Reads the anchor names a developer build renames, from the plugin assembly's <c>Jellyfin.Plugin.Kit.RenameAnchors</c>
    /// metadata (<c>-p:KitRenameAnchors=a,b</c>, Kit.props). Empty in normal builds.
    /// </summary>
    /// <param name="pluginAssembly">The plugin's assembly.</param>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> RenamedAnchors(Assembly pluginAssembly)
    {
        ArgumentNullException.ThrowIfNull(pluginAssembly);
        var value = pluginAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "Jellyfin.Plugin.Kit.RenameAnchors", StringComparison.Ordinal))?.Value;
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();
    }

    private static WebAnchorCatalog Parse(string? kitJson, string? pluginJson, List<string> problems)
    {
        var anchors = new List<WebAnchor>();
        if (kitJson is not null)
        {
            anchors.AddRange(ParseFile("kit/web/anchors.json", kitJson, kit: true, problems));
        }

        if (pluginJson is not null)
        {
            anchors.AddRange(ParseFile("Web/anchors.json", pluginJson, kit: false, problems));
        }

        var unique = new List<WebAnchor>();
        foreach (var anchor in anchors)
        {
            if (unique.Any(a => string.Equals(a.Name, anchor.Name, StringComparison.Ordinal)))
            {
                problems.Add($"The anchor '{anchor.Name}' is declared twice; the second is left out.");
                continue;
            }

            unique.Add(anchor);
        }

        return new WebAnchorCatalog(unique, problems);
    }

    private static List<WebAnchor> ParseFile(string label, string json, bool kit, List<string> problems)
    {
        var result = new List<WebAnchor>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException ex)
        {
            problems.Add($"{label} isn't valid JSON: {ex.Message}");
            return result;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("anchors", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"{label} must be an object with an \"anchors\" array.");
                return result;
            }

            foreach (var property in root.EnumerateObject().Where(p => p.Name is not ("anchors" or "comment")))
            {
                problems.Add($"{label}: unknown top-level property \"{property.Name}\".");
            }

            var index = 0;
            foreach (var entry in list.EnumerateArray())
            {
                var anchor = ParseEntry($"{label} anchor #{index}", entry, kit, problems);
                if (anchor is not null)
                {
                    result.Add(anchor);
                }

                index++;
            }
        }

        return result;
    }

    private static WebAnchor? ParseEntry(string label, JsonElement entry, bool kit, List<string> problems)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{label} isn't an object.");
            return null;
        }

        var before = problems.Count;
        foreach (var property in entry.EnumerateObject().Where(p => !_entryProperties.Contains(p.Name, StringComparer.Ordinal)))
        {
            problems.Add($"{label}: unknown property \"{property.Name}\" (allowed: {string.Join(", ", _entryProperties)}).");
        }

        var name = entry.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : null;
        if (name is null || !(kit ? KitName() : PluginName()).IsMatch(name))
        {
            problems.Add(kit
                ? $"{label}: name '{name}' must be 'kit.' followed by a camelCase name such as 'kit.f1Host'."
                : $"{label}: name '{name}' must be a camelCase name such as 'detailButtons' (names starting with 'kit.' are the kit's).");
        }

        label = name is null ? label : $"{label} ('{name}')";
        var css = Strings(entry, "css");
        if (css is null || css.Count is 0 or > MaxSelectors)
        {
            problems.Add($"{label}: css must be an array of 1 to {MaxSelectors} selectors, the primary first.");
        }
        else
        {
            foreach (var selector in css.Where(s => s.Length is 0 or > MaxSelectorLength || s.Trim() != s || s.Any(c => char.IsControl(c) || c is '<' or '{' or '}' or ';' or '\\' or '`')))
            {
                problems.Add($"{label}: the selector \"{selector}\" must be 1 to {MaxSelectorLength} characters without surrounding spaces, control characters, < {{ }} ; \\ or `.");
            }

            if (css.Distinct(StringComparer.Ordinal).Count() != css.Count)
            {
                problems.Add($"{label}: css lists a selector twice.");
            }
        }

        var required = entry.TryGetProperty("required", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : (bool?)null;
        if (required is null)
        {
            problems.Add($"{label}: required must be true or false.");
        }

        var views = Strings(entry, "views");
        if (views is null || views.Count is 0 or > 8 || views.Any(v => !ViewName().IsMatch(v)) || views.Distinct(StringComparer.Ordinal).Count() != views.Count)
        {
            problems.Add($"{label}: views must be an array of 1 to 8 distinct lower-case view names such as 'details'.");
        }

        var note = entry.TryGetProperty("note", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : null;
        if (string.IsNullOrWhiteSpace(note) || note.Length > 400)
        {
            problems.Add($"{label}: note must say where the anchor comes from (the bundle chunk or jellyfin-web source file), in at most 400 characters.");
        }

        return problems.Count == before ? new WebAnchor(name!, css!, required!.Value, views!, note!) : null;
    }

    private static List<string>? Strings(JsonElement entry, string property)
    {
        if (!entry.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            list.Add(item.GetString()!);
        }

        return list;
    }

    private static string? ReadResource(Assembly assembly, string name, List<string> problems)
    {
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        try
        {
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException)
        {
            problems.Add($"{name} in {assembly.GetName().Name} isn't valid UTF-8.");
            return null;
        }
    }

    [GeneratedRegex(@"^[a-z][A-Za-z0-9]{0,39}\z")]
    private static partial Regex PluginName();

    [GeneratedRegex(@"^kit\.[a-z][A-Za-z0-9]{0,39}\z")]
    private static partial Regex KitName();

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}\z")]
    private static partial Regex ViewName();

    // A class or id in a selector: '.' or '#' followed by a CSS name (not a number such as .5).
    [GeneratedRegex(@"(?<=[.#])-?[A-Za-z_][\w-]*")]
    private static partial Regex ClassOrId();
}
