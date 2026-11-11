using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.TestSupport;

/// <summary>
/// The scanner behind the web coupling checks: comments, strings, templates and regex literals.
/// </summary>
public sealed class JsSourceTests
{
    [Fact]
    public void Strings_AreCollected_WithTheirLines_AndBlankedInTheCode()
    {
        var source = JsSource.Parse("const a = 'one';\nconst b = \"two \\\" still\";\nconst c = `three ${x}`;");

        Assert.Equal(["one", "two \\\" still", "three ${x}"], source.Strings.Select(s => s.Value));
        Assert.Equal([1, 2, 3], source.Strings.Select(s => s.Line));
        Assert.Equal("const a = '';\nconst b = \"\";\nconst c = ``;", source.Code);
    }

    [Fact]
    public void Comments_AreRemoved_ButLinesKept()
    {
        var source = JsSource.Parse("a(); // 'not a string'\n/* 'nor\nthis' */ b('yes');");

        Assert.Equal(["yes"], source.Strings.Select(s => s.Value));
        Assert.Equal(3, source.Strings[0].Line);
        Assert.DoesNotContain("not a string", source.Code, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RegexLiterals_AreSkipped_AndDivisionIsNot()
    {
        var source = JsSource.Parse("if (/^(INPUT|'x')$/.test(t)) { return a / b / 'c'; }\nconst r = x.replace(/\"/g, '');\nreturn /a'b/;");

        Assert.Equal(["c", string.Empty], source.Strings.Select(s => s.Value));
        Assert.Contains("a / b / ''", source.Code, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Tokens_SplitSelectorsAndClassLists()
    {
        Assert.Equal(["itemDetailPage", "not", "hide", "detailPageContent"], JsSource.Tokens("#itemDetailPage:not(.hide) .detailPageContent"));
        Assert.Equal(["listItem", "listItem-border", "jfbm-x"], JsSource.Tokens("listItem listItem-border jfbm-x"));
    }
}
