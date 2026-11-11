using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// scripts/Test-WebAnchors.ps1 against synthetic web client bundles in a
/// temporary folder: every declared anchor found, a required one renamed away, only a fallback left, the web version read
/// from the bundle, and Plugin Pages' three bundle anchors moved or gone. It runs in Windows PowerShell
/// 5.1, the shell the scripts target, and only reads the bundle.
/// </summary>
public sealed partial class WebAnchorsScriptTests
{
    // Plugin Pages 2.4.x's anchors as jellyfin-web 10.11.5 has them: once in runtime.bundle.js, and path:"queue"
    // twice (the stable and the experimental routes) and "./home.html" once in main.jellyfin.bundle.js.
    private const string PluginPagesRuntime = "var n={8372:\"home-html\",1234:\"other\"};";
    private const string PluginPagesMain = "var r=[{path:\"queue\",element:q}];var x=[{path:\"queue\",element:q}];var c={\"./home.html\":[5939,8372]};";

    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();

    [Fact]
    public async Task EveryAnchorInTheBundle_Passes_AndTheWebVersionIsRead()
    {
        using var web = Bundle(AllNames());

        var (exitCode, result) = await RunAsync(web.Path);

        Assert.Equal(0, exitCode);
        Assert.True(result.GetProperty("pass").GetBoolean());
        Assert.Equal("10.11.9", result.GetProperty("webVersion").GetString());
        Assert.All(result.GetProperty("anchors").EnumerateArray(), a => Assert.Equal("ok", a.GetProperty("status").GetString()));
        Assert.Contains(result.GetProperty("anchors").EnumerateArray(), a => a.GetProperty("plugin").GetString() == "kit" && a.GetProperty("name").GetString() == "kit.f1Host");
        Assert.Contains(result.GetProperty("anchors").EnumerateArray(), a => a.GetProperty("plugin").GetString() == "Bookmarks" && a.GetProperty("name").GetString() == "detailButtons");
        Assert.Empty(result.GetProperty("warnings").EnumerateArray());
        var pluginPages = result.GetProperty("pluginPages");
        Assert.True(pluginPages.GetProperty("pass").GetBoolean());
        Assert.Equal(
            [("runtime.bundle.js", "8372:\"home-html\"", 1, "ok"), ("main.jellyfin.bundle.js", "path:\"queue\"", 2, "ok"), ("main.jellyfin.bundle.js", "\"./home.html\":[", 1, "ok")],
            pluginPages.GetProperty("anchors").EnumerateArray().Select(a => (a.GetProperty("file").GetString(), a.GetProperty("text").GetString(), a.GetProperty("found").GetInt32(), a.GetProperty("status").GetString())));
    }

