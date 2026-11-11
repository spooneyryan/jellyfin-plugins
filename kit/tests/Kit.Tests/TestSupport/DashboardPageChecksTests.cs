using System;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.TestSupport;

/// <summary>
/// The dashboard-module test: dashboard scripts build HTML only from string literals, and
/// dashboard HTML holds no <c>${</c>.
/// </summary>
public sealed class DashboardPageChecksTests
{
    [Theory]
    [InlineData("row.textContent = entry.Anchor;")]
    [InlineData("view.innerHTML = '';")]
    [InlineData("view.innerHTML = \"<p>Loading</p>\";")]
    [InlineData("view.innerHTML += '<hr>';")]
    [InlineData("view.innerHTML = `<p>static</p>`;")]
    [InlineData("list.insertAdjacentHTML('beforeend', '<li>static</li>');")]
    [InlineData("if (a.innerHTML === b) { x(); }")]
    [InlineData("// view.innerHTML = data;\nview.textContent = data;")]
    [InlineData("var text = 'view.innerHTML = data';")]
    public void TextContentAndLiteralHtml_Pass(string script)
    {
        Assert.Empty(DashboardPageChecks.CheckScript("x.js", script));
    }

    [Theory]
    [InlineData("view.innerHTML = data;", "innerHTML")]
    [InlineData("view.innerHTML = '<p>' + entry.Anchor + '</p>';", "innerHTML")]
    [InlineData("view.outerHTML = html;", "outerHTML")]
    [InlineData("view.innerHTML += row;", "innerHTML")]
    [InlineData("view.innerHTML = `<p>${entry.Anchor}</p>`;", "innerHTML")]
    [InlineData("view.innerHTML\n    = data;", "innerHTML")]
    [InlineData("list.insertAdjacentHTML('beforeend', html);", "insertAdjacentHTML")]
    [InlineData("list.insertAdjacentHTML(where, '<li>' + x + '</li>');", "insertAdjacentHTML")]
    [InlineData("document.write(html);", "document.write")]
    [InlineData("range.createContextualFragment(html);", "createContextualFragment")]
    public void HtmlFromData_Fails(string script, string expected)
    {
        Assert.Contains(DashboardPageChecks.CheckScript("x.js", script), p => p.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void HtmlPages_AreCheckedInTheirInlineScripts_AndForDollarBraces()
    {
        const string Page = """
            <div data-role="page"><script src="other.js"></script>
            <script type="text/javascript">
                row.textContent = entry.Anchor;
            </script>
            <script>
                view.innerHTML = data;
            </script></div>
            """;

        var problems = DashboardPageChecks.CheckHtml("page.html", Page);

        Assert.Single(problems);
        Assert.Contains("inline script #2 line 2: innerHTML", problems[0], StringComparison.Ordinal);
        Assert.Contains(DashboardPageChecks.CheckHtml("page.html", "<p>${Name}</p>"), p => p.Contains("translateHtml", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ReadsEveryPageOfGetPages_FromTheAssembly()
    {
        var plugin = new ResourceAssembly()
            .With("Fx.Configuration.configPage.html", "<div><script>view.textContent = 'ok';</script></div>")
            .With("Fx.Configuration.FxJs.js", "export default function (view) { view.innerHTML = view.dataset.html; }");
        PluginPageInfo[] pages =
        [
            new() { Name = "Fx", EmbeddedResourcePath = "Fx.Configuration.configPage.html" },
            new() { Name = "FxJs", EmbeddedResourcePath = "Fx.Configuration.FxJs.js" },
            new() { Name = "FxMissing", EmbeddedResourcePath = "Fx.Configuration.missing.html" },
        ];

        var problems = DashboardPageChecks.Check(plugin, pages);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("FxJs (Fx.Configuration.FxJs.js) line 1: innerHTML", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("isn't embedded", StringComparison.Ordinal));
    }
}
