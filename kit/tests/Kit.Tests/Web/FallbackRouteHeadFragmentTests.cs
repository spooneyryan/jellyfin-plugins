using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Web;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web;

/// <summary>
/// The generated fallback-route head fragment: the pre-hide rule, the <c>&lt;pfx&gt;-route</c> class and the
/// capture-phase title, as static anonymous content.
/// </summary>
public sealed class FallbackRouteHeadFragmentTests
{
    private static readonly string Fragment = FallbackRouteHeadFragment.Build("jfkt", ("clips", "My Clips"), ("bookmarks", "My Bookmarks"));

    [Fact]
    public void TheStyle_HidesEverythingButOurPageRoot_WhileOurRouteClassIsSet()
    {
        Assert.StartsWith(
            "<style data-jfkt-head>html.jfkt-route #fallbackPage > :not([data-jfkt-page]) { display:none !important }</style><script data-jfkt-head>",
            Fragment,
            StringComparison.Ordinal);
        Assert.EndsWith("</script>", Fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void TheScript_CarriesTheTitleMap_AndNeverTouchesApiClient()
    {
        var script = Script(Fragment);

        Assert.Contains("var t = {\"bookmarks\":\"My Bookmarks\",\"clips\":\"My Clips\"};", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener('viewbeforeshow'", script, StringComparison.Ordinal);
        Assert.Contains("}, true);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiClient", script, StringComparison.Ordinal);
        Assert.DoesNotContain("`", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Titles_CannotEndTheScriptElement()
    {
        var fragment = FallbackRouteHeadFragment.Build("jfkt", ("clips", "</script><script>alert(1)</script> & 'quotes' \"too\" Größe"));

        Assert.Equal(1, IndexHtmlInjectionStartupFilterTests.Count(fragment, "</script>"));
        Assert.Equal(1, IndexHtmlInjectionStartupFilterTests.Count(fragment, "<script"));
        Assert.DoesNotContain("Größe", fragment, StringComparison.Ordinal);
        Assert.True(fragment.All(c => c < 128), "The fragment should be ASCII.");
    }

    [Fact]
    public void BothElements_CarryTheHeadMarker_SoTheMiddlewareAcceptsIt()
    {
        var bytes = Encoding.UTF8.GetBytes("<html><head></head><body></body></html>");

        var result = IndexHtmlInjector.Insert(bytes, "data-jfkt-plugin", "<script data-jfkt-plugin></script>", "data-jfkt-head", Fragment, out _);

        Assert.Equal(IndexHtmlHeadOutcome.Inserted, result.Head);
        Assert.Equal(2, IndexHtmlInjectionStartupFilterTests.Count(Fragment, "data-jfkt-head"));
    }

    [Fact]
    public void TheOutput_DoesNotDependOnTheOrderOfTheRoutes()
    {
        Assert.Equal(Fragment, FallbackRouteHeadFragment.Build("jfkt", ("bookmarks", "My Bookmarks"), ("clips", "My Clips")));
    }

    [Theory]
    [InlineData("jfKT", "clips", "My Clips")]
    [InlineData("kt", "clips", "My Clips")]
    [InlineData("jfkt'", "clips", "My Clips")]
    [InlineData("jfkt", "Clips", "My Clips")]
    [InlineData("jfkt", "clips.html", "My Clips")]
    [InlineData("jfkt", "-clips", "My Clips")]
    [InlineData("jfkt", "clips", " ")]
    public void InvalidInput_Throws(string prefix, string key, string title)
    {
        Assert.Throws<ArgumentException>(() => FallbackRouteHeadFragment.Build(prefix, (key, title)));
    }

    [Fact]
    public void NoRoutesOrADuplicate_Throws()
    {
        Assert.Throws<ArgumentException>(() => FallbackRouteHeadFragment.Build("jfkt", Array.Empty<(string, string)>()));
        Assert.Throws<ArgumentException>(() => FallbackRouteHeadFragment.Build("jfkt", ("clips", "A"), ("clips", "B")));
    }

    [Fact]
    public async Task InABrowserLikeContext_OurRoutesGetTheClassAndTheTitle_OthersNothing()
    {
        var node = FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        string[] hashes = ["#/jfkt-clips", "#/jfkt-bookmarks?tab=running&n=2", "#!/jfkt-clips", "#/jfkt-unknown", "#/jfkt-clips.html", "#/home", "#/jfktx-clips", "#/jfkt-constructor", string.Empty];
        var results = await RunInNode(node!, Script(Fragment), hashes, TestContext.Current.CancellationToken);

        var byHash = results.ToDictionary(r => r.GetProperty("hash").GetString()!);
        Assert.Equal(hashes.Length, byHash.Count);
        AssertOurs(byHash["#/jfkt-clips"], "My Clips");
        AssertOurs(byHash["#/jfkt-bookmarks?tab=running&n=2"], "My Bookmarks");
        AssertOurs(byHash["#!/jfkt-clips"], "My Clips");
        foreach (var other in new[] { "#/jfkt-unknown", "#/jfkt-clips.html", "#/home", "#/jfktx-clips", "#/jfkt-constructor", string.Empty })
        {
            var result = byHash[other];
            Assert.Equal(0, result.GetProperty("classes").GetArrayLength());
            Assert.Equal("Page not found", result.GetProperty("fallbackTitle").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("fallbackOriginal").ValueKind);
            Assert.Equal("Other plugin", result.GetProperty("claimedTitle").GetString());
        }

        Assert.All(results, r =>
        {
            // One capture-phase listener; other pages never get an attribute from us; a page another kit already claimed
            // keeps the original title that kit recorded; ApiClient is never read.
            Assert.Equal("[true]", r.GetProperty("captures").GetRawText());
            Assert.Equal(0, r.GetProperty("otherWrites").GetInt32());
            Assert.Equal("Earlier", r.GetProperty("claimedOriginal").GetString());
            Assert.False(r.GetProperty("apiClientRead").GetBoolean());
        });
    }

    [Fact]
    public void TheScript_KeepsTheOriginalTitle_InTheKitsSharedAttribute()
    {
        Assert.Equal("data-jfkit-orig-title", FallbackRouteHeadFragment.OriginalTitleAttribute);
        Assert.Contains("if (!p.hasAttribute('data-jfkit-orig-title')) { p.setAttribute('data-jfkit-orig-title', p.getAttribute('data-title') || ''); }p.setAttribute('data-title', t[x]);", Script(Fragment), StringComparison.Ordinal);
    }

    [Fact]
    public void TheJavaScriptTestsFixture_IsTheBuiltFragment()
    {
        // kit/web/tests/pages.test.mjs runs this fragment's real script before the kit, as index.html does.
        var expected = FallbackRouteHeadFragment.Build("jfkt", ("alpha", "Alpha"), ("beta", "Beta")) + "\n";
        var file = Path.Combine(TestPaths.KitRoot, "web", "tests", "head-fragment-jfkt.html");

        var actual = File.Exists(file) ? File.ReadAllText(file).ReplaceLineEndings("\n") : null;

        Assert.True(actual == expected, $"{file} is out of date; replace its content with:\n{expected}");
    }

    private static void AssertOurs(JsonElement result, string title)
    {
        // The page's own title is kept for the hand-back before ours replaces it, and a second show (a kit's page
        // events on a swap) doesn't overwrite it with ours.
        Assert.Equal("[\"jfkt-route\"]", result.GetProperty("classes").GetRawText());
        Assert.Equal(title, result.GetProperty("fallbackTitle").GetString());
        Assert.Equal("Page not found", result.GetProperty("fallbackOriginal").GetString());
        Assert.Equal(title, result.GetProperty("claimedTitle").GetString());
    }

    private static string Script(string fragment)
    {
        const string Open = "<script data-jfkt-head>";
        var start = fragment.IndexOf(Open, StringComparison.Ordinal) + Open.Length;
        return fragment[start..fragment.LastIndexOf("</script>", StringComparison.Ordinal)];
    }

    private static string? FindNode()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe" } : new[] { "node" };
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(name => Path.Combine(dir.Trim('"'), name)))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<List<JsonElement>> RunInNode(string node, string script, string[] hashes, CancellationToken cancellationToken)
    {
        // Runs the script once per hash in a fresh context with just the globals it may use: location and a document
        // whose <html> class list and listeners are recorded, then fires viewbeforeshow at #fallbackPage and other pages.
        const string Runner = """
            const vm = require('vm');
            const fs = require('fs');
            const script = fs.readFileSync(process.argv[2], 'utf8');
            const hashes = JSON.parse(fs.readFileSync(process.argv[3], 'utf8'));
            const results = hashes.map((hash) => {
              const classes = [];
              const listeners = [];
              let apiClientRead = false;
              const context = {
                location: { hash },
                document: {
                  documentElement: { classList: { add: (c) => classes.push(c) } },
                  addEventListener: (type, fn, capture) => listeners.push({ type, fn, capture })
                }
              };
              Object.defineProperty(context, 'ApiClient', { get() { apiClientRead = true; return undefined; } });
              vm.createContext(context);
              vm.runInContext(script, context);
              const page = (id, attributes) => {
                const values = Object.assign({}, attributes);
                const node = { id, writes: 0 };
                node.hasAttribute = (name) => Object.prototype.hasOwnProperty.call(values, name);
                node.getAttribute = (name) => (node.hasAttribute(name) ? values[name] : null);
                node.setAttribute = (name, value) => { node.writes++; values[name] = String(value); };
                return node;
              };
              // FallbackRoute's page as React mounts it (shown twice: the mount, then a kit's page events on a swap), the
              // same page already claimed by another plugin's kit, and another view.
              const fresh = page('fallbackPage', { 'data-title': 'Page not found' });
              const claimed = page('fallbackPage', { 'data-title': 'Other plugin', 'data-jfkit-orig-title': 'Earlier' });
              const other = page('indexPage', { 'data-title': 'Home' });
              for (const l of listeners.filter((x) => x.type === 'viewbeforeshow')) {
                l.fn({ target: fresh }); l.fn({ target: fresh }); l.fn({ target: claimed }); l.fn({ target: other });
              }
              return {
                hash,
                classes,
                captures: listeners.map((l) => l.capture === true),
                fallbackTitle: fresh.getAttribute('data-title'),
                fallbackOriginal: fresh.getAttribute('data-jfkit-orig-title'),
                claimedTitle: claimed.getAttribute('data-title'),
                claimedOriginal: claimed.getAttribute('data-jfkit-orig-title'),
                otherWrites: other.writes,
                apiClientRead
              };
            });
            process.stdout.write(JSON.stringify(results));
            """;

        using var folder = new TempDirectory();
        var runnerFile = folder.CreateFile("runner.js", Runner);
        var scriptFile = folder.CreateFile("fragment.js", script);
        var hashesFile = folder.CreateFile("hashes.json", JsonSerializer.Serialize(hashes));
        var start = new ProcessStartInfo(node)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(runnerFile);
        start.ArgumentList.Add(scriptFile);
        start.ArgumentList.Add(hashesFile);

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, "node failed: " + await errors);
            return JsonDocument.Parse(output).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
        }
        catch (OperationCanceledException)
        {
            // Only the node process this test started.
            process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
