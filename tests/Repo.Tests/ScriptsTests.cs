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
using Jellyfin.Plugin.Kit.TestSupport.Web;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// Static checks of the repository scripts. The scripts themselves are exercised against real
/// sandboxes; these tests only catch mistakes that would break them before they run.
/// </summary>
public sealed class ScriptsTests
{
    private static readonly string Scripts = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "scripts");

    // Two probes that need a separate demo plugin or seed script, which this repository doesn't include, can be kept in the
    // optional local folder private/scripts (gitignored, not part of this repository); their tests run only where they are.
    private static readonly string LocalScripts = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "private", "scripts");

    private static readonly string[] WebcheckActions =
        ["login", "goto", "waitFor", "eval", "script", "assert", "click", "type", "key", "screenshot", "sleep", "reload", "noExceptions", "initScript", "emulate", "delayRequests", "cdp", "context"];

    [Fact]
    public void PowerShellScripts_AreAscii()
    {
        // Windows PowerShell 5.1 reads BOM-less scripts in the ANSI code page, so non-ASCII text breaks them.
        var offenders = Directory.EnumerateFiles(Scripts, "*.ps*1", SearchOption.AllDirectories)
            .Where(path => File.ReadAllBytes(path).Any(b => b > 0x7F))
            .Select(path => Path.GetRelativePath(Scripts, path))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void PowerShellScripts_ProtectTheRealServer()
    {
        // The guard every script relies on: C:\ProgramData\Jellyfin and port 8096 are refused.
        var module = File.ReadAllText(Path.Combine(Scripts, "JellyfinSandbox.psm1"));
        Assert.Contains(@"$roots.Add('C:\ProgramData\Jellyfin')", module, StringComparison.Ordinal);
        Assert.Contains("$script:ProductionPort = 8096", module, StringComparison.Ordinal);

        foreach (var script in new[] { "dev-server.ps1", "Deploy-Plugin.ps1", "New-SandboxMedia.ps1", "Initialize-Sandbox.ps1", "Stop-Sandbox.ps1", "Set-SandboxPluginConfig.ps1", "Test-WebAnchors.ps1" })
        {
            Assert.Contains("JellyfinSandbox.psm1", File.ReadAllText(Path.Combine(Scripts, script)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PathGuard_RefusesEverySpellingOfTheRealServerFolder()
    {
        // Runs Test-ProtectedPath from JellyfinSandbox.psm1 in Windows PowerShell 5.1, the shell the scripts target.
        // It only reads attributes of these paths; nothing is created under C:\ProgramData\Jellyfin.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The scripts run on Windows only.");

        var sandboxData = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), ".sandbox", "guard-test", "data");
        (string Path, bool Protected)[] cases =
        [
            (@"C:\ProgramData\Jellyfin", true),
            (@"C:\ProgramData\Jellyfin\Server\data", true),
            (@"c:/programdata/JELLYFIN/server/log", true),
            (@"C:\Users\..\ProgramData\Jellyfin\x", true),
            (@"C:\ProgramData\Jellyfin.\Server", true),
            (@"\\?\C:\ProgramData\Jellyfin\Server", true),
            (@"\\.\C:\ProgramData\Jellyfin\Server", true),
            (@"\\localhost\C$\ProgramData\Jellyfin\Server", true),
            (@"\\?\UNC\localhost\C$\ProgramData\Jellyfin\Server", true),
            (@"C:\ProgramData\JellyfinOther\data", false),
            (sandboxData, false),
            (@"\\?\" + sandboxData, false),
        ];

        var token = TestContext.Current.CancellationToken;
        var work = Directory.CreateTempSubdirectory("jf-guard-");
        try
        {
            var casesFile = Path.Combine(work.FullName, "cases.txt");
            var scriptFile = Path.Combine(work.FullName, "guard.ps1");
            await File.WriteAllLinesAsync(casesFile, cases.Select(c => c.Path), token);
            await File.WriteAllTextAsync(
                scriptFile,
                "$ErrorActionPreference = 'Stop'\n"
                + $"Import-Module '{Path.Combine(Scripts, "JellyfinSandbox.psm1")}' -Force\n"
                + $"foreach ($line in [System.IO.File]::ReadAllLines('{casesFile}')) {{ [string](Test-ProtectedPath $line) }}\n",
                token);

            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptFile })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            string output, errors;
            try
            {
                var errorsTask = process.StandardError.ReadToEndAsync(timeout.Token);
                output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                errors = await errorsTask;
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Only the powershell.exe this test started.
                process.Kill(entireProcessTree: true);
                throw;
            }

            Assert.True(process.ExitCode == 0, $"Windows PowerShell failed: {errors}");

            var results = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(cases.Length, results.Length);
            var wrong = cases.Zip(results)
                .Where(pair => !string.Equals(pair.First.Protected.ToString(), pair.Second, StringComparison.OrdinalIgnoreCase))
                .Select(pair => $"{pair.First.Path}: expected protected={pair.First.Protected}, got {pair.Second}")
                .ToList();
            Assert.Empty(wrong);
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SandboxServerEnvironment_GivesTheChildADeadProxy_AndLeavesThisProcessAsItWas()
    {
        // A sandbox's jellyfin.exe gets HTTP_PROXY and HTTPS_PROXY at a dead loopback port and NO_PROXY for
        // loopback, so an outbound request is refused instead of downloading, while the sandbox's own URL goes direct. The
        // variables are set in the PowerShell process only while the child starts, and put back afterwards, also when the
        // start fails. A child started the way dev-server.ps1 starts jellyfin.exe reads every spelling, and none of the
        // parent's JELLYFIN_* or proxy values.
        using var work = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var child = Path.Combine(work.Path, "child.ps1");
        var childOutput = Path.Combine(work.Path, "child.txt");
        var temp = Path.Combine(work.Path, "temp");
        string[] names = ["HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "NO_PROXY", "no_proxy", "TMP", "TEMP", "JELLYFIN_DATA_DIR"];
        await File.WriteAllTextAsync(
            child,
            "foreach ($n in " + string.Join(", ", names.Select(PowerShellQuote)) + ") { $n + '=' + [Environment]::GetEnvironmentVariable($n) }\n",
            token);

        const string Snapshot = "((Get-ChildItem Env: | Sort-Object Name | ForEach-Object { $_.Name + '=' + $_.Value }) -join \"`n\")";
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "$env:JELLYFIN_DATA_DIR = 'X:\\parent-data'\n"
            + "$env:http_proxy = 'http://parent.example:3128'\n"
            + "$env:NO_PROXY = 'parent.example'\n"
            + "Remove-Item Env:HTTPS_PROXY -ErrorAction SilentlyContinue\n"
            + "$before = " + Snapshot + "\n"
            + $"$variables = @(Get-SandboxServerEnvironment -TempDir {PowerShellQuote(temp)})\n"
            + "'vars:' + (($variables | ForEach-Object { $_.Name + '=' + $_.Value }) -join ';')\n"
            + "$process = Invoke-WithChildEnvironment -Variables $variables -Action {\n"
            + "    Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', "
            + PowerShellQuote("\"" + child + "\"") + " -RedirectStandardOutput " + PowerShellQuote(childOutput) + " -NoNewWindow -PassThru -Wait\n"
            + "}\n"
            + "'exit:' + $process.ExitCode\n"
            + "try { Invoke-WithChildEnvironment -Variables $variables -Action { throw 'start failed' } } catch { 'threw:' + $_.Exception.Message }\n"
            + "'unchanged:' + ($before -ceq " + Snapshot + ")\n"
            + "'parent:' + $env:HTTP_PROXY + '|' + $env:NO_PROXY + '|' + (Test-Path Env:HTTPS_PROXY) + '|' + $env:JELLYFIN_DATA_DIR\n");
        Assert.True(run.ExitCode == 0, run.Output);

        // Every inherited JELLYFIN_* variable is removed; then TMP/TEMP, and the proxy in both spellings.
        var variables = run.Lines.Single(l => l.StartsWith("vars:", StringComparison.Ordinal))[5..].Split(';');
        Assert.Contains("JELLYFIN_DATA_DIR=", variables);
        Assert.All(variables.Where(IsJellyfinVariable), v => Assert.EndsWith("=", v, StringComparison.Ordinal));
        string[] proxy = ["HTTP_PROXY=http://127.0.0.1:9", "http_proxy=http://127.0.0.1:9", "HTTPS_PROXY=http://127.0.0.1:9", "https_proxy=http://127.0.0.1:9", "NO_PROXY=127.0.0.1,localhost", "no_proxy=127.0.0.1,localhost"];
        Assert.Equal([$"TMP={temp}", $"TEMP={temp}", .. proxy], variables.Where(v => !IsJellyfinVariable(v)));

        // What the child reads, whatever spelling it asks for (Windows keeps one variable per name, whatever its case).
        Assert.Contains("exit:0", run.Lines);
        var seen = (await File.ReadAllLinesAsync(childOutput, token)).Where(l => l.Length > 0);
        Assert.Equal([.. proxy, $"TMP={temp}", $"TEMP={temp}", "JELLYFIN_DATA_DIR="], seen);

        // This process is as it was, after the start and after a failed one: the parent's proxy values, no HTTPS_PROXY.
        Assert.Contains("threw:start failed", run.Lines);
        Assert.Contains("unchanged:True", run.Lines);
        Assert.Contains(@"parent:http://parent.example:3128|parent.example|False|X:\parent-data", run.Lines);

        static bool IsJellyfinVariable(string pair) => pair.StartsWith("JELLYFIN_", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DevServer_StartsJellyfinWithTheServerEnvironment_AndNoScriptWritesUserOrMachineVariables()
    {
        var devServer = File.ReadAllText(Path.Combine(Scripts, "dev-server.ps1")).ReplaceLineEndings("\n");
        Assert.Contains("$serverEnvironment = @(Get-SandboxServerEnvironment -TempDir $sandbox.TempDir)", devServer, StringComparison.Ordinal);
        var block = devServer.IndexOf("$process = Invoke-WithChildEnvironment -Variables $serverEnvironment -Action {\n", StringComparison.Ordinal);
        Assert.True(block > 0, "dev-server.ps1 starts jellyfin.exe inside Invoke-WithChildEnvironment.");
        var end = devServer.IndexOf("\n}\n", block, StringComparison.Ordinal);
        // -Background through Start-ProcessWithLogs (only its log files and NUL inherited), else Start-Process.
        var starts = Regex.Matches(devServer, @"(Start-Process|Start-ProcessWithLogs) -FilePath \$jellyfinExe").Select(m => m.Index).ToList();
        Assert.Equal(2, starts.Count);
        Assert.All(starts, start => Assert.InRange(start, block, end));
        Assert.DoesNotContain("$env:", devServer, StringComparison.OrdinalIgnoreCase);

        // Something listening on the dead proxy's port would receive the server's outbound requests.
        var refusal = devServer.IndexOf("$proxyListeners = @(Get-ListeningProcessIds -Port $proxy.Port)", StringComparison.Ordinal);
        Assert.True(refusal > 0 && refusal < block, "dev-server.ps1 checks the proxy port before it starts jellyfin.exe.");

        // Only the process scope is ever written; the user's and the machine's variables are never changed.
        foreach (var script in Directory.EnumerateFiles(Scripts, "*.ps*1"))
        {
            var text = File.ReadAllText(script);
            foreach (Match call in Regex.Matches(text, @"SetEnvironmentVariable\([^()\r\n]*\)"))
            {
                Assert.EndsWith(", 'Process')", call.Value, StringComparison.Ordinal);
            }

            Assert.DoesNotMatch(@"(?i)\bsetx\b|EnvironmentVariableTarget\]::(User|Machine)|HKCU:\\Environment|Session Manager\\Environment", text);
        }
    }

    [Fact]
    public void IdentityProbe_KeepsItsSafetyGuards()
    {
        // The identity probe renames folders of the shared sandbox media and writes a move journal into the sandbox. It refuses
        // port 8096, moves only inside .sandbox/media and its own hold folder, records each move before making it, and
        // undoes the moves and removes the journal at the end.
        var source = File.ReadAllText(Path.Combine(Scripts, "identity-probe.mjs"));
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox media or the hold folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push({ from: src, to: dst });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("assertInside(journal0.File, sandboxRoot)", source, StringComparison.Ordinal);
        Assert.Contains("undoMoves(0);", source, StringComparison.Ordinal);
        Assert.Contains("fs.rmSync(context.journalFile);", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LibraryMaintenanceIndexProbe_KeepsItsSafetyGuards()
    {
        // The probe only reads the sandbox and rebuilds Library Maintenance's in-memory index. It
        // refuses port 8096, calls only the sandbox's loopback URL, posts nothing but the rebuild, samples only the process
        // sandbox.json records once its executable is jellyfin.exe with this sandbox's data folder on its command line, reads
        // only the sandbox's own log folder, restarts nothing and writes no file but its --out result. Its --setups
        // are opt-in: libraries named "LM Probe …" over the sandbox's own media only, Jellyfin's own merge of
        // the edition pair, and a finally that splits it and removes only those libraries.
        var source = File.ReadAllText(Path.Combine(Scripts, "librarymaintenance-index-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains(@"/\\jellyfin\.exe$/i.test(exe.trim())", source, StringComparison.Ordinal);
        Assert.Contains("commandLine.toLowerCase().includes(dataDir.toLowerCase())", source, StringComparison.Ordinal);
        // The anonymous, viewer and admin calls of the rebuild (four), and the setups' scan, library and merge (three).
        Assert.Equal(7, Regex.Matches(source, "'POST'").Count);
        Assert.Equal(4, Regex.Matches(source, "'POST', REBUILD\\)").Count);
        Assert.Contains("call('admin', 'POST', REBUILD)", source, StringComparison.Ordinal);
        Assert.Contains("await admin('POST', '/Library/Refresh');", source, StringComparison.Ordinal);
        Assert.Contains("await admin('POST', '/Library/VirtualFolders', { query, body: libraryOptions(enabled, paths) });", source, StringComparison.Ordinal);
        Assert.Contains("await call('admin', 'POST', '/Videos/MergeVersions', { query: { ids: ids.join(',') } });", source, StringComparison.Ordinal);
        // The setups only with --setups, only "LM Probe …" libraries over the sandbox's media, removed by that name only.
        Assert.Contains("const SETUP_PREFIX = 'LM Probe ';", source, StringComparison.Ordinal);
        Assert.Contains("const mediaRoot = path.join(repoRoot, '.sandbox', 'media');", source, StringComparison.Ordinal);
        Assert.Contains("else if (a === '--setups') o.setups = true;", source, StringComparison.Ordinal);
        Assert.Contains("if (opts.setups) evidence.setup = await applySetups();", source, StringComparison.Ordinal);
        Assert.Contains("if (!name.startsWith(SETUP_PREFIX) || !paths.every(inMedia)) throw", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, "'DELETE'").Count);
        Assert.Contains("call('admin', 'DELETE', `/Videos/${setup.merged[0]}/AlternateSources`)", source, StringComparison.Ordinal);
        Assert.Contains(
            "if (!name.startsWith(SETUP_PREFIX)) continue;\n        done[name] = (await call('admin', 'DELETE', '/Library/VirtualFolders',",
            source,
            StringComparison.Ordinal);
        Assert.Contains("} finally {\n    if (opts.setups) {", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"fs\.writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "librarymaintenance-index.json")));
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "librarymaintenance-tv.json")));
    }

    [Fact]
    public void LibraryMaintenanceFreshnessProbe_KeepsItsSafetyGuards()
    {
        // The probe refuses port 8096 and calls only the sandbox's loopback URL; it posts nothing but Library
        // Maintenance's rebuild, a library scan and a refresh of one library; it adds one movie folder of its own name inside
        // .sandbox/media only (a copy of the library's own file, recorded before it is made, never overwriting) and removes
        // only that, also in a finally and on the next run; it reads only the sandbox's own log folder, restarts nothing,
        // samples no process and writes no file but its record and its --out result.
        var source = File.ReadAllText(Path.Combine(Scripts, "librarymaintenance-freshness-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(source, "'POST'").Count);
        Assert.Equal(3, Regex.Matches(source, @"call\('admin', 'POST', REBUILD\)").Count);
        Assert.Contains("call('admin', 'POST', '/Library/Refresh')", source, StringComparison.Ordinal);
        Assert.Contains("call('admin', 'POST', `/Items/${library.ItemId}/Refresh`", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'DELETE'", source, StringComparison.Ordinal);
        // The media: only the probe's folder name, inside .sandbox/media, never the real server's folders or its media drive.
        Assert.Contains("const MOVIE = 'Freshness Probe Movie (2026)';", source, StringComparison.Ordinal);
        Assert.Contains("const mediaRoot = path.join(repoRoot, '.sandbox', 'media');", source, StringComparison.Ordinal);
        Assert.Contains("if (path.basename(full) !== MOVIE) throw new Error(`refusing to touch ${full}: not the probe's folder`);", source, StringComparison.Ordinal);
        Assert.Contains("/^z:/i.test(full)", source, StringComparison.Ordinal);
        Assert.Contains("fs.copyFileSync(sources[0].f, file, fs.constants.COPYFILE_EXCL);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"fs\.copyFileSync\("));
        Assert.Equal(2, Regex.Matches(source, @"fs\.rmSync\(").Count);
        Assert.Contains("const full = probeFolder(folder);\n        if (fs.existsSync(full)) fs.rmSync(full, { recursive: true });", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    if (loadCreated().length) {\n        evidence.cleanup = { removed: removeCreated() };", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.writeFileSync\(").Count);
        Assert.Contains("fs.writeFileSync(createdFile, JSON.stringify([...created, folder], null, 2));", source, StringComparison.Ordinal);
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("child_process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log(users", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryMaintenanceGroupsProbe_KeepsItsSafetyGuards()
    {
        // The probe refuses port 8096 and calls only the sandbox's loopback URL. It reads the groups, the index's
        // status and Diagnostics; its one POST is Library Maintenance's own in-memory Rebuild (which clears the groups'
        // cache between timing rounds). It deletes nothing, touches no media, samples no process, runs nothing and writes
        // no file but its --out result.
        var source = File.ReadAllText(Path.Combine(Scripts, "librarymaintenance-groups-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("const REBUILD = '/LibraryMaintenance/Duplicates/Index/Rebuild';", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, "'POST'"));
        Assert.Contains("await admin('POST', REBUILD);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'DELETE'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'PUT'", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"fs\.writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.rmSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.copyFileSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("child_process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log(users", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TagCloudProbe_KeepsItsSafetyGuards()
    {
        // The probe refuses port 8096 and calls only the sandbox's loopback URL. What it changes: the sandbox's synthetic
        // items' metadata (POST /Items/{id}), viewer2's policy (saved to its folder first and put back), Tag Cloud's own
        // settings (one source off, then the settings as they were), Tag Cloud's index (Rebuild) and its own saved clouds
        // (deleted again). It deletes no item, user or file but its saved policy, runs nothing and writes only into
        // .sandbox/<name>/tagcloud-probe or its --out file.
        var source = File.ReadAllText(Path.Combine(Scripts, "tagcloud-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"fetch\("));
        Assert.Contains("await fetch(new URL(url, origin), ", source, StringComparison.Ordinal);
        Assert.Contains("const workRoot = path.join(sandboxRoot, 'tagcloud-probe');", source, StringComparison.Ordinal);

        var writes = Regex.Matches(source, "'(POST|PUT|DELETE)', (`[^`]*`|'[^']*')").Select(m => (Method: m.Groups[1].Value, Target: m.Groups[2].Value.Trim('`', '\''))).ToList();
        Assert.NotEmpty(writes);
        foreach (var (method, target) in writes)
        {
            var allowed = method == "DELETE"
                ? target.StartsWith("/TagCloud/Clouds/", StringComparison.Ordinal)
                : target.StartsWith("/TagCloud/", StringComparison.Ordinal)
                    || target.StartsWith("/Items/", StringComparison.Ordinal)
                    || target == "/Users/${users.viewer2.id}/Policy"
                    || target == "/Plugins/${pluginGuid}/Configuration";
            Assert.True(allowed, $"{method} {target}");
        }

        Assert.Equal(2, Regex.Matches(source, @"fs\.writeFileSync\(").Count);
        Assert.Single(Regex.Matches(source, @"fs\.rmSync\("));
        Assert.Contains("fs.rmSync(saved);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("child_process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("console.log(users", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryMaintenanceAccessProbe_KeepsItsSafetyGuards()
    {
        // The probe refuses port 8096 and calls only the sandbox's loopback URL. For the run it adds
        // one library of its own name over Movies' folder inside .sandbox/media, makes viewer2 an administrator limited to
        // Movies (refusing to start when viewer2 is one already) and adds one API key of its own name; a finally revokes
        // the key, restores the policy and removes only that library. It touches no file, samples no process, runs
        // nothing and writes no file but its --out result.
        var source = File.ReadAllText(Path.Combine(Scripts, "librarymaintenance-access-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("const SHARED = 'LM Access Probe Shared';", source, StringComparison.Ordinal);
        Assert.Contains("const KEY_APP = 'librarymaintenance-access-probe';", source, StringComparison.Ordinal);
        Assert.Contains("const mediaRoot = path.join(repoRoot, '.sandbox', 'media');", source, StringComparison.Ordinal);
        Assert.Contains("if (!inMedia(location)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Contains("!/^z:/i.test(resolved)", source, StringComparison.Ordinal);
        Assert.Contains("if (original.IsAdministrator) {", source, StringComparison.Ordinal);

        // The writes: the library (added, removed by its name only), a scan, the index's Rebuild, viewer2's policy (set,
        // restored), the key (added, revoked by its app name only).
        Assert.Equal(6, Regex.Matches(source, "'POST'").Count);
        Assert.Equal(3, Regex.Matches(source, "'DELETE'").Count);
        Assert.DoesNotContain("'PUT'", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"'DELETE', '/Library/VirtualFolders'"));
        Assert.Contains("const r = await call('admin', 'DELETE', '/Library/VirtualFolders', { query: { name: SHARED, refreshLibrary: true } });", source, StringComparison.Ordinal);
        Assert.Contains(".filter((k) => k.AppName === KEY_APP)", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"/Users/\$\{users\.viewer2\.id\}/Policy").Count);
        Assert.Contains("} finally {\n    const cleanup = {};", source, StringComparison.Ordinal);
        Assert.Contains("if (original) cleanup.policyRestored = (await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: original })).status;", source, StringComparison.Ordinal);

        Assert.Single(Regex.Matches(source, @"fs\.writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.rmSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.copyFileSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("child_process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log(users", source, StringComparison.Ordinal);
    }

    [Fact]
    public void JeSwitchOffProbe_KeepsItsSafetyGuards()
    {
        // The probe changes Jellyfin Enhanced's and Custom Tabs' settings in a running je sandbox through their
        // configuration APIs, writes one synthetic per-user JE shortcuts file under the sandbox's JE data folder, restarts
        // the sandbox only through Stop-Sandbox.ps1 and dev-server.ps1 -Background -NoBuild, runs its two webchecks
        // through webcheck.mjs, and puts the pinned fixture back in a finally (asserted by Initialize-Sandbox.ps1). It
        // refuses port 8096 and a sandbox without JE, and works only under .sandbox.
        var source = File.ReadAllText(Path.Combine(Scripts, "je-switchoff-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("includes('JellyfinEnhanced')", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Initialize-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, @"runScript\('").Count);
        Assert.Contains("'--steps', steps", source, StringComparison.Ordinal);
        Assert.Contains("await setPluginConfig(JE_ID, original.je)", source, StringComparison.Ordinal);
        Assert.Contains("await setPluginConfig(CUSTOM_TABS_ID, original.tabs)", source, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "je-shortcuts.json")));
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "je-settings.json")));
        // JE 12.11's Pages tab, read-only (the webcheck never clicks JE's Save).
        Assert.Contains("webcheck('je-pages-order.json', 'pages-order')", source, StringComparison.Ordinal);
        var pagesOrder = File.ReadAllText(Path.Combine(Scripts, "webchecks", "je-pages-order.json"));
        Assert.DoesNotContain("je-save-dock-btn", pagesOrder, StringComparison.Ordinal);
        Assert.DoesNotContain("updatePluginConfiguration", pagesOrder, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProbeRestart_KeepsTheSandboxsOwnPort()
    {
        // The Bookmarks 1.1.2 follow-up: dev-server.ps1 without -Port takes a standard sandbox name's port (je: 8098), so a
        // probe's restart of a je sandbox made on another port moved it there. Every probe that starts the sandbox again
        // passes the port sandbox.json has, which each reads as port at its start.
        var restarts = Directory.EnumerateFiles(Scripts, "*.mjs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"runScript\('dev-server\.ps1', \[[^\]]*\]").Select(m => (File: Path.GetFileName(f), Call: m.Value)))
            .ToList();
        // Thirteen: the nine before, Bookmarks' clip rename probe (its crash simulation) and User Ratings' api probe (three).
        Assert.Equal(13, restarts.Count);
        Assert.All(restarts, r => Assert.True(r.Call == "runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild']", r.File + ": " + r.Call));
        foreach (var file in restarts.Select(r => r.File).Distinct())
        {
            var source = File.ReadAllText(Path.Combine(Scripts, file)).ReplaceLineEndings("\n");
            Assert.Contains("const port = Number(state.Port);", source, StringComparison.Ordinal);
            Assert.Contains("readJson(path.join(sandboxRoot, 'sandbox.json'))", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BookmarksJeImportProbe_KeepsItsSafetyGuards()
    {
        // The probe writes the synthetic 81-style JE bookmark files (kit/testdata/je-data/import-81, never real JE
        // data) under the sandbox's JE data folder, refusing to overwrite a file it didn't write, and deletes them again;
        // changes JE's, Custom Tabs' and Bookmarks' settings through their configuration APIs and puts them back; copies
        // Bookmarks' data file first and puts it back only with the sandbox stopped; restarts only through Stop-Sandbox.ps1 and
        // dev-server.ps1 -Background -NoBuild; runs its webchecks through webcheck.mjs; and asserts the pinned fixture with
        // Initialize-Sandbox.ps1. It refuses port 8096 and a sandbox without JE, and works only under .sandbox.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-je-import-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [jeDataFolder, storeFile, preOpFolder, workRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("includes('JellyfinEnhanced')", source, StringComparison.Ordinal);
        Assert.Contains("const FIXTURE = path.join(repoRoot, 'kit', 'testdata', 'je-data', 'import-81');", source, StringComparison.Ordinal);
        Assert.Contains("is there already; the probe writes only its own JE files", source, StringComparison.Ordinal);
        Assert.Contains("const stopSandbox = () => runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox]);", source, StringComparison.Ordinal);
        Assert.Contains("runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Initialize-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, @"runScript\('").Count);
        Assert.Contains("'--steps', steps", source, StringComparison.Ordinal);
        Assert.Contains("await setPluginConfig(JE_ID, original.je)", source, StringComparison.Ordinal);
        Assert.Contains("await setPluginConfig(CUSTOM_TABS_ID, original.tabs)", source, StringComparison.Ordinal);
        Assert.Contains("await setPluginConfig(BOOKMARKS_ID, original.bookmarks)", source, StringComparison.Ordinal);

        // The data file goes back only after the sandbox stopped.
        var stop = source.IndexOf("const stopped = stopSandbox();", StringComparison.Ordinal);
        var copyBack = source.IndexOf("if (fs.existsSync(from)) fs.copyFileSync(from, to);", StringComparison.Ordinal);
        Assert.True(stop > 0 && copyBack > stop, "the data file is put back after the stop");
        foreach (var webcheck in new[] { "je-settings.json", "je-import-ui.json", "je-import-page.json" })
        {
            Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", webcheck)), webcheck);
        }

        Assert.True(File.Exists(Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "testdata", "je-data", "import-81", "library.json")));
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UsersProbe_KeepsItsSafetyGuards()
    {
        // The probe restarts the sandbox and edits the demo's files between restarts. It refuses port 8096, works only
        // in the sandbox's Bookmarks data folder and its own work folder under .sandbox, restarts only through
        // Stop-Sandbox.ps1 and dev-server.ps1 -Background -NoBuild, and never stores the throwaway users' passwords.
        var source = File.ReadAllText(Path.Combine(Scripts, "users-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'])", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"runScript\('").Count);
        Assert.Contains("Password: crypto.randomBytes(24).toString('base64url')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksDataProbe_KeepsItsSafetyGuards()
    {
        // The probe runs the kit's user, data and retention checks on Bookmarks' own store: two throwaway users with random passwords
        // (memory only), deleted again; the stored user ids replaced in bookmarks.json while the sandbox is stopped; a
        // snapshot copied over it while stopped; the test series' Season 02 and a movie folder held out of the shared media
        // (recorded, undone); retention with the developer clock. It copies the data files it changes first and puts them
        // back only with the sandbox stopped. It refuses port 8096, works only under .sandbox, restarts only through
        // Stop-Sandbox.ps1 and dev-server.ps1 -Background -NoBuild, and never deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-data-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [dataFolder, workRoot, logDir]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("const password = crypto.randomBytes(24).toString('base64url');", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'])", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"runScript\('").Count);
        Assert.Contains("refusing to move ${p}", source, StringComparison.Ordinal);
        Assert.Contains("moves.push({ from: src, to: dst });\n    saveMoves(moves);", source, StringComparison.Ordinal);

        // bookmarks.json is written by the probe only while the sandbox is stopped: the ids replaced, the snapshot copied
        // over it, and the backup put back.
        foreach (var (stop, write, start) in new[]
        {
            ("stopSandbox('restored data folder');", "fs.writeFileSync(file, text);", "await startSandbox('restored data folder');"),
            ("stopSandbox('manual restore');", "fs.copyFileSync(zFile, storeFile);", "await startSandbox('manual restore');"),
            ("stopSandbox('clean-up: the data put back');", "restoreBackup();", "await startSandbox('clean-up: the data put back');"),
        })
        {
            var a = source.IndexOf(stop, StringComparison.Ordinal);
            var b = source.IndexOf(write, a, StringComparison.Ordinal);
            var c = source.IndexOf(start, StringComparison.Ordinal);
            Assert.True(a > 0 && a < b && b < c, $"{write} only between {stop} and {start}");
        }

        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "users-banner.json")));
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksJeWebProbe_KeepsItsSafetyGuards()
    {
        // The probe works Jellyfin Enhanced's and Custom Tabs' own settings pages (webchecks), changes JE's, Custom
        // Tabs' and Bookmarks' settings through their configuration APIs, writes viewer's JE hidden content through JE's own
        // API, restarts only through Stop-Sandbox.ps1 and dev-server.ps1 -Background -NoBuild, and puts everything back in a
        // finally, asserted by Initialize-Sandbox.ps1. It refuses port 8096 and a sandbox without JE, and works only under
        // .sandbox.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-je-web-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("includes('JellyfinEnhanced')", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Initialize-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, @"runScript\('").Count);
        Assert.Contains("'--steps', steps", source, StringComparison.Ordinal);
        Assert.Contains("if (original.je) await setPluginConfig(JE_ID, original.je);", source, StringComparison.Ordinal);
        Assert.Contains("if (original.tabs) await setPluginConfig(CUSTOM_TABS_ID, original.tabs);", source, StringComparison.Ordinal);
        Assert.Contains("if (original.bookmarks) await setPluginConfig(BOOKMARKS_ID, original.bookmarks);", source, StringComparison.Ordinal);
        // The key step on JE's Keyboard tab has a webcheck per page: 12.11's Key field (je-switchoff-disabled.json) and 12.12's grid
        // of keys (je-switchoff-grid-key.json, the JE 12.12 re-check), picked by JE's version.
        foreach (var webcheck in new[] { "je-name-dialog.json", "je-coexist.json", "je-switchoff-save.json", "je-switchoff-osd.json", "je-switchoff-ct-remove.json", "je-switchoff-disabled.json", "je-switchoff-grid-key.json", "je-use-b.json", "je-hidden-content.json" })
        {
            Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", webcheck)), webcheck);
            Assert.Contains($"'{webcheck}'", source, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "probes", "je-spy-probe.js")));
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksUpgradeProbe_KeepsItsSafetyGuards()
    {
        // The probe seeds bookmarks and clips through Bookmarks 1.0.0's REST API in a sandbox and reads the upgraded
        // and the downgraded data; it never restarts, deploys or writes a data file. It refuses port 8096 and reads only the
        // sandbox's own data folder.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-upgrade-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("spawnSync", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.writeFileSync\(").Count);
        Assert.Contains("fs.writeFileSync(manifestFile,", source, StringComparison.Ordinal);
        Assert.Contains("fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "upgrade-names.json")));
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksSettingsProbe_KeepsItsSafetyGuards()
    {
        // The probe changes Bookmarks' settings in a running sandbox (through the API and the settings page), makes
        // one bookmark of its own with a clip, imports the sandbox's own export and restores the snapshot that import took.
        // It refuses port 8096, works only under .sandbox, puts every setting back and deletes its bookmark in a finally,
        // never restarts or deploys anything, and names no real server path.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-settings-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n        for (const id of created.reverse()) {", source, StringComparison.Ordinal);
        Assert.Contains("        if (context.saved) {\n            await setConfig(context.saved)", source, StringComparison.Ordinal);
        Assert.Contains("const NOTE = 'settings-probe';", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Install-RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "settings-roundtrip.json")));
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "settings-data.json")));
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "settings-phone.json")));

        // The round trip's clip folder is inside the sandbox and never created; both webchecks put the settings back or
        // change data only to what it was (the sandbox's own export, the snapshot the import took).
        var roundtrip = File.ReadAllText(Path.Combine(Scripts, "webchecks", "settings-roundtrip.json"));
        Assert.Contains("roundtrip-clips-never-created", roundtrip, StringComparison.Ordinal);
        Assert.Contains("put the settings back", roundtrip, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgramData", roundtrip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClipGuardProbe_KeepsItsSafetyGuards()
    {
        // The probe changes Bookmarks' settings in a running sandbox (the reserve above C:'s free space, a clip folder
        // that doesn't exist, one on a drive letter that isn't there, the x264 preset) and puts them back in a finally. It
        // refuses port 8096, works only under .sandbox, never restarts or deploys anything, and names no real server path.
        var source = File.ReadAllText(Path.Combine(Scripts, "clip-guard-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n        if (context.saved) {\n            await setConfig(context.saved)", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "clip-guard-settings.json")));
        Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", "clip-guard-refused.json")));
    }

    [Fact]
    public void LibraryChangesProbe_KeepsItsSafetyGuards()
    {
        // The probe adds a movie folder (with its NFO) and a series folder to the shared sandbox media and scans. It
        // refuses port 8096, touches only its own two folders inside .sandbox/media, records each before writing into it,
        // never overwrites a file, removes them at the end and at the start of the next run, reads only the sandbox's own
        // log folder, and never restarts or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "library-changes-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("not one of the probe's folders", source, StringComparison.Ordinal);
        Assert.Contains("if (!created.includes(top)) saveCreated([...created, top]);\n    fs.mkdirSync(dir, { recursive: true });", source, StringComparison.Ordinal);
        Assert.Contains("fs.constants.COPYFILE_EXCL", source, StringComparison.Ordinal);
        Assert.Contains("{ encoding: 'utf8', flag: 'wx' }", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("if (removeCreated() > 0) await scan('clean-up: probe media removed');", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryDemoProbe_KeepsItsSafetyGuards()
    {
        // The library demo probe renames folders of the shared sandbox media (and adds one season folder of its own), changes a test
        // user's user data and library access, merges versions of sandbox movies, and restarts the sandbox once. It refuses port
        // 8096, moves only inside .sandbox/media and its own hold folder, records each move and its one new folder before
        // making them, undoes everything at the end, restarts only through Stop-Sandbox.ps1 and dev-server.ps1 -Background
        // -NoBuild, and reads only the sandbox's own log and data folders.
        var source = File.ReadAllText(Path.Combine(Scripts, "library-demo-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox media or the hold folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push({ from: src, to: dst });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("fs.writeFileSync(createdFile, JSON.stringify([dir], null, 2));\n    fs.mkdirSync(dir);", source, StringComparison.Ordinal);
        Assert.Contains("fs.constants.COPYFILE_EXCL", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to remove ${dir}: not the probe's folder`)", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("undoMoves(0);", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'])", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"runScript\('").Count);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksIdentityProbe_KeepsItsSafetyGuards()
    {
        // The probe renames folders of the shared sandbox media, writes the sandbox's move journal as Library Maintenance
        // would, edits bookmarks.json once while the sandbox is stopped, and restarts it once. It refuses port 8096, moves only
        // inside .sandbox/media and its own hold folder, records each move before making it, undoes everything at the end,
        // keeps every file it writes inside the sandbox, restarts only through Stop-Sandbox.ps1 and dev-server.ps1
        // -Background -NoBuild, and reads only the sandbox's own log and data folders.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-identity-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox media or the hold folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push({ from: src, to: dst });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, journalFile, workRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("undoMoves(0);", source, StringComparison.Ordinal);
        Assert.Contains("runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'])", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"runScript\('").Count);

        // bookmarks.json is written only between the stop and the start.
        var stop = source.IndexOf("stopSandbox('journal and backfill');", StringComparison.Ordinal);
        var write = source.IndexOf("fs.writeFileSync(storeFile,", StringComparison.Ordinal);
        var start = source.IndexOf("await startSandbox('journal and backfill');", StringComparison.Ordinal);
        Assert.True(stop > 0 && stop < write && write < start, "bookmarks.json is edited only while the sandbox is stopped.");
        Assert.Single(Regex.Matches(source, @"fs\.writeFileSync\(storeFile"));
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksQueryProbe_KeepsItsSafetyGuards()
    {
        // The probe renames one movie folder of the shared sandbox media into its hold folder, restricts a test user's
        // libraries for one check, sets a test user's naming preferences, and makes and deletes bookmarks and clips. It refuses
        // port 8096, moves only inside .sandbox/media and its own hold folder, records the move before making it, puts
        // everything back in a finally (the policy it read, the preferences, the media), reads only the sandbox's own log and
        // data folders, and never restarts or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-query-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox media or the hold folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push({ from: src, to: dst });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, clipRoot, workRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("undoMoves(0);", source, StringComparison.Ordinal);
        Assert.Contains("if (context.viewer2Policy) await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy });", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksClipSweepProbe_KeepsItsSafetyGuards()
    {
        // The probe runs Bookmarks' daily maintenance through the API, sets ClipOutputPath (for a few clips, to folders
        // in its own work folder, inside the sandbox), MissingClipRetentionDays and the Debug build's DeveloperClockOffsetDays,
        // renames clip files into its hold folder and two clip folders aside, and makes and deletes bookmarks and clips
        // through the API. It refuses port 8096, works only inside the sandbox, refuses a build without the developer clock and
        // a sandbox whose own clips the retention would take, saves the settings to a file before it changes them, records each
        // rename before making it, puts it all back in a finally and at the next start, never writes bookmarks.json or the
        // archive, and never restarts or deploys anything. It deletes two kinds of file itself: its saved-settings file, and
        // the held copy of a clip file no record names any more (in its hold folder only); it also makes one
        // empty folder in place of an unplugged one, recorded before it is made, and removes it with rmdir, which refuses a
        // folder that isn't empty. It also moves a file into that stray folder (recorded, and put back
        // first), holds a clip folder's marker file out and discards that held copy once the folder has a new one (the admin's
        // confirmation wrote it), renames the sandbox's default clip folder into its work folder (recorded, key 'default'),
        // and sets the folder Bookmarks made again in its place aside there before the real one goes back, where it deletes that
        // folder's marker file and removes its empty folders with rmdir; a made-again folder holding anything else stops it.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-clip-sweep-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, archiveFile, clipRoot, workRoot, usbRoot, e1Root, heldRoot, defaultAway, defaultRemade, snapshotsRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("const defaultAway = path.join(workRoot, 'default-clips-away');\nconst defaultRemade = path.join(workRoot, 'default-clips-remade');", source, StringComparison.Ordinal);

        // The stray folder: in the work folder only, recorded before it is made, removed by rmdir alone (never recursively),
        // once the files the probe moved into it are back.
        Assert.Contains(
            "    const full = assertInside(folder, workRoot);\n    if (fs.existsSync(full)) throw new Error(`${full} exists already`);\n    const created = loadCreated();\n    created.push(full);\n    fs.writeFileSync(createdFile, JSON.stringify(created, null, 2));\n    fs.mkdirSync(full, { recursive: true });",
            source,
            StringComparison.Ordinal);
        Assert.Equal(["fs.rmdirSync(current)", "fs.rmdirSync(current)"], Regex.Matches(source, @"fs\.rmdirSync\([^)]*\)").Select(m => m.Value));
        Assert.DoesNotContain("recursive: true, force", source, StringComparison.Ordinal);
        Assert.Contains(
            "        if (loadCreated().some((c) => lower(m.to).startsWith(lower(c) + path.sep))) undoOne(m);\n    }\n    removeCreated();\n    let restoredDefault = false;\n    let moves = loadMoves();",
            source,
            StringComparison.Ordinal);

        // The default clip folder: only it, only into defaultAway; the folder made again in its place only set aside when it
        // holds nothing but the marker file at its top and empty folders, and discarded in the work folder only.
        Assert.Contains("    const wholeDefault = key === 'default' && src === clipRoot && dst === defaultAway;", source, StringComparison.Ordinal);
        Assert.Contains(
            "        if (!(entry.isFile() && entry.name === MARKER && path.dirname(full) === folder)) throw new Error(`refusing to discard ${folder}: it holds ${full}; fix by hand`);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "    const folder = assertInside(defaultRemade, workRoot);\n    assertOnlyMarker(folder);\n    const marker = path.join(folder, MARKER);\n    if (fs.existsSync(marker)) fs.rmSync(marker);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("        assertOnlyMarker(m.from);\n        fs.renameSync(m.from, defaultRemade);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox's clip folders or the probe's folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push({ key, from: src, to: dst });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, @"fs\.renameSync\(").Count);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;", source, StringComparison.Ordinal);

        // The refusals before anything is changed: no developer clock, or a clip of the sandbox's own at stake.
        var refusals = source.IndexOf("if (state.Configuration !== 'Debug') throw new Error(", StringComparison.Ordinal);
        var atStake = source.IndexOf("if (atStake.length > 0) throw new Error(`refusing to run:", StringComparison.Ordinal);
        var saved = source.IndexOf("fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));", StringComparison.Ordinal);
        var firstChange = source.IndexOf("await setConfiguration(", source.IndexOf("async function run(", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(refusals > 0 && refusals < atStake && atStake < saved && saved < firstChange, "The refusals come first, and the settings are saved to a file before the first change.");
        Assert.Contains("cleanup.settingsRestored = await restoreSavedSettings();", source, StringComparison.Ordinal);
        Assert.Contains("const restoredDefault = undoMoves();\n    await restoreSavedSettings();", source, StringComparison.Ordinal);

        // The held copy: only inside the hold folder, only a clip file there, only when no record names its path; a held marker
        // file only when its folder has a marker again; the made-again default folder's marker in defaultRemade only.
        Assert.Equal(["fs.rmSync(held)", "fs.rmSync(marker)", "fs.rmSync(held)", "fs.rmSync(savedSettingsFile)"], Regex.Matches(source, @"fs\.rmSync\([^)]*\)").Select(m => m.Value));
        Assert.Contains(
            "    if (held !== path.join(heldRoot, MARKER) || !fs.statSync(held).isFile() || !fs.existsSync(from)) throw new Error(`refusing to delete ${held}: not a held marker file whose folder has a new one`);\n    fs.rmSync(held);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "    const held = assertInside(to, heldRoot);\n    if (path.dirname(held) !== heldRoot || !fs.statSync(held).isFile() || !/\\.(mkv|mp4)$/i.test(held)) throw new Error(`refusing to delete ${held}: not a held clip file`);\n    if (referencedPaths().has(lower(from))) throw new Error(`refusing to delete ${held}: a record still names ${from}`);\n    fs.rmSync(held);",
            source,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"discardHeldCopy\(m\)"));

        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.writeFileSync(archiveFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("appendFileSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Sandbox", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Admin/Detached/Purge", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksClipsProbe_KeepsItsSafetyGuards()
    {
        // The probe sets Bookmarks' ClipOutputPath for two clips to a folder in its own work folder
        // (inside the sandbox, so the sandbox still starts), renames that folder aside and two of the sandbox's clip files into
        // its hold folder, and makes and deletes bookmarks and clips through the API. It refuses port 8096, works only inside
        // the sandbox, saves the setting to a file before it changes it, records each rename before making it, puts all of it
        // back in a finally and at the next start, never writes bookmarks.json, and never restarts or deploys anything. It
        // deletes two files itself: its saved-settings file, and the held copy of a clip Bookmarks made again under the same
        // name (in its hold folder only, for a move recorded as remade whose path the new clip has).
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-clips-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, clipRoot, workRoot, usbRoot, heldRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox's clip folders or the probe's folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push({ from: src, to: dst, remade });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.renameSync\(").Count);
        Assert.Contains("undoMoves(0);", source, StringComparison.Ordinal);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;", source, StringComparison.Ordinal);

        // The setting is saved before it changes, put back right after the clips, in the finally and at the next start.
        var saved = source.IndexOf("fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));", StringComparison.Ordinal);
        var changed = source.IndexOf("await setConfiguration({ ClipOutputPath: usbRoot });", StringComparison.Ordinal);
        Assert.True(saved > 0 && saved < changed, "ClipOutputPath is saved to a file before it is changed.");
        Assert.Contains("cleanup.settingsRestored = await restoreSavedSettings();", source, StringComparison.Ordinal);
        Assert.Contains("undoMoves(0);\n    await restoreSavedSettings();", source, StringComparison.Ordinal);
        Assert.Equal(["fs.rmSync(held)", "fs.rmSync(savedSettingsFile)"], Regex.Matches(source, @"fs\.rmSync\([^)]*\)").Select(m => m.Value));

        // The held copy: only inside the hold folder, only a file there, only for a remade move whose clip is there again
        // (at its path, or at the numbered name the remake took, `remadeAs`), and only when undoing that
        // move finds both.
        Assert.Contains(
            "    const held = assertInside(to, heldRoot);\n    if (remade !== true || path.dirname(held) !== heldRoot || !fs.statSync(held).isFile()) throw new Error(`refusing to delete ${held}: not a held copy of a clip made again`);\n    if (![from, remadeAs].some((p) => p && fs.existsSync(path.resolve(p)))) throw new Error(`refusing to delete ${held}: ${remadeAs || from}, made again, isn't there`);\n    fs.rmSync(held);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "        if (fs.existsSync(to) && last.remade === true && [from, last.remadeAs].some((p) => p && fs.existsSync(path.resolve(p)))) {\n            discardHeldCopy(last);",
            source,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"remadeAs: remadePath \}"));
        Assert.Single(Regex.Matches(source, @"discardHeldCopy\(last\)"));
        Assert.Single(Regex.Matches(source, @"\{ remade: true \}"));

        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Sandbox", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksClipsPageProbe_KeepsItsSafetyGuards()
    {
        // The probe sets Bookmarks' ClipOutputPath for one clip to a folder in its own work folder (inside the
        // sandbox, so the sandbox still starts) and VideoPreset to slow while page-clips.json runs, renames that folder aside
        // and one of the sandbox's clip files into its hold folder, makes and deletes bookmarks and clips through the API and
        // runs the My Clips page's webchecks. It refuses port 8096, works only inside the sandbox, saves each setting to a
        // file before it changes it, records each rename before making it, puts all of it back in a finally and at the next
        // start, never writes bookmarks.json, deletes no file but its saved-settings file, and never restarts or deploys
        // anything. It also sets ClipOutputPath for one clip each to two more folders of its work folder and
        // VideoPreset to slow while page-clips-actions.json runs (withSettings: saved first, put back right after), renames
        // those folders aside, makes an empty folder in place of one (recorded first, removed with rmdir before any move is
        // undone), holds one clip file open from a PowerShell process (the path through the environment), and deletes one
        // more file: the held copy of the clip file it forgets, in its hold folder only and only once no record names it.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-clips-page-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, clipRoot, workRoot, usbRoot, heldRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [cpUsbRoot, cpStrayRoot, createdFile]) assertInside(p, workRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox's clip folders or the probe's folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push(forgettable ? { from: src, to: dst, forgettable: true } : { from: src, to: dst });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.renameSync\(").Count);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;", source, StringComparison.Ordinal);

        // Each setting is saved to a file before it changes and put back right after its clip or its webcheck, in the
        // finally and at the next start; nothing else changes a setting.
        var saves = Regex.Matches(source, Regex.Escape("fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));")).Select(m => (m.Index, Kind: "save"));
        var changes = Regex.Matches(source, @"await setConfiguration\((\{ (ClipOutputPath: usbRoot|VideoPreset: 'slow') \}|patch)\);").Select(m => (m.Index, Kind: "change"));
        var order = saves.Concat(changes).OrderBy(x => x.Index).Select(x => x.Kind).ToList();
        Assert.Equal(["save", "change", "save", "change", "save", "change"], order);
        Assert.Equal(4, Regex.Matches(source, @"await setConfiguration\(").Count);
        Assert.Contains("await setConfiguration(readJson(savedSettingsFile));\n    fs.rmSync(savedSettingsFile);", source, StringComparison.Ordinal);
        Assert.Contains("        } finally {\n            await restoreSavedSettings();\n            context.savedSettings = null;\n        }", source, StringComparison.Ordinal);
        Assert.Contains("    try {\n        return await step();\n    } finally {\n        await restoreSavedSettings();\n        context.savedSettings = null;\n    }", source, StringComparison.Ordinal);
        Assert.Contains("cleanup.settingsRestored = await restoreSavedSettings();", source, StringComparison.Ordinal);
        Assert.Contains("removeCreated();\n    evidence.leftovers.discarded = discardForgotten();\n    undoMoves(0);\n    await restoreSavedSettings();", source, StringComparison.Ordinal);
        Assert.Contains("removeCreated();\n            cleanup.discarded = discardForgotten();\n            undoMoves(0);", source, StringComparison.Ordinal);

        // The files it deletes: its saved settings, and a forgotten clip's held copy in its hold folder only, never one a record
        // names; the folder it makes, in its work folder only, recorded first and removed with rmdir (an empty folder only).
        Assert.Equal(["fs.rmSync(savedSettingsFile)", "fs.rmSync(held)"], Regex.Matches(source, @"fs\.rmSync\([^)]*\)").Select(m => m.Value));
        Assert.Contains(
            "if (m.forgettable && !referenced.has(path.resolve(m.from).toLowerCase()) && held.startsWith(heldRoot + path.sep) && fs.existsSync(held)) {\n            fs.rmSync(held);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("if (!dir.startsWith(workRoot + path.sep)) throw new Error(`refusing to make ${dir}: not in the probe's folder`);", source, StringComparison.Ordinal);
        Assert.Contains("created.push(dir);\n    fs.writeFileSync(createdFile, JSON.stringify(created, null, 2));\n    fs.mkdirSync(dir);", source, StringComparison.Ordinal);
        Assert.Equal(["fs.rmdirSync(dir)"], Regex.Matches(source, @"fs\.rmdirSync\([^)]*\)").Select(m => m.Value));
        Assert.DoesNotContain("recursive: true, force", source, StringComparison.Ordinal);

        // The file held open: by a PowerShell process of its own, the path through the environment, never a command line.
        Assert.Contains("{ env: { ...process.env, BOOKMARKS_PROBE_HOLD: file }, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"\bspawn\("));

        // The webchecks are the repository's, run against this sandbox only.
        Assert.Contains(
            "spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out], {",
            source,
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        foreach (var name in new[] { "page-clips", "page-clips-tv", "page-clips-phone", "page-clips-deeplink", "page-clips-actions", "page-clips-inuse", "page-clips-je" })
        {
            Assert.Contains($"webcheck('{name}')", source, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", name + ".json")), name + ".json is missing.");
        }

        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Sandbox", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksClipRenameProbe_KeepsItsSafetyGuards()
    {
        // The probe renames clips through the API and runs the rename webchecks; it sets ClipOutputPath for one clip
        // each to two folders of its work folder and VideoPreset to slow for one clip being made (saved first, put back right
        // after), renames those folders aside and makes an empty folder in place of one, holds clip files open from a
        // PowerShell process, and for its crash simulation stops the sandbox, writes two rename journal entries into
        // Bookmarks' data folder while it is stopped, moves one clip file within the clip folder, and starts it again. It
        // refuses port 8096, works only inside the sandbox, records every change before making it, puts all of it back in a
        // finally and at the next start, never writes bookmarks.json, deletes no file but its saved-settings file, restarts
        // only through Stop-Sandbox.ps1 and dev-server.ps1 -Background -NoBuild, and never deploys anything. It also
        // imports an export it read moments before (Replace) and restores the snapshot that import took, both
        // through the admin API, for its restore after renames.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-clip-rename-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [dataFolder, storeFile, clipRoot, workRoot, logDir]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [usbRoot, strayRoot, movesFile, createdFile, plantedFile, savedSettingsFile]) assertInside(p, workRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox's clip folders or the probe's folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push(adopted ? { from: src, to: dst, adopted: true } : { from: src, to: dst });\n    saveMoves(moves);\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.renameSync\(").Count);
        Assert.Contains("if (m.adopted && referenced.has(path.resolve(m.to).toLowerCase())) {", source, StringComparison.Ordinal);

        // Each setting saved to a file before it changes and put back right after its step; nothing else changes a setting.
        Assert.Single(Regex.Matches(source, Regex.Escape("fs.writeFileSync(savedSettingsFile, JSON.stringify(context.savedSettings));")));
        Assert.Equal(2, Regex.Matches(source, @"await setConfiguration\(").Count);
        Assert.Contains("    try {\n        return await step();\n    } finally {\n        await restoreSavedSettings();\n        context.savedSettings = null;\n    }", source, StringComparison.Ordinal);
        Assert.Contains("removeCreated();\n    evidence.leftovers.adopted = discardAdopted();\n    undoMoves();\n    await restoreSavedSettings();", source, StringComparison.Ordinal);
        Assert.Contains("removeCreated();\n            cleanup.adopted = discardAdopted();\n            undoMoves();", source, StringComparison.Ordinal);

        // The files it deletes: its saved settings only; the folder it makes, in its work folder, recorded first and removed
        // with rmdir (an empty folder only).
        Assert.Equal(["fs.rmSync(savedSettingsFile)"], Regex.Matches(source, @"fs\.rmSync\([^)]*\)").Select(m => m.Value));
        Assert.Equal(["fs.rmdirSync(dir)"], Regex.Matches(source, @"fs\.rmdirSync\([^)]*\)").Select(m => m.Value));
        Assert.Contains("if (!dir.startsWith(workRoot + path.sep)) throw new Error(`refusing to make ${dir}: not in the probe's folder`);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("recursive: true, force", source, StringComparison.Ordinal);

        // The journal entries: written only while the sandbox is stopped, into Bookmarks' data folder, naming paths in the
        // sandbox's clip folder only, recorded first, never over an entry there; bookmarks.json never written.
        Assert.Contains("if (running) throw new Error('refusing to write a journal entry while the sandbox runs');", source, StringComparison.Ordinal);
        Assert.Contains("if (!p.startsWith(clipRoot + path.sep)) throw new Error(`refusing to name ${p} in a journal entry: not in the sandbox's clip folder`);", source, StringComparison.Ordinal);
        Assert.Contains("planted.push(file);\n    fs.writeFileSync(plantedFile, JSON.stringify(planted, null, 2));\n    fs.writeFileSync(file, JSON.stringify(entry, null, 2));", source, StringComparison.Ordinal);
        Assert.Contains("assertInside(file, dataFolder);", source, StringComparison.Ordinal);
        var stop = source.IndexOf("stopSandbox('crash simulation');", StringComparison.Ordinal);
        var plant = source.IndexOf("const plantedCrashed = plantEntry(", StringComparison.Ordinal);
        var start = source.IndexOf("await startSandbox('crash simulation');", StringComparison.Ordinal);
        Assert.True(stop > 0 && stop < plant && plant < start, "the entries are written only between the stop and the start");
        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.copyFileSync(", source.Replace("fs.copyFileSync(shot, path.join(workRoot, path.basename(shot)));", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // The restarts: the repository's scripts only, on this sandbox and its port.
        Assert.Contains("runScript('Stop-Sandbox.ps1', ['-Name', opts.sandbox])", source, StringComparison.Ordinal);
        Assert.Contains("runScript('dev-server.ps1', ['-Name', opts.sandbox, '-Port', String(port), '-Background', '-NoBuild'])", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"runScript\('").Count);

        // The restore check: the import's body is the export the probe read just before, and the one snapshot
        // it restores is the one that import took of the data right before it; nothing else is imported or restored.
        Assert.Contains("const exported = await call('admin', 'GET', '/Bookmarks/Admin/Export');", source, StringComparison.Ordinal);
        Assert.Contains("{ query: { mode: 'Replace', dryRun: 'false' }, body: exported.json || JSON.parse(exported.text) });", source, StringComparison.Ordinal);
        Assert.Contains("const snapshot = imported.json && imported.json.SafetySnapshot;", source, StringComparison.Ordinal);
        Assert.Contains("'/Bookmarks/Admin/Snapshots/Restore', { query: { name: snapshot } });", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, Regex.Escape("'/Bookmarks/Admin/Import'")));
        Assert.Single(Regex.Matches(source, Regex.Escape("'/Bookmarks/Admin/Snapshots/Restore'")));

        // The file held open: by a PowerShell process of its own, the path through the environment, never a command line.
        Assert.Contains("{ env: { ...process.env, BOOKMARKS_PROBE_HOLD: file }, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"\bspawn\("));

        // The webchecks are the repository's, run against this sandbox only.
        Assert.Contains(
            "spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out], {",
            source,
            StringComparison.Ordinal);
        foreach (var name in new[] { "page-clips-rename", "page-clips-rename-tv" })
        {
            Assert.Contains($"webcheck('{name}')", source, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", name + ".json")), name + ".json is missing.");
        }

        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksRowsProbe_KeepsItsSafetyGuards()
    {
        // The probe runs two webchecks, renames one movie folder of the shared sandbox media into its hold folder for
        // a real relink, and makes and deletes bookmarks and clips. It refuses port 8096, moves only inside .sandbox/media and
        // its own hold folder, records the move before making it, puts it back in a finally, reads only the sandbox's own log
        // and data folders (bookmarks.json and the clip folder read only), and never restarts or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-rows-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox media or the hold folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push({ from: src, to: dst });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, clipRoot, workRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("undoMoves(0);", source, StringComparison.Ordinal);
        Assert.Contains("[path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.rmSync(clipRoot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksPlayerProbe_KeepsItsSafetyGuards()
    {
        // The probe changes Bookmarks' settings (ShowPlayerButtons, AskForNameDefault, SuggestChapterNames) and a
        // user's preferences in a running sandbox through the plugin configuration and preferences APIs, runs two
        // webchecks and may scan once. It refuses port 8096, reads only the sandbox's own log and data folders
        // (bookmarks.json read only), puts the settings and the preferences back in a finally, deletes only the
        // bookmarks of the movie it works on, and never restarts or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-player-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, workRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n        const cleanup = { deleted: [], configurationRestored: null, preferencesRestored: null };", source, StringComparison.Ordinal);
        Assert.Contains("await setConfiguration({ ShowPlayerButtons: original.configuration.ShowPlayerButtons, AskForNameDefault: original.configuration.AskForNameDefault, SuggestChapterNames: original.configuration.SuggestChapterNames });", source, StringComparison.Ordinal);
        Assert.Contains("await setPreferences('viewer', original.preferences);", source, StringComparison.Ordinal);
        Assert.Contains("[path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.renameSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.rmSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Sandbox", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksPageProbe_KeepsItsSafetyGuards()
    {
        // The probe runs two webchecks (one of which turns EnableBookmarksPage off and on through the plugin
        // configuration API), reads the shell and index.html over raw HTTP, and makes and deletes bookmarks. It also
        // renames one movie folder of the shared sandbox media into its hold folder for a detached bookmark
        // (the Not in library filter), as the rows probe does: only inside .sandbox/media and that folder, the move
        // recorded before it is made, undone in a finally and at the next start. It refuses port 8096, reads only the
        // sandbox's own log folder, puts the setting back in a finally, deletes only its own "page-probe" bookmarks
        // (through the bulk delete), never writes the store, and never restarts or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-page-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, workRoot]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("not in the sandbox media or the hold folder", source, StringComparison.Ordinal);
        Assert.Contains(
            "moves.push({ from: src, to: dst });\n    saveMoves(moves);\n    fs.mkdirSync(path.dirname(dst), { recursive: true });\n    fs.renameSync(src, dst);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("undoMoves(0);", source, StringComparison.Ordinal);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${pluginGuid}/Configuration`;", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n        const cleanup = { configurationRestored: null, deleted: [] };", source, StringComparison.Ordinal);
        Assert.Contains("await setConfiguration({ EnableBookmarksPage: original.EnableBookmarksPage });", source, StringComparison.Ordinal);
        Assert.Contains("const PROBE = 'page-probe';", source, StringComparison.Ordinal);
        Assert.Contains(".filter((b) => (b.Name || '').startsWith(PROBE) || (b.Note || '').startsWith(PROBE))", source, StringComparison.Ordinal);
        Assert.Contains("[path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', `${name}.json`, '--out', out]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.renameSync\(").Count);
        Assert.DoesNotContain("fs.unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.rmSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Sandbox", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserRatingsApiProbe_KeepsItsSafetyGuards()
    {
        // The member API's walkthrough. It refuses port 8096 and talks to 127.0.0.1 only; it works only in the
        // sandbox's own database and its own folder under .sandbox; it restarts only through Stop-Sandbox.ps1 and
        // dev-server.ps1 -Background -NoBuild (the newer-version drill, on a copy of the database it puts back, again in a
        // finally when the drill stopped half way); it deletes the API key it makes, puts the plugin's configuration and
        // viewer2's policy back in a finally, deletes every rating it made by its id, and never reads a password.
        var source = File.ReadAllText(Path.Combine(Scripts, "userratings-api-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("import { PRODUCTION_PORT } from './sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("export const PRODUCTION_PORT = 8096;", File.ReadAllText(Path.Combine(Scripts, "sandbox-policy.mjs")), StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [database, workDir]) {\n    if (!inside(p, sandboxRoot) || /programdata/i.test(p)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Contains("if (context.key) await call('admin', 'DELETE', `/Auth/Keys/${context.key}`)", source, StringComparison.Ordinal);
        Assert.Contains("if (context.configuration) await call('admin', 'POST', ", source, StringComparison.Ordinal);
        Assert.Contains("if (context.viewer2Policy) await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy })", source, StringComparison.Ordinal);
        Assert.Contains("if (context.drill && !context.drill.restored) {", source, StringComparison.Ordinal);
        Assert.Contains("for (const left of loadCreated()) await call(left.who, 'DELETE', `/UserRatings/Mine/${left.ratingId}`);", source, StringComparison.Ordinal);
        var scripts = Regex.Matches(source, @"runScript\('([^']+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(6, scripts.Count);
        Assert.All(scripts, s => Assert.Contains(s, new[] { "Stop-Sandbox.ps1", "dev-server.ps1" }));
        Assert.Equal(3, Regex.Matches(source, @"fs\.copyFileSync\(").Count);
        Assert.Equal(5, Regex.Matches(source, @"fs\.rmSync\(").Count);
        Assert.Equal(2, Regex.Matches(source, @"for \(const f of sidecars\(database\)\) fs\.rmSync\(f, \{ force: true \}\);").Count);
        Assert.DoesNotContain("fs.writeFileSync(database", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserRatingsSettingsProbe_KeepsItsSafetyGuards()
    {
        // The Settings API's round trip. It refuses port 8096 and talks to 127.0.0.1 only; it changes the plugin's
        // settings only through its own API and puts the configuration back as it read it, in a finally; it deletes every
        // rating it made by its id, as its owner, in that finally too; it writes no file but its result, and no token into
        // it; it starts, restarts and deploys nothing, and never reads a password.
        var source = File.ReadAllText(Path.Combine(Scripts, "userratings-settings-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("import { PRODUCTION_PORT } from './sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    for (const left of context.made) await call(left.who, 'DELETE', `/UserRatings/Mine/${left.ratingId}`).catch(() => null);\n    if (context.configuration) await call('admin', 'POST', `/Plugins/${PLUGIN}/Configuration`, { raw: context.configuration }).catch(() => null);\n}", source, StringComparison.Ordinal);
        Assert.Contains("context.configuration = await configuration();\n", source, StringComparison.Ordinal);
        Assert.Contains("if (u && u.token && text.includes(u.token)) throw new Error('a token reached the result; nothing written');", source, StringComparison.Ordinal);
        Assert.Equal(["fs.writeFileSync(opts.out, text)"], Regex.Matches(source, @"fs\.writeFileSync\([^;]*\)").Select(m => m.Value));
        Assert.DoesNotContain("fs.rmSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("child_process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Devices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Auth/Keys", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgramData", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserRatingsDataProbe_KeepsItsSafetyGuards()
    {
        // The export and import round trip and the restore. It refuses port 8096 and talks to 127.0.0.1 only; it
        // reads the sandbox's own sandbox.json, credentials.json and its userratings.db and -wal (whether a marker is still
        // there), nothing else; it deletes every rating it made by its id, as its owner, in a finally; it writes no file
        // but its result, and no token into it; it starts, restarts and deploys nothing, and never reads a password.
        var source = File.ReadAllText(Path.Combine(Scripts, "userratings-data-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("import { PRODUCTION_PORT } from './sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("const sandboxRoot = path.join(repoRoot, '.sandbox', opts.sandbox);", source, StringComparison.Ordinal);
        Assert.Contains("const pluginData = path.join(sandboxRoot, 'data', 'data', 'userratings');", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    for (const left of made) await call(left.who, 'DELETE', `/UserRatings/Mine/${left.ratingId}`).catch(() => null);\n}", source, StringComparison.Ordinal);
        Assert.Contains("if (u && u.token && text.includes(u.token)) throw new Error('a token reached the result; nothing written');", source, StringComparison.Ordinal);
        Assert.Equal(["fs.writeFileSync(opts.out, text)"], Regex.Matches(source, @"fs\.writeFileSync\([^;]*\)").Select(m => m.Value));
        Assert.DoesNotContain("fs.rmSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("unlink", source, StringComparison.Ordinal);
        Assert.DoesNotContain("child_process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Devices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Auth/Keys", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        // The database files it reads: the sandbox's own, never under C:\ProgramData.
        Assert.Contains("if (!path.resolve(pluginData).toLowerCase().startsWith(path.resolve(repoRoot, '.sandbox').toLowerCase()) || /programdata/i.test(pluginData)) throw new Error('refusing the data folder');", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserRatingsBusyProbe_KeepsItsSafetyGuards()
    {
        // A separate process holds BEGIN IMMEDIATE on the sandbox's own userratings.db while the probe
        // writes through PUT Mine/Rating (the Debug-only DebugWriteProbe step is gone). It refuses port 8096 and any
        // file outside the sandbox or under C:\ProgramData, never restarts or deploys anything, ends the holder in a finally
        // and deletes the rating it made.
        var source = File.ReadAllText(Path.Combine(Scripts, "userratings-busy-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("import { PRODUCTION_PORT } from './sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:${port}", source, StringComparison.Ordinal);
        Assert.Contains("if (!inside(database, sandboxRoot) || /programdata/i.test(database)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Contains("/Mine/Rating", source, StringComparison.Ordinal);
        Assert.Contains("if (holder && holder.child.exitCode === null) {", source, StringComparison.Ordinal);
        Assert.Contains("await call('DELETE', `/UserRatings/Items/${movie.id}/Mine`)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("const PROBE_STEP", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/ScheduledTasks/Running", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.writeFileSync(database", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.Contains("const queued = await Promise.all([1, 2, 3].map(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserRatingsDetailsProbe_KeepsItsSafetyGuards()
    {
        // Synthetic members of its own (random passwords held in memory only; a run that stopped half way resets
        // them as the admin) rate and review Long Test Movie, with viewer's own row, for the reviews webcheck it runs as
        // viewer2 (webcheck.mjs, the one process it starts); every row is deleted by its owner and the members deleted, also
        // what a stopped run left (its state file). It refuses port 8096, talks to 127.0.0.1 only, never restarts or deploys
        // anything, never reads a password, and writes nothing outside .sandbox/<name>/details-probe.
        var source = File.ReadAllText(Path.Combine(Scripts, "userratings-details-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("import { PRODUCTION_PORT } from './sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("if (!inside(workDir, sandboxRoot) || /programdata/i.test(workDir)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Contains("const secret = crypto.randomBytes(18).toString('base64url');", source, StringComparison.Ordinal);
        Assert.Contains("if (leftovers.users.length || leftovers.viewerRow) await cleanUp('leftovers', movieId);", source, StringComparison.Ordinal);
        Assert.Contains("const outcome = await cleanUp('end', movieId);", source, StringComparison.Ordinal);
        Assert.Contains("outcome.rows.push((await call(u.name, 'DELETE', `/UserRatings/Items/${movieId}/Mine`)).status);", source, StringComparison.Ordinal);
        Assert.Contains("const d = await call('admin', 'DELETE', `/Users/${u.id}`);", source, StringComparison.Ordinal);
        Assert.Contains("outcome.viewer = (await call('viewer', 'DELETE', `/UserRatings/Items/${movieId}/Mine`)).status;", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), ", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.writeFileSync\(").Count);
        Assert.Contains("fs.writeFileSync(stateFile, ", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.rmSync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log(secret", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserRatingsVisibilityProbe_KeepsItsSafetyGuards()
    {
        // Libraries of the probe's own over media copies in its own folder of the sandbox, two users
        // of its own (random passwords held in memory only; a run that stopped half way resets them as the admin), synthetic
        // copies rated, and all of it taken away again, also what a stopped run left (its state file). It refuses port 8096,
        // talks to 127.0.0.1 only, refuses a library name it didn't make, never restarts or deploys anything, never reads a
        // password, and writes nothing outside .sandbox/<name>/visibility-probe. Its --details opens the items
        // in the web client as viewer2 (webcheck.mjs, the one process it starts, with steps it writes into its own folder)
        // with kid's and tagger's limits, and puts viewer2's limits back in a finally and from state.json after a stopped run.
        var source = File.ReadAllText(Path.Combine(Scripts, "userratings-visibility-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("import { PRODUCTION_PORT } from './sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [workDir, mediaDir]) {\n    if (!inside(p, sandboxRoot) || /programdata/i.test(p)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Contains("if (!inside(dst, mediaDir)) throw new Error(`refusing to copy into ${dst}`);", source, StringComparison.Ordinal);
        Assert.Contains("if (existing.includes(name)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Contains("if (leftovers.users.length || leftovers.libraries.length || leftovers.ratings.length || leftovers.viewer2) await cleanUp('leftovers');", source, StringComparison.Ordinal);
        Assert.Contains("const outcome = await cleanUp('end');", source, StringComparison.Ordinal);
        Assert.Contains("for (const name of s.libraries) {", source, StringComparison.Ordinal);
        Assert.Contains("for (const u of s.users) {", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"fs\.copyFileSync\("));
        Assert.Equal(3, Regex.Matches(source, @"fs\.writeFileSync\(").Count);
        Assert.Contains("fs.writeFileSync(stateFile, ", source, StringComparison.Ordinal);
        Assert.Contains("if (!inside(file, workDir) || !inside(out, workDir)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"fs\.rmSync\("));
        Assert.Contains("if (opts.removeMedia) fs.rmSync(mediaDir, { recursive: true, force: true });", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), ", source, StringComparison.Ordinal);
        Assert.Contains("if (loadState().viewer2) outcome.viewer2 = await restoreViewer2();", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n            const back = await restoreViewer2();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("exec(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UserRatingsLifecycleProbe_KeepsItsSafetyGuards()
    {
        // Synthetic media of its own (copies of the seed's clips) in the shared media folder, renamed, moved,
        // copied and deleted there; the seed's Music folder and its artist folders moved away into its own hold folder and
        // back (a run that stopped half way puts them back), never deleted; ratings as the seed's users, every one deleted by
        // its owner; viewer2's library access narrowed for one step and put back, again in a finally. Its other
        // steps: the Music library renamed to the probe's name and back (a run that stopped half way renames it back), a
        // junction made only in the media folder to a folder in the hold folder and removed as a link (never its target),
        // a file of "another drive" written and removed only in the Music folder. It refuses port 8096, talks to 127.0.0.1
        // only, starts no process, restarts and deploys nothing, never touches devices or API keys, never reads a password,
        // and writes no token into its result.
        var source = File.ReadAllText(Path.Combine(Scripts, "userratings-lifecycle-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("import { PRODUCTION_PORT } from './sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [media, workDir, hold, logDir]) {\n    if (!inside(p, sandboxes) || /programdata/i.test(p) || !/^c:/i.test(p)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Contains("if (!inside(p, media)) throw new Error(`refusing ${p}: not in ${media}`);", source, StringComparison.Ordinal);
        Assert.Contains("if (!inside(p, media) && !inside(p, hold)) throw new Error(`refusing to move ${p}: not in ${media} or ${hold}`);", source, StringComparison.Ordinal);
        Assert.Contains("if (!inside(folder, media) || !fs.existsSync(folder)) return;", source, StringComparison.Ordinal);
        Assert.Contains("if (!inside(from, media) || !inside(to, media)) throw new Error(`refusing to copy ${from} to ${to}`);", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    if (context.viewer2Policy) await call('admin', 'POST', `/Users/${users.viewer2.id}/Policy`, { body: context.viewer2Policy })", source, StringComparison.Ordinal);
        Assert.Contains("if (u && u.token && text.includes(u.token)) throw new Error('a token reached the result; nothing written');", source, StringComparison.Ordinal);
        Assert.Equal(["fs.renameSync(from, to)"], Regex.Matches(source, @"fs\.renameSync\([^;]*\)").Select(m => m.Value));
        Assert.Equal(3, Regex.Matches(source, @"fs\.writeFileSync\(").Count);
        Assert.Contains("fs.writeFileSync(createdFile, ", source, StringComparison.Ordinal);
        Assert.Contains("fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Contains("fs.writeFileSync(otherDrive(), ", source, StringComparison.Ordinal);
        Assert.Contains("const otherDrive = () => at('Music', OTHER_DRIVE);", source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, @"fs\.rmSync\(").Count);
        Assert.Contains("const SEED_FOLDERS = [ARTIST, ARTIST_B, OFFLINE];", source, StringComparison.Ordinal);
        Assert.Contains("if (!opts.keepFiles && !SEED_FOLDERS.some((name) => fs.existsSync(path.join(hold, name)))) fs.rmSync(hold, ", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.rmdirSync\(").Count);

        // The junction: made only from the media folder into the hold folder, and only a junction is ever unlinked.
        Assert.Equal(["fs.symlinkSync(target, link, 'junction')"], Regex.Matches(source, @"fs\.symlinkSync\([^;]*\)").Select(m => m.Value));
        Assert.Contains("if (!inside(target, hold) || !inside(link, media)) throw new Error(`refusing a junction at ${link} to ${target}`);\n    fs.symlinkSync(", source, StringComparison.Ordinal);
        Assert.Equal(["fs.unlinkSync(link)", "fs.unlinkSync(otherDrive())"], Regex.Matches(source, @"fs\.unlinkSync\([^;]*?\)(?=;)").Select(m => m.Value));
        Assert.Contains("if (!inside(link, media) || !isJunction(link)) throw new Error(`refusing to unlink ${link}: not a junction in ${media}`);\n    fs.unlinkSync(link);", source, StringComparison.Ordinal);

        // The library rename: Music to the probe's name and back, nothing else in /Library/VirtualFolders written.
        Assert.Equal(
            ["renameLibrary(RENAMED_LIBRARY, 'Music')", "renameLibrary('Music', RENAMED_LIBRARY)", "renameLibrary(RENAMED_LIBRARY, 'Music')"],
            Regex.Matches(source, @"await renameLibrary\(([^)]*)\)").Select(m => m.Value["await ".Length..]));
        Assert.Contains("const RENAMED_LIBRARY = 'Lifecycle Probe Music';", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"'/Library/VirtualFolders/Name'"));
        Assert.DoesNotContain("/Library/VirtualFolders/Paths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'DELETE', '/Library", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Library/VirtualFolders/LibraryOptions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("child_process", source, StringComparison.Ordinal);
        Assert.DoesNotContain("spawnSync(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Auth/Keys", source, StringComparison.Ordinal);
        Assert.DoesNotContain("/Devices", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void KitSqliteRecoveryProbe_KeepsItsSafetyGuards()
    {
        // A refused recovery of a SQLite database that reads well again, through the kit's SQLite demo: it refuses port
        // 8096 and talks to 127.0.0.1 only; it writes only the demo's own synthetic database in the sandbox (a page, then the
        // good copy back in place, again in a finally when it stopped between them) and its own folder; it restarts only
        // through Stop-Sandbox.ps1 and dev-server.ps1 -Background -NoBuild on the sandbox's port; it never deploys, never
        // reads a password, and never prints the admin's token. Its other case stages, with the
        // server stopped, a recovery that couldn't put the database back: only inside the demo's own folder (a quarantine
        // folder with its note, the database moved into it, the journal, an empty folder in the database's place that it
        // removes again, in a finally too).
        var file = Path.Combine(LocalScripts, "kit-sqlite-recovery-probe.mjs");
        Assert.SkipUnless(File.Exists(file), "The kit's SQLite recovery probe isn't in this checkout.");
        var source = File.ReadAllText(file).ReplaceLineEndings("\n");
        Assert.Contains("import { PRODUCTION_PORT } from '../../scripts/sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("const database = path.join(folder, 'kitsqlite-demo.db');", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [database, workDir]) {\n    if (!inside(p, sandboxRoot) || /programdata/i.test(p)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Contains("if (context.corrupted && !context.putBack && context.good) {", source, StringComparison.Ordinal);
        var scripts = Regex.Matches(source, @"runScript\('([^']+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["Stop-Sandbox.ps1", "dev-server.ps1"], scripts);
        Assert.Single(Regex.Matches(source, @"fs\.openSync\(database, 'r\+'\)"));
        Assert.Equal(4, Regex.Matches(source, @"fs\.writeFileSync\(").Count);
        Assert.Contains("fs.writeFileSync(goodFile, good);", source, StringComparison.Ordinal);
        Assert.Contains("fs.writeFileSync(path.join(stagedDir, 'reason.txt'), ", source, StringComparison.Ordinal);
        Assert.Contains("fs.writeFileSync(journal, JSON.stringify(", source, StringComparison.Ordinal);
        Assert.Contains("const journal = database + '.recovery';", source, StringComparison.Ordinal);
        Assert.Contains("const stagedDir = path.join(folder, staged);", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [stagedDir, journal]) {\n        if (!inside(p, folder)) throw new Error(", source, StringComparison.Ordinal);
        Assert.Equal(["fs.renameSync(path.join(folder, n), path.join(stagedDir, n))"], Regex.Matches(source, @"fs\.renameSync\([^;]*\)").Select(m => m.Value));
        Assert.Equal(["fs.mkdirSync(stagedDir)", "fs.mkdirSync(database)"], Regex.Matches(source, @"fs\.mkdirSync\((stagedDir|database)\)").Select(m => m.Value));
        Assert.Equal(2, Regex.Matches(source, @"fs\.rmdirSync\(").Count);
        Assert.Equal(2, Regex.Matches(source, @"fs\.rmdirSync\(database\)").Count);
        Assert.Equal(2, Regex.Matches(source, @"fs\.rmSync\(").Count);
        Assert.DoesNotContain("fs.writeFileSync(database", source, StringComparison.Ordinal);
        Assert.DoesNotContain("console.log(admin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RealServer", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BookmarksClipsAdminProbe_KeepsItsSafetyGuards()
    {
        // The probe acts as admin on viewer2's clips through the admin clip routes and runs the admin webchecks; it
        // sets VideoPreset and AdminsCanManageAllClips for one step each (saved first, put back right after), moves one clip
        // file aside into its work folder, holds clip files open from a PowerShell process, makes an API key and a test user
        // of its own (their secrets in memory only, deleted at the end and at the next start). It refuses port 8096, works
        // only inside the sandbox, records every change before making it, puts all of it back in a finally and at the next
        // start, never writes bookmarks.json, never restarts or deploys anything, and never prints a token or a password.
        var source = File.ReadAllText(Path.Combine(Scripts, "bookmarks-clips-admin-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const origin = `http://127.0.0.1:${port}`;", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [dataFolder, storeFile, clipRoot, workRoot, logDir]) assertInside(p, sandboxRoot);", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [heldRoot, movesFile, savedSettingsFile, testUserFile]) assertInside(p, workRoot);", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to work in ${p}`)", source, StringComparison.Ordinal);

        // The one move: from the sandbox's clip folder into its own held folder, recorded first; undone (or, once the clip it
        // stood for is forgotten, its own copy deleted) in the finally and at the next start.
        Assert.Contains("if (!src.startsWith(clipRoot + path.sep)) throw new Error(`refusing to move ${src}: not in the sandbox's clip folder`);", source, StringComparison.Ordinal);
        Assert.Contains("moves.push({ from: src, to: dst });\n    saveMoves(moves);\n    fs.renameSync(src, dst);", source, StringComparison.Ordinal);
        Assert.Contains("const held = assertInside(to, heldRoot);", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.renameSync\(").Count);

        // The files it deletes: its own copy in held/, its saved settings and its test user's record; nothing in the clip
        // folder (Bookmarks deletes what the admin deletes).
        Assert.Equal(["fs.rmSync(held)", "fs.rmSync(savedSettingsFile)", "fs.rmSync(testUserFile)"], Regex.Matches(source, @"fs\.rmSync\([^)]*\)").Select(m => m.Value));
        Assert.DoesNotContain("fs.unlinkSync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("recursive: true, force", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.writeFileSync(storeFile", source, StringComparison.Ordinal);

        // Each setting saved to a file before it changes and put back right after its step.
        Assert.Single(Regex.Matches(source, Regex.Escape("fs.writeFileSync(savedSettingsFile, JSON.stringify(saved));")));
        Assert.Equal(2, Regex.Matches(source, @"await setConfiguration\(").Count);
        Assert.Contains("    try {\n        return await step();\n    } finally {\n        await restoreSavedSettings();\n    }", source, StringComparison.Ordinal);
        Assert.Contains("    undoMoves();\n    await restoreSavedSettings();\n    evidence.leftovers.apiKeys = await deleteApiKeys();\n    evidence.leftovers.testUsers = await deleteTestUser();", source, StringComparison.Ordinal);

        // The secrets: the test user's password made here and never kept, its token and the API key in memory only, never
        // written or printed; the test user's file holds its id and name only.
        Assert.Contains("const password = crypto.randomBytes(18).toString('base64url');", source, StringComparison.Ordinal);
        Assert.Contains("fs.writeFileSync(testUserFile, JSON.stringify({ id: made.json.Id, name: TEST_USER }));", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, Regex.Escape("secrets.testUserToken = answer.AccessToken;")));
        Assert.Single(Regex.Matches(source, Regex.Escape("secrets.apiKey = mine[0].AccessToken;")));
        Assert.DoesNotContain("console.log(secrets", source, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence.secrets", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON.stringify(secrets", source, StringComparison.Ordinal);

        // No restarts, no deploys, nothing of the real server.
        Assert.DoesNotContain("runScript(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deploy-Plugin", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dev-server.ps1", source, StringComparison.Ordinal);
        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".password", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessRolesProbe_KeepsItsSafetyGuards()
    {
        // The probe only reads a running access sandbox, its files under .sandbox and its API on the loopback, and
        // with --webcheck runs accessroles-settings.json through webcheck.mjs, which only reads the page. It refuses port
        // 8096, writes nothing but its own --out file, prints no token, and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to read ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("[path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'accessroles-settings.json']", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        foreach (var change in new[] { "fs.renameSync", "fs.rmSync", "fs.unlinkSync", "fs.mkdirSync", "fs.copyFileSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);

        var webcheck = File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-settings.json"));
        // The status tables are on the Diagnostics tab: the check's one click opens it (a tab writes nothing).
        Assert.Equal(["#AccessRolesConfigPage:not(.hide) #AccessRolesTabDiagnostics"], Regex.Matches(webcheck, "\"click\": \"([^\"]*)\"").Select(m => m.Groups[1].Value));
        Assert.DoesNotContain("\"type\"", webcheck, StringComparison.Ordinal);
        Assert.DoesNotContain("updatePluginConfiguration", webcheck, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessRolesRolesProbe_KeepsItsSafetyGuards()
    {
        // The walkthrough changes a running access sandbox only through Access Roles' admin API on the loopback
        // (/AccessRoles/Admin/...; every other request is a GET), apart from its own two synthetic users,
        // reads the sandbox's files under .sandbox, refuses port 8096, writes nothing but its own --out file, prints no token
        // (the transcript names it <user>) or password, starts no process, and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-roles-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to read ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("Token=<${user}>", source, StringComparison.Ordinal);
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        foreach (var change in new[] { "spawnSync(", "child_process", "fs.renameSync", "fs.rmSync", "fs.unlinkSync", "fs.mkdirSync", "fs.copyFileSync", "appendFileSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password", "/Policy'" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // Every request that isn't a GET goes to Access Roles' admin API, except three, each once: creating, giving a
        // native library access to, and deleting the probe's own users, behind the guards that refuse any other user.
        var userWrites = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step|request)\((?:`[^`]*`|'[^']*'|\w+), '(POST|PUT|DELETE)', [`']([^`']*)")
            .Concat(Regex.Matches(source, @"request\('(POST|PUT|DELETE)', [`']([^`']*)")))
        {
            if (!call.Groups[2].Value.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                userWrites.Add(call.Groups[1].Value + " " + call.Groups[2].Value);
            }
        }

        Assert.Equal(["DELETE /Users/${id}", "POST /Users/${id}/Policy", "POST /Users/New"], userWrites.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(3, userWrites.Count);

        // One more write, the plugin's own setting: the library sync paused for the run (LibrarySyncEnabled),
        // the rest of the settings as read, and the setting put back in a finally.
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${plugin.guid}/Configuration`;", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"request\('POST', CONFIGURATION_PATH, 'admin', \{ \.\.\.\(await getConfiguration\(\)\), LibrarySyncEnabled: enabled \}\)"));
        Assert.Contains("} finally {\n    // The library sync's switch as the probe found it.\n    if (librarySyncToRestore !== null) {\n        try {\n            await setLibrarySync(librarySyncToRestore);", source, StringComparison.Ordinal);
        Assert.Contains("const PROBE_USER = 'rolesprobe-';", source, StringComparison.Ordinal);
        Assert.Contains(
            "    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);\n    const r = await request('POST', '/Users/New', 'admin', { Name: name, Password: crypto.randomBytes(24).toString('base64url') });",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "    if (!own.has(ids([id])[0])) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);\n    const r = method === 'DELETE' ? await request('DELETE', `/Users/${id}`, 'admin') : await request('POST', `/Users/${id}/Policy`, 'admin', policy);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);

        // Only users the probe created, or an earlier run's (by their rolesprobe- name), are its own.
        Assert.Equal(2, Regex.Matches(source, @"own\.set\(").Count);
        Assert.Contains("const leftover = ((await request('GET', '/Users', 'admin')).json || []).filter((u) => u.Name.startsWith(PROBE_USER));", source, StringComparison.Ordinal);
        Assert.Contains("own.set(ids([r.json.Id])[0], name);", source, StringComparison.Ordinal);
        Assert.Contains("own.set(ids([u.Id])[0], u.Name);", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessRolesSyncProbe_KeepsItsSafetyGuards()
    {
        // The sync probe changes a running access sandbox only through Access Roles' admin API and what is its
        // own: its users (syncprobe-..., created with a random password kept in memory for the race's sign-ins, their policy
        // set natively, deleted), its library (named "Sync probe ...", over an empty folder under .sandbox/<name>/syncprobe,
        // added, removed, renamed), the plugin's own settings (put back in a finally) and its SAFE-MODE file (removed in a
        // finally). It reads the sandbox's database read-only without the password column, refuses port 8096, prints no
        // token or password, starts no process, and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-sync-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [storeFile, auditFolder, safeModeFile, dbFile, libraryFolder, path.join(sandboxRoot, 'log')]) assertInSandbox(p);", source, StringComparison.Ordinal);
        Assert.Contains("Token=<admin>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "spawnSync(", "child_process", "fs.renameSync", "fs.rmSync", "fs.copyFileSync", "appendFileSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password", "INSERT ", "UPDATE ", "DELETE FROM" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // Files: its --out, and the SAFE-MODE file in the sandbox's data folder (removed again); one folder for its library.
        Assert.Equal(2, Regex.Matches(source, @"writeFileSync\(").Count);
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Contains("    const file = assertInSandbox(safeModeFile);\n    if (on) fs.writeFileSync(file, 'accessroles-sync-probe\\n');\n    else if (fs.existsSync(file)) fs.unlinkSync(file);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"unlinkSync\("));
        Assert.Single(Regex.Matches(source, @"mkdirSync\("));
        Assert.Contains("fs.mkdirSync(assertInSandbox(libraryFolder), { recursive: true });", source, StringComparison.Ordinal);
        Assert.Contains("const libraryFolder = path.join(sandboxRoot, 'syncprobe', 'library');", source, StringComparison.Ordinal);

        // The database: read only, never the password.
        Assert.Contains("new sqlite.DatabaseSync(assertInSandbox(dbFile), { readOnly: true })", source, StringComparison.Ordinal);
        Assert.Contains(".filter((n) => n !== 'Password')", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or one of these, behind the guards below.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'[^']*'), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Library/VirtualFolders?name=${encodeURIComponent(name)}&refreshLibrary=true",
                "DELETE /Users/${id}",
                "POST /Library/VirtualFolders/Name?name=${encodeURIComponent(name)}&newName=${encodeURIComponent(newName)}&refreshLibrary=true",
                "POST /Users/${id}/Policy",
                "POST /Users/AuthenticateByName",
                "POST /Users/New",
                "POST CONFIGURATION_PATH",
                "POST query"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(8, writes.Count);
        Assert.Contains("const query = `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=homevideos&paths=${encodeURIComponent(libraryFolder)}&refreshLibrary=true`;", source, StringComparison.Ordinal);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${plugin.guid}/Configuration`;", source, StringComparison.Ordinal);

        // The guards: only its own users, only its own library names.
        Assert.Contains("const PROBE_USER = 'syncprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Sync probe ';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name)) throw new Error(`refusing to add the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name)) throw new Error(`refusing to remove the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name) || !isOwnLibrary(newName)) throw new Error(`refusing to rename the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"own\.set\(").Count);
        Assert.Contains("own.set(idN(r.json.Id), name);", source, StringComparison.Ordinal);
        Assert.Contains("for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.startsWith(PROBE_USER))) {\n        own.set(idN(u.Id), u.Name);", source, StringComparison.Ordinal);

        // Passwords: random, in memory, only in the sign-in's body.
        Assert.Contains("const password = crypto.randomBytes(24).toString('base64url');", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"secrets\.get\("));
        Assert.Contains("return request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password }, deviceId);", source, StringComparison.Ordinal);

        // The settings and SAFE-MODE as found, in a finally.
        Assert.Contains("} finally {\n    // Everything of the probe's goes, and the settings and SAFE-MODE are as the probe found them.\n    const cleanupResult = { steps: [] };\n    try {\n        setSafeMode(false);\n        await cleanup('cleanup');\n        if (configurationToRestore) await setConfiguration(configurationToRestore);", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessRolesSyncReviewProbe_KeepsItsSafetyGuards()
    {
        // The sync race probe changes a running access sandbox only through Access Roles' admin API and what
        // is its own: its users (syncrace-..., random passwords and their sign-in tokens kept in memory, their policies set
        // natively, deleted), its libraries (named "Sync race probe ...", over empty folders under .sandbox/<name>/syncrace,
        // added, removed, renamed with and without refreshLibrary), a library scan, and the plugin's own settings (put back in
        // a finally). It reads the sandbox's database read-only without the password column, refuses port 8096, prints no
        // token or password, starts no process, and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-sync-review-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [dbFile, secretFolder, libraryFolder, path.join(sandboxRoot, 'log')]) assertInSandbox(p);", source, StringComparison.Ordinal);
        Assert.Contains("Token=<admin>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "spawnSync(", "child_process", "fs.renameSync", "fs.rmSync", "fs.copyFileSync", "appendFileSync", "unlinkSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", "SAFE-MODE", ".password", "INSERT ", "UPDATE ", "DELETE FROM" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // Files: its --out only; one folder per library, both under .sandbox/<name>/syncrace.
        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"mkdirSync\("));
        Assert.Contains("fs.mkdirSync(assertInSandbox(folder), { recursive: true });", source, StringComparison.Ordinal);
        Assert.Contains("const secretFolder = path.join(sandboxRoot, 'syncrace', 'secret');", source, StringComparison.Ordinal);
        Assert.Contains("const libraryFolder = path.join(sandboxRoot, 'syncrace', 'library');", source, StringComparison.Ordinal);

        // The database: read only, never the password.
        Assert.Contains("new sqlite.DatabaseSync(assertInSandbox(dbFile), { readOnly: true })", source, StringComparison.Ordinal);
        Assert.Contains(".filter((n) => n !== 'Password')", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or one of these, behind the guards below.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'[^']*'), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Library/VirtualFolders?name=${encodeURIComponent(name)}&refreshLibrary=true",
                "DELETE /Users/${id}",
                "POST /Library/Refresh",
                "POST /Library/VirtualFolders/Name?name=${encodeURIComponent(name)}&newName=${encodeURIComponent(newName)}&refreshLibrary=${refresh ? 'true' : 'false'}",
                "POST /Users/${id}/Policy",
                "POST /Users/AuthenticateByName",
                "POST /Users/New",
                "POST CONFIGURATION_PATH",
                "POST query"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(9, writes.Count);
        Assert.Contains("const query = `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=homevideos&paths=${encodeURIComponent(assertInSandbox(folder))}&refreshLibrary=true`;", source, StringComparison.Ordinal);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${plugin.guid}/Configuration`;", source, StringComparison.Ordinal);

        // The guards: only its own users, only its own library names.
        Assert.Contains("const PROBE_USER = 'syncrace-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Sync race probe ';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name)) throw new Error(`refusing to add the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name)) throw new Error(`refusing to remove the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name) || !isOwnLibrary(newName)) throw new Error(`refusing to rename the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"own\.set\(").Count);
        Assert.Contains("own.set(idN(r.json.Id), name);", source, StringComparison.Ordinal);
        Assert.Contains("for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.startsWith(PROBE_USER))) {\n        own.set(idN(u.Id), u.Name);", source, StringComparison.Ordinal);

        // Passwords and the users' tokens: random or the sign-in's, in memory, never in the transcript or the result.
        Assert.Contains("const password = crypto.randomBytes(24).toString('base64url');", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"secrets\.get\("));
        Assert.Contains("const r = await request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password }, `syncrace-${idN(user.Id).slice(0, 8)}`);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"AccessToken\);"));
        Assert.Contains("tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.get\("));

        // The settings as found, in a finally.
        Assert.Contains("} finally {\n    // Everything of the probe's goes, and the settings are as the probe found them.\n    const cleanupResult = {};\n    try {\n        await cleanup('cleanup');\n        if (configurationToRestore) await setConfiguration(configurationToRestore);", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessRolesSyncHooksProbe_KeepsItsSafetyGuards()
    {
        // The probe changes a running access sandbox only through Access Roles' admin API and what is its own: its
        // users (hook-..., random passwords and their sign-in tokens kept in memory, their policies set as an admin's
        // Dashboard save, deleted), its roles ("Hook probe ..."), the default roles (none again in a finally), the plugin's
        // own settings (put back in a finally), and the import's records (its roles, and assignments of the sandbox's own
        // users, which write no policy and are taken out with Keep). It reads the database read-only without the password
        // column, refuses port 8096, prints no token or password, runs webcheck.mjs once with its own steps file (which only
        // clicks the import's buttons and the dashboard's confirmation), and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-sync-hooks-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [dbFile, path.join(sandboxRoot, 'log')]) assertInSandbox(p);", source, StringComparison.Ordinal);
        Assert.Contains("Token=<admin>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "fs.renameSync", "fs.rmSync", "fs.copyFileSync", "appendFileSync", "unlinkSync", "mkdirSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", "SAFE-MODE", ".password", "INSERT ", "UPDATE ", "DELETE FROM", "/Library/VirtualFolders?", "/Library/Refresh" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // One process: webcheck.mjs with the probe's own steps file.
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'accessroles-import.json']", source, StringComparison.Ordinal);

        // Files: its --out only.
        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);

        // The database: read only, never the password.
        Assert.Contains("new sqlite.DatabaseSync(assertInSandbox(dbFile), { readOnly: true })", source, StringComparison.Ordinal);
        Assert.Contains(".filter((n) => n !== 'Password')", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or one of these, behind the guards below.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'[^']*'), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Users/${id}",
                "POST /Users/${id}/Policy",
                "POST /Users/AuthenticateByName",
                "POST /Users/New",
                "POST CONFIGURATION_PATH"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(5, writes.Count);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${plugin.guid}/Configuration`;", source, StringComparison.Ordinal);

        // The guards: only its own users and roles.
        Assert.Contains("const PROBE_USER = 'hook-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Hook probe ';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"own\.set\(").Count);
        Assert.Contains("own.set(idN(r.json.Id), name);", source, StringComparison.Ordinal);
        Assert.Contains("for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.startsWith(PROBE_USER))) {\n        own.set(idN(u.Id), u.Name);", source, StringComparison.Ordinal);

        // Passwords and the users' tokens: random or the sign-in's, in memory, never in the transcript or the result.
        Assert.Contains("const password = crypto.randomBytes(24).toString('base64url');", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"secrets\.get\("));
        Assert.Single(Regex.Matches(source, @"AccessToken\);"));
        Assert.Contains("tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.get\("));
        Assert.Contains("return r.json.User;", source, StringComparison.Ordinal);

        // The default roles and the settings as found, in a finally.
        Assert.Contains("await step(`${label}: no default roles`, 'PUT', '/AccessRoles/Admin/DefaultRoles', { RoleIds: [] });", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    // Everything of the probe's goes, and the settings are as the probe found them.\n    const cleanupResult = {};\n    try {\n        await cleanup('cleanup');\n        if (configurationToRestore) await setConfiguration(configurationToRestore);", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);

        // Its webcheck clicks only the import's two buttons and the confirmation (since 0.9.8 the kit's dialog, whose
        // title it waits for), never the every-library box, and types nothing.
        var webcheck = File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-import.json"));
        var clicks = Regex.Matches(webcheck, "\"click\": \"([^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(
            ["#AccessRolesConfigPage:not(.hide) #AccessRolesImportPreview", "#AccessRolesConfigPage:not(.hide) #AccessRolesImportApply", ".jfar-backdrop .jfar-panel .jfar-button-primary"],
            clicks);
        Assert.Contains("p.querySelector('h3').textContent === 'Import current setup'", webcheck, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", webcheck, StringComparison.Ordinal);
        Assert.DoesNotContain("updatePluginConfiguration", webcheck, StringComparison.Ordinal);
        Assert.DoesNotContain("ajax", webcheck, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessRolesSyncHooksRaceProbe_KeepsItsSafetyGuards()
    {
        // The race probe changes a running access sandbox only through Access Roles' admin API and what
        // is its own: its users (hookrace-..., random passwords and their sign-in tokens kept in memory, their policies set as
        // an admin's Dashboard save, deleted), its role ("Hook race probe ..."), the default roles (none again in a finally), the
        // plugin's own settings (put back in a finally), and the restore hold's file in the sandbox's Access Roles folder
        // (deleted in a finally). Its one process, the locker, opens the sandbox's database only (the asserted path), takes
        // BEGIN IMMEDIATE and ends with ROLLBACK, writing no row. It reads the database read-only without the password column,
        // keeps nothing of /Devices but whether a device id is listed, refuses port 8096, prints no token or password, and
        // never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-sync-hooks-race-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("for (const p of [dbFile, holdFile, path.join(sandboxRoot, 'log')]) assertInSandbox(p);", source, StringComparison.Ordinal);
        Assert.Contains("const dbFile = path.join(dataDir, 'data', 'jellyfin.db');", source, StringComparison.Ordinal);
        Assert.Contains("const holdFile = path.join(dataDir, 'data', 'accessroles', 'library-sync-hold.json');", source, StringComparison.Ordinal);
        Assert.Contains("Token=<admin>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "spawnSync(", "execSync", "execFile", "fork(", "fs.renameSync", "fs.rmSync", "fs.copyFileSync", "appendFileSync", "mkdirSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", "SAFE-MODE", ".password", "INSERT ", "UPDATE ", "DELETE FROM", "COMMIT", "/Library/VirtualFolders?", "/Library/Refresh" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // One process: the locker, node itself on the inline module below, with the sandbox's database.
        Assert.Single(Regex.Matches(source, @"spawn\("));
        Assert.Contains("const child = spawn(process.execPath, ['--no-warnings', '--input-type=module', '-e', LOCKER, assertInSandbox(dbFile), name, String(holdMs)], { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to lock for ${name}`);", source, StringComparison.Ordinal);
        var locker = Regex.Match(source, @"const LOCKER = \[\n(?<body>.*?)\n\]\.join\('\\n'\);", RegexOptions.Singleline).Groups["body"].Value;
        Assert.NotEmpty(locker);
        Assert.Equal(
            ["BEGIN IMMEDIATE", "ROLLBACK", "SELECT Id FROM ActivityLogs WHERE UserId = ? AND Type = 'UserCreated'", "SELECT Id FROM Users WHERE Username = ?"],
            Regex.Matches(locker, @"(?:db\.exec|db\.prepare)\((?:'(?<sql>[^']*)'|\\""(?<sql>[^""\\]*)\\"")\)").Select(m => m.Groups["sql"].Value).Order(StringComparer.Ordinal));
        Assert.Equal(4, Regex.Matches(locker, @"db\.(?:exec|prepare)\(").Count);

        // The lock waits for Jellyfin's UserCreated activity entry, so Access Roles' consumer (the event's
        // next one) has marked the user before the manual sync, which must count them as managed and being created.
        Assert.Contains("    'while (!logged && Date.now() - seen < 10000) { try { logged = created.get(row.Id); } catch { logged = null; } if (!logged) sleep(1); }',", source, StringComparison.Ordinal);
        Assert.Contains("r.assignedAtManualSync && r.beingCreatedInManualSync && !r.writtenByManualSync", source, StringComparison.Ordinal);
        Assert.Contains("'const db = new DatabaseSync(file);',", locker, StringComparison.Ordinal);
        Assert.Contains("const sleep = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);", locker, StringComparison.Ordinal);
        Assert.Contains("fs.writeSync(1, ", locker, StringComparison.Ordinal);
        Assert.DoesNotContain("writeFileSync", locker, StringComparison.Ordinal);
        Assert.Contains("const HOLD_MS = 4000;", source, StringComparison.Ordinal);
        Assert.Contains("for (const child of lockers) child.kill();", source, StringComparison.Ordinal);

        // Files: its --out, and the hold file, written and deleted only by these two functions.
        Assert.Equal(2, Regex.Matches(source, @"writeFileSync\(").Count);
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Contains("    fs.writeFileSync(assertInSandbox(holdFile), JSON.stringify({ Reason: 'StoreRestored', ", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"unlinkSync\("));
        Assert.Contains("        fs.unlinkSync(assertInSandbox(holdFile));", source, StringComparison.Ordinal);
        Assert.Contains("        cleanupResult.holdFileRemoved = removeHoldFile();", source, StringComparison.Ordinal);

        // The database: read only, never the password.
        Assert.Contains("new sqlite.DatabaseSync(assertInSandbox(dbFile), { readOnly: true })", source, StringComparison.Ordinal);
        Assert.Contains(".filter((n) => n !== 'Password')", source, StringComparison.Ordinal);

        // /Devices: only whether the device id of a probe's user is listed (its answer holds tokens).
        Assert.Contains("const hasDevice = async (user) => ((((await request('GET', '/Devices', 'admin')).json || {}).Items) || []).some((d) => d.Id === deviceOf(user));", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"'/Devices'"));

        // Every request that isn't a GET: Access Roles' admin API, or one of these, behind the guards below.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'[^']*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Users/${id}",
                "POST /Users/${id}/Policy",
                "POST /Users/AuthenticateByName",
                "POST /Users/New",
                "POST CONFIGURATION_PATH"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(5, writes.Count);
        Assert.Contains("const CONFIGURATION_PATH = `/Plugins/${plugin.guid}/Configuration`;", source, StringComparison.Ordinal);

        // The guards: only its own users and role.
        Assert.Contains("const PROBE_USER = 'hookrace-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Hook race probe ';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"own\.set\(").Count);
        Assert.Contains("        own.set(idN(r.json.Id), name);", source, StringComparison.Ordinal);
        Assert.Contains("for (const u of ((await request('GET', '/Users', 'admin')).json || []).filter((x) => x.Name.startsWith(PROBE_USER))) {\n        own.set(idN(u.Id), u.Name);", source, StringComparison.Ordinal);

        // Passwords and the users' tokens: random or the sign-in's, in memory, never in the transcript or the result; the
        // empty password only to show it is refused.
        Assert.Contains("const password = crypto.randomBytes(24).toString('base64url');", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"secrets\.get\("));
        Assert.Contains("    const password = withPassword ? secrets.get(idN(user.Id)) : '';", source, StringComparison.Ordinal);
        Assert.Contains("const r = await request('POST', '/Users/AuthenticateByName', null, { Username: user.Name, Pw: password }, deviceOf(user));", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"AccessToken\);"));
        Assert.Contains("tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.get\("));

        // The default roles, the hold file and the settings as found, in a finally.
        Assert.Contains("    await setDefaultRoles(`${label}: no default roles`, []);", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    // Everything of the probe's goes, and the settings are as the probe found them.\n    const cleanupResult = {};\n    try {\n        for (const child of lockers) child.kill();\n        cleanupResult.holdFileRemoved = removeHoldFile();\n        await cleanup('cleanup');\n        if (configurationToRestore) await setConfiguration(configurationToRestore);", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessRolesWebGuardProbe_KeepsItsSafetyGuards()
    {
        // The web guard probe changes a running access sandbox only through Access Roles' admin API: its two
        // lock-only roles ("Guard probe ..."), viewer's and viewer2's assignments (viewer2's with no library, unmanaged with
        // Restore afterwards) and the default roles (back as they were). It runs webcheck.mjs on its own steps files, refuses
        // port 8096, prints no token, never starts, stops or deploys anything, and writes its --out only.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-web-guard-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Guard probe ';", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "fs.renameSync", "fs.rmSync", "fs.unlinkSync", "fs.copyFileSync", "appendFileSync", "mkdirSync", "RealServer", "Deploy-Plugin", "Stop-Sandbox", "SAFE-MODE", ".password", "/System/Restart", "/System/Shutdown", "/Users/New", "/Policy" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // The admin's token only in the Authorization header; the output is the result (and the usage).
        Assert.Single(Regex.Matches(source, @"credentials\.admin\.token"));
        Assert.Equal(["console.log(text)", "console.log(usage())"], Regex.Matches(source, @"console\.log\([^;]*\)").Select(m => m.Value).Order(StringComparer.Ordinal));

        // One process: webcheck.mjs on the probe's own steps files (accessroles-web-guard-*.json).
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("const run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file], { encoding: 'utf8', timeout: 300000 });", source, StringComparison.Ordinal);
        var files = Regex.Matches(source, @"file: '([^']+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(9, files.Count);
        Assert.All(files, f => Assert.Matches(@"^accessroles-web-guard-[a-z-]+\.json$", f));
        Assert.All(files, f => Assert.True(File.Exists(Path.Combine(Scripts, "webchecks", f)), f));

        // Files: its --out only.
        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);

        // Every request that isn't a GET goes to Access Roles' admin API, but for viewer's usersettings display preferences,
        // which the admin writes twice: without their Display (account) keys (one check needs a user who
        // never stored them), and back as they were; and viewer's and viewer2's configuration, which the admin writes back
        // exactly as it was read when it differs (jellyfin-web's forms post "" for a text never
        // set), after the cleanup.
        var writes = Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\([^)]*\))?)")
            .Select(m => m.Groups[2].Value.Trim('`', '\''))
            .ToList();
        Assert.NotEmpty(writes);
        const string ViewerPrefs = "/DisplayPreferences/usersettings?userId=${users.viewer.Id}&client=emby";
        const string ConfigBack = "/Users/Configuration?userId=${users[name].Id}";
        Assert.All(writes.Where(w => w != ViewerPrefs && w != ConfigBack), w => Assert.StartsWith("/AccessRoles/Admin/", w, StringComparison.Ordinal));
        Assert.Equal(2, writes.Count(w => w == ViewerPrefs));
        Assert.Equal(1, writes.Count(w => w == ConfigBack));
        Assert.Contains("        for (const name of ['viewer', 'viewer2']) {", source, StringComparison.Ordinal);
        Assert.Contains("                if (now && stable(now) !== before.config[name]) {\n                    const r = await step(`${name}'s configuration back exactly as it was (the admin)`, 'POST', `" + ConfigBack + "`, before.configuration[name]);", source, StringComparison.Ordinal);
        Assert.Contains("        const r = await step('viewer\\'s usersettings display preferences without the Display (account) keys (the review\\'s precondition)', 'POST', `" + ViewerPrefs + "`, without);", source, StringComparison.Ordinal);
        Assert.Contains("            const r = await step('viewer\\'s usersettings display preferences back as they were', 'POST', `" + ViewerPrefs + "`, viewerPrefsBefore);", source, StringComparison.Ordinal);
        Assert.Contains("viewerPrefs: viewerPrefsBefore === null || stable(prefsAfter) === stable(viewerPrefsBefore.CustomPrefs),", source, StringComparison.Ordinal);

        // The webchecks write nothing but through jellyfin-web as viewer: its own ApiClient or its settings pages' forms,
        // whose saves the merges keep or which they put back.
        foreach (var file in files)
        {
            var steps = File.ReadAllText(Path.Combine(Scripts, "webchecks", file));
            Assert.DoesNotContain("8096", steps, StringComparison.Ordinal);
            Assert.DoesNotContain("deleteUser", steps, StringComparison.Ordinal);
            Assert.DoesNotContain("updateUserPolicy", steps, StringComparison.Ordinal);
        }

        // A webcheck is run again only when its first sign-in failed (a later one is what it tests).
        Assert.Contains("        if (!signInFlake(result)) break;", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AccessRolesWebGuardProbe_RunsAWebcheckAgain_OnlyWhenItsFirstSignInFailed()
    {
        // The first-load webcheck's second sign-in (viewer2's, without a reload) is what it tests;
        // a failure there, or in any step but the first sign-in, must stand.
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");
        var source = (await File.ReadAllTextAsync(Path.Combine(Scripts, "accessroles-web-guard-probe.mjs"), TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
        var function = Regex.Match(source, @"^function signInFlake\(result\) \{\n.*?\n\}$", RegexOptions.Singleline | RegexOptions.Multiline).Value;
        Assert.NotEmpty(function);
        const string Cases = """
            const login = (ok) => ({ action: 'login', ok });
            const step = (ok) => ({ action: 'eval', ok });
            const cases = [
                [{ steps: [login(false)] }, true],
                [{ steps: [step(true), login(false)] }, true],
                [{ steps: [login(true), step(true), login(false)] }, false],
                [{ steps: [login(true), step(false)] }, false],
                [{ steps: [login(true), step(true)] }, false],
                [{ steps: [] }, false],
                [null, false]
            ];
            const wrong = cases.map(([r, expected], i) => [i, signInFlake(r), expected]).filter(([, got, expected]) => got !== expected);
            process.stdout.write(wrong.length === 0 ? 'ok' : 'wrong: ' + JSON.stringify(wrong));
            """;
        var run = await NodeRunner.RunAsync(node!, ["-e", function + "\n" + Cases], cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("ok", run.StandardOutput.Trim());
    }

    [Fact]
    public void AccessRolesSettingsLocksProbe_KeepsItsSafetyGuards()
    {
        // The settings probe changes a running access sandbox only through Access Roles' admin API and what is its
        // own: its users (lockprobe-..., random passwords and their sign-in tokens kept in memory; their passwords, images,
        // names and settings changed as themselves and written back by the admin; deleted), its role ("Lock probe ..."), its
        // API key (lockprobe-key, only its own token kept from the keys' listing, revoked in a finally), and the webcheck
        // user viewer's assignment (lock-only, removed in a finally; viewer's settings written back only if a merge failed to
        // keep them). It reads the sandbox's log only, runs webcheck.mjs on its own steps file, refuses port 8096, prints no
        // token or password, never sends a restart or a remote search, and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-settings-locks-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));", source, StringComparison.Ordinal);
        Assert.Contains("Token=<admin>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "fs.renameSync", "fs.rmSync", "fs.unlinkSync", "fs.copyFileSync", "appendFileSync", "mkdirSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", "SAFE-MODE", ".password", "/System/Restart", "/System/Shutdown", "/Items/RemoteSearch", "/Library/VirtualFolders?", "/Library/Refresh", "sqlite" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // One process: webcheck.mjs on the probe's own steps file.
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', 'accessroles-settings-locks.json'], { encoding: 'utf8', timeout: 300000 });", source, StringComparison.Ordinal);

        // Files: its --out only.
        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or one of these, behind the guards below.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'[^']*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\.\w+)?(?:\([^)]*\))?)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Auth/Keys/${key.AccessToken}",
                "DELETE /Users/${id}",
                "POST /Auth/Keys?app=${PROBE_KEY}",
                "POST /JellyfinEnhanced/spoiler-blur/user-prefs",
                "POST /JellyfinEnhanced/user-settings/${u.Id}/bookmark.json",
                "POST /JellyfinEnhanced/user-settings/${u.Id}/settings.json",
                "POST /QuickConnect/Authorize?code=000000",
                "POST /Users/${id}/Policy",
                "POST /Users/${u.Id}",
                "POST /Users/${u.Id}/Password",
                "POST /Users/AuthenticateByName",
                "POST /Users/Configuration",
                "POST /Users/Configuration?userId=${id}",
                "POST /Users/Configuration?userId=${lockedUser.Id}",
                "POST /Users/New",
                "POST /Users/Password",
                "POST /Users?userId=${u.Id}",
                "POST prefsPath(id, document, client)",
                "POST prefsPath(lockedUser.Id)",
                "POST prefsPath(u.Id, document, client)",
                "POST route.replace('{id}', u.Id)"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));

        // The image routes, each of the probe's users on its own image, by turns (method, path, body).
        Assert.Contains(
            "                    ['POST', '/UserImage', PNG], ['DELETE', `/Users/${u.Id}/Images/Primary/0`, null],\n                    ['POST', `/Users/${u.Id}/Images/Primary`, PNG], ['DELETE', `/Users/${u.Id}/Images/Primary`, null],\n                    ['POST', `/Users/${u.Id}/Images/Primary/0`, PNG], ['DELETE', '/UserImage', null]]) {\n                    const answer = await request(method, p, as(who), body, 'image/png');",
            source,
            StringComparison.Ordinal);
        // request(method, …) elsewhere: its own definition, and the admin's step().
        Assert.Equal(3, Regex.Matches(source, @"request\(method, ").Count);
        Assert.Contains("function request(method, p, user = 'admin', body = null, contentType = 'application/json') {", source, StringComparison.Ordinal);
        Assert.Contains("    const r = await request(method, p, 'admin', body);", source, StringComparison.Ordinal);

        // The requests as a probe's user go with that user's own token, only ever set at its sign-in; `as` names the three.
        Assert.Contains("const users = { locked: lockedUser, open: openUser, admin: adminUser };\n    const as = (who) => idN(users[who].Id);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.set\("));
        Assert.Contains("    tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);
        Assert.Contains("    const password = secrets.get(idN(user.Id));\n    if (!password) throw new Error(`no password for ${user.Name}`);", source, StringComparison.Ordinal);

        // The guards: only its own users, and viewer's settings only to write them back.
        Assert.Contains("const PROBE_USER = 'lockprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Lock probe ';", source, StringComparison.Ordinal);
        Assert.Contains("const PROBE_KEY = 'lockprobe-key';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id)) && idN(id) !== idN(credentials[WEBCHECK_USER] && credentials[WEBCHECK_USER].id)) throw new Error(`refusing to write the settings of ${id}`);", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"assertOwnOrWebcheck\(id\);").Count);
        Assert.Equal(2, Regex.Matches(source, @"own\.set\(").Count);
        Assert.Contains("const WEBCHECK_USER = 'viewer';", source, StringComparison.Ordinal);
        Assert.Contains("        if (!keptAll) {\n            // A merge failed to keep viewer's settings: write them back as they were.", source, StringComparison.Ordinal);

        // The API key: only the probe's own token kept from the listing, and only the probe's keys revoked.
        Assert.Contains("    const keys = (((await request('GET', '/Auth/Keys', 'admin')).json || {}).Items || []).filter((k) => k.AppName === PROBE_KEY);\n    apiKey = keys.length ? keys[keys.length - 1].AccessToken : null;", source, StringComparison.Ordinal);
        Assert.Contains("    for (const key of (((await request('GET', '/Auth/Keys', 'admin')).json || {}).Items || []).filter((k) => k.AppName === PROBE_KEY)) {", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    const cleanupResult = {};\n    try {\n        await cleanup('cleanup');", source, StringComparison.Ordinal);
        Assert.Contains("    await revokeKeys();\n}", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);

        // The webcheck: viewer only reads, but for the routes it types, two saves of its own settings through jellyfin-web's
        // ApiClient (the settings pages redirect, so their forms can't be used), which the merges keep, and
        // the Home screen and Display pages' own form saves with the guard off for its page (an init script
        // that makes the head's flags say enforcement is off; the server still merges), then the document put back.
        var webcheck = File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-settings-locks.json"));
        Assert.DoesNotContain("\"click\"", webcheck, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\"", webcheck, StringComparison.Ordinal);
        Assert.DoesNotContain("updatePluginConfiguration", webcheck, StringComparison.Ordinal);
        Assert.DoesNotContain("updateUserPolicy", webcheck, StringComparison.Ordinal);
        Assert.Contains("{ \"name\": \"sign in as viewer\", \"login\": \"viewer\" }", webcheck, StringComparison.Ordinal);
        Assert.Contains("\"initScript\": \"(() => { let head; Object.defineProperty(window, '__jfarHead', { configurable: true, get() { return head; }, set(v) { head = Object.assign({}, v, { enforced: false }); } }); ", webcheck, StringComparison.Ordinal);
        var scripts = Regex.Matches(webcheck, "\"script\": \"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(6, scripts.Count);
        Assert.StartsWith("window.__jfarSaves.length = 0; const page = document.querySelector('.page:not(.hide) .homeScreenSettingsContainer'); const box = page.querySelector('.chkHidePlayedFromLatest'); box.checked = !box.checked; page.querySelector('.btnSave').click();", scripts[3], StringComparison.Ordinal);
        Assert.StartsWith("window.__jfarSaves.length = 0; const page = document.querySelector('.page:not(.hide)'); const box = page.querySelector('.chkDisplayMissingEpisodes'); box.checked = !box.checked; page.querySelector('.btnSave').click();", scripts[4], StringComparison.Ordinal);
        Assert.StartsWith("await ApiClient.updateDisplayPreferences('usersettings', __vars.prefsBefore, ApiClient.getCurrentUserId(), 'emby');", scripts[5], StringComparison.Ordinal);
        Assert.StartsWith("const ends = {}; for (const route of [", scripts[0], StringComparison.Ordinal);
        Assert.StartsWith("const id = ApiClient.getCurrentUserId(); const c = (await ApiClient.getUser(id)).Configuration;", scripts[1], StringComparison.Ordinal);
        Assert.Contains("await ApiClient.updateUserConfiguration(id, c);", scripts[1], StringComparison.Ordinal);
        Assert.StartsWith("const id = ApiClient.getCurrentUserId(); const p = await ApiClient.getDisplayPreferences('usersettings', id, 'emby');", scripts[2], StringComparison.Ordinal);
        Assert.Contains("await ApiClient.updateDisplayPreferences('usersettings', p, id, 'emby');", scripts[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AccessRolesHardeningProbe_KeepsItsSafetyGuards()
    {
        // The admin surfaces and hardening probe changes a running sandbox only through the admin API and what is its
        // own: its users (hardeningprobe-..., random passwords and sign-in tokens kept in memory, their policies), its role
        // ("Hardening probe ..."), viewer's assignment for the webcheck (Unmanage Keep), its playlists ("Hardening probe ..."), its API
        // key (--restart only, revoked), its Live TV tuner and listing provider over two files of its own under
        // .sandbox/<name>/hardeningprobe, an ASS subtitle file beside the fixture's visible movie (with the item's refresh), and Access
        // Roles' settings, the network configuration's certificate password (only on a configuration without HTTPS or a
        // certificate) and the Live TV configuration, each put back. The restart runs only with --restart, only in the
        // throwaway access-scratch sandbox, and only when it listens on the loopback address alone. It refuses
        // port 8096, prints no token or password, sends no remote search but as the user whose lock refuses it, and never
        // starts, stops or deploys anything.
        var file = Path.Combine(LocalScripts, "accessroles-hardening-probe.mjs");
        Assert.SkipUnless(File.Exists(file), "The Access Roles hardening probe isn't in this checkout.");
        var source = File.ReadAllText(file).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));", source, StringComparison.Ordinal);
        Assert.Contains("const workFolder = assertInSandbox(path.join(sandboxRoot, 'hardeningprobe'));", source, StringComparison.Ordinal);
        Assert.Contains("Token=<admin>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "fs.renameSync", "fs.unlinkSync", "fs.copyFileSync", "appendFileSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", "SAFE-MODE", ".password", "/System/Shutdown", "/Library/VirtualFolders?", "/Library/Refresh", "sqlite", "console.log(token", "console.log(apiKey" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // The restart: only with --restart, only in access-scratch, only on the loopback address.
        Assert.Contains("const RESTART_SANDBOX = 'access-scratch';", source, StringComparison.Ordinal);
        Assert.Contains("    if (o.restart && o.sandbox !== RESTART_SANDBOX) throw new Error(`--restart runs only in the throwaway ${RESTART_SANDBOX} sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!check(`the ${RESTART_SANDBOX} sandbox listens on the loopback address only`, loopbackOnly, evidence.network)) return;", source, StringComparison.Ordinal);
        Assert.Contains("    if (opts.restart) {\n        await restartMain();\n        return;\n    }", source, StringComparison.Ordinal);
        var restarts = Regex.Matches(source, @"'/System/Restart'").Count;
        Assert.Equal(5, restarts);
        var restartMain = source[source.IndexOf("async function restartMain()", StringComparison.Ordinal)..source.IndexOf("let webcheckUserBefore = null;", StringComparison.Ordinal)];
        Assert.Equal(restarts, Regex.Matches(restartMain, @"'/System/Restart'").Count);

        // Remote searches and the requests that would reach out or change something: as the locked user only.
        Assert.Contains("const safeForOpen = new Set(['GET /LiveTv/ListingProviders/Default', 'GET /LiveTv/TunerHosts/Types', 'GET /Libraries/AvailableOptions']);", source, StringComparison.Ordinal);
        Assert.Contains("        const r = await request(method, p, as('locked'), body);\n        surfaceAnswers[label] = { locked: { status: r.status, refused: locked(r) } };\n        if (safeForOpen.has(label)) {", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, "/Items/RemoteSearch/").Count);
        Assert.Contains("        'POST /Items/RemoteSearch/Movie': ['POST', '/Items/RemoteSearch/Movie', { SearchInfo: { Name: 'x' } }],", source, StringComparison.Ordinal);

        // Processes: webcheck.mjs on the probe's own two steps files.
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("for (const steps of ['accessroles-hardening-playback.json', 'accessroles-hardening-login.json']) {", source, StringComparison.Ordinal);
        Assert.Contains("run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', path.join(here, 'webchecks', steps)], { encoding: 'utf8', timeout: 300000 });", source, StringComparison.Ordinal);

        // Files: --out, its own two Live TV files under workFolder, the ASS subtitle beside the movie; removed afterwards.
        Assert.Equal(3, Regex.Matches(source, @"writeFileSync\(").Count);
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Contains("    const file = assertInSandbox(path.join(workFolder, name));\n    fs.mkdirSync(workFolder, { recursive: true });\n    fs.writeFileSync(file, text);", source, StringComparison.Ordinal);
        Assert.Contains("    const file = assertInSandbox(video.replace(/\\.[^.\\\\/]+$/, '.hardeningprobe.ass'));\n    fs.writeFileSync(file, ASS);", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"fs\.rmSync\(").Count);
        Assert.Contains("    if (fs.existsSync(subtitleFile.file)) fs.rmSync(assertInSandbox(subtitleFile.file));", source, StringComparison.Ordinal);
        Assert.Contains("        const file = assertInSandbox(path.join(workFolder, name));\n        if (fs.existsSync(file)) fs.rmSync(file);", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or one of these, behind the guards below.
        var writes = new List<string>();
        // A label may hold an escaped quote ('the probe\'s tuner'), so a quoted string is read with its escapes.
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\.\w+)?(?:\([^)]*\))?)"))
        {
            // The fixture's folder names are its seed's; the test reads them as a placeholder.
            var target = Regex.Replace(call.Groups[2].Value.Trim('`', '\''), @"ids\.folders\['[^']*'\]", "ids.folders[<folder>]");
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Auth/Keys/${key.AccessToken}",
                "DELETE /Items/${p.Id}",
                "DELETE /LiveTv/ListingProviders?id=${enc(providerId)}",
                "DELETE /LiveTv/TunerHosts?id=${enc(tunerId)}",
                "DELETE /Users/${id}",
                "POST /Auth/Keys?app=${PROBE_KEY}",
                "POST /Items/${itemId}/Refresh?metadataRefreshMode=FullRefresh&imageRefreshMode=None&replaceAllMetadata=false&replaceAllImages=false",
                "POST /LiveTv/ListingProviders?validateListings=false&validateLogin=false",
                "POST /LiveTv/TunerHosts",
                "POST /Playlists",
                "POST /ScheduledTasks/Running/${task.Id}",
                "POST /Sessions/${enc(theirs.Id)}/Playing?playCommand=PlayNow&itemIds=${ids.folders[<folder>]}",
                "POST /Sessions/Capabilities/Full",
                "POST /Sessions/Playing",
                "POST /Sessions/Playing/Stopped",
                "POST /Sessions/Viewing?itemId=${idD(alpha)}",
                "POST /System/Configuration/network",
                "POST /System/Restart",
                "POST /Users/${id}/Policy",
                "POST /Users/AuthenticateByName",
                "POST /Users/New",
                "POST settingsPath()"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        // request(method, …): its own definition, the admin's step(), and the AdminSurfaces table, whose writes go as the locked
        // user (refused before the action) and only its safe reads as the open user too.
        Assert.Equal(4, Regex.Matches(source, @"request\(method, ").Count);
        Assert.Contains("function request(method, p, user = 'admin', body = null, contentType = 'application/json', deviceId = undefined) {", source, StringComparison.Ordinal);
        Assert.Contains("    const r = await request(method, p, 'admin', body);", source, StringComparison.Ordinal);
        Assert.Contains("            const o = await request(method, p, as('open'), body);", source, StringComparison.Ordinal);

        // The guards: only its own users, playlists, roles; the network only without HTTPS or a certificate.
        Assert.Contains("const PROBE_USER = 'hardeningprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Hardening probe ';", source, StringComparison.Ordinal);
        Assert.Contains("const PROBE_KEY = 'hardeningprobe-key';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PREFIX)) throw new Error(`refusing to create the playlist ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    return itemsOf(r).filter((i) => i.Name.startsWith(PREFIX));", source, StringComparison.Ordinal);
        Assert.Contains("    if (networkBefore.EnableHttps || networkBefore.RequireHttps || networkBefore.CertificatePath) throw new Error('refusing to touch a network configuration that uses HTTPS or a certificate');", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.set\("));
        Assert.Contains("    tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);
        Assert.Contains("const WEBCHECK_USER = 'viewer';", source, StringComparison.Ordinal);

        // The cleanup puts back what it changed, in a finally.
        Assert.Contains("} finally {\n    const cleanupResult = {};\n    try {\n        if (!opts.restart) {\n            cleanupResult.subtitleRemoved = await removeSubtitle();\n            cleanupResult.liveTv = await removeLiveTvFixture();\n            cleanupResult.network = await restoreNetwork();\n        }\n        cleanupResult.settings = await restoreSettings();\n        await cleanup('cleanup');", source, StringComparison.Ordinal);
        Assert.Contains("    const keys = (((await request('GET', '/Auth/Keys', 'admin')).json || {}).Items || []).filter((k) => k.AppName === PROBE_KEY);\n    apiKey = keys.length ? keys[keys.length - 1].AccessToken : null;", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);

        // The webchecks only read, but for the playback (a click on Play, a subtitle chosen), and sign in as viewer or nobody.
        foreach (var name in new[] { "accessroles-hardening-playback.json", "accessroles-hardening-login.json" })
        {
            var webcheck = File.ReadAllText(Path.Combine(LocalScripts, "webchecks", name));
            Assert.DoesNotContain("\"type\"", webcheck, StringComparison.Ordinal);
            Assert.DoesNotContain("updatePluginConfiguration", webcheck, StringComparison.Ordinal);
            Assert.DoesNotContain("updateUser", webcheck, StringComparison.Ordinal);
            Assert.DoesNotContain("\"login\": \"admin\"", webcheck, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("\"login\"", File.ReadAllText(Path.Combine(LocalScripts, "webchecks", "accessroles-hardening-login.json")), StringComparison.Ordinal);
        Assert.Contains("{ \"name\": \"sign in as viewer\", \"login\": \"viewer\" }", File.ReadAllText(Path.Combine(LocalScripts, "webchecks", "accessroles-hardening-playback.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void AccessRolesRecoveryProbe_KeepsItsSafetyGuards()
    {
        // The lockout and recovery probe changes a running sandbox only through the admin API and what is its own:
        // its users (recoveryprobe-..., random passwords and sign-in tokens kept in memory, their policies), its roles ("Recovery
        // probe ..."), their assignments and Access Roles' settings, put back in a finally. The restarts, the files
        // (SAFE-MODE, the data file and its .bak, copied first and put back) and the uninstall run only with --scratch, only
        // in the throwaway access-scratch sandbox, and only when it listens on the loopback address alone. It
        // refuses port 8096, prints no token or password, and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-recovery-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("const workFolder = assertInSandbox(path.join(sandboxRoot, 'recoveryprobe'));", source, StringComparison.Ordinal);
        Assert.Contains("const storeFolder = assertInSandbox(path.join(sandboxRoot, 'data', 'data', 'accessroles'));", source, StringComparison.Ordinal);
        Assert.Contains("const safeModeFile = assertInSandbox(path.join(storeFolder, 'SAFE-MODE'));", source, StringComparison.Ordinal);
        Assert.Contains("const dataFile = assertInSandbox(path.join(storeFolder, 'accessroles.json'));", source, StringComparison.Ordinal);
        Assert.Contains("const backupFile = assertInSandbox(path.join(storeFolder, 'accessroles.json.bak'));", source, StringComparison.Ordinal);
        Assert.Contains("Token=<${user === 'admin' ? 'admin' : 'user'}>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "spawnSync", "fs.renameSync", "fs.unlinkSync", "fs.copyFileSync", "appendFileSync", "rmdirSync", "recursive: true })", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password", "/System/Shutdown", "/Library/", "/Auth/Keys", "sqlite", "console.log(token", "console.log(secrets" })
        {
            if (change == "recursive: true })")
            {
                // mkdirSync of its own work folder only.
                Assert.Single(Regex.Matches(source, Regex.Escape(change)));
                Assert.Contains("    fs.mkdirSync(workFolder, { recursive: true });", source, StringComparison.Ordinal);
                continue;
            }

            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // --scratch: only in access-scratch, only on the loopback address; everything that restarts, writes a file or
        // uninstalls is in the scratch section, which only --scratch runs.
        Assert.Contains("const SCRATCH_SANDBOX = 'access-scratch';", source, StringComparison.Ordinal);
        Assert.Contains("    if (o.scratch && o.sandbox !== SCRATCH_SANDBOX) throw new Error(`--scratch runs only in the throwaway ${SCRATCH_SANDBOX} sandbox`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!check(`the ${SCRATCH_SANDBOX} sandbox listens on the loopback address only`, loopbackOnly, evidence.network)) return;", source, StringComparison.Ordinal);
        Assert.Contains("    if (opts.scratch) await scratchMain();\n    else await liveMain();", source, StringComparison.Ordinal);
        var scratch = source[source.IndexOf("// The restart rows (the access-scratch sandbox)", StringComparison.Ordinal)..source.IndexOf("let uninstalled = false;", StringComparison.Ordinal)];
        var live = source[..source.IndexOf("// The restart rows (the access-scratch sandbox)", StringComparison.Ordinal)];
        Assert.Single(Regex.Matches(source, @"'/System/Restart'"));
        Assert.Single(Regex.Matches(scratch, @"'/System/Restart'"));
        Assert.Single(Regex.Matches(source, @"`/Plugins/\$\{installed\.Id\}/\$\{installed\.Version\}`"));
        Assert.Single(Regex.Matches(scratch, @"'DELETE', `/Plugins/"));
        Assert.Equal(11, Regex.Matches(source, @"writeFileSync\(").Count);
        Assert.Equal(10, Regex.Matches(scratch, @"writeFileSync\(").Count);
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(source, @"fs\.rmSync\(").Count);
        Assert.Equal(4, Regex.Matches(scratch, @"fs\.rmSync\(").Count);
        // The busy temp file row: an exclusive handle on the store's own temp file, let go and removed in a finally.
        Assert.Contains("const tempFile = assertInSandbox(path.join(storeFolder, 'accessroles.json.tmp'));", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"openSync\("));
        Assert.Contains("    const held = fs.openSync(tempFile, fs.constants.O_RDWR | UV_FS_O_EXLOCK);", scratch, StringComparison.Ordinal);
        Assert.Contains("    } finally {\n        fs.closeSync(held);\n        fs.rmSync(tempFile, { force: true });", scratch, StringComparison.Ordinal);
        Assert.Contains("        if (opts.scratch && fs.existsSync(safeModeFile)) {\n            fs.rmSync(safeModeFile);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.writeFileSync(", live, StringComparison.Ordinal);
        Assert.DoesNotContain("fs.rmSync(", live, StringComparison.Ordinal);
        // The data file and its .bak are copied under the work folder before anything writes them, and put back in a finally.
        Assert.Contains("    const copy = assertInSandbox(path.join(workFolder, path.basename(file) + '.kept'));", source, StringComparison.Ordinal);
        Assert.Contains("    fs.writeFileSync(assertInSandbox(file), fs.readFileSync(assertInSandbox(copy)));", source, StringComparison.Ordinal);
        Assert.True(scratch.IndexOf("const keptData = keepCopy(dataFile);", StringComparison.Ordinal) < scratch.IndexOf("fs.writeFileSync(dataFile", StringComparison.Ordinal));
        Assert.Equal(3, Regex.Matches(scratch, @"    \} finally \{\n        putBackBoth\(").Count);

        // Every request that isn't a GET: Access Roles' admin API, or one of these.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\.\w+)?(?:\([^)]*\))?)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Plugins/${installed.Id}/${installed.Version}",
                "DELETE /Users/${id}",
                "POST /ScheduledTasks/Running/${task.Id}",
                "POST /System/Restart",
                "POST /Users/${id}/Policy",
                "POST /Users/AuthenticateByName",
                "POST /Users/New",
                "POST /Users/Password",
                "POST settingsPath()"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(3, Regex.Matches(source, @"request\(method, ").Count);

        // The guards: only its own users and roles; a password change only with a wrong current password.
        Assert.Contains("const PROBE_USER = 'recoveryprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Recovery probe ';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"'/Users/Password'"));
        Assert.Contains("    const r = await request('POST', '/Users/Password', idN(user.Id), { CurrentPw: `wrong-${crypto.randomBytes(6).toString('hex')}`, NewPw: crypto.randomBytes(12).toString('base64url') });", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.set\("));
        Assert.Contains("    tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);

        // The cleanup puts back what it changed, in a finally (not after the uninstall, which leaves the sandbox to delete).
        Assert.Contains("} finally {\n    const cleanupResult = {};\n    try {\n        if (opts.scratch && fs.existsSync(safeModeFile)) {\n            fs.rmSync(safeModeFile);\n            cleanupResult.safeModeRemoved = true;\n        }\n        if (!uninstalled) {\n            await cleanup('after');\n            cleanupResult.settings = await restoreSettings();", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessRolesDataProbe_KeepsItsSafetyGuards()
    {
        // The Data tab and Diagnostics probe changes a running sandbox only through Access Roles' admin API and what
        // is its own: its users (exportprobe-..., random passwords and sign-in tokens kept in memory, their policies), its roles
        // ("Export probe ...") and their assignments, unmanaged (Keep) and deleted in a finally; its imports and its restore
        // put back the store it exported plus its own records, and a restore's hold is confirmed in the finally. It refuses
        // port 8096, reads only the sandbox's log and hold file under .sandbox, writes no file but its own --out, prints no
        // token or password, and never starts, stops, restarts or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-data-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("const holdFile = assertInSandbox(path.join(sandboxRoot, 'data', 'data', 'accessroles', 'library-sync-hold.json'));", source, StringComparison.Ordinal);
        Assert.Contains("Token=<${user === 'admin' ? 'admin' : 'user'}>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "spawnSync", "fs.renameSync", "fs.unlinkSync", "fs.copyFileSync", "fs.rmSync", "fs.mkdirSync", "appendFileSync", "rmdirSync", "openSync", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password", "/System/Restart", "/System/Shutdown", "/Library/", "/Auth/Keys", "sqlite", "console.log(token", "console.log(secrets" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or one of these; a non-admin's only to the admin API.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\.\w+)?(?:\([^)]*\))?)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Users/${id}",
                "POST /Users/${id}/Policy",
                "POST /Users/AuthenticateByName",
                "POST /Users/New"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(3, Regex.Matches(source, @"request\(method, ").Count);
        Assert.Contains("    const r = await request(method, p, user, body);", source, StringComparison.Ordinal);
        var asUser = source[source.IndexOf("    const asUser = [];", StringComparison.Ordinal)..source.IndexOf("    transcript.push({ step: 'every Data and Diagnostics action as a non-admin'", StringComparison.Ordinal)];
        Assert.Contains("        const r = await request(method, p, idN(a.Id), body);", asUser, StringComparison.Ordinal);
        foreach (Match target in Regex.Matches(asUser, @"\['(GET|POST)', (`[^`]*`|'[^']*')"))
        {
            Assert.StartsWith("/AccessRoles/Admin/", target.Groups[2].Value.Trim('`', '\''), StringComparison.Ordinal);
        }

        // The guards: only its own users and roles; the store's hold confirmed and its things removed in a finally.
        Assert.Contains("const PROBE_USER = 'exportprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Export probe ';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.set\("));
        Assert.Contains("    tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    const cleanupResult = {};\n    try {\n        cleanupResult.holdReleased = await releaseHold('after');\n        await cleanup('after');", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AccessRolesJeProbe_KeepsItsSafetyGuards()
    {
        // The JE coexistence probe writes only what is its own: its users (jeprobe-..., random passwords
        // and sign-in tokens kept in memory), their JE files and bookmarks (JE's saves sent with bodies JE refuses, but for
        // the locked user's, which Access Roles refuses), its roles ("JE probe ...") and assignments, viewer's lock-only
        // assignment (unmanaged with Keep in a finally), JE's HiddenContentEnabled (put back) and one movie hidden for its
        // own user (unhidden). With --webcheck it runs only its own two steps files, which sign in as viewer and only read
        // and press keys. It refuses port 8096, writes no file but its own --out, prints no token or password, and never
        // starts, stops, restarts or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-je-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("Token=<${user === 'admin' ? 'admin' : 'user'}>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "fs.renameSync", "fs.unlinkSync", "fs.copyFileSync", "fs.rmSync", "fs.mkdirSync", "appendFileSync", "rmdirSync", "openSync", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password", "/System/Restart", "/System/Shutdown", "/Library/", "/Auth/Keys", "/JellyfinEnhanced/cdn", "/Users/${id}/Policy", "sqlite", "console.log(token", "console.log(secrets" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("const run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file], { encoding: 'utf8', timeout: 300000 });", source, StringComparison.Ordinal);
        Assert.Contains("const WEBCHECKS = ['accessroles-je-legacy.json', 'accessroles-je-experimental.json'];", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or one of these.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\.\w+)?(?:\([^)]*\))?)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Bookmarks/${made.json.Id}",
                "DELETE /JellyfinEnhanced/user-settings/${idN(kid.Id)}/bookmark.json/${enc(mine[0])}",
                "DELETE /Users/${id}",
                "POST /Bookmarks",
                "POST /JellyfinEnhanced/admin/hidden-content/${idN(hidden.kid)}/unhide",
                "POST /JellyfinEnhanced/admin/hidden-content/${idN(kid.Id)}/hide",
                "POST /JellyfinEnhanced/spoiler-blur/user-prefs",
                "POST /JellyfinEnhanced/user-settings/${idN(kid.Id)}/bookmark.json/add",
                "POST /JellyfinEnhanced/user-settings/${idN(target.Id)}/${f}",
                "POST /Plugins/${je.Id}/Configuration",
                "POST /Users/AuthenticateByName",
                "POST /Users/New",
                "POST /jellyfinenhanced/USER-SETTINGS/${kid.Id.toUpperCase().replace(/^(.{8})(.{4})(.{4})(.{4})(.{12})$/, '$1-$2-$3-$4-$5')}/Settings.json"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(2, Regex.Matches(source, @"request\(method, ").Count);

        // JE's saves: the locked kid's bodies are valid (refused before JE), everyone else's invalid (JE writes nothing); the
        // admin's go to the kid's files; JE's settings are put back and the hidden movie unhidden in the finally.
        Assert.Contains("            const r = await request('POST', `/JellyfinEnhanced/user-settings/${idN(target.Id)}/${f}`, as(who), who === kid ? '{}' : '{ not json');", source, StringComparison.Ordinal);
        Assert.Contains("        const target = who === 'admin' ? kid : who;", source, StringComparison.Ordinal);
        Assert.Contains("        cleanupResult.jeSettings = je ? await restoreJeConfig() : true;", source, StringComparison.Ordinal);
        Assert.Contains("    if (hidden && hidden.kid) {", source, StringComparison.Ordinal);

        // The guards: only its own users and roles; viewer only given a lock-only role and unmanaged with Keep.
        Assert.Contains("const PROBE_USER = 'jeprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'JE probe ';", source, StringComparison.Ordinal);
        Assert.Contains("const WEB_LOCKS = ['Profile', 'QuickConnect', 'EnhancedPanel'];", source, StringComparison.Ordinal);
        Assert.Contains("        const webRole = await createRole('web', WEB_LOCKS);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.set\("));
        Assert.Contains("    tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);
        Assert.Contains("        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage`, { Choice: 'Keep' });", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);

        // The webchecks sign in as viewer and only read, click and press keys.
        foreach (var name in new[] { "accessroles-je-legacy.json", "accessroles-je-experimental.json" })
        {
            var webcheck = File.ReadAllText(Path.Combine(Scripts, "webchecks", name));
            Assert.Single(Regex.Matches(webcheck, "\"login\": "));
            Assert.Contains("{ \"name\": \"sign in as viewer\", \"login\": \"viewer\" }", webcheck, StringComparison.Ordinal);
            foreach (var write in new[] { "\"type\"", "ApiClient.update", "ApiClient.ajax", "fetch(", "XMLHttpRequest", "method:" })
            {
                Assert.DoesNotContain(write, webcheck, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AccessRolesSuiteProbe_KeepsItsSafetyGuards()
    {
        // The suite probe writes only what is its own: its users (suiteprobe-...,
        // random passwords and sign-in tokens kept in memory), admin2's admin flag, its role ("Suite probe ...") and the
        // assignments (unmanaged with Keep in a finally), the kid's bookmarks and its one clip (deleted with the clip file in
        // the finally), Access Roles' enabled state (enabled again in the finally) and the two in-process restarts that takes.
        // Its sweeps send a write only after every read of the same caller was refused, with a body that can't bind. Its one
        // webcheck signs in as admin and only unticks the probe's own admin2's admin flag. It refuses port 8096, writes no file
        // but its own --out, prints no token or password, and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-suite-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("Token=<${user === 'admin' ? 'admin' : user === null ? 'none' : 'user'}>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "fs.renameSync", "fs.unlinkSync", "fs.copyFileSync", "fs.rmSync", "fs.mkdirSync", "appendFileSync", "rmdirSync", "openSync", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password", "/System/Shutdown", "/Library/", "/Auth/Keys", "/JellyfinEnhanced", "/Uninstall", "'DELETE', `/Plugins", "sqlite", "console.log(token", "console.log(secrets" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("const run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file], { encoding: 'utf8', timeout: 300000 });", source, StringComparison.Ordinal);
        Assert.Contains("const DEMOTE_WEBCHECK = 'accessroles-suite-demote.json';", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or one of these.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\.\w+)?(?:\([^)]*\))?)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Bookmarks/${fresh.json.Id}",
                "DELETE /Bookmarks/${id}?deleteClip=true",
                "DELETE /Users/${id}",
                "POST /Bookmarks",
                "POST /Bookmarks/${ownBookmark.Id}/Clip",
                "POST /Plugins/${idN(ar.Id)}/${enc(ar.Version)}/${enabled ? 'Enable' : 'Disable'}",
                "POST /System/Restart",
                "POST /Users/${id}/Policy",
                "POST /Users/AuthenticateByName",
                "POST /Users/New"
            ],
            writes.Distinct().Order(StringComparer.Ordinal));

        // The requests whose method is a variable: the helper itself, step and scan (whose methods are literals at every call),
        // a pair's second request (the same method as its first), and the sweeps' writes, sent only after every read of that
        // caller was refused and with a body that can't bind.
        Assert.Equal(5, Regex.Matches(source, @"request\(method, ").Count);
        Assert.Equal(2, Regex.Matches(source, @"    const r = await request\(method, p, user, body\);").Count);
        Assert.Contains("    const b = await request(method, make(fake), user, makeBody ? makeBody(fake) : null);", source, StringComparison.Ordinal);
        Assert.Contains("    const readsRefused = out.every((x) => x.ok);\n    if (readsRefused) {\n        for (const r of writes) {\n            const method = r.method;\n            const a = await request(method, r.path, user, r.body ? '{ not json' : null);", source, StringComparison.Ordinal);

        // Plugins: only Access Roles (by build.yaml's guid) disabled and enabled, each with one in-process restart, and enabled
        // again in the finally when a run stops while it is disabled; the plugin is never uninstalled.
        Assert.Single(Regex.Matches(source, @"'/System/Restart'"));
        Assert.Contains("    const restart = await step('restart the sandbox (in-process)', 'POST', '/System/Restart');", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"/Plugins/\$\{"));
        Assert.Contains("    const ar = (await plugins()).find((p) => idN(p.Id) === idN(plugin.guid));\n    if (!ar) throw new Error('Access Roles is not in /Plugins');", source, StringComparison.Ordinal);
        Assert.Contains("    if (arDisabled) {\n        try {\n            evidence.cleanupEnable = await setAccessRoles(true);", source, StringComparison.Ordinal);

        // Users: only its own are created, written (admin2 made an administrator) and deleted; the kid deletes its own
        // bookmarks with their clips.
        Assert.Contains("const PROBE_USER = 'suiteprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Suite probe ';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to write user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"postOwnPolicy\(admin2\.Id"));
        Assert.Contains("    await postOwnPolicy(admin2.Id, { IsAdministrator: true }, 'make the probe\\'s admin2 an administrator');", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"tokens\.set\("));
        Assert.Contains("    tokens.set(idN(user.Id), r.json.AccessToken);", source, StringComparison.Ordinal);
        Assert.Contains("            const r = await step(`${label}: the kid deletes its bookmark with its clip`, 'DELETE', `/Bookmarks/${id}?deleteClip=true`, null, as(kid));", source, StringComparison.Ordinal);
        Assert.Contains("        await step(`${label}: unmanage ${u.Name} (Keep)`, 'POST', `/AccessRoles/Admin/Users/${u.UserId}/Unmanage`, { Choice: 'Keep' });", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);

        // The webcheck signs in as admin and only opens the probe's own admin2, unticks its admin flag and saves.
        var demote = File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-suite-demote.json"));
        Assert.Single(Regex.Matches(demote, "\"login\": "));
        Assert.Contains("{ \"name\": \"sign in as admin\", \"login\": \"admin\" }", demote, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(demote, "\"click\": ").Count);
        Assert.Contains("\"click\": \".localUsers .card[data-username=\\\"suiteprobe-admin2\\\"] a.cardContent\"", demote, StringComparison.Ordinal);
        Assert.Contains("\"click\": \"#editUserPage:not(.hide) label:has(.chkIsAdmin)\"", demote, StringComparison.Ordinal);
        Assert.Contains("\"click\": \"#editUserPage:not(.hide) .editUserProfileForm .button-submit\"", demote, StringComparison.Ordinal);
        Assert.Contains("location.hash.indexOf('#/dashboard/users/profile?userId=' + __vars.target) === 0", demote, StringComparison.Ordinal);
        foreach (var write in new[] { "\"type\"", "\"key\"", "\"script\"", "ApiClient.update", "ApiClient.ajax", "fetch(", "XMLHttpRequest", "method:", ".click()" })
        {
            Assert.DoesNotContain(write, demote, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AccessRolesAdminPageProbe_KeepsItsSafetyGuards()
    {
        // The admin page probe changes a running sandbox only through Access Roles' admin API and what is its own:
        // its two users (pageprobe-..., random passwords kept in memory and never used), their assignments, and its roles
        // ("Page probe ..."), deleted in a finally. The web client writes only through the page, as admin, on those users and
        // roles, but for the desktop check's "another administrator" (its races): PUT Users/{id} of the probe's
        // two users and PUT Roles/{id} of its role "Page probe Kid" through the page's session; and the TV confirm's:
        // the kid's library access changed through the stock policy API and put back, so the import its OK on Apply
        // sends is refused. The TV and phone checks only read. It refuses port 8096, reads only the sandbox's log under .sandbox,
        // writes nothing but its own --out file, prints no token or password, and never starts, stops or deploys anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-admin-page-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));", source, StringComparison.Ordinal);
        Assert.Contains("Token=<admin>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "fs.renameSync", "fs.rmSync", "fs.unlinkSync", "fs.mkdirSync", "fs.copyFileSync", "appendFileSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password", "/System/Shutdown", "/System/Restart", "AuthenticateByName", "/Auth/Keys", "/Policy", "sqlite", "console.log(token" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // Processes: webcheck.mjs on the probe's own four steps files. Files: --out only.
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("const WEBCHECKS = { desktop: 'accessroles-admin-page-desktop.json', tv: 'accessroles-admin-page-tv.json', tvConfirm: 'accessroles-admin-page-tv-confirm.json', phone: 'accessroles-admin-page-phone.json' };", source, StringComparison.Ordinal);
        Assert.Contains("run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file], { encoding: 'utf8', timeout: 300000 });", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API, or its own users' creation and deletion.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\.\w+)?(?:\([^)]*\))?)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(["DELETE /Users/${id}", "POST /Users/New"], writes.Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(2, Regex.Matches(source, @"request\(method, ").Count);
        Assert.Contains("function request(method, p, user = 'admin', body = null, headersExtra = {}) {", source, StringComparison.Ordinal);
        Assert.Contains("    const r = await request(method, p, 'admin', body);", source, StringComparison.Ordinal);

        // The guards: only its own users and roles; the default roles must be none (its users would get them).
        Assert.Contains("const PROBE_USER = 'pageprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Page probe ';", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && x.Name.toLowerCase().startsWith(PROBE_USER))) {", source, StringComparison.Ordinal);
        Assert.Contains("    for (const r of (await rolesList()).filter((x) => x.Name.startsWith(PREFIX))) {", source, StringComparison.Ordinal);
        Assert.Contains("    if (!check('no default role for new users (the probe\\'s users must start unmanaged)', defaultRolesBefore.length === 0, defaultRolesBefore)) return;", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    const cleanupResult = {};\n    try {\n        await cleanup('cleanup');", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);

        // The webchecks sign in as admin and write nothing but through the page's own buttons: no API write of their own, but
        // the desktop check's "another administrator" (its one ApiClient.ajax, in the helpers), which only PUTs an assignment
        // of one of the probe's two users or the probe's own role, and the TV confirm's change of the kid's library access
        // (below).
        foreach (var name in new[] { "accessroles-admin-page-desktop.json", "accessroles-admin-page-tv.json", "accessroles-admin-page-tv-confirm.json", "accessroles-admin-page-phone.json" })
        {
            var webcheck = File.ReadAllText(Path.Combine(Scripts, "webchecks", name));
            Assert.Contains("{\n      \"name\": \"sign in as admin\",\n      \"login\": \"admin\"\n    }", webcheck.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Single(Regex.Matches(webcheck, "\"login\""));
            var stockWrites = name == "accessroles-admin-page-tv-confirm.json" ? webcheck.Replace("ApiClient.updateUserPolicy(", string.Empty, StringComparison.Ordinal) : webcheck;
            foreach (var write in new[] { "ApiClient.fetch", "fetch(", "XMLHttpRequest", "updateUser", "updatePluginConfiguration", "method:", "localStorage.clear" })
            {
                Assert.DoesNotContain(write, stockWrites, StringComparison.Ordinal);
            }

            if (name != "accessroles-admin-page-desktop.json")
            {
                Assert.DoesNotContain("ApiClient.ajax", webcheck, StringComparison.Ordinal);
                Assert.DoesNotContain("__arPage.api(", webcheck, StringComparison.Ordinal);
            }
        }

        var raced = File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-admin-page-desktop.json"));
        Assert.Single(Regex.Matches(raced, @"ApiClient\.ajax"));
        Assert.Contains("  api: (type, path, body) => ApiClient.ajax({ type, url: ApiClient.getUrl(path), data: body ? JSON.stringify(body) : undefined, contentType: 'application/json', dataType: 'json' }),", raced.Replace("\\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        var apiCalls = Regex.Matches(raced, @"__arPage\.api\(([^)]*)\)");
        Assert.Equal(3, apiCalls.Count);
        Assert.Equal(2, apiCalls.Count(call => Regex.IsMatch(call.Groups[1].Value, @"^'PUT', 'AccessRoles/Admin/Users/' \+ (kid|t)\.UserId, \{ UserId: (kid|t)\.UserId, ")));
        // The role race: the probe's own role, found by its name, given one lock more.
        var roleCall = Assert.Single(apiCalls, call => call.Groups[1].Value.StartsWith("'PUT', 'AccessRoles/Admin/Roles/' + r.Id, ", StringComparison.Ordinal));
        Assert.Contains("const r = (await roles()).find(x => x.Name === \\\"Page probe Kid\\\"); await __arPage.api(" + roleCall.Groups[1].Value, raced, StringComparison.Ordinal);

        foreach (Match row in Regex.Matches(raced, @"__arPage\.row\('([^']*)'\)"))
        {
            Assert.StartsWith("pageprobe-", row.Groups[1].Value, StringComparison.Ordinal);
        }

        // The desktop flow works on the probe's two users and its own role only.
        var desktop = File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-admin-page-desktop.json"));
        Assert.Contains("\"type\": \"Page probe Kid\"", desktop, StringComparison.Ordinal);
        var named = Regex.Matches(desktop, @"__arPage\.user\(\\""([^\\]+)\\""\)");
        Assert.NotEmpty(named);
        foreach (Match user in named)
        {
            Assert.StartsWith("pageprobe-", user.Groups[1].Value, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("#AccessRolesDefaultRolesSave", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("#AccessRolesSelectAll", desktop, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessRolesDriftRevert", desktop, StringComparison.Ordinal);

        // The TV, TV confirm and phone checks only read: no save, create, assign, delete or unmanage, nothing typed; the TV
        // confirm never presses a button (its confirm is dismissed by OK on Cancel and by Back, and its OK on Apply sends an
        // import the server refuses), and the probe runs it only while the import is offered. Its one stock write is the
        // probe's kid's library access, changed after the import's preview and put back as it was.
        var tvConfirm = File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-admin-page-tv-confirm.json"));
        Assert.DoesNotContain("__arPage.press(", tvConfirm, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(tvConfirm, @"ApiClient\.updateUserPolicy\(").Count);
        Assert.Contains("const kid = users.find(u => u.Name === 'pageprobe-kid'); ", tvConfirm, StringComparison.Ordinal);
        Assert.Contains("window.__arPageKid = { id: kid.Id, policy: JSON.parse(JSON.stringify(kid.Policy)) }; await ApiClient.updateUserPolicy(kid.Id, Object.assign({}, kid.Policy, { EnableAllFolders: false, EnabledFolders: [one.Id] }));", tvConfirm, StringComparison.Ordinal);
        Assert.Contains("await ApiClient.updateUserPolicy(window.__arPageKid.id, window.__arPageKid.policy);", tvConfirm, StringComparison.Ordinal);
        Assert.Contains("&& stable(kidNow) === stable(accessAtStart[KID])", source, StringComparison.Ordinal);
        Assert.Contains("        const offered = !!before.Records && before.Records.Assignments === 0 && !(before.Store && before.Store.ReadOnly);\n        if (offered) {\n            const run = await runWebcheck(WEBCHECKS.tvConfirm);", source, StringComparison.Ordinal);
        foreach (var name in new[] { "accessroles-admin-page-tv.json", "accessroles-admin-page-tv-confirm.json", "accessroles-admin-page-phone.json" })
        {
            var webcheck = File.ReadAllText(Path.Combine(Scripts, "webchecks", name));
            foreach (var write in new[] { "\"type\"", "'Save')", "'Create')", "'Assign')", "'Delete')", "'Unmanage')", "'Continue')", "#AccessRolesUserSave", "#AccessRolesUnmanage", "#AccessRolesBulk", "#AccessRolesNewRole", "data-action" })
            {
                Assert.DoesNotContain(write, webcheck, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AccessRolesAdminTabsProbe_KeepsItsSafetyGuards()
    {
        // The probe of the admin page's Overview chips, Libraries, Audit, Settings, Data and Diagnostics changes a
        // running sandbox only through Access Roles' admin API and what is its own: its two users (tabsprobe-..., random
        // passwords kept in memory and never used), their assignments and native library access (the drift), its roles and
        // libraries ("Tabs probe ...", empty folders under .sandbox/<name>/tabsprobe), and the settings DriftPolicy and
        // ReconcileIntervalMinutes, put back in a finally. It refuses port 8096, reads only the sandbox's log under .sandbox,
        // writes no file but its own --out and the folders, prints no token or password, and never starts, stops or deploys
        // anything.
        var source = File.ReadAllText(Path.Combine(Scripts, "accessroles-admin-tabs-probe.mjs")).ReplaceLineEndings("\n");
        Assert.Contains("port === PRODUCTION_PORT", source, StringComparison.Ordinal);
        Assert.Contains("const PRODUCTION_PORT = 8096;", source, StringComparison.Ordinal);
        Assert.Contains("http.request({ host: '127.0.0.1', port, method, path: baseUrl + p, headers, agent: false }", source, StringComparison.Ordinal);
        Assert.Contains("throw new Error(`refusing to touch ${p}`)", source, StringComparison.Ordinal);
        Assert.Contains("const logFolder = assertInSandbox(path.join(sandboxRoot, 'log'));", source, StringComparison.Ordinal);
        Assert.Contains("const libraryRoot = assertInSandbox(path.join(sandboxRoot, 'tabsprobe'));", source, StringComparison.Ordinal);
        Assert.Contains("Token=<admin>", source, StringComparison.Ordinal);
        Assert.Contains("-d '{\"Name\":\"${name}\",\"Password\":\"<random>\"}'", source, StringComparison.Ordinal);
        foreach (var change in new[] { "execSync", "execFile", "fork(", "spawn(", "fs.renameSync", "fs.unlinkSync", "fs.copyFileSync", "appendFileSync", "runScript(", "RealServer", "Deploy-Plugin", "Stop-Sandbox", ".password", "/System/Shutdown", "/System/Restart", "AuthenticateByName", "/Auth/Keys", "/Devices", "sqlite", "console.log(token" })
        {
            Assert.DoesNotContain(change, source, StringComparison.Ordinal);
        }

        // Processes: webcheck.mjs on the probe's own three steps files. Files: --out, and the library folders inside the sandbox.
        Assert.Single(Regex.Matches(source, @"spawnSync\("));
        Assert.Contains("const WEBCHECKS = { desktop: 'accessroles-admin-tabs-desktop.json', tv: 'accessroles-admin-tabs-tv.json', phone: 'accessroles-admin-tabs-phone.json' };", source, StringComparison.Ordinal);
        Assert.Contains("run = spawnSync(process.execPath, [path.join(repoRoot, 'scripts', 'webcheck.mjs'), '--sandbox', opts.sandbox, '--steps', file], { encoding: 'utf8', timeout: 300000 });", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"writeFileSync\("));
        Assert.Contains("if (opts.out) fs.writeFileSync(opts.out, text);", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"mkdirSync\("));
        Assert.Contains("    const folder = assertInSandbox(path.join(libraryRoot, FOLDERS[name]));\n    fs.mkdirSync(folder, { recursive: true });", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"rmSync\("));
        Assert.Contains("    if (fs.existsSync(libraryRoot)) fs.rmSync(assertInSandbox(libraryRoot), { recursive: true, force: true });", source, StringComparison.Ordinal);

        // Every request that isn't a GET: Access Roles' admin API; its own users' creation, deletion and native library
        // access; its own libraries' creation, rename and removal.
        var writes = new List<string>();
        foreach (Match call in Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', (`[^`]*`|'[^']*'|\w+(?:\.\w+)?(?:\([^)]*\))?)"))
        {
            var target = call.Groups[2].Value.Trim('`', '\'');
            if (!target.StartsWith("/AccessRoles/Admin/", StringComparison.Ordinal))
            {
                writes.Add(call.Groups[1].Value + " " + target);
            }
        }

        Assert.Equal(
            [
                "DELETE /Library/VirtualFolders?name=${encodeURIComponent(name)}&refreshLibrary=true",
                "DELETE /Users/${id}",
                "POST /Library/VirtualFolders/Name?name=${encodeURIComponent(name)}&newName=${encodeURIComponent(newName)}&refreshLibrary=true",
                "POST /Users/${user.Id}/Policy",
                "POST /Users/New",
                "POST query",
            ],
            writes.Distinct().Order(StringComparer.Ordinal));
        // Every quoted method is one of those calls, but the allowed 409's method in the desktop check's result.
        Assert.Equal(Regex.Matches(source, @"'(?:POST|PUT|DELETE)'").Count - 1, Regex.Matches(source, @"(?:step\((?:`[^`]*`|'(?:[^'\\]|\\.)*'|\w+), |request\()'(POST|PUT|DELETE)', ").Count);
        Assert.Contains("run.failedRequests[0].method === 'PUT'", source, StringComparison.Ordinal);
        Assert.Contains("    const query = `/Library/VirtualFolders?name=${encodeURIComponent(name)}&collectionType=homevideos&paths=${encodeURIComponent(folder)}&refreshLibrary=true`;", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, @"request\(method, ").Count);
        Assert.Contains("function request(method, p, user = 'admin', body = null) {", source, StringComparison.Ordinal);
        Assert.Contains("    const r = await request(method, p, 'admin', body);", source, StringComparison.Ordinal);

        // The guards: only its own users, libraries and roles; the default roles must be none (its users would get them).
        Assert.Contains("const PROBE_USER = 'tabsprobe-';", source, StringComparison.Ordinal);
        Assert.Contains("const PREFIX = 'Tabs probe ';", source, StringComparison.Ordinal);
        Assert.Contains("const isOwnLibrary = (name) => name.startsWith(PREFIX);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!name.startsWith(PROBE_USER)) throw new Error(`refusing to create ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(id))) throw new Error(`refusing to delete user ${id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!own.has(idN(user.Id))) throw new Error(`refusing to write user ${user.Id}: it isn't one of the probe's own`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name) || !FOLDERS[name]) throw new Error(`refusing to add the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name)) throw new Error(`refusing to remove the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    if (!isOwnLibrary(name) || !isOwnLibrary(newName)) throw new Error(`refusing to rename the library ${name}`);", source, StringComparison.Ordinal);
        Assert.Contains("    for (const u of (await usersRows()).filter((x) => x.State !== 'NotManaged' && x.Name.toLowerCase().startsWith(PROBE_USER))) {", source, StringComparison.Ordinal);
        Assert.Contains("    for (const r of (await rolesList()).filter((x) => x.Name.startsWith(PREFIX))) {", source, StringComparison.Ordinal);
        Assert.Contains("    for (const l of (await arLibraries()).filter((x) => isOwnLibrary(x.Name || '') && x.State === 'Orphaned')) {", source, StringComparison.Ordinal);
        Assert.Contains("    if (!check('no default role for new users (the probe\\'s users must start unmanaged)', defaultRolesBefore.length === 0, defaultRolesBefore)) return;", source, StringComparison.Ordinal);
        Assert.Contains("} finally {\n    const cleanupResult = {};\n    try {\n        await cleanup('cleanup');\n        if (settingsBefore) {", source, StringComparison.Ordinal);

        Assert.DoesNotContain(@"ProgramData\Jellyfin\Server", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"ProgramData\\Jellyfin\\Server", source, StringComparison.OrdinalIgnoreCase);

        // The webchecks sign in as admin and write nothing but through the page's own buttons, but the desktop check's
        // "another administrator" (its one ApiClient.ajax, a helper of its own), which only PUTs the interval setting.
        var names = new[] { "accessroles-admin-tabs-desktop.json", "accessroles-admin-tabs-tv.json", "accessroles-admin-tabs-phone.json" };
        foreach (var name in names)
        {
            var webcheck = File.ReadAllText(Path.Combine(Scripts, "webchecks", name));
            Assert.Contains("{\n      \"name\": \"sign in as admin\",\n      \"login\": \"admin\"\n    }", webcheck.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            Assert.Single(Regex.Matches(webcheck, "\"login\""));
            foreach (var write in new[] { "ApiClient.fetch", "fetch(", "XMLHttpRequest", "updateUser", "updatePluginConfiguration", "method:", "localStorage.clear", "\"type\"", "#AccessRolesDefaultRolesSave", "#AccessRolesSelectAll", "#AccessRolesImport", "#AccessRolesUserSave", "#AccessRolesUnmanage", "#AccessRolesBulk", "#AccessRolesNewRole" })
            {
                Assert.DoesNotContain(write, webcheck, StringComparison.Ordinal);
            }

            // Revert everyone isn't pressed: only the kid's Revert this user, in the desktop check.
            Assert.DoesNotMatch(@"#AccessRolesDriftRevert(?!User)", webcheck);
        }

        var desktop = File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-admin-tabs-desktop.json"));
        Assert.Single(Regex.Matches(desktop, @"ApiClient\.ajax"));
        Assert.Contains("__arTabs.otherAdmin = (body) => ApiClient.ajax({ type: 'PUT', url: ApiClient.getUrl('AccessRoles/Admin/Settings'), data: JSON.stringify(body), contentType: 'application/json', dataType: 'json' });", desktop, StringComparison.Ordinal);
        Assert.Equal(["__arTabs.otherAdmin({ ReconcileIntervalMinutes: __vars.minutes.k })"], Regex.Matches(desktop, @"__arTabs\.otherAdmin\([^)]*\)").Select(m => m.Value));

        // The desktop flow's writes through the page: Grant again and Remove references on the probe's own library cards, and
        // Revert this user on the probe's kid.
        var actions = Regex.Matches(desktop, @"(__arTabs\.card\('[^']*'\))?\.querySelector\('button\[data-action=");
        Assert.Equal(2, actions.Count);
        Assert.Contains("__arTabs.card('Tabs probe Again').querySelector('button[data-action=\\\"grant-again\\\"]').click();", desktop, StringComparison.Ordinal);
        Assert.Contains("__arTabs.card('Tabs probe Gone').querySelector('button[data-action=\\\"purge\\\"]').click();", desktop, StringComparison.Ordinal);
        foreach (Match card in Regex.Matches(desktop, @"__arTabs\.card\('([^']*)'\)"))
        {
            Assert.StartsWith("Tabs probe ", card.Groups[1].Value, StringComparison.Ordinal);
        }

        Assert.Contains(".find(u => u.Name === 'tabsprobe-kid').UserId))()\",\n      \"save\": \"kidId\"", desktop.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("\"click\": \"#AccessRolesPanelUsers [data-ar-focus=\\\"user-${kidId}\\\"]\"", desktop, StringComparison.Ordinal);
        foreach (Match user in Regex.Matches(desktop, @"tabsprobe-\w*"))
        {
            Assert.Contains(user.Value, new[] { "tabsprobe-kid", "tabsprobe-teen" });
        }

        // The TV and phone checks only read: no API write, no button of the page pressed but the TV's Review (a dry run) and
        // Undo, nothing saved; their local storage is only the layout and the remembered tab.
        foreach (var name in new[] { "accessroles-admin-tabs-tv.json", "accessroles-admin-tabs-phone.json" })
        {
            var webcheck = File.ReadAllText(Path.Combine(Scripts, "webchecks", name));
            foreach (var write in new[] { "ApiClient.ajax", "otherAdmin", "__arTabs.press(", "data-action", "\"click\": \"#AccessRolesSettingsSave", "#AccessRolesSettingsSave').click", "#AccessRolesSettingsSave').focus", "#AccessRolesSettingsLookAgain", "#AccessRolesRevertConfirm", "#AccessRolesDriftRevertUser", "'Grant again'", "'Remove references'", "'Revert'", "#AccessRolesAuditExport" })
            {
                Assert.DoesNotContain(write, webcheck, StringComparison.Ordinal);
            }

            foreach (Match set in Regex.Matches(webcheck, @"localStorage\.setItem\('([^']*)'"))
            {
                Assert.Contains(set.Groups[1].Value, new[] { "layout", "jfar:adminTab" });
            }

            // What a click or a key may reach: the tabs, a card, Review (its dry run) and Undo.
            foreach (Match click in Regex.Matches(webcheck, "\"click\": \"([^\"]*)\""))
            {
                Assert.Matches(@"^#AccessRoles(Tab\w+|SettingsUndo)$", click.Groups[1].Value);
            }

            foreach (Match focus in Regex.Matches(webcheck, @"querySelector\('([^']*)'\)\.focus\(\)"))
            {
                Assert.Matches(@"^(#AccessRolesTab\w+|#AccessRolesSettingsReview|\.jfar-page-library)$", focus.Groups[1].Value);
            }
        }

        Assert.Equal(3, Regex.Matches(File.ReadAllText(Path.Combine(Scripts, "webchecks", "accessroles-admin-tabs-tv.json")), "\"key\": \"Enter\"").Count);
    }

    [Fact]
    public async Task NodeScripts_Parse()
    {
        // webcheck.mjs and index-probe.mjs run on Node 22; a syntax error would only show when a sandbox check starts.
        var node = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node"))
            .FirstOrDefault(File.Exists);
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        // The webcheck probes run in the page (initScript), so a syntax error would only show in a sandbox run too.
        var scripts = Directory.EnumerateFiles(Scripts, "*.mjs")
            .Concat(Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "*.mjs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks", "probes"), "*.js"))
            .ToList();
        Assert.Contains(scripts, s => Path.GetFileName(s) == "gen-pages.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "pages-probe.js");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "index-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "compare-snapshots.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "kit-boot-probe.js");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "api-docs-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "sandbox-policy.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "page-noise.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "redact.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "librarymaintenance-index-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "librarymaintenance-freshness-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "librarymaintenance-groups-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "librarymaintenance-access-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-api-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "identity-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "users-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "library-changes-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "library-demo-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-identity-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-query-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-clips-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-clip-sweep-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-clips-page-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-rows-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-player-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-je-import-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-settings-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-data-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-je-web-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-upgrade-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-busy-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-api-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-visibility-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-details-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-lifecycle-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "gen-details.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "gen-moderation.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "gen-settings.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-settings-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "gen-data.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-data-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-je-import-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "userratings-details-probe.js");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "accessroles-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "accessroles-sync-hooks-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "accessroles-admin-page-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "accessroles-admin-tabs-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "accessroles-recovery-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "accessroles-data-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "accessroles-je-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "accessroles-suite-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-clip-rename-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-clips-admin-probe.mjs");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "je-spy-probe.js");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "console-probe.js");
        Assert.Contains(scripts, s => Path.GetFileName(s) == "bookmarks-requests-probe.js");
        foreach (var script in scripts)
        {
            var start = new ProcessStartInfo(node!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--check");
            start.ArgumentList.Add(script);
            using var process = Process.Start(start)!;
            var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, $"{Path.GetFileName(script)}: {errors}");
        }
    }

    [Fact]
    public async Task ScriptNodeTests_Pass()
    {
        // scripts/tests: the request policy of webcheck.mjs (loopback only, no port 8096, no route that makes the sandbox
        // server download, such as Jellyfin Enhanced's CDN proxy), jellyfin-web's known page noise it doesn't count
        // (page-noise.mjs), api-docs-probe.mjs against a fake server, and dev-server.ps1's
        // background start that leaves a piped caller's handles alone (dev-server-launch), and the
        // Bookmarks requests probe's callers behind another plugin's fetch wrapper (Bookmarks 1.2.1).
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        var files = Directory.EnumerateFiles(Path.Combine(Scripts, "tests"), "*.test.mjs").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["api-docs-probe.test.mjs", "bookmarks-requests-probe.test.mjs", "dev-server-launch.test.mjs", "page-noise.test.mjs", "redact.test.mjs", "sandbox-policy.test.mjs"], files.Select(Path.GetFileName));
        foreach (var file in files)
        {
            var run = await NodeRunner.RunAsync(node!, ["--test", file], cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(run.ExitCode == 0, $"{Path.GetFileName(file)}: {run.Output}");
            Assert.Contains("# fail 0", run.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("# pass 0", run.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Webcheck_TakesItsRequestPolicyFromSandboxPolicy()
    {
        var source = File.ReadAllText(Path.Combine(Scripts, "webcheck.mjs"));
        Assert.Contains("import { assertSafeUrl, isAllowedRequest } from './sandbox-policy.mjs';", source, StringComparison.Ordinal);
        Assert.DoesNotContain("function isAllowedRequest", source, StringComparison.Ordinal);
        Assert.Contains("options.allowExternal || isAllowedRequest(p.request.url)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Webcheck_RedactsTokensBeforeItPrintsOrSavesItsResult()
    {
        // Session tokens and API keys in jellyfin-web's console lines and failed request URLs (the WebSocket's ?api_key=)
        // reached saved results three times during development; redact.mjs replaces them before anything is printed or saved.
        var source = File.ReadAllText(Path.Combine(Scripts, "webcheck.mjs"));
        Assert.Contains("import { redactSecrets } from './redact.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("const json = redactSecrets(JSON.stringify(result, null, 2));", source, StringComparison.Ordinal);
        Assert.Contains("redactSecrets(JSON.stringify({ ok: false, error: error.message }, null, 2))", source, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, @"process\.stdout\.write\(json"));
    }

    [Fact]
    public void Webcheck_DoesNotCountJellyfinWebsKnownNoise_ButKeepsIt()
    {
        // jellyfin-web's own exceptions in a headless browser (page-noise.mjs, such as its
        // new-item notification without permission) are recorded with `known` and counted neither by a noExceptions step
        // nor by --fail-on-exception; every other exception still is.
        var source = File.ReadAllText(Path.Combine(Scripts, "webcheck.mjs"));
        Assert.Contains("import { knownPageNoise } from './page-noise.mjs';", source, StringComparison.Ordinal);
        Assert.Contains("const known = knownPageNoise(exception);", source, StringComparison.Ordinal);
        Assert.Contains("result.exceptions.push(exception);", source, StringComparison.Ordinal);
        Assert.Contains("result.exceptions.filter((e) => !e.known && (!ignore || ", source, StringComparison.Ordinal);
        Assert.Contains("(!failOnException || result.exceptions.every((e) => e.known))", source, StringComparison.Ordinal);
        Assert.DoesNotContain("KNOWN_PAGE_NOISE = [", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WebcheckStepFiles_UseKnownActions()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var steps = document.RootElement.GetProperty("steps").EnumerateArray().ToList();
            Assert.NotEmpty(steps);
            foreach (var step in steps)
            {
                var actions = step.EnumerateObject().Select(p => p.Name).Where(n => WebcheckActions.Contains(n)).ToList();
                Assert.True(actions.Count == 1, $"{Path.GetFileName(file)}: every step needs exactly one action, found [{string.Join(", ", actions)}] in {step}.");
            }
        }
    }

    [Fact]
    public void Webcheck_SkipsAStepWhoseIfIsFalse()
    {
        // Bookmarks 1.1.1: "if" on a step runs it only while a page expression is truthy (je-coexist.json's second click and
        // its fallback); a skipped step passes, marked skipped, and saves nothing. It is a step option, not an action.
        var source = File.ReadAllText(Path.Combine(Scripts, "webcheck.mjs"));
        Assert.Contains("const skipped = 'if' in rawStep && !(await evaluate(substitute(rawStep.if, vars), { timeoutMs }));", source, StringComparison.Ordinal);
        Assert.Contains("if (skipped) entry.skipped = true;", source, StringComparison.Ordinal);
        Assert.Contains("switch (skipped ? null : action) {", source, StringComparison.Ordinal);
        Assert.Contains("if (!skipped && rawStep.save) vars[rawStep.save] = output;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("if", WebcheckActions);
    }

    [Fact]
    public void JeCoexistWebcheck_ClicksJesDrawerLink_OnceTheDrawerHasSettled_ThenOnceMore_ThenItsHref()
    {
        // Bookmarks 1.1.1 (a flaky check of the JE web probe, 2 runs in 6 on JE 12.12): JE adds its own drawer entry (Enhanced
        // Panel) above the libraries a moment after the drawer is built, which moves its Bookmarks link down by one section,
        // onto where Sign Out was, so a click measured just before signed the admin out. The webcheck waits for JE's entry (or
        // 5 s), the link's box unchanged for 600 ms and the link under its own centre, then clicks; when the address doesn't
        // change it clicks once more, then opens the link's href, and records which ("how JE's page was opened").
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Scripts, "webchecks", "je-coexist.json")));
        var steps = document.RootElement.GetProperty("steps").EnumerateArray().ToList();
        string? Text(JsonElement step, string property) => step.TryGetProperty(property, out var value) ? value.GetString() : null;
        var names = steps.Select(s => Text(s, "name")).ToList();
        var click = names.IndexOf("open JE's Bookmarks page from Plugin Pages' section");
        Assert.True(click > 2, "the first click on JE's drawer link");
        Assert.Equal("the address before the click", names[click - 1]);
        Assert.Null(Text(steps[click], "if"));
        var settle = Text(steps[click - 2], "waitFor")!;
        foreach (var part in new[] { "document.getElementById('jellyfinEnhancedSettingsLink')", "< 5000", "getBoundingClientRect()", "for (let i = 0; i < 6; i++)", "setTimeout(resolve, 100)", "box() !== first", "document.elementFromPoint(", "el.contains(hit)" })
        {
            Assert.Contains(part, settle, StringComparison.Ordinal);
        }

        Assert.Equal("location.hash !== __vars.hashBeforeClick", Text(steps[click + 1], "waitFor"));
        Assert.True(steps[click + 1].GetProperty("optional").GetBoolean());
        var again = steps.Single(s => Text(s, "name") == "open JE's Bookmarks page from Plugin Pages' section, once more");
        Assert.Equal("location.hash === __vars.hashBeforeClick", Text(again, "if"));
        Assert.Equal(Text(steps[click], "click"), Text(again, "click"));
        var fallback = steps.Single(s => (Text(s, "name") ?? string.Empty).StartsWith("fallback (recorded)", StringComparison.Ordinal));
        Assert.Equal("location.hash === __vars.hashBeforeClick", Text(fallback, "if"));
        Assert.Contains("location.href = a.href;", Text(fallback, "eval"), StringComparison.Ordinal);
        Assert.Equal("fallbackHref", Text(fallback, "save"));
        Assert.True(names.IndexOf(Text(fallback, "name")) < names.IndexOf("JE's page in Plugin Pages' host, not ours"));
        Assert.Contains("fallback: !!__vars.fallbackHref", Text(steps.Single(s => Text(s, "name") == "how JE's page was opened"), "eval"), StringComparison.Ordinal);

        var probe = File.ReadAllText(Path.Combine(Scripts, "bookmarks-je-web-probe.mjs"));
        Assert.Contains("const opened = co.value('how JE\\'s page was opened') || null;", probe, StringComparison.Ordinal);
        Assert.Contains("evidence.jeDrawerLink = opened;", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Webcheck_ContextStep_OpensAPageInItsOwnBrowserContext_UnderTheSameRequestPolicy()
    {
        // Two windows of one member: {"context": "<name>"} runs the next steps in a page of its own
        // browser context (its own storage, signed in on its own), made on first use; every page gets the same Fetch
        // interception, so the sandbox policy holds in each, and each page's requests are answered on its own session.
        var source = File.ReadAllText(Path.Combine(Scripts, "webcheck.mjs"));
        Assert.Contains("const { browserContextId } = await cdp.send('Target.createBrowserContext', { disposeOnDetach: true });", source, StringComparison.Ordinal);
        Assert.Contains("await own('Fetch.enable', { patterns: [{ urlPattern: '*' }] });", source, StringComparison.Ordinal);
        Assert.Contains("const reply = (method, params) => cdp.send(method, params, page.sessionId);", source, StringComparison.Ordinal);
        Assert.Contains("const send = (method, params = {}, timeoutMs) => cdp.send(method, params, current.sessionId, timeoutMs);", source, StringComparison.Ordinal);
        Assert.Contains("case 'context': output = await switchContext(value); break;", source, StringComparison.Ordinal);
        Assert.Contains("current = await openPage('main', null);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WebcheckActions_MatchWebcheckScript()
    {
        // The list above is webcheck.mjs's own; a new step action has to be added to both.
        var source = File.ReadAllText(Path.Combine(Scripts, "webcheck.mjs"));
        var match = Regex.Match(source, @"const actions = \[(?<list>[^\]]*)\];");
        Assert.True(match.Success, "webcheck.mjs declares `const actions = [...]`.");
        var actions = match.Groups["list"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.Trim('\''))
            .ToArray();
        Assert.Equal(WebcheckActions, actions);
    }

    [Fact]
    public async Task PageWebchecks_AreWhatTheirGeneratorWrites()
    {
        // scripts/webchecks/pages-*.json come from gen-pages.mjs; an edit to one of them alone would be lost.
        var node = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node"))
            .FirstOrDefault(File.Exists);
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var output = new TempDirectory();
        var start = new ProcessStartInfo(node!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(Scripts, "webchecks", "gen-pages.mjs"));
        start.ArgumentList.Add("--out");
        start.ArgumentList.Add(output.Path);
        using (var process = Process.Start(start)!)
        {
            var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, errors);
        }

        var generated = Directory.EnumerateFiles(output.Path, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "pages-*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal),
            generated);
        foreach (var file in generated)
        {
            var written = await File.ReadAllTextAsync(Path.Combine(output.Path, file!), TestContext.Current.CancellationToken);
            var committed = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", file!), TestContext.Current.CancellationToken);
            Assert.True(
                written == committed.ReplaceLineEndings("\n"),
                $"scripts/webchecks/{file} differs from what gen-pages.mjs writes; run node scripts/webchecks/gen-pages.mjs.");
        }
    }

    [Fact]
    public async Task MoveDialogReleaseWebcheck_IsWhatItsGeneratorWrites()
    {
        // librarymaintenance-move-dialog-release.json comes from gen-move-dialog-release.mjs, out of the Debug webcheck: an
        // edit of the Release variant alone would be lost, and one of the Debug webcheck must reach it (LM 1.0.3).
        var node = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node"))
            .FirstOrDefault(File.Exists);
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var output = new TempDirectory();
        var start = new ProcessStartInfo(node!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(Scripts, "webchecks", "gen-move-dialog-release.mjs"));
        start.ArgumentList.Add("--out");
        start.ArgumentList.Add(output.Path);
        using (var process = Process.Start(start)!)
        {
            var errors = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, errors);
        }

        const string Variant = "librarymaintenance-move-dialog-release.json";
        var written = await File.ReadAllTextAsync(Path.Combine(output.Path, Variant), TestContext.Current.CancellationToken);
        var committed = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", Variant), TestContext.Current.CancellationToken);
        Assert.True(
            written == committed.ReplaceLineEndings("\n"),
            $"scripts/webchecks/{Variant} differs from what gen-move-dialog-release.mjs writes; run node scripts/webchecks/gen-move-dialog-release.mjs.");
        using var steps = JsonDocument.Parse(written);
        Assert.Equal(269, steps.RootElement.GetProperty("steps").GetArrayLength());
    }

    [Fact]
    public async Task StarWebchecks_AreWhatTheirGeneratorWrites()
    {
        // scripts/webchecks/userratings-stars-*.json come from gen-stars.mjs; an edit to one of them alone would be
        // lost. Their probe mounts the served 30-stars.js and never calls User Ratings' API. One webcheck uses a pen.
        // The probe also answers the details section's two reads itself with a 404 (the section shows
        // nothing then), so they never reach the server and the section's own slider stays out of the page under test.
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var output = new TempDirectory();
        var run = await NodeRunner.RunAsync(node!, [Path.Combine(Scripts, "webchecks", "gen-stars.mjs"), "--out", output.Path], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(run.ExitCode == 0, run.Output);

        var generated = Directory.EnumerateFiles(output.Path, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["userratings-stars-desktop.json", "userratings-stars-je.json", "userratings-stars-pen.json", "userratings-stars-touch.json", "userratings-stars-tv.json"], generated);
        Assert.Equal(generated, Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "userratings-stars-*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var file in generated)
        {
            var written = await File.ReadAllTextAsync(Path.Combine(output.Path, file!), TestContext.Current.CancellationToken);
            var committed = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", file!), TestContext.Current.CancellationToken);
            Assert.True(written == committed.ReplaceLineEndings("\n"), $"scripts/webchecks/{file} differs from what gen-stars.mjs writes; run node scripts/webchecks/gen-stars.mjs.");
            Assert.Contains("\"file\": \"probes/userratings-stars-probe.js\"", committed, StringComparison.Ordinal);
        }

        var probe = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", "probes", "userratings-stars-probe.js"), TestContext.Current.CancellationToken);
        Assert.Contains("'// ---- Web/js/30-stars.js\\n'", probe, StringComparison.Ordinal);
        Assert.Contains("window.__jfkit && window.__jfkit.jfur", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("UserRatings/Items", probe, StringComparison.Ordinal);
        Assert.Contains("if (method === 'GET' && /\\/UserRatings\\/(Capabilities|Items\\/[0-9a-f-]+)(\\?|$)/i.test(url)) {", probe, StringComparison.Ordinal);
        Assert.Contains("return Promise.resolve(new Response(JSON.stringify({ title: 'Not Found', status: 404, ", probe, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(probe, @"originalFetch\("));
        Assert.DoesNotContain("8096", probe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailsWebchecks_AreWhatTheirGeneratorWrites()
    {
        // scripts/webchecks/userratings-details-*.json come from gen-details.mjs; an edit to one of them alone
        // would be lost. Their probe watches the section the served script mounts and answers a request in its place only
        // when a step asks it to (a 503, a 404 after the write, IsPartial); every webcheck deletes the member's rows it made.
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var output = new TempDirectory();
        var run = await NodeRunner.RunAsync(node!, [Path.Combine(Scripts, "webchecks", "gen-details.mjs"), "--out", output.Path], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(run.ExitCode == 0, run.Output);

        var generated = Directory.EnumerateFiles(output.Path, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["userratings-details-chip-je.json", "userratings-details-chip-touch.json", "userratings-details-chip-tv.json", "userratings-details-chip.json", "userratings-details-desktop.json", "userratings-details-experimental.json", "userratings-details-je.json", "userratings-details-part2.json", "userratings-details-reviews.json", "userratings-details-three-windows.json", "userratings-details-touch.json", "userratings-details-tv.json", "userratings-details-windows.json"],
            generated);
        Assert.Equal(generated, Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "userratings-details-*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var file in generated)
        {
            var written = await File.ReadAllTextAsync(Path.Combine(output.Path, file!), TestContext.Current.CancellationToken);
            var committed = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", file!), TestContext.Current.CancellationToken);
            Assert.True(written == committed.ReplaceLineEndings("\n"), $"scripts/webchecks/{file} differs from what gen-details.mjs writes; run node scripts/webchecks/gen-details.mjs.");
            Assert.Contains("\"file\": \"probes/userratings-details-probe.js\"", committed, StringComparison.Ordinal);
            Assert.DoesNotContain("8096", committed, StringComparison.Ordinal);
            if (file != "userratings-details-reviews.json")
            {
                // The member's own rows on the movies it used, deleted at the end (the reviews webcheck writes nothing; its
                // seed is userratings-details-probe.mjs's).
                Assert.Contains("clean: Long Test Movie at the end without a rating of this member", committed, StringComparison.Ordinal);
            }
        }

        var probe = (await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", "probes", "userratings-details-probe.js"), TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
        Assert.Contains("if (rule && rule.mode === 'stub') {", probe, StringComparison.Ordinal);
        Assert.Contains("if (!/\\/UserRatings\\//i.test(parsed.pathname) || /\\/UserRatings\\/ClientScript/i.test(parsed.pathname)) {\n            return originalFetch(input, init);", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("8096", probe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModerationWebchecks_AreWhatTheirGeneratorWrites()
    {
        // scripts/webchecks/userratings-moderation-*.json (the settings page's Moderation tab) and
        // userratings-remove-*.json (Remove... on the details page) come from gen-moderation.mjs; an edit to one of them
        // alone would be lost. Each removes every rating on the movies it uses through the moderation API before and after
        // the run, and deletes the synthetic member it creates; the member's password is random and kept nowhere.
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var output = new TempDirectory();
        var run = await NodeRunner.RunAsync(node!, [Path.Combine(Scripts, "webchecks", "gen-moderation.mjs"), "--out", output.Path], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(run.ExitCode == 0, run.Output);

        var generated = Directory.EnumerateFiles(output.Path, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["userratings-moderation-desktop.json", "userratings-moderation-phone.json", "userratings-moderation-tv.json", "userratings-remove-desktop.json", "userratings-remove-experimental.json", "userratings-remove-je.json"],
            generated);
        Assert.Equal(
            generated,
            Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "userratings-moderation-*.json")
                .Concat(Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "userratings-remove-*.json"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
        foreach (var file in generated)
        {
            var written = await File.ReadAllTextAsync(Path.Combine(output.Path, file!), TestContext.Current.CancellationToken);
            var committed = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", file!), TestContext.Current.CancellationToken);
            Assert.True(written == committed.ReplaceLineEndings("\n"), $"scripts/webchecks/{file} differs from what gen-moderation.mjs writes; run node scripts/webchecks/gen-moderation.mjs.");
            Assert.Contains("\"file\": \"probes/userratings-details-probe.js\"", committed, StringComparison.Ordinal);
            Assert.DoesNotContain("8096", committed, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"clean: before the run: every rating on movieId and taggedId removed through the moderation API\"", committed, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"clean: at the end: every rating on ", committed, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"clean: the synthetic member deleted\"", committed, StringComparison.Ordinal);
            if (file!.StartsWith("userratings-moderation-", StringComparison.Ordinal))
            {
                // The Moderation tab's checks create the member with the long right-to-left name: a random password, not saved.
                Assert.Contains("crypto.getRandomValues", committed, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("\"save\": \"pw\"", committed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SettingsWebchecks_AreWhatTheirGeneratorWrites()
    {
        // scripts/webchecks/userratings-settings-desktop.json, -tv.json and -phone.json (the settings page's
        // Settings tab) come from gen-settings.mjs; an edit to one of them alone would be lost. userratings-settings-page.json
        // (the page's tabs) is written by hand. Each reads the settings first and stops unless the ones it changes
        // are the plugin's defaults, puts every setting back as it began through the Settings API and compares them, and the
        // desktop one removes every rating on its movie through the moderation API before and after the run.
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var output = new TempDirectory();
        var run = await NodeRunner.RunAsync(node!, [Path.Combine(Scripts, "webchecks", "gen-settings.mjs"), "--out", output.Path], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(run.ExitCode == 0, run.Output);

        var generated = Directory.EnumerateFiles(output.Path, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["userratings-settings-desktop.json", "userratings-settings-phone.json", "userratings-settings-tv.json"], generated);
        Assert.Equal(
            generated,
            Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "userratings-settings-*.json")
                .Select(Path.GetFileName)
                .Where(f => f != "userratings-settings-page.json")
                .Order(StringComparer.Ordinal));
        foreach (var file in generated)
        {
            var written = await File.ReadAllTextAsync(Path.Combine(output.Path, file!), TestContext.Current.CancellationToken);
            var committed = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", file!), TestContext.Current.CancellationToken);
            Assert.True(written == committed.ReplaceLineEndings("\n"), $"scripts/webchecks/{file} differs from what gen-settings.mjs writes; run node scripts/webchecks/gen-settings.mjs.");
            Assert.Contains("\"file\": \"probes/userratings-details-probe.js\"", committed, StringComparison.Ordinal);
            Assert.DoesNotContain("8096", committed, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"the settings this run changes are at their defaults\"", committed, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"clean: every setting as it was before the run (what differs sent back through the Settings API)\"", committed, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"clean: every setting as it began\"", committed, StringComparison.Ordinal);
            if (file == "userratings-settings-desktop.json")
            {
                Assert.Contains("\"name\": \"clean: before the run: every rating on Long Test Movie removed through the moderation API\"", committed, StringComparison.Ordinal);
                Assert.Contains("\"name\": \"clean: Long Test Movie has no rating left\"", committed, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task DataWebchecks_AreWhatTheirGeneratorWrites()
    {
        // scripts/webchecks/userratings-data-desktop.json, -tv.json and -phone.json (the settings page's Data tab)
        // and userratings-detached-desktop.json (its detached list) come from gen-data.mjs; an edit to one of them alone
        // would be lost. The desktop ones remove every rating on Long Test Movie through the moderation API before and after
        // the run, and the detached one leaves none of its ratings detached; the export's file is kept in the page
        // (a[download] clicks prevented), so no file is written; the TV and phone ones change nothing and compare the
        // snapshots at the end.
        var node = NodeRunner.FindNode();
        Assert.SkipWhen(node is null, "Node.js is not on the PATH.");

        using var output = new TempDirectory();
        var run = await NodeRunner.RunAsync(node!, [Path.Combine(Scripts, "webchecks", "gen-data.mjs"), "--out", output.Path], cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(run.ExitCode == 0, run.Output);

        var generated = Directory.EnumerateFiles(output.Path, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["userratings-data-desktop.json", "userratings-data-je-desktop.json", "userratings-data-phone.json", "userratings-data-tv.json", "userratings-detached-desktop.json"], generated);
        Assert.Equal(
            generated,
            Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "userratings-data-*.json")
                .Concat(Directory.EnumerateFiles(Path.Combine(Scripts, "webchecks"), "userratings-detached-*.json"))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
        foreach (var file in generated)
        {
            var written = await File.ReadAllTextAsync(Path.Combine(output.Path, file!), TestContext.Current.CancellationToken);
            var committed = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", file!), TestContext.Current.CancellationToken);
            Assert.True(written == committed.ReplaceLineEndings("\n"), $"scripts/webchecks/{file} differs from what gen-data.mjs writes; run node scripts/webchecks/gen-data.mjs.");
            Assert.Contains("\"file\": \"probes/userratings-details-probe.js\"", committed, StringComparison.Ordinal);
            Assert.DoesNotContain("8096", committed, StringComparison.Ordinal);
            Assert.DoesNotContain("\"save\": \"pw\"", committed, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"no uncaught exception\"", committed, StringComparison.Ordinal);
            if (file == "userratings-data-je-desktop.json")
            {
                // The je sandbox's JE import, undone at the end by restoring the import's own snapshot.
                Assert.Contains("\"name\": \"clean: the import undone: its snapshot restored through the API\"", committed, StringComparison.Ordinal);
            }
            else if (file!.EndsWith("-desktop.json", StringComparison.Ordinal))
            {
                Assert.Contains("\"name\": \"clean: before the run: every rating on Long Test Movie removed through the moderation API\"", committed, StringComparison.Ordinal);
                Assert.Contains("\"name\": \"clean: Long Test Movie has no rating left\"", committed, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("\"name\": \"clean: the snapshots as before the run: nothing restored or imported\"", committed, StringComparison.Ordinal);
            }
        }

        var desktop = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", "userratings-data-desktop.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"name\": \"export: the download kept in the page: a[download] clicks prevented, so no file is written\"", desktop, StringComparison.Ordinal);
        Assert.Contains("e.preventDefault();", desktop, StringComparison.Ordinal);
        var detached = await File.ReadAllTextAsync(Path.Combine(Scripts, "webchecks", "userratings-detached-desktop.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"name\": \"clean: none of the run's ratings left detached\"", detached, StringComparison.Ordinal);
    }

    [Fact]
    public void WebcheckStepFiles_InitScriptFilesExist()
    {
        // webcheck resolves a relative initScript file in the steps file's folder first (scripts/README.md).
        var folder = Path.Combine(Scripts, "webchecks");
        foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var step in document.RootElement.GetProperty("steps").EnumerateArray())
            {
                if (step.TryGetProperty("initScript", out var script) && script.ValueKind == JsonValueKind.Object)
                {
                    var probe = script.GetProperty("file").GetString()!;
                    Assert.True(File.Exists(Path.Combine(folder, probe)), $"{Path.GetFileName(file)}: initScript file {probe} is missing from scripts/webchecks.");
                }
            }
        }
    }

    private static string PowerShellQuote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
