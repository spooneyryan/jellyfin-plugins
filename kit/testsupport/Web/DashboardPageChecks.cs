using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Kit.TestSupport.Web;

/// <summary>
/// The dashboard-module test. A plugin's dashboard pages (<c>IHasWebPages.GetPages()</c>:
/// the settings HTML page, and the <c>&lt;Plugin&gt;Js</c> ES module page when there is one) render every server-supplied
/// value (Diagnostics rows, client health reports, import samples, review text) with <c>textContent</c>, because the
/// ClientScript's <c>h</c> builder isn't there. So their scripts, the module and the HTML pages' inline scripts:
/// <list type="bullet">
/// <item><description>assign <c>innerHTML</c>/<c>outerHTML</c> (<c>=</c> or <c>+=</c>) only a string literal (a template
/// literal without <c>${…}</c> counts as one);</description></item>
/// <item><description>call <c>insertAdjacentHTML</c> only with a string literal as its HTML;</description></item>
/// <item><description>never call <c>document.write</c> or <c>createContextualFragment</c>.</description></item>
/// </list>
/// And no dashboard HTML page holds <c>${</c> anywhere: jellyfin-web's <c>translateHtml</c> would replace it.
/// </summary>
public static partial class DashboardPageChecks
{
    /// <summary>
    /// Checks every page of <c>GetPages()</c>, read from the plugin's embedded resources.
    /// </summary>
    /// <param name="pluginAssembly">The plugin's assembly.</param>
    /// <param name="pages">The plugin's <c>GetPages()</c>.</param>
    /// <returns>The problems; empty when every page follows the rules.</returns>
    public static IReadOnlyList<string> Check(Assembly pluginAssembly, IEnumerable<PluginPageInfo> pages)
    {
        ArgumentNullException.ThrowIfNull(pluginAssembly);
        ArgumentNullException.ThrowIfNull(pages);
        var problems = new List<string>();
        foreach (var page in pages)
        {
            var label = $"{page.Name} ({page.EmbeddedResourcePath})";
            using var stream = pluginAssembly.GetManifestResourceStream(page.EmbeddedResourcePath);
            if (stream is null)
            {
                problems.Add($"{label}: the resource isn't embedded in {pluginAssembly.GetName().Name}.");
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            problems.AddRange(page.EmbeddedResourcePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? CheckScript(label, text) : CheckHtml(label, text));
        }

        return problems;
    }

    /// <summary>
    /// Checks a dashboard HTML page: no <c>${</c>, and its inline scripts follow <see cref="CheckScript"/>.
    /// </summary>
    /// <param name="label">The page, for messages.</param>
    /// <param name="html">The page.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckHtml(string label, string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var problems = new List<string>();
        if (html.Contains("${", StringComparison.Ordinal))
        {
            problems.Add($"{label}: holds '${{'; jellyfin-web's translateHtml replaces ${{...}} in dashboard HTML, scripts included.");
        }

        var index = 0;
        foreach (Match script in InlineScript().Matches(html))
        {
            if (!script.Groups["attrs"].Value.Contains("src=", StringComparison.OrdinalIgnoreCase))
            {
                problems.AddRange(CheckScript($"{label} inline script #{index}", script.Groups["body"].Value));
            }

            index++;
        }

        return problems;
    }

    /// <summary>
    /// Checks a dashboard script (the <c>&lt;Plugin&gt;Js</c> module or an inline script): HTML only from string literals.
    /// </summary>
    /// <param name="label">The script, for messages.</param>
    /// <param name="script">The script.</param>
    /// <returns>The problems.</returns>
    public static IReadOnlyList<string> CheckScript(string label, string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var problems = new List<string>();
        var source = JsSource.Parse(script);
        var code = source.Code;
        foreach (Match sink in HtmlAssignment().Matches(code))
        {
            var line = LineOf(code, sink.Index);
            var literal = sink.Groups["literal"];
            if (!literal.Success || (literal.Value == "``" && source.Strings.Any(s => s.Line == line && s.Value.Contains("${", StringComparison.Ordinal))))
            {
                problems.Add($"{label} line {line}: {sink.Groups["sink"].Value} is assigned something other than a string literal; render server values with textContent.");
            }
        }

        foreach (Match call in InsertAdjacent().Matches(code))
        {
            var line = LineOf(code, call.Index);
            if (!InsertAdjacentLiteral().IsMatch(code[call.Index..]))
            {
                problems.Add($"{label} line {line}: insertAdjacentHTML is given something other than a string literal; render server values with textContent.");
            }
        }

        foreach (Match call in NeverAllowed().Matches(code))
        {
            problems.Add($"{label} line {LineOf(code, call.Index)}: {call.Value.TrimEnd('(').Trim()} builds HTML from text; use DOM nodes with textContent.");
        }

        return problems;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    [GeneratedRegex(@"<script(?<attrs>[^>]*)>(?<body>.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    // x.innerHTML = <rhs>: the rhs is a literal ("" '' `` in JsSource.Code) followed by the end of the expression.
    [GeneratedRegex(@"\.(?<sink>innerHTML|outerHTML)\s*\+?=(?!=)\s*(?:(?<literal>""""|''|``)\s*(?=[;,)\r\n}]|$))?")]
    private static partial Regex HtmlAssignment();

    [GeneratedRegex(@"\binsertAdjacentHTML\s*\(")]
    private static partial Regex InsertAdjacent();

    [GeneratedRegex(@"^insertAdjacentHTML\s*\(\s*[^,()]*,\s*(?:""""|''|``)\s*\)")]
    private static partial Regex InsertAdjacentLiteral();

    [GeneratedRegex(@"\b(?:document\s*\.\s*write(?:ln)?|createContextualFragment)\s*\(")]
    private static partial Regex NeverAllowed();
}
