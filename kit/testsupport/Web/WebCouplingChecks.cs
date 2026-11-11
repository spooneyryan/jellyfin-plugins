using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// The DOM-coupling policy as greps:
/// <list type="bullet">
/// <item><description><b>kit.js</b>: jellyfin-web names appear only inside its two tables, <c>KIT_NATIVE_CLASSES</c> and
/// <c>KIT_ANCHORS</c>; it declares nothing but the kit's own top-level names; no HTML strings; ES2019 only.</description></item>
/// <item><description><b>A plugin's <c>Web/js</c> files</b>: named <c>NN-name.js</c>; jellyfin-web anchors in none of them
/// (their selectors live in <c>Web/anchors.json</c>, and <c>10-selectors.js</c> finds them by name through
/// <c>kit.anchors</c>), and no CSS selector at all in <c>10-selectors.js</c>, so every selector the plugin uses on
/// jellyfin-web is declared and covered by the health reports and <c>Test-WebAnchors.ps1</c>; native
/// styling classes nowhere (they come from the kit's helpers); no HTML strings; none of the names the ClientScript IIFE
/// reserves redeclared.</description></item>
/// <item><description><b>Style sheets</b>: never the <c>--jf-palette-*</c> surface, text or contrast-text variables;
/// <c>kit.css</c> styles only its own <c>__PFX__-</c> classes.</description></item>
/// <item><description><b>Anchor lists</b>: the plugin's <c>Web/anchors.json</c> exists with a script, is well formed,
/// and every anchor it declares is used by name in the plugin's scripts; the classes and ids of its selectors join the
/// denylist above.</description></item>
/// </list>
/// </summary>
public static partial class WebCouplingChecks
{
    /// <summary>The resource name of kit.js in a plugin assembly (Kit.props).</summary>
    public const string KitScriptResource = "Kit.kit.js";

    /// <summary>The resource name of kit.css in a plugin assembly (Kit.props).</summary>
    public const string KitCssResource = "Kit.kit.css";

    /// <summary>
    /// The one plugin file that reaches jellyfin-web anchors, by the names <c>Web/anchors.json</c> declares; it writes no
    /// selector itself.
    /// </summary>
    public const string SelectorsFile = "10-selectors.js";

    /// <summary>
    /// Gets jellyfin-web's anchor names that plugin code never writes (their selectors live in <c>Web/anchors.json</c>) and
    /// the kit writes only in <c>KIT_ANCHORS</c>: the kit's own list plus the anchors of Bookmarks 1.0 and of the
    /// kit's planned modules.
    /// </summary>
    public static IReadOnlyList<string> AnchorDenylist { get; } =
    [
        "itemDetailPage", "detailPageContent", "mainDetailButtons", "btnMoreCommands", "actionSheet", "actionSheetMenuItem",
        "dialogContainer", "videoOsdBottom", "videoOsdPage", "videoPlayerContainer", "htmlvideoplayer", "btnSubtitles",
        "btnAudio", "btnVideoOsdSettings", "mainDrawer", "mainDrawer-scrollContainer", "userMenuOptions", "pluginMenuOptions",
        "fallbackPage", "cardScalable", "itemMiscInfo", "userPreferencesPage", "headerSearchButton", "searchFieldsInner",
        "selectSource", "btnSelectionPanelOptions", "chkItemSelect", "skinHeader", "mainAnimatedPage", "libraryPage",
        "actionSheetScroller", "itemAction", "selectionCommandsPanel", "itemSelectionPanel", "btnCloseSelectionPanel",
    ];

    /// <summary>
    /// Gets the names the ClientScript IIFE declares before the plugin's files (EmbeddedScript), which a plugin must not
    /// redeclare.
    /// </summary>
    public static IReadOnlyList<string> ReservedNames { get; } =
        ["KIT_BUILD", "KIT_CSS", "KIT_ANCHOR_DECLARATIONS", "KIT_NATIVE_CLASSES", "KIT_ANCHORS", "KitApiError", "createKit", "kit"];

