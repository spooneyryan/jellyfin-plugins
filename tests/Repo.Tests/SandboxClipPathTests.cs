using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// A sandbox never starts while Bookmarks' data names a clip path outside it. Bookmarks writes clips
/// into, and deletes files from, the folders its data names, and it deletes stale <c>.partial</c> files in every
/// recorded clip folder at startup, unattended, so a store copied from elsewhere (a real server's 1.0 file used as a
/// <c>--fixture</c>) would have the sandbox clean up the real server's clip folders. <c>Test-SandboxClipPaths</c> in
/// scripts/JellyfinSandbox.psm1 decides; <c>dev-server.ps1</c> refuses before it writes anything. Everything runs in
/// Windows PowerShell 5.1 against temporary folders; nothing is created under C:\ProgramData\Jellyfin and no server starts.
/// </summary>
public sealed class SandboxClipPathTests
{
    private const string RealClips = @"C:\ProgramData\Jellyfin\Server\data\bookmarks\clips";

    // The default sandbox that the clip paths of the three fixtures the sandbox runs name: that of a checkout at
    // C:\src\jellyfin-plugins, the synthetic prefix their paths have (the Fixtures README). They are installed
    // in a default sandbox (scripts/README.md, "The 1.0.0 store").
    private const string MainCheckoutSandbox = @"C:\src\jellyfin-plugins\.sandbox\default";

    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();

    private static readonly string Fixtures = Path.Combine(Root, "plugins", "Bookmarks", "tests", "Jellyfin.Plugin.Bookmarks.Tests", "Fixtures");