    [Theory]
    [InlineData("var n={8373:\"home-html\"};", PluginPagesMain, "Plugin Pages bundle anchor 8372:\"home-html\" in runtime.bundle.js: missing, found 0, expected at least 1")]
    [InlineData(null, PluginPagesMain, "Plugin Pages bundle anchor 8372:\"home-html\" in runtime.bundle.js: missing, found 0, expected at least 1 (no such file)")]
    [InlineData(PluginPagesRuntime, "var r=[{path:\"queue\",element:q}];var c={\"./home.html\":[5939,8372]};", "Plugin Pages bundle anchor path:\"queue\" in main.jellyfin.bundle.js: changed, found 1, expected exactly 2")]
    [InlineData(PluginPagesRuntime, "var r=[{path:\"queue\"},{path:\"queue\"},{path:\"queue\"}];var c={\"./home.html\":[5939,8372]};", "Plugin Pages bundle anchor path:\"queue\" in main.jellyfin.bundle.js: changed, found 3, expected exactly 2")]
    [InlineData(PluginPagesRuntime, "var r=[{path:\"queue\"},{path:\"queue\"}];var c={\"./home.js\":[5939,8372]};", "Plugin Pages bundle anchor \"./home.html\":[ in main.jellyfin.bundle.js: missing, found 0, expected at least 1")]
    public async Task APluginPagesAnchorMovedOrGone_IsAWarning(string? runtime, string main, string expected)
    {
        using var web = Bundle(AllNames(), runtime, main);

        var (exitCode, result) = await RunAsync(web.Path);

        Assert.Equal(0, exitCode);
        Assert.True(result.GetProperty("pass").GetBoolean());
        Assert.Equal([expected], result.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(result.GetProperty("failed").EnumerateArray());
        Assert.False(result.GetProperty("pluginPages").GetProperty("pass").GetBoolean());
    }

    [Fact]
    public async Task APluginPagesAnchorMoved_FailsWithRequirePluginPages_OrStrict()
    {
        // Page hosting defaults to FallbackOnly, so a moved anchor only warns; with PageHostMode Auto or PluginPagesOnly
        // the check before an install or after an update is run with -RequirePluginPages.
        const string Expected = "Plugin Pages bundle anchor path:\"queue\" in main.jellyfin.bundle.js: changed, found 1, expected exactly 2";
        using var web = Bundle(AllNames(), PluginPagesRuntime, "var r=[{path:\"queue\",element:q}];var c={\"./home.html\":[5939,8372]};");

        var (requiredExitCode, required) = await RunAsync(web.Path, "-RequirePluginPages");
        var (strictExitCode, strict) = await RunAsync(web.Path, "-Strict");

        Assert.Equal(1, requiredExitCode);
        Assert.False(required.GetProperty("pass").GetBoolean());
        Assert.True(required.GetProperty("requirePluginPages").GetBoolean());
        Assert.Equal([Expected], required.GetProperty("failed").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(required.GetProperty("warnings").EnumerateArray());
        Assert.Equal(1, strictExitCode);
        Assert.Equal([Expected], strict.GetProperty("failed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ARequiredAnchorRenamedAway_Fails()
    {
        using var web = Bundle(AllNames().Where(n => n != "mainDetailButtons"));

        var (exitCode, result) = await RunAsync(web.Path);

        Assert.Equal(1, exitCode);
        Assert.False(result.GetProperty("pass").GetBoolean());
        Assert.Equal(["Bookmarks anchor detailButtons: missing (required)"], result.GetProperty("failed").EnumerateArray().Select(e => e.GetString()));
        // User Ratings' detailButtons (its Rate button) is optional: a warning only.
        Assert.Equal(["UserRatings anchor detailButtons: missing"], result.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()));
        var anchor = result.GetProperty("anchors").EnumerateArray().Single(a => a.GetProperty("plugin").GetString() == "Bookmarks" && a.GetProperty("name").GetString() == "detailButtons");
        Assert.Empty(anchor.GetProperty("selectors")[0].GetProperty("names").GetProperty("mainDetailButtons").EnumerateArray());
    }

    [Fact]
    public async Task OnlyAFallbackLeft_IsAWarning_AndFailsOnlyWithStrict()
    {
        using var web = Bundle(AllNames().Where(n => n != "btnSubtitles"));

        var (exitCode, result) = await RunAsync(web.Path);
        var (strictExitCode, strict) = await RunAsync(web.Path, "-Strict");

        Assert.Equal(0, exitCode);
        Assert.Equal(["Bookmarks anchor osdInsertBefore: fallback"], result.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, strictExitCode);
        Assert.Equal(["Bookmarks anchor osdInsertBefore: fallback"], strict.GetProperty("failed").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task AnOptionalAnchorMissing_IsAWarning()
    {
        using var web = Bundle(AllNames().Where(n => n != "videoPlayerContainer" && n != "htmlvideoplayer"));

        var (exitCode, result) = await RunAsync(web.Path, "-Plugin", "Bookmarks");

        Assert.Equal(0, exitCode);
        Assert.Equal(["Bookmarks anchor videoElement: missing"], result.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task ANameInsideALongerWord_DoesNotCount()
    {
        // "btnMoreCommands" inside "btnMoreCommandsX" or "x-btnMoreCommands" is another class.
        var names = AllNames().Where(n => n != "btnMoreCommands").Append("btnMoreCommandsX").Append("x-btnMoreCommands");
        using var web = Bundle(names);

        var (_, result) = await RunAsync(web.Path);

        // User Ratings declares detailButtonsBefore too (its Rate button goes before More): both are missing.
        Assert.Equal(["Bookmarks:missing", "UserRatings:missing"], result.GetProperty("anchors").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == "detailButtonsBefore")
            .Select(a => a.GetProperty("plugin").GetString() + ":" + a.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task AFolderWithoutABundle_ExitsWith2()
    {
        using var empty = new TempDirectory();

        var (exitCode, _) = await RunAsync(empty.Path, readResult: false);

        Assert.Equal(2, exitCode);
    }

    // Every class, id and attribute name the kit's and the plugins' anchor lists use.
    private static List<string> AllNames()
    {
        var lists = new[] { Path.Combine(Root, "kit", "web", "anchors.json") }
            .Concat(Directory.EnumerateFiles(Path.Combine(Root, "plugins"), "anchors.json", SearchOption.AllDirectories)
                .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)));
        var names = new List<string>();
        foreach (var file in lists)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var css in json.RootElement.GetProperty("anchors").EnumerateArray().SelectMany(a => a.GetProperty("css").EnumerateArray()).Select(c => c.GetString()!))
            {
                names.AddRange(SelectorName().Matches(css).Select(m => m.Groups["name"].Value).Where(n => !names.Contains(n)));
            }
        }

        return names;
    }

    // A small bundle: the names spread over a script, a style sheet and index.html, as jellyfin-web has them, and Plugin
    // Pages' anchors in runtime.bundle.js (none without one) and main.jellyfin.bundle.js.
    private static TempDirectory Bundle(IEnumerable<string> names, string? pluginPagesRuntime = PluginPagesRuntime, string pluginPagesMain = PluginPagesMain)
    {
        var web = new TempDirectory();
        var list = names.ToList();
        var half = list.Count / 2;
        File.WriteAllText(Path.Combine(web.Path, "index.html"), "<!DOCTYPE html><html><body class=\"" + string.Join(' ', list.Take(3)) + "\"></body></html>");
        File.WriteAllText(
            Path.Combine(web.Path, "main.jellyfin.bundle.js"),
            "console.info(\"[\".concat(\"jellyfin-web\",\"]\\nversion: \").concat(\"10.11.9\",\"\\ncommit: \"));\n"
            + string.Join("\n", list.Take(half).Select(n => "e.querySelector(\"." + n + "\");")));
        Directory.CreateDirectory(Path.Combine(web.Path, "themes"));
        File.WriteAllText(Path.Combine(web.Path, "themes", "theme.css"), string.Join("\n", list.Skip(half).Select(n => "." + n + "{display:block}")));
        File.WriteAllText(Path.Combine(web.Path, "notes.txt"), string.Join(" ", ["itemDetailPage", "never", "read"]));
        File.AppendAllText(Path.Combine(web.Path, "main.jellyfin.bundle.js"), Environment.NewLine + pluginPagesMain);
        if (pluginPagesRuntime is not null)
        {
            File.WriteAllText(Path.Combine(web.Path, "runtime.bundle.js"), pluginPagesRuntime);
        }

        return web;
    }

    private static async Task<(int ExitCode, JsonElement Result)> RunAsync(string webDir, params string[] extra) => await RunAsync(webDir, readResult: true, extra);

    private static async Task<(int ExitCode, JsonElement Result)> RunAsync(string webDir, bool readResult, params string[] extra)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The scripts run on Windows only.");
        var token = TestContext.Current.CancellationToken;
        using var output = new TempDirectory();
        var resultFile = Path.Combine(output.Path, "result.json");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(Root, "scripts", "Test-WebAnchors.ps1"), "-WebDir", webDir, "-Out", resultFile }.Concat(extra))
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await errors;
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Only the powershell.exe this test started.
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (!readResult)
        {
            return (process.ExitCode, default);
        }

        Assert.True(File.Exists(resultFile), $"Test-WebAnchors.ps1 wrote no result (exit code {process.ExitCode}).");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile, token));
        return (process.ExitCode, document.RootElement.Clone());
    }

    [GeneratedRegex(@"[.#](?<name>-?[A-Za-z_][\w-]*)|\[\s*(?<name>[A-Za-z_][\w-]*)")]
    private static partial Regex SelectorName();
}
