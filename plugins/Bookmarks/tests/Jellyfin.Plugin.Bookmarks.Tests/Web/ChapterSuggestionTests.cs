using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Jellyfin.Plugin.Kit.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Web;

/// <summary>
/// The name dialog's chapter suggestion: the generic-name rule's word lists in <c>Web/js/36-name-dialog.js</c> are the
/// ones derived from Jellyfin 10.11.5's localisation files (<c>Web/chapter-names.json</c>, <c>words</c>), and the rule
/// classifies every case of that file as recorded (<c>chapter-names.test.mjs</c> on the built ClientScript).
/// </summary>
public sealed partial class ChapterSuggestionTests
{
    private static readonly string TestsFolder = Path.Combine(
        PluginIdentityChecks.FindRepositoryRoot(), "plugins", "Bookmarks", "tests", "Jellyfin.Plugin.Bookmarks.Tests", "Web");

    private static readonly Lazy<EmbeddedScript> Scripts = new(() => new EmbeddedScript(BookmarksKit.Options, NullLoggerFactory.Instance));

    [Fact]
    public void TheWordLists_AreTheGeneratedTable()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestsFolder, "chapter-names.json")));
        var words = fixture.RootElement.GetProperty("words");
        var expectedWithNumber = words.GetProperty("withNumber").EnumerateArray().Select(w => w.GetString()!).ToList();
        var expectedWithoutNumber = words.GetProperty("withoutNumber").EnumerateArray().Select(w => w.GetString()!).ToList();

        var script = Scripts.Value.ClientScriptText;
        Assert.Equal(expectedWithNumber, ListConstant(script, "CHAPTER_WORDS_WITH_NUMBER"));
        Assert.Equal(expectedWithoutNumber, ListConstant(script, "CHAPTER_WORDS_WITHOUT_NUMBER"));

        // 86 cultures with a "{0}" format reduce to 67 words; 3 formats have no "{0}".
        Assert.Equal(67, expectedWithNumber.Count);
        Assert.Equal(3, expectedWithoutNumber.Count);
        Assert.Equal(expectedWithNumber.Count, expectedWithNumber.Distinct(StringComparer.Ordinal).Count());
        Assert.All(expectedWithNumber, w => Assert.Equal(w, w.Normalize()));
        Assert.Contains("chapter", expectedWithNumber);
        Assert.Contains("kapitel", expectedWithNumber);
        Assert.Contains("глава", expectedWithNumber);
    }

    [Fact]
    public void TheCases_CoverTheSandboxNames_AndTheGenericShapesSeenInUse()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestsFolder, "chapter-names.json")));
        var cases = fixture.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("generic").GetBoolean(), StringComparer.Ordinal);

        // The sandbox movies' names as getItem returned them.
        Assert.False(cases["Opening"]);
        Assert.False(cases["Bank job"]);
        Assert.True(cases["Chapter 4"]);
        Assert.True(cases["Capítulo 5"]);
        Assert.True(cases["00:00:40,000"]);

        // Generic names an earlier regex let through on a server in use.
        Assert.True(cases["Chapter 1-2"]);
        Assert.True(cases["Глава 01"]);
        Assert.True(cases["(01)00:00:00:000"]);
        Assert.True(cases["Chapter 1."]);

        // Titles with a digit or a word stay.
        Assert.False(cases["Bank job 2"]);
        Assert.False(cases["Chapter 1 - Opening"]);
        Assert.True(cases.Count >= 40);
    }

    [Fact]
    public async Task TheRule_ClassifiesEveryCase_ThroughTheDialog_InAFakePage()
    {
        var run = await KitScriptChecks.RunTestFileAsync(Path.Combine(TestsFolder, "chapter-names.test.mjs"), Scripts.Value.ClientScriptText, BookmarksKit.Options.Prefix, TestContext.Current.CancellationToken);
        Assert.SkipWhen(run is null, "Node.js is not on the PATH.");

        Assert.True(run!.ExitCode == 0, run.Output);
        Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("# pass 2", run.StandardOutput, StringComparison.Ordinal);
    }

    // The strings of a JavaScript array constant: const NAME = ['a', 'b', ...];
    private static List<string> ListConstant(string script, string name)
    {
        var match = Regex.Match(script, "const " + name + @" = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(match.Success, name + " is not in the ClientScript");
        return QuotedStrings().Matches(match.Groups[1].Value).Select(m => m.Groups[1].Value).ToList();
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex QuotedStrings();
}