    [Fact]
    public async Task ClipPaths_InTheSandbox_Pass_AndEveryOtherPlace_IsRefused_InEveryStoreFile_AndTheSettings()
    {
        using var temp = new TempDirectory();
        var elsewhere = Path.Combine(temp.Path, "elsewhere", "clips");
        var media = Path.Combine(temp.Path, "media", "Movie (2020)", "Movie (2020).mkv");
        var cases = new Dictionary<string, Action<Sandbox>>(StringComparer.Ordinal)
        {
            // A new sandbox has no Bookmarks data at all.
            ["none"] = _ => { },

            // The sandbox's own data: every clip path in its clip folder, in the store, its .bak, a .v1-* copy and a .tmp
            // a kill cut short; a media file outside it (not a clip path); ClipOutputPath relative, so in the sandbox
            // folder, the server's working directory.
            ["own"] = s =>
            {
                var store = Schema2(s.Clips, [Path.Combine(s.Clips, "admin", "Movie (2020) [00.00.01-00.00.02] (2).mkv")], Path.Combine(s.Clips, "admin", "Movie (2020) [00.00.01-00.00.02].mkv"), media);
                s.Store("bookmarks.json", store);
                s.Store("bookmarks.json.bak", store);
                s.Store("bookmarks.json.v1-20260930000000", Schema1(Path.Combine(s.Clips, "admin", "Movie (2020) [00.00.01-00.00.02].mkv")));
                s.Store("bookmarks.json.v1-20260930000001.tmp", store[..(store.Length / 2)]);
                s.Settings("clips-out");
            },

            // A real server's 1.0 file installed as a --fixture: the upgrade would record its folder in ClipRoots.
            ["real-1.0"] = s => s.Store("bookmarks.json", Schema1(Path.Combine(RealClips, "admin", "Movie (2020) [00.00.01-00.00.02].mkv"))),

            // The store is the sandbox's, but its .bak (8.3 spelling) and a .v1-* copy are not: both could become the store.
            ["copies"] = s =>
            {
                s.Store("bookmarks.json", Schema2(s.Clips, [], null, null));
                s.Store("bookmarks.json.bak", Schema2(@"C:\PROGRA~3\Jellyfin\Server\data\bookmarks\clips", [], null, null));
                s.Store("bookmarks.json.v1-20260930000000", Schema1(Path.Combine(elsewhere, "admin", "Movie (2020) [00.00.01-00.00.02].mkv")));
            },

            // A pending delete through a share, and a clip folder spelled as a device path.
            ["pending"] = s => s.Store("bookmarks.json", Schema2(@"\\?\" + RealClips, [@"\\localhost\C$\ProgramData\Jellyfin\Server\data\bookmarks\clips\admin\x [00.00.01-00.00.02].mkv"], null, null)),

            ["settings-relative"] = s => s.Settings(@"..\..\elsewhere\clips"),
            ["settings-real"] = s => s.Settings(RealClips),

            // Files ConvertFrom-Json refuses: cut short, and keys that differ only in case (Bookmarks' reader takes
            // FilePath and ignores filePath). Only drive paths under C:\ProgramData\Jellyfin count there.
            ["unreadable"] = s =>
            {
                var whole = Schema2(RealClips, [], Path.Combine(elsewhere, "a [00.00.01-00.00.02].mkv"), media);
                s.Store("bookmarks.json.v1-20260930000000.tmp", whole[..^3]);
                s.Store("bookmarks.json", "{\"Version\":2,\"ClipRoots\":[],\"Bookmarks\":[{\"Clip\":{\"FilePath\":" + JsonSerializer.Serialize(Path.Combine(s.Clips, "a.mkv"))
                    + ",\"filePath\":" + JsonSerializer.Serialize(Path.Combine(RealClips, "b.mkv")) + "}}]}");
            },
        };

        var script = new List<string>();
        var sandboxes = new Dictionary<string, Sandbox>(StringComparer.Ordinal);
        foreach (var (name, arrange) in cases)
        {
            var sandbox = new Sandbox(Path.Combine(temp.Path, "repo", ".sandbox", name));
            arrange(sandbox);
            sandboxes[name] = sandbox;
            script.Add($"'case:{name}'");
            script.Add($"foreach ($p in @(Test-SandboxClipPaths -Root '{sandbox.Root}' -DataDir '{sandbox.Data}')) {{ $p }}");
        }

        var run = await WindowsPowerShell.RunWithModuleAsync(string.Join('\n', script) + "\n");
        Assert.True(run.ExitCode == 0, run.Output);
        var results = Group(run.Lines);

        Assert.Empty(results["none"]);
        Assert.Empty(results["own"]);
        Assert.Equal([$@"bookmarks.json: Clip.FilePath {RealClips}\admin\Movie (2020) [00.00.01-00.00.02].mkv is under C:\ProgramData\Jellyfin, the real server's folder"], results["real-1.0"]);
        Assert.Equal(
            [
                @"bookmarks.json.bak: ClipRoots C:\PROGRA~3\Jellyfin\Server\data\bookmarks\clips is under C:\ProgramData\Jellyfin, the real server's folder",
                $@"bookmarks.json.v1-20260930000000: Clip.FilePath {elsewhere}\admin\Movie (2020) [00.00.01-00.00.02].mkv is outside the sandbox",
            ],
            results["copies"]);
        Assert.Equal(
            [
                $@"bookmarks.json: ClipRoots \\?\{RealClips} is under C:\ProgramData\Jellyfin, the real server's folder",
                @"bookmarks.json: PendingFileDeletes.Path \\localhost\C$\ProgramData\Jellyfin\Server\data\bookmarks\clips\admin\x [00.00.01-00.00.02].mkv is not on a local drive",
            ],
            results["pending"]);
        Assert.Equal([@"Jellyfin.Plugin.Bookmarks.xml: ClipOutputPath ..\..\elsewhere\clips is outside the sandbox"], results["settings-relative"]);
        Assert.Equal([$@"Jellyfin.Plugin.Bookmarks.xml: ClipOutputPath {RealClips} is under C:\ProgramData\Jellyfin, the real server's folder"], results["settings-real"]);

        // The media path and the path outside the sandbox in the cut-short file don't count there; the real server's do.
        Assert.Equal(
            [
                $@"bookmarks.json (not JSON this check reads): a string {RealClips}\b.mkv is under C:\ProgramData\Jellyfin, the real server's folder",
                $@"bookmarks.json.v1-20260930000000.tmp (not JSON this check reads): a string {RealClips} is under C:\ProgramData\Jellyfin, the real server's folder",
            ],
            results["unreadable"]);
    }

    [Fact]
    public async Task CommittedFixtures_TheSandboxRunsUse_Pass_AndTheUnitTestOnesAreRefused()
    {
        // scripts/README.md ("The 1.0.0 store"): the three fixtures installed in the default sandbox point into it; the
        // 1.0 development build's file points into another repository's sandbox (C:\src\jellyfin-plugin-bookmarks) and
        // the schema 2 one into the real server's folder and D:\Clips, so dev-server.ps1 refuses them.
        // The three name the default sandbox of a checkout at C:\src\jellyfin-plugins (MainCheckoutSandbox). Run from
        // any other checkout, the test maps that folder to this checkout's default sandbox, where they would be installed
        // from here, so it checks the same rule in every checkout; in a checkout at C:\src\jellyfin-plugins the files are
        // used byte for byte, as they are committed.
        using var temp = new TempDirectory();
        var sandbox = Path.Combine(Root, ".sandbox", "default");
        var mainCheckout = string.Equals(Path.GetFullPath(sandbox), MainCheckoutSandbox, StringComparison.OrdinalIgnoreCase);
        string[] names = ["bookmarks-1.0.0.json", "bookmarks-1.0-duplicate-key.json", "bookmarks-schema3.json", "bookmarks-1.0-dev-sandbox.json", "bookmarks-schema2.json"];
        string[] sandboxRuns = ["bookmarks-1.0.0.json", "bookmarks-1.0-duplicate-key.json", "bookmarks-schema3.json"];
        var from = JsonEncodedText.Encode(MainCheckoutSandbox + @"\").ToString();
        var to = JsonEncodedText.Encode(Path.GetFullPath(sandbox) + @"\").ToString();
        var token = TestContext.Current.CancellationToken;
        var script = new List<string>();
        foreach (var name in names)
        {
            var data = Path.Combine(temp.Path, name);
            Directory.CreateDirectory(Path.Combine(data, "data", "bookmarks"));
            var source = Path.Combine(Fixtures, name);
            var target = Path.Combine(data, "data", "bookmarks", "bookmarks.json");
            if (!sandboxRuns.Contains(name) || mainCheckout)
            {
                File.Copy(source, target);
            }
            else
            {
                var text = await File.ReadAllTextAsync(source, token);
                await File.WriteAllTextAsync(target, text.Replace(from, to, StringComparison.Ordinal), token);
            }

            if (sandboxRuns.Contains(name))
            {
                // Each names C:\src\jellyfin-plugins' default sandbox, so the mapping above (or, in a checkout there, the
                // files as they are) is tested.
                Assert.Contains(from, await File.ReadAllTextAsync(source, token), StringComparison.Ordinal);
            }

            script.Add($"'case:{name}'");
            script.Add($"foreach ($p in @(Test-SandboxClipPaths -Root '{sandbox}' -DataDir '{data}')) {{ $p }}");
        }

        var run = await WindowsPowerShell.RunWithModuleAsync(string.Join('\n', script) + "\n");
        Assert.True(run.ExitCode == 0, run.Output);
        var results = Group(run.Lines);

        Assert.Empty(results["bookmarks-1.0.0.json"]);
        Assert.Empty(results["bookmarks-1.0-duplicate-key.json"]);
        Assert.Empty(results["bookmarks-schema3.json"]);
        Assert.Equal(4, results["bookmarks-1.0-dev-sandbox.json"].Count);
        Assert.All(results["bookmarks-1.0-dev-sandbox.json"], l => Assert.Matches(@"^bookmarks\.json: Clip\.FilePath C:\\src\\jellyfin-plugin-bookmarks\\\.sandbox\\jellyfin\\data\\bookmarks\\clips\\admin\\.* is outside the sandbox$", l));
        Assert.Equal(
            [
                $"bookmarks.json: ClipRoots {RealClips} is under C:\\ProgramData\\Jellyfin, the real server's folder",
                "bookmarks.json: ClipRoots D:\\Clips is outside the sandbox",
                $"bookmarks.json: PendingFileDeletes.Path {RealClips}\\alice\\Heat (1995) - Bank job [01.02.03-01.05.00] (2).mp4 is under C:\\ProgramData\\Jellyfin, the real server's folder",
            ],
            results["bookmarks-schema2.json"].Take(3));
        Assert.Equal(6, results["bookmarks-schema2.json"].Count);
    }

    [Fact]
    public async Task DevServer_RefusesBeforeWritingAnything_AndStartsWhenEveryClipPathIsInTheSandbox()
    {
        // The real dev-server.ps1 and module in a copy of the repository layout under a temporary folder, with a synthetic
        // Jellyfin folder whose jellyfin.exe is an empty file: the refusal comes before anything is written; with the
        // sandbox's own paths the script goes on to start the server, which fails on the empty file (nothing runs).
        using var temp = new TempDirectory();
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        Directory.CreateDirectory(Path.Combine(repo, "plugins"));
        foreach (var file in new[] { "dev-server.ps1", "JellyfinSandbox.psm1", "Deploy-Plugin.ps1" })
        {
            File.Copy(Path.Combine(Root, "scripts", file), Path.Combine(repo, "scripts", file));
        }

        var server = Path.Combine(temp.Path, "server");
        Directory.CreateDirectory(Path.Combine(server, "jellyfin-web"));
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(Path.Combine(server, "jellyfin.exe"), string.Empty, token);
        await File.WriteAllTextAsync(Path.Combine(server, "ffmpeg.exe"), string.Empty, token);
        await File.WriteAllTextAsync(Path.Combine(server, "jellyfin-web", "index.html"), "<html></html>", token);

        var sandbox = new Sandbox(Path.Combine(repo, ".sandbox", "cliptest"));
        var devServer = Path.Combine(repo, "scripts", "dev-server.ps1");
        var start = $"try {{ & '{devServer}' -Name cliptest -Port {FreePort()} -Plugins None -Community None -NoBuild -Background -JellyfinDir '{server}' 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }}; [Console]::Out.WriteLine('started') }}\n"
            + "catch { [Console]::Out.WriteLine('stopped: ' + ($_.Exception.Message -replace \"`r?`n\", ' | ')) }\n";

        // A real server's clip folder in the store's .bak, and a clip file in another folder in the store.
        sandbox.Store("bookmarks.json", Schema1(Path.Combine(temp.Path, "elsewhere", "Movie (2020) [00.00.01-00.00.02].mkv")));
        sandbox.Store("bookmarks.json.bak", Schema2(RealClips, [], null, null));
        var refused = await WindowsPowerShell.RunWithModuleAsync(start);
        Assert.True(refused.ExitCode == 0, refused.Output);
        var stopped = Assert.Single(refused.Lines, l => l.StartsWith("stopped: ", StringComparison.Ordinal));
        Assert.StartsWith("stopped: Refusing to start sandbox 'cliptest': Bookmarks' data names clip paths outside it", stopped, StringComparison.Ordinal);
        Assert.Contains($"bookmarks.json: Clip.FilePath {temp.Path}\\elsewhere\\Movie (2020) [00.00.01-00.00.02].mkv is outside the sandbox", stopped, StringComparison.Ordinal);
        Assert.Contains($"bookmarks.json.bak: ClipRoots {RealClips} is under C:\\ProgramData\\Jellyfin, the real server's folder", stopped, StringComparison.Ordinal);
        Assert.Contains("scripts/README.md, 'The 1.0.0 store'", stopped, StringComparison.Ordinal);
        Assert.DoesNotContain("started", refused.Lines);
        Assert.False(Directory.Exists(Path.Combine(sandbox.Data, "config")), "Nothing was written: no network.xml, no trigger files.");
        Assert.False(Directory.Exists(Path.Combine(sandbox.Root, "web")), "Nothing was written: no web client copy.");

        // The sandbox's own paths: past the check, network.xml written, then the start of the empty jellyfin.exe fails.
        sandbox.Store("bookmarks.json", Schema1(Path.Combine(sandbox.Clips, "admin", "Movie (2020) [00.00.01-00.00.02].mkv")));
        sandbox.Store("bookmarks.json.bak", Schema2(sandbox.Clips, [], null, null));
        var passed = await WindowsPowerShell.RunWithModuleAsync(start);
        Assert.True(passed.ExitCode == 0, passed.Output);
        Assert.DoesNotContain("Refusing", passed.Output, StringComparison.Ordinal);
        Assert.Contains("host: Starting sandbox 'cliptest'", passed.Lines.Select(l => l.Split(" on ", StringSplitOptions.None)[0]));
        Assert.True(File.Exists(Path.Combine(sandbox.Data, "config", "network.xml")));
        Assert.Single(passed.Lines, l => l.StartsWith("stopped: ", StringComparison.Ordinal));
    }

    [Fact]
    public void DevServer_ChecksTheClipPaths_AfterFresh_AndBeforeItWritesOrStartsAnything()
    {
        var devServer = File.ReadAllText(Path.Combine(Root, "scripts", "dev-server.ps1"));
        var check = devServer.IndexOf("@(Test-SandboxClipPaths -Root $resolved['sandbox folder'] -DataDir $sandbox.DataDir)", StringComparison.Ordinal);
        Assert.True(check > 0, "dev-server.ps1 runs Test-SandboxClipPaths.");
        Assert.True(check > devServer.IndexOf("if ($Fresh) {", StringComparison.Ordinal), "-Fresh removes the data first.");
        foreach (var later in new[] { "New-Item -ItemType Directory -Force -Path $folder", "Copy-Item -LiteralPath $sourceWeb", "network.xml", "Deploy-Plugin.ps1') -Plugin", "Start-Process -FilePath $jellyfinExe" })
        {
            Assert.True(devServer.IndexOf(later, check, StringComparison.Ordinal) > check, $"'{later}' comes after the clip-path check.");
            Assert.True(devServer.LastIndexOf(later, check, StringComparison.Ordinal) < 0 || later == "network.xml", $"'{later}' doesn't come before the clip-path check.");
        }
    }

    private static Dictionary<string, List<string>> Group(string[] lines)
    {
        var results = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var line in lines)
        {
            if (line.StartsWith("case:", StringComparison.Ordinal))
            {
                current = results[line[5..]] = [];
            }
            else
            {
                current!.Add(line);
            }
        }

        return results;
    }

    // A Bookmarks 1.0 store (schema 1) with one bookmark whose clip file is at the path.
    private static string Schema1(string clipFile) => JsonSerializer.Serialize(new
    {
        Version = 1,
        Bookmarks = new object[]
        {
            new { Id = "5d1c0b6e-4b8a-4c7e-9a51-1d2b3c4d5e6f", ItemName = "Movie (2020)", Clip = new { Status = "Completed", Mode = "Fast", FilePath = clipFile } },
        },
    });

    // A schema 2 store: one clip folder, pending deletes, and a bookmark with a clip file and a media path when given.
    private static string Schema2(string clipRoot, string[] pending, string? clipFile, string? mediaPath) => JsonSerializer.Serialize(new
    {
        Version = 2,
        ClipRoots = new[] { clipRoot },
        PendingFileDeletes = pending.Select(p => new { Path = p, SinceUtc = "2026-09-30T00:00:00Z", Attempts = 1 }).ToArray(),
        Bookmarks = clipFile is null
            ? Array.Empty<object>()
            : new object[]
            {
                new
                {
                    Id = "5d1c0b6e-4b8a-4c7e-9a51-1d2b3c4d5e6f",
                    Identity = new { Kind = "Movie", Name = "Movie (2020)", Path = mediaPath },
                    Clip = new { Status = "Completed", Mode = "Fast", FilePath = clipFile },
                },
            },
    });

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    // A sandbox folder as dev-server.ps1 lays it out: data\data\bookmarks for the store, data\plugins\configurations for
    // the settings.
    private sealed class Sandbox(string root)
    {
        public string Root { get; } = root;

        public string Data => Path.Combine(Root, "data");

        public string Clips => Path.Combine(Data, "data", "bookmarks", "clips");

        public void Store(string name, string text)
        {
            var folder = Path.Combine(Data, "data", "bookmarks");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name), text);
        }

        public void Settings(string clipOutputPath)
        {
            var folder = Path.Combine(Data, "plugins", "configurations");
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, "Jellyfin.Plugin.Bookmarks.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n"
                + $"  <EnableClips>true</EnableClips>\n  <ClipOutputPath>{System.Security.SecurityElement.Escape(clipOutputPath)}</ClipOutputPath>\n</PluginConfiguration>\n");
        }
    }
}