    /// <summary>
    /// Gets the palette variables our CSS never uses: they follow MUI's palettes, not the theme our nodes sit in.
    /// </summary>
    public static IReadOnlyList<string> ForbiddenPaletteVariables { get; } =
        ["--jf-palette-background-", "--jf-palette-text-", "--jf-palette-primary-contrastText"];

    // Native class names that are also plain words (role="dialog", "card", ...): only whole literals equal to them count.
    private static readonly HashSet<string> _plainWords = new(StringComparer.Ordinal) { "card", "dialog", "hide", "raised", "secondary" };

    private static readonly string[] _htmlSinks = ["innerHTML", "outerHTML", "insertAdjacentHTML", "document.write"];

    /// <summary>
    /// Reads the native styling classes from kit.js's <c>KIT_NATIVE_CLASSES</c> table.
    /// </summary>
    /// <param name="kitJs">kit.js.</param>
    /// <returns>The class names.</returns>
    public static IReadOnlyList<string> NativeClasses(string kitJs)
        => JsSource.Parse(Block(kitJs, "kit-native-classes") ?? string.Empty).Strings.Select(s => s.Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Checks kit.js.
    /// </summary>
    /// <param name="kitJs">kit.js.</param>
    /// <returns>The problems; empty when it follows the policy.</returns>
    public static IReadOnlyList<string> CheckKitScript(string kitJs)
    {
        ArgumentNullException.ThrowIfNull(kitJs);
        var problems = new List<string>();
        var classes = Block(kitJs, "kit-native-classes");
        var anchors = Block(kitJs, "kit-anchors");
        if (classes is null || anchors is null)
        {
            problems.Add("kit.js: the KIT_NATIVE_CLASSES and KIT_ANCHORS tables must each sit once between their // <kit-native-classes> and // <kit-anchors> markers.");
            return problems;
        }

        var outside = kitJs.Replace(classes, string.Empty, StringComparison.Ordinal).Replace(anchors, string.Empty, StringComparison.Ordinal);
        var source = JsSource.Parse(outside);
        var names = NativeClasses(kitJs).Concat(AnchorDenylist).ToHashSet(StringComparer.Ordinal);
        foreach (var literal in source.Strings)
        {
            foreach (var token in JsSource.Tokens(literal.Value).Where(t => names.Contains(t) && !_plainWords.Contains(t)))
            {
                problems.Add($"kit.js: '{token}' (in \"{literal.Value}\") is a jellyfin-web name outside KIT_NATIVE_CLASSES and KIT_ANCHORS.");
            }
        }

        problems.AddRange(CheckHtmlSinks("kit.js", source));
        problems.AddRange(CheckEs2019("kit.js", source));
        foreach (Match declaration in TopLevelDeclaration().Matches(JsSource.Parse(kitJs).Code))
        {
            var name = declaration.Groups["name"].Value;
            if (name is not ("KIT_NATIVE_CLASSES" or "KIT_ANCHORS" or "KitApiError" or "createKit"))
            {
                problems.Add($"kit.js: the top-level name '{name}' would share the plugin's IIFE scope; keep it inside createKit.");
            }
        }

        foreach (var statement in TopLevelLine().Matches(JsSource.Parse(kitJs).Code).Select(m => m.Value.Trim()))
        {
            if (!TopLevelDeclaration().IsMatch(statement + " ") && !statement.StartsWith('}') && statement.Length > 0)
            {
                problems.Add($"kit.js: top-level statement '{statement}': kit.js only declares; the plugin's script runs it.");
            }
        }

        return problems;
    }

    /// <summary>
    /// Checks a plugin's <c>Web/js</c> files, read from its assembly (the <c>Web.js.*</c> resources of Kit.props), against
    /// the native classes of the kit.js compiled into it.
    /// </summary>
    /// <param name="pluginAssembly">The plugin's assembly.</param>
    /// <returns>The problems; empty when the files follow the policy.</returns>
    public static IReadOnlyList<string> CheckPluginScripts(Assembly pluginAssembly)
    {
        ArgumentNullException.ThrowIfNull(pluginAssembly);
        var problems = new List<string>();
        var files = Resources(pluginAssembly, "Web.js.", ".js").Concat(Resources(pluginAssembly, "Web.admin.js.", ".js")).ToList();
        if (files.Count == 0)
        {
            return ["No Web/js files are embedded in " + pluginAssembly.GetName().Name + "; Kit.props embeds Web/js/*.js."];
        }

        var nativeClasses = NativeClasses(ReadResource(pluginAssembly, KitScriptResource)).ToHashSet(StringComparer.Ordinal);
        // The anchor list's classes and ids join the denylist; its anchor names are what the code uses instead.
        var declared = ReadAnchors(pluginAssembly, []);
        var anchors = AnchorDenylist.Concat(AnchorTokens(declared).Except(declared.Select(a => a.Name), StringComparer.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (var (resource, prefix) in files)
        {
            var name = resource[prefix.Length..];
            var label = prefix.Replace('.', '/') + name;
            if (!FileName().IsMatch(name))
            {
                problems.Add($"{label}: plugin scripts are named NN-name.js (lower case), so the name order is the load order.");
            }

            var source = JsSource.Parse(ReadResource(pluginAssembly, resource));
            var selectorsFile = name == SelectorsFile && prefix == "Web.js.";
            foreach (var literal in source.Strings)
            {
                // No file is exempt: a selector written in 10-selectors.js would reach jellyfin-web undeclared,
                // unseen by kit.health, the ClientHealth endpoint and Test-WebAnchors.ps1. A kit anchor's name
                // ("kit.drawer") is a name, not a selector.
                if (selectorsFile && !KitAnchorName().IsMatch(literal.Value) && CssSelectorSyntax().Match(literal.Value) is { Success: true } selector)
                {
                    problems.Add($"{label} line {literal.Line}: '{selector.Value.Trim()}' (in \"{literal.Value}\") is a CSS selector; declare jellyfin-web's selectors in Web/anchors.json and find them by name through kit.anchors, and select the plugin's own nodes in the file that builds them.");
                }

                foreach (var token in JsSource.Tokens(literal.Value))
                {
                    if (anchors.Contains(token))
                    {
                        problems.Add($"{label} line {literal.Line}: the jellyfin-web anchor '{token}' belongs in Web/anchors.json, found by name through kit.anchors in {SelectorsFile}.");
                    }

                    if (nativeClasses.Contains(token) && (!_plainWords.Contains(token) || literal.Value == token))
                    {
                        problems.Add($"{label} line {literal.Line}: the native class '{token}' comes from the kit's markup helpers, not plugin code.");
                    }
                }
            }

            problems.AddRange(CheckHtmlSinks(label, source));
            problems.AddRange(CheckEs2019(label, source));
            foreach (Match declaration in TopLevelDeclaration().Matches(source.Code))
            {
                if (ReservedNames.Contains(declaration.Groups["name"].Value, StringComparer.Ordinal))
                {
                    problems.Add($"{label}: '{declaration.Groups["name"].Value}' is declared by the ClientScript itself (EmbeddedScript).");
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Checks a style sheet for the palette variables our CSS never uses.
    /// </summary>
    /// <param name="label">The file, for messages.</param>
    /// <param name="css">The style sheet.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckCss(string label, string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        var rules = CssComment().Replace(css, string.Empty);
        return ForbiddenPaletteVariables
            .Where(v => rules.Contains(v, StringComparison.Ordinal))
            .Select(v => $"{label}: uses {v}*, which follows MUI's palette rather than the theme; use neutral colours, native classes or --jf-palette-primary-main.")
            .ToList();
    }

    /// <summary>
    /// Checks kit.css: the palette rule, and every class selector is one of the kit's own <c>__PFX__-</c> classes.
    /// </summary>
    /// <param name="kitCss">kit.css.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckKitCss(string kitCss)
    {
        ArgumentNullException.ThrowIfNull(kitCss);
        var problems = CheckCss("kit.css", kitCss).ToList();
        var withoutComments = CssComment().Replace(kitCss, string.Empty);
        foreach (Match selector in CssClassSelector().Matches(withoutComments))
        {
            if (!selector.Groups["name"].Value.StartsWith("__PFX__-", StringComparison.Ordinal))
            {
                problems.Add($"kit.css: '.{selector.Groups["name"].Value}' isn't a kit class; kit.css styles only __PFX__- classes (native classes come from the theme).");
            }
        }

        return problems;
    }

    /// <summary>
    /// Checks a plugin's embedded style sheets (<c>Web.css.*</c>, <c>Web.admin.css.*</c>) for the palette rule.
    /// </summary>
    /// <param name="pluginAssembly">The plugin's assembly.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckPluginStyles(Assembly pluginAssembly)
    {
        ArgumentNullException.ThrowIfNull(pluginAssembly);
        return Resources(pluginAssembly, "Web.css.", ".css").Concat(Resources(pluginAssembly, "Web.admin.css.", ".css"))
            .SelectMany(r => CheckCss(r.Resource, ReadResource(pluginAssembly, r.Resource)))
            .ToList();
    }

    private static IEnumerable<string> CheckHtmlSinks(string label, JsSource source)
        => _htmlSinks.Where(sink => source.Code.Contains(sink, StringComparison.Ordinal))
            .Select(sink => $"{label}: uses {sink}; build nodes with kit.h, which writes text nodes only.");

    private static IEnumerable<string> CheckEs2019(string label, JsSource source)
    {
        if (OptionalChaining().IsMatch(source.Code))
        {
            yield return $"{label}: uses optional chaining (?.), which is ES2020; the web scripts are ES2019.";
        }

        if (source.Code.Contains("??", StringComparison.Ordinal))
        {
            yield return $"{label}: uses ??, which is ES2020; the web scripts are ES2019.";
        }
    }

    private static string? Block(string text, string marker)
    {
        var start = text.IndexOf("// <" + marker + ">", StringComparison.Ordinal);
        var endMarker = "// </" + marker + ">";
        var end = text.IndexOf(endMarker, StringComparison.Ordinal);
        if (start < 0 || end < start || text.IndexOf("// <" + marker + ">", start + 1, StringComparison.Ordinal) >= 0)
        {
            return null;
        }

        return text[start..(end + endMarker.Length)];
    }

    private static IEnumerable<(string Resource, string Prefix)> Resources(Assembly assembly, string prefix, string extension)
        => assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .Select(n => (n, prefix));

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{assembly.GetName().Name} has no resource {name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [GeneratedRegex(@"^(?:const|let|var|class|function|async function)\s+(?<name>[A-Za-z_$][\w$]*)", RegexOptions.Multiline)]
    private static partial Regex TopLevelDeclaration();

    [GeneratedRegex(@"^\S.*$", RegexOptions.Multiline)]
    private static partial Regex TopLevelLine();

    [GeneratedRegex(@"^\d{2}-[a-z0-9-]+\.js$")]
    private static partial Regex FileName();

    [GeneratedRegex(@"\?\.(?!\d)")]
    private static partial Regex OptionalChaining();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CssComment();

    // A class selector: a dot that starts a name, not a number such as .5em or rgba(0,0,0,.6).
    [GeneratedRegex(@"(?<![\w\d)])\.(?<name>-?[A-Za-z_][\w-]*)")]
    private static partial Regex CssClassSelector();

    // A class (.name), id (#name) or attribute ([name], [name=...]) selector anywhere in a string, compound ones such as
    // "div.name" included; not a number (".5em", "10.11.5") or a hash route ("#/details").
    [GeneratedRegex(@"[.#]-?[A-Za-z_][\w-]*|\[\s*[A-Za-z_][\w-]*\s*(?:[~|^$*]?=|\])")]
    private static partial Regex CssSelectorSyntax();
}
