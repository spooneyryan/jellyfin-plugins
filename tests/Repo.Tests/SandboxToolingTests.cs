using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// The scripts' sandbox and deploy safeguards that early measurements called for: Deploy's reference-version check
/// against the Jellyfin its target is for, JavaScript Injector in the je sandbox, and the scheduled tasks that
/// would reach the internet on their own, which every sandbox keeps without triggers (Jellyfin's "Update Plugins";
/// Jellyfin Enhanced's "Refresh CDN Assets" and "Send Anonymous Usage Report"), with the check that decides
/// Initialize-Sandbox's assertion. They run the functions of scripts/JellyfinSandbox.psm1 in Windows PowerShell 5.1
/// against temporary folders and synthetic assemblies; nothing here starts a server or writes outside the temporary
/// folders.
/// </summary>
public sealed partial class SandboxToolingTests
{
    private const string UpdatePlugins = "Emby.Server.Implementations.ScheduledTasks.Tasks.PluginUpdateTask";

    private const string CdnRefresh = "Jellyfin.Plugin.JellyfinEnhanced.ScheduledTasks.RefreshCdnAssetsTask";

    private const string UsageReport = "Jellyfin.Plugin.JellyfinEnhanced.ScheduledTasks.AnalyticsReportTask";

    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();

    // --------------------------------------------------------------------------------- tasks kept without triggers

    [Fact]
    public async Task DisabledTasks_AreJellyfinsUpdatePlugins_AndJellyfinEnhancedsCdnRefreshAndUsageReport_WithTheIdsJellyfinGivesThem()
    {
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "foreach ($c in @(@(), @('JellyfinEnhanced'), @('FileTransformation', 'PluginPages', 'CustomTabs', 'JavaScriptInjector'))) {\n"
            + "  'set:' + ($c -join ',')\n"
            + "  foreach ($t in @(Get-SandboxDisabledTasks -Community $c)) { $t.Owner + '|' + $t.Type + '|' + $t.Key + '|' + (Get-JellyfinTaskId -TypeName $t.Type) }\n"
            + "}\n");
        Assert.True(run.ExitCode == 0, run.Output);

        var sets = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        List<string[]>? current = null;
        foreach (var line in run.Lines)
        {
            if (line.StartsWith("set:", StringComparison.Ordinal))
            {
                current = sets[line[4..]] = [];
            }
            else
            {
                current!.Add(line.Split('|'));
            }
        }

        // JE's "Send Anonymous Usage Report" uploads usage statistics when AnalyticsEnabled is on; unless it is listed
        // here it keeps an IntervalTrigger, first due an hour after a start, in every sandbox with JE.
        Assert.Equal([UpdatePlugins], sets[string.Empty].Select(t => t[1]));
        Assert.Equal([UpdatePlugins, CdnRefresh, UsageReport], sets["JellyfinEnhanced"].Select(t => t[1]));
        Assert.Equal([UpdatePlugins], sets["FileTransformation,PluginPages,CustomTabs,JavaScriptInjector"].Select(t => t[1]));
        Assert.Equal(["PluginUpdates", "JellyfinEnhancedRefreshCdnAssets", "JellyfinEnhancedAnalyticsReport"], sets["JellyfinEnhanced"].Select(t => t[2]));

        // The id is what Jellyfin itself computes (ScheduledTaskWorker.Id: the type name's GetMD5), in the "D" format of
        // the trigger file's name; the two values were also seen as history file names in sandboxes run by 10.11.5.
        foreach (var task in sets["JellyfinEnhanced"])
        {
            Assert.Equal(new Guid(task[1].GetMD5().ToString("N", CultureInfo.InvariantCulture)).ToString("D"), task[3]);
        }

        Assert.Equal("f9b057c0-54e9-e6da-ee4a-88ffd146a403", sets["JellyfinEnhanced"][0][3]);
        Assert.Equal("e32160f3-c9e0-dcb2-33c8-11fe73c65b1a", sets["JellyfinEnhanced"][1][3]);
        Assert.Equal("f3e92136-da16-23c2-26a0-c0f15a08f3b7", sets["JellyfinEnhanced"][2][3]);
    }

    [Fact]
    public async Task TriggerFiles_AreEmptyTriggerLists_InTheSandboxConfigFolder()
    {
        using var sandbox = new TempDirectory();
        var config = Path.Combine(sandbox.Path, "data", "config");
        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"$written = @(Set-SandboxTaskTriggersOff -ConfigDir '{config}' -Tasks @(Get-SandboxDisabledTasks -Community @('JellyfinEnhanced')))\n"
            + "foreach ($w in $written) { $w.Name + '|' + $w.File }\n"
            + $"@(Set-SandboxTaskTriggersOff -ConfigDir '{config}' -Tasks @(Get-SandboxDisabledTasks -Community @('JellyfinEnhanced'))).Count\n");
        Assert.True(run.ExitCode == 0, run.Output);

        var folder = Path.Combine(config, "ScheduledTasks");
        Assert.Equal(
            [
                "Update Plugins|" + Path.Combine(folder, "f9b057c0-54e9-e6da-ee4a-88ffd146a403.js"),
                "Refresh CDN Assets|" + Path.Combine(folder, "e32160f3-c9e0-dcb2-33c8-11fe73c65b1a.js"),
                "Send Anonymous Usage Report|" + Path.Combine(folder, "f3e92136-da16-23c2-26a0-c0f15a08f3b7.js"),
                "3",
            ],
            run.Lines);
        Assert.Equal(3, Directory.GetFiles(folder).Length);
        foreach (var file in Directory.GetFiles(folder))
        {
            var bytes = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
            Assert.Equal("[]"u8.ToArray(), bytes);

            // What ScheduledTaskWorker.LoadTriggerSettings reads: an empty list, not null, so the defaults don't apply.
            var triggers = JsonSerializer.Deserialize<TaskTriggerInfo[]>(bytes, JsonDefaults.Options);
            Assert.NotNull(triggers);
            Assert.Empty(triggers);
        }
    }

    [Fact]
    public void DevServer_WritesTheTriggerFiles_BeforeEveryStart_AndInitializeSandbox_AssertsThem()
    {
        var devServer = File.ReadAllText(Path.Combine(Root, "scripts", "dev-server.ps1"));
        var write = devServer.IndexOf("Set-SandboxTaskTriggersOff -ConfigDir $configDir", StringComparison.Ordinal);
        var start = devServer.IndexOf("Start-Process -FilePath $jellyfinExe", StringComparison.Ordinal);
        Assert.True(write > 0 && start > write, "dev-server.ps1 writes the trigger files before it starts jellyfin.exe.");
        Assert.DoesNotContain("$firstStart", devServer.Substring(write - 400, 400), StringComparison.Ordinal);

        // Initialize-Sandbox reads GET /ScheduledTasks and fails on every problem the module's Test-SandboxTasksOff finds
        // (its logic is tested below), with the start time sandbox.json records and JE's cdn-cache folder.
        var initialize = File.ReadAllText(Path.Combine(Root, "scripts", "Initialize-Sandbox.ps1"));
        Assert.Contains("Assert-SandboxTasksOff -BaseUrl $baseUrl -Token $token -Community $community -Sandbox $sandbox", initialize, StringComparison.Ordinal);
        Assert.Contains("$tasks = @(Invoke-SandboxApi -BaseUrl $BaseUrl -Path 'ScheduledTasks' -Token $Token)", initialize, StringComparison.Ordinal);
        Assert.Contains("$startedUtc = $Sandbox.State.StartedUtc", initialize, StringComparison.Ordinal);
        Assert.Contains(@"$cdnCache = Join-Path $Sandbox.PluginsDir 'configurations\Jellyfin.Plugin.JellyfinEnhanced\cdn-cache'", initialize, StringComparison.Ordinal);
        Assert.Contains("$result = Test-SandboxTasksOff -Tasks $tasks -StartedUtc $startedUtc -Community $Community -CdnCacheDir $cdnCache", initialize, StringComparison.Ordinal);
        Assert.Contains("if (@($result.Problems).Count -gt 0) {", initialize, StringComparison.Ordinal);
        Assert.DoesNotContain("function ConvertTo-UtcTime", initialize, StringComparison.Ordinal);
        Assert.Contains("api-docs-probe.mjs') --sandbox $Name --summary", initialize, StringComparison.Ordinal);

        // dev-server.ps1 records the start time the check compares with, in UTC.
        Assert.Contains("$state.StartedUtc = (Get-Date).ToUniversalTime().ToString('o')", devServer, StringComparison.Ordinal);
    }

    [Fact]
    public void DevServer_AndInstallRealServer_PassTheirJellyfinDir_ToDeploy()
    {
        // The reference-version check must compare with the Jellyfin the sandbox (or the real server) runs, which is the
        // script's own -JellyfinDir, not whatever Deploy-Plugin.ps1 defaults to.
        foreach (var script in new[] { "dev-server.ps1", "Install-RealServer.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(Root, "scripts", script));
            var calls = DeployCall().Matches(text);
            Assert.True(calls.Count > 0, script + " calls Deploy-Plugin.ps1.");
            Assert.All(calls, call => Assert.Contains(" -JellyfinDir $JellyfinDir", call.Value, StringComparison.Ordinal));
            Assert.Contains(@"[string] $JellyfinDir = 'C:\ProgramData\Jellyfin\Server'", text, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------------ the download-tasks assertion

    [Fact]
    public async Task TasksOff_PassesTasksWithNoTriggersThatHaveNotRunSinceTheStart_AndFailsEveryOtherCase()
    {
        // Test-SandboxTasksOff decides Initialize-Sandbox's download-tasks check. Each case is what GET /ScheduledTasks
        // returns (TaskInfo as Jellyfin 10.11.5 serializes it: LastExecutionResult left out when null, Triggers always
        // there), the start time from sandbox.json and JE's cdn-cache folder.
        const string Started = "2026-09-29T12:00:00.0000000Z";
        using var temp = new TempDirectory();
        string[] none = [];
        string[] je = ["JellyfinEnhanced"];
        object[] jeTasks = [ApiTask(UpdatePlugins, "PluginUpdates"), ApiTask(CdnRefresh, "JellyfinEnhancedRefreshCdnAssets"), ApiTask(UsageReport, "JellyfinEnhancedAnalyticsReport")];
        var cases = new Dictionary<string, (string[] Community, object[] Tasks, string? CdnFileUtc)>(StringComparer.Ordinal)
        {
            ["pass-never-run"] = (none, [ApiTask(UpdatePlugins, "PluginUpdates")], null),
            ["pass-je"] = (je, jeTasks, null),
            ["pass-run-before-start"] = (none, [ApiTask(UpdatePlugins, "PluginUpdates", lastStart: "2026-09-29T11:59:00.0000000Z")], null),

            // 13:30+02:00 is 11:30 UTC: before the start, although 13:30 read as UTC or as local time might not be.
            ["pass-run-before-start-offset"] = (none, [ApiTask(UpdatePlugins, "PluginUpdates", lastStart: "2026-09-29T13:30:00+02:00")], null),
            ["pass-old-cdn-file"] = (je, jeTasks, "2026-09-28T12:00:00Z"),
            ["fail-trigger"] = (none, [ApiTask(UpdatePlugins, "PluginUpdates", triggers: [new { Type = "IntervalTrigger", IntervalTicks = 864000000000 }])], null),
            ["fail-run-after-start"] = (none, [ApiTask(UpdatePlugins, "PluginUpdates", lastStart: "2026-09-29T12:00:03.1234567Z")], null),

            // 08:30-05:00 is 13:30 UTC: after the start, although 08:30 read as UTC or as local time might not be.
            ["fail-run-after-start-offset"] = (none, [ApiTask(UpdatePlugins, "PluginUpdates", lastStart: "2026-09-29T08:30:00-05:00")], null),
            ["fail-running"] = (none, [ApiTask(UpdatePlugins, "PluginUpdates", state: "Running")], null),
            ["fail-missing-usage-report"] = (je, [ApiTask(UpdatePlugins, "PluginUpdates"), ApiTask(CdnRefresh, "JellyfinEnhancedRefreshCdnAssets")], null),
            ["fail-wrong-id"] = (none, [ApiTask("Emby.Server.Implementations.ScheduledTasks.Tasks.RenamedPluginUpdateTask", "PluginUpdates")], null),
            ["fail-no-triggers-property"] = (none, [new { Name = "Update Plugins", State = "Idle", Id = TaskId(UpdatePlugins), Key = "PluginUpdates" }], null),
            ["fail-new-cdn-file"] = (je, jeTasks, "2026-09-29T12:05:00Z"),
        };

        foreach (var (name, (community, tasks, cdnFileUtc)) in cases)
        {
            await File.WriteAllTextAsync(
                Path.Combine(temp.Path, name + ".json"),
                JsonSerializer.Serialize(new { Community = community, StartedUtc = Started, Tasks = tasks }),
                TestContext.Current.CancellationToken);
            if (cdnFileUtc is not null)
            {
                var file = Path.Combine(temp.Path, name + "-cdn", "flags", "de.svg");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, "<svg/>", TestContext.Current.CancellationToken);
                File.SetLastWriteTimeUtc(file, DateTime.Parse(cdnFileUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
            }
        }

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"foreach ($file in @(Get-ChildItem -LiteralPath '{temp.Path}' -Filter '*.json' -File | Sort-Object Name)) {{\n"
            + "  $case = [System.IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json\n"
            + "  $tasks = @($case.Tasks | ForEach-Object { $_ })\n"
            + "  $cdn = Join-Path $file.DirectoryName ($file.BaseName + '-cdn')\n"
            + "  $r = Test-SandboxTasksOff -Tasks $tasks -StartedUtc $case.StartedUtc -Community @($case.Community) -CdnCacheDir $cdn\n"
            + "  [Console]::Out.WriteLine('case|' + $file.BaseName + '|' + @($r.Problems).Count + '|' + $r.StartedUtc.ToString('o'))\n"
            + "  foreach ($p in @($r.Problems)) { [Console]::Out.WriteLine('problem|' + $file.BaseName + '|' + $p) }\n"
            + "  foreach ($l in @($r.Report)) { [Console]::Out.WriteLine('report|' + $file.BaseName + '|' + $l) }\n"
            + "}\n");
        Assert.True(run.ExitCode == 0, run.Output);

        var caseLines = run.Lines.Where(l => l.StartsWith("case|", StringComparison.Ordinal)).Select(l => l.Split('|')).ToList();
        Assert.Equal(cases.Keys.Order(StringComparer.Ordinal), caseLines.Select(p => p[1]));
        Assert.All(caseLines, p => Assert.Equal(Started, p[3]));
        var counts = caseLines.ToDictionary(p => p[1], p => int.Parse(p[2], CultureInfo.InvariantCulture), StringComparer.Ordinal);
        string[] Problems(string name) => [.. run.Lines.Where(l => l.StartsWith("problem|" + name + "|", StringComparison.Ordinal)).Select(l => l[("problem|" + name + "|").Length..])];
        string[] Report(string name) => [.. run.Lines.Where(l => l.StartsWith("report|" + name + "|", StringComparison.Ordinal)).Select(l => l[("report|" + name + "|").Length..])];

        foreach (var name in cases.Keys)
        {
            var expected = name.StartsWith("pass-", StringComparison.Ordinal) ? 0 : 1;
            Assert.True(counts[name] == expected, $"{name}: {counts[name]} problem(s), expected {expected}: {string.Join(" / ", Problems(name))}");
        }

        Assert.Equal(["'Update Plugins': no triggers, never run"], Report("pass-never-run"));
        Assert.Equal(
            ["'Update Plugins': no triggers, never run", "'Refresh CDN Assets': no triggers, never run", "'Send Anonymous Usage Report': no triggers, never run", "Jellyfin Enhanced's CDN cache: 0 file(s), none written since this start"],
            Report("pass-je"));
        Assert.Equal(["'Update Plugins': no triggers, last run 2026-09-29 11:59:00Z, before this start"], Report("pass-run-before-start"));
        Assert.Equal(["'Update Plugins': no triggers, last run 2026-09-29 11:30:00Z, before this start"], Report("pass-run-before-start-offset"));
        Assert.Contains("Jellyfin Enhanced's CDN cache: 1 file(s), none written since this start", Report("pass-old-cdn-file"));

        Assert.StartsWith("Jellyfin's task 'Update Plugins' has 1 trigger(s) (IntervalTrigger). Restart the sandbox with dev-server.ps1", Problems("fail-trigger").Single(), StringComparison.Ordinal);
        Assert.Equal("Jellyfin's task 'Update Plugins' ran at 2026-09-29T12:00:03.1234567Z, after the server started at 2026-09-29T12:00:00.0000000Z.", Problems("fail-run-after-start").Single());
        Assert.Equal("Jellyfin's task 'Update Plugins' ran at 2026-09-29T13:30:00.0000000Z, after the server started at 2026-09-29T12:00:00.0000000Z.", Problems("fail-run-after-start-offset").Single());
        Assert.Equal("Jellyfin's task 'Update Plugins' is Running, not Idle.", Problems("fail-running").Single());
        Assert.StartsWith("Jellyfin Enhanced's task 'Send Anonymous Usage Report' (key JellyfinEnhancedAnalyticsReport) is not listed.", Problems("fail-missing-usage-report").Single(), StringComparison.Ordinal);
        Assert.Contains($"but its trigger file is written for f9b057c054e9e6daee4a88ffd146a403 ({UpdatePlugins}). The task type was renamed", Problems("fail-wrong-id").Single(), StringComparison.Ordinal);
        Assert.Contains("without a Triggers property", Problems("fail-no-triggers-property").Single(), StringComparison.Ordinal);
        Assert.StartsWith(@"Jellyfin Enhanced wrote 1 file(s) into its CDN cache since the server started, downloaded from the internet (flags\de.svg).", Problems("fail-new-cdn-file").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TasksOff_RefusesAMissingStartTime_AndAJellyfinEnhancedSandboxWithoutItsCdnCacheFolder()
    {
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "try { Test-SandboxTasksOff -Tasks @() -StartedUtc '' | Out-Null; [Console]::Out.WriteLine('passed') } catch { [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }\n"
            + "try { Test-SandboxTasksOff -Tasks @() -StartedUtc '2026-09-29T12:00:00Z' -Community @('JellyfinEnhanced') | Out-Null; [Console]::Out.WriteLine('passed') } catch { [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }\n");
        Assert.True(run.ExitCode == 0, run.Output);

        Assert.Equal(2, run.Lines.Length);
        Assert.StartsWith("refused: StartedUtc is empty", run.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("refused: With Jellyfin Enhanced, pass -CdnCacheDir", run.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UtcTimes_FromStringsAndDateTimes_AreTheSameInstant_WhateverTheMachinesTimeZone()
    {
        // Jellyfin writes *Utc times with Z, and so does sandbox.json. Windows PowerShell 5.1 keeps JSON dates as strings,
        // PowerShell 7 turns them into DateTime values (Utc or Local kind); neither an offset nor a missing zone may shift them.
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "$utc = [DateTime]::SpecifyKind([DateTime]::new(2026, 9, 29, 12, 0, 0), [DateTimeKind]::Utc)\n"
            + "$values = @(\n"
            + "  '2026-09-29T12:00:00.0000000Z', '2026-09-29T12:00:00Z', '2026-09-29T12:00:00', '2026-09-29T14:00:00+02:00', '2026-09-29T07:00:00-05:00',\n"
            + "  $utc, [DateTime]::SpecifyKind($utc, [DateTimeKind]::Unspecified), $utc.ToLocalTime(),\n"
            + "  [DateTimeOffset]::new(2026, 9, 29, 21, 0, 0, [TimeSpan]::FromHours(9)))\n"
            + "foreach ($v in $values) { $u = ConvertTo-UtcTime $v; $u.ToString('o') + '|' + $u.Kind }\n"
            + "'null|' + ($null -eq (ConvertTo-UtcTime $null)) + '|' + ($null -eq (ConvertTo-UtcTime ''))\n");
        Assert.True(run.ExitCode == 0, run.Output);

        Assert.Equal(Enumerable.Repeat("2026-09-29T12:00:00.0000000Z|Utc", 9).Append("null|True|True"), run.Lines);
    }

    // -------------------------------------------------------------------------------- community plugins, je sandbox

    [Fact]
    public async Task JavaScriptInjector_IsACommunityPlugin_AndInTheJeSandbox()
    {
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "$table = Get-CommunityPluginTable\n"
            + "foreach ($k in $table.Keys) { 'plugin|' + $k + '|' + $table[$k].Folder + '|' + $table[$k].Guid }\n"
            + "'je|' + ((Get-StandardSandbox -Name je).Community -join ',')\n"
            + "'default|' + ((Get-StandardSandbox -Name default).Community -join ',')\n");
        Assert.True(run.ExitCode == 0, run.Output);

        var plugins = run.Lines.Where(l => l.StartsWith("plugin|", StringComparison.Ordinal)).Select(l => l.Split('|')).ToDictionary(p => p[1], p => (Folder: p[2], Guid: p[3]));
        Assert.Equal(("JavaScript Injector", "f5a34f7b-2e8a-4e6a-a722-3a216a81b374"), plugins["JavaScriptInjector"]);
        Assert.Equal("je|FileTransformation,PluginPages,JellyfinEnhanced,CustomTabs,JavaScriptInjector", run.Lines.Single(l => l.StartsWith("je|", StringComparison.Ordinal)));
        Assert.Equal("default|FileTransformation,PluginPages", run.Lines.Single(l => l.StartsWith("default|", StringComparison.Ordinal)));

        // dev-server.ps1 -Community accepts exactly the table's plugins (and None).
        var devServer = await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "dev-server.ps1"), TestContext.Current.CancellationToken);
        var validateSet = CommunityValidateSet().Match(devServer);
        Assert.True(validateSet.Success, "dev-server.ps1 declares -Community with a ValidateSet.");
        Assert.Equal(new[] { "None" }.Concat(plugins.Keys), validateSet.Groups["list"].Value.Split(',').Select(v => v.Trim().Trim('\'')));

        // The je fixture's expected plugin ids are the table's.
        using var expected = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Root, "kit", "testdata", "je-sandbox", "expected.json"), TestContext.Current.CancellationToken));
        Assert.Equal(plugins["JellyfinEnhanced"].Guid, expected.RootElement.GetProperty("jellyfinEnhanced").GetProperty("pluginId").GetString());
        Assert.Equal(plugins["CustomTabs"].Guid, expected.RootElement.GetProperty("customTabs").GetProperty("pluginId").GetString());
        Assert.Equal(plugins["JavaScriptInjector"].Guid, expected.RootElement.GetProperty("javaScriptInjector").GetProperty("pluginId").GetString());

        // index-probe.mjs expects JavaScript Injector's tag when sandbox.json lists it.
        Assert.Contains("JavaScriptInjector: 'JavaScriptInjector/public.js'", await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "index-probe.mjs"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    // --------------------------------------------------------------------------------- reference-version check

    [Fact]
    public async Task ReferenceCheck_RefusesAReferenceNewerThanTheServerShips_AndNamesIt()
    {
        using var temp = new TempDirectory();
        var server = FakeServer(temp.Path);
        var plugin = SyntheticAssembly.Write(Path.Combine(temp.Path, "out", "Jellyfin.Plugin.Newer.dll"), "Jellyfin.Plugin.Newer", new Version(1, 0, 0, 0), [
            ("System.Runtime", new Version(9, 0, 0, 0)),
            ("Microsoft.Data.Sqlite", new Version(10, 0, 11, 0)),
            ("MediaBrowser.Controller", new Version(10, 11, 5, 0)),
        ]);

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ Assert-AssemblyReferenceVersions -AssemblyPaths @('{plugin}') -JellyfinDir '{server}' -What 'Newer' | Out-Null; [Console]::Out.WriteLine('passed') }} catch {{ [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }}\n"
            + $"$r = Test-AssemblyReferenceVersions -AssemblyPaths @('{plugin}') -JellyfinDir '{server}'\n"
            + "foreach ($e in @($r.Newer)) { 'newer|' + $e.Reference + '|' + $e.Referenced + '|' + $e.Shipped }\n"
            + "foreach ($e in @($r.Checked)) { 'checked|' + $e.Reference }\n");
        Assert.True(run.ExitCode == 0, run.Output);

        var refused = Assert.Single(run.Lines, l => l.StartsWith("refused: ", StringComparison.Ordinal));
        Assert.Contains("Newer references assemblies newer than the Jellyfin in", refused, StringComparison.Ordinal);
        Assert.Contains("Jellyfin.Plugin.Newer.dll references Microsoft.Data.Sqlite 10.0.11.0, but the server ships 9.0.11.0", refused, StringComparison.Ordinal);
        Assert.Contains("can stop the whole server at startup: reference exactly the version the server ships", refused, StringComparison.Ordinal);
        Assert.Equal(["newer|Microsoft.Data.Sqlite|10.0.11.0|9.0.11.0"], run.Lines.Where(l => l.StartsWith("newer|", StringComparison.Ordinal)));
        Assert.Equal(["checked|System.Runtime", "checked|Microsoft.Data.Sqlite", "checked|MediaBrowser.Controller"], run.Lines.Where(l => l.StartsWith("checked|", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ReferenceCheck_PassesEqualAndOlderReferences_AndSkipsWhatTheServerDoesNotShip()
    {
        using var temp = new TempDirectory();
        var server = FakeServer(temp.Path);
        var plugin = SyntheticAssembly.Write(Path.Combine(temp.Path, "out", "Jellyfin.Plugin.Fine.dll"), "Jellyfin.Plugin.Fine", new Version(1, 0, 0, 0), [
            ("System.Runtime", new Version(8, 0, 0, 0)),
            ("Microsoft.Data.Sqlite", new Version(9, 0, 11, 0)),
            ("MediaBrowser.Controller", new Version(10, 11, 0, 0)),
            ("Jellyfin.Plugin.Fine.Extra", new Version(1, 0, 0, 0)),
            ("NotAnAssembly", new Version(1, 0, 0, 0)),
        ]);

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"$r = Assert-AssemblyReferenceVersions -AssemblyPaths @('{plugin}') -JellyfinDir '{server}'\n"
            + "'checked ' + @($r.Checked).Count + ', newer ' + @($r.Newer).Count\n"
            + "foreach ($e in @($r.NotShipped)) { 'not shipped|' + $e.Reference }\n");
        Assert.True(run.ExitCode == 0, run.Output);

        Assert.Equal(["checked 3, newer 0", "not shipped|Jellyfin.Plugin.Fine.Extra", "not shipped|NotAnAssembly"], run.Lines);
    }

    [Fact]
    public async Task ReferenceCheck_RefusesAFolderWithoutJellyfin()
    {
        using var temp = new TempDirectory();
        var plugin = SyntheticAssembly.Write(Path.Combine(temp.Path, "out", "Jellyfin.Plugin.X.dll"), "Jellyfin.Plugin.X", new Version(1, 0, 0, 0));
        Directory.CreateDirectory(Path.Combine(temp.Path, "empty"));

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ Assert-AssemblyReferenceVersions -AssemblyPaths @('{plugin}') -JellyfinDir '{Path.Combine(temp.Path, "empty")}' | Out-Null; [Console]::Out.WriteLine('passed') }} catch {{ [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }}\n");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.StartsWith("refused: ", run.Lines.Single(), StringComparison.Ordinal);
        Assert.Contains("is not a Jellyfin program folder", run.Lines.Single(), StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------- the Jellyfin the build is for

    [Fact]
    public async Task JellyfinTarget_Matches_TheMajorAndMinorItNames()
    {
        // The kit's ABI guard's rule (kit/README.md): a 10.11 build is for 10.11.x, a 12 build for 12.x.
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "foreach ($c in @(@('10.11', '10.11.5.0'), @('10.11', '10.11.0'), @('10.11', '10.10.7.0'), @('10.11', '12.0.0.0'), @('12', '12.0.0.0'), @('12', '12.1.3.0'), @('12', '10.11.5.0'), @('12', '13.0.0.0'))) {\n"
            + "  [Console]::Out.WriteLine($c[0] + '|' + $c[1] + '|' + (Test-JellyfinTargetMatch -JellyfinTarget $c[0] -Version ([version]$c[1])))\n"
            + "}\n");
        Assert.True(run.ExitCode == 0, run.Output);

        Assert.Equal(
            ["10.11|10.11.5.0|True", "10.11|10.11.0|True", "10.11|10.10.7.0|False", "10.11|12.0.0.0|False", "12|12.0.0.0|True", "12|12.1.3.0|True", "12|10.11.5.0|False", "12|13.0.0.0|False"],
            run.Lines);
    }

    [Fact]
    public async Task JellyfinTarget_RefusesAServerOfAnotherVersion_NamingJellyfinDir_NotThePackagePins()
    {
        using var temp = new TempDirectory();
        var server = FakeServer(Path.Combine(temp.Path, "10.11"));
        var server12 = FakeServer(Path.Combine(temp.Path, "12"), new Version(12, 0, 1, 0));
        var unknown = Path.Combine(temp.Path, "unknown");
        Directory.CreateDirectory(unknown);
        await File.WriteAllTextAsync(Path.Combine(unknown, "jellyfin.dll"), "not an assembly", TestContext.Current.CancellationToken);

        var run = await WindowsPowerShell.RunWithModuleAsync(
            "function Try-Target([string] $Dir, [string] $Target) {\n"
            + "  try { [Console]::Out.WriteLine('passed ' + (Assert-JellyfinTarget -JellyfinDir $Dir -JellyfinTarget $Target)) } catch { [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }\n"
            + "}\n"
            + $"Try-Target '{server}' '10.11'\n"
            + $"Try-Target '{server}' '12'\n"
            + $"Try-Target '{server12}' '12'\n"
            + $"Try-Target '{server12}' '10.11'\n"
            + $"Try-Target '{unknown}' '10.11'\n");
        Assert.True(run.ExitCode == 0, run.Output);

        Assert.Equal(5, run.Lines.Length);
        Assert.Equal("passed 10.11.5.0", run.Lines[0]);
        Assert.Equal(
            $"refused: -JellyfinTarget 12 builds for Jellyfin 12.x, but {server} holds Jellyfin 10.11.5.0 (-JellyfinDir defaults to the installed server). "
            + $"Pass -JellyfinDir with the program folder of a Jellyfin 12.x server, whose assemblies the reference-version check compares with; for the Jellyfin in {server} the target is -JellyfinTarget 10.11.",
            run.Lines[1]);
        Assert.Equal("passed 12.0.1.0", run.Lines[2]);
        Assert.StartsWith($"refused: -JellyfinTarget 10.11 builds for Jellyfin 10.11.x, but {server12} holds Jellyfin 12.0.1.0", run.Lines[3], StringComparison.Ordinal);
        Assert.EndsWith("the target is -JellyfinTarget 12.", run.Lines[3], StringComparison.Ordinal);
        Assert.StartsWith($"refused: Can't tell which Jellyfin {unknown} holds", run.Lines[4], StringComparison.Ordinal);
        Assert.All(run.Lines, l => Assert.DoesNotContain("Directory.Packages.props", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_RefusesATarget12Deploy_AgainstA1011Server_BeforeTheReferenceCheck_AndInstallsNothing()
    {
        // A 12 build (net10.0: System.Runtime 10.0.0.0) compared with 10.11.5's assemblies would be refused for "newer"
        // references with advice to pin Directory.Packages.props, the wrong fix. Deploy names -JellyfinDir instead.
        using var temp = new TempDirectory();
        var (deploy, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(10, 0, 0, 0)), ("MediaBrowser.Controller", new Version(12, 0, 0, 0))]);

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{deploy}' -Plugin RefCheck -DataDir '{data}' -NoBuild -JellyfinTarget 12 -JellyfinDir '{server}'; [Console]::Out.WriteLine('deployed') }}\n"
            + "catch { [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }\n");

        Assert.True(run.ExitCode == 0, run.Output);
        var refused = Assert.Single(run.Lines, l => l.StartsWith("refused: ", StringComparison.Ordinal));
        Assert.StartsWith($"refused: -JellyfinTarget 12 builds for Jellyfin 12.x, but {server} holds Jellyfin 10.11.5.0", refused, StringComparison.Ordinal);
        Assert.Contains("Pass -JellyfinDir", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Packages.props", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("deployed", run.Lines);
        Assert.False(Directory.Exists(data), "Nothing was created or installed.");
    }

    [Fact]
    public async Task Deploy_RefusesAPluginBuiltAgainstANewerSqliteThanTheServerShips_AndInstallsNothing()
    {
        // The deploy's acceptance rule: "a plugin referencing a newer Microsoft.Data.Sqlite than the target server ships is refused by
        // Deploy". The real Deploy-Plugin.ps1 and module run from a copy of the repository layout in a temporary folder,
        // with -NoBuild on a synthetic build output, a synthetic server folder and a temporary data folder.
        using var temp = new TempDirectory();
        var (deploy, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(9, 0, 0, 0)), ("Microsoft.Data.Sqlite", new Version(10, 0, 11, 0))]);

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{deploy}' -Plugin RefCheck -DataDir '{data}' -NoBuild -JellyfinDir '{server}'; [Console]::Out.WriteLine('deployed') }}\n"
            + "catch { [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }\n");

        Assert.True(run.ExitCode == 0, run.Output);
        var refused = Assert.Single(run.Lines, l => l.StartsWith("refused: ", StringComparison.Ordinal));
        Assert.Contains("RefCheck references assemblies newer than the Jellyfin in " + server + " ships", refused, StringComparison.Ordinal);
        Assert.Contains("Jellyfin.Plugin.RefCheck.dll references Microsoft.Data.Sqlite 10.0.11.0, but the server ships 9.0.11.0", refused, StringComparison.Ordinal);

        // Real package drift (the target matches the server) keeps the advice to pin the server's package version.
        Assert.Contains("reference exactly the version the server ships (Directory.Packages.props)", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("deployed", run.Lines);
        Assert.False(Directory.Exists(Path.Combine(data, "plugins")) && Directory.EnumerateDirectories(Path.Combine(data, "plugins"), "RefCheck_*").Any(), "Nothing was installed.");
    }

    [Fact]
    public async Task Deploy_RefusesABuildThatShipsSqlite_AndInstallsNothing()
    {
        // Never ship SQLite managed or native binaries: a SQLite plugin whose output holds the
        // SQLite assemblies, as a plain PackageReference would leave them, is refused by the DLL allowlist before anything
        // is installed. The kit's KitUseSqlite reference (ExcludeAssets=runtime, PrivateAssets=all) never produces them.
        using var temp = new TempDirectory();
        var (deploy, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(9, 0, 0, 0)), ("Microsoft.Data.Sqlite", new Version(9, 0, 11, 0))]);
        var output = Path.Combine(temp.Path, "repo", "artifacts", "deploy", "RefCheck", "Release");
        SyntheticAssembly.Write(Path.Combine(output, "Microsoft.Data.Sqlite.dll"), "Microsoft.Data.Sqlite", new Version(9, 0, 11, 0));
        SyntheticAssembly.Write(Path.Combine(output, "SQLitePCLRaw.core.dll"), "SQLitePCLRaw.core", new Version(2, 1, 10, 2445));
        Directory.CreateDirectory(Path.Combine(output, "runtimes", "win-x64", "native"));
        await File.WriteAllTextAsync(Path.Combine(output, "runtimes", "win-x64", "native", "e_sqlite3.dll"), "native", TestContext.Current.CancellationToken);

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{deploy}' -Plugin RefCheck -DataDir '{data}' -NoBuild -JellyfinDir '{server}'; [Console]::Out.WriteLine('deployed') }}\n"
            + "catch { [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }\n");

        Assert.True(run.ExitCode == 0, run.Output);
        var refused = Assert.Single(run.Lines, l => l.StartsWith("refused: ", StringComparison.Ordinal));
        Assert.Contains("contains DLLs that are not in build.yaml's artifacts", refused, StringComparison.Ordinal);
        foreach (var file in new[] { "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.core.dll", Path.Combine("runtimes", "win-x64", "native", "e_sqlite3.dll") })
        {
            Assert.Contains(file, refused, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("deployed", run.Lines);
        Assert.False(Directory.Exists(Path.Combine(data, "plugins")) && Directory.EnumerateDirectories(Path.Combine(data, "plugins"), "RefCheck_*").Any(), "Nothing was installed.");
    }

    [Fact]
    public async Task Deploy_PassesTheTargetAndReferenceChecks_WhenTheServerIsTheTargetAndShipsEveryReference()
    {
        // The positive control of the two refusals above: same fixture, the 10.11 target (explicit and by default) and
        // references no newer than 10.11.5 ships. A synthetic assembly has no version resource, so Deploy then stops at
        // its file-version check, the step after the reference check, before installing anything.
        using var temp = new TempDirectory();
        var (deploy, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(9, 0, 0, 0)), ("Microsoft.Data.Sqlite", new Version(9, 0, 11, 0))]);

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"foreach ($target in @('10.11', '')) {{\n"
            + $"  $a = @{{ Plugin = 'RefCheck'; DataDir = '{data}'; NoBuild = $true; JellyfinDir = '{server}' }}\n"
            + "  if ($target) { $a.JellyfinTarget = $target }\n"
            + $"  try {{ & '{deploy}' @a 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }}; [Console]::Out.WriteLine('deployed') }}\n"
            + "  catch { [Console]::Out.WriteLine('stopped: ' + $_.Exception.Message) }\n"
            + "}\n");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(2, run.Lines.Count(l => l == $"host: Reference check passed: 2 references, none newer than {server} (Jellyfin 10.11.5.0) ships."));
        Assert.Equal(2, run.Lines.Count(l => l == "stopped: Jellyfin.Plugin.RefCheck.dll has file version , but build.yaml says 1.0.0.0."));
        Assert.DoesNotContain("deployed", run.Lines);
    }

    [Fact]
    public async Task ReferenceCheck_PassesForTheBookmarksBuild_AgainstTheInstalledServer()
    {
        // The real plugin against the real server's program folder, read only (as Deploy-Plugin.ps1 and dev-server.ps1
        // do by default). Skipped where Jellyfin isn't installed.
        const string Installed = @"C:\ProgramData\Jellyfin\Server";
        Assert.SkipWhen(JellyfinLine.Is12, "The installed server is Jellyfin 10.11; the 12 build's references are checked against a 12.x folder (Deploy-Plugin.ps1 -JellyfinTarget 12).");
        Assert.SkipUnless(File.Exists(Path.Combine(Installed, "jellyfin.dll")), "Jellyfin is not installed in " + Installed + ".");
        var bookmarks = Path.Combine(AppContext.BaseDirectory, "Jellyfin.Plugin.Bookmarks.dll");
        Assert.True(File.Exists(bookmarks), bookmarks);

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"$r = Assert-AssemblyReferenceVersions -AssemblyPaths @('{bookmarks}') -JellyfinDir '{Installed}'\n"
            + "'checked ' + @($r.Checked).Count + ', newer ' + @($r.Newer).Count + ', not shipped ' + @($r.NotShipped).Count\n"
            + "foreach ($e in @($r.Checked)) { $e.Reference + '|' + $e.Referenced }\n");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Matches(@"^checked \d+, newer 0, not shipped 0$", run.Lines[0]);
        Assert.Contains("MediaBrowser.Controller|10.11.5.0", run.Lines);
        Assert.Contains("System.Runtime|9.0.0.0", run.Lines);
    }

    // ------------------------------------------------------------------------------------- Install-RealServer.ps1

    [Fact]
    public async Task InstallRealServer_TakesAColdBackupOfTheSettingsPluginsDatabasesAndThePluginsData_ChecksIt_ThenDeploys()
    {
        // The real script, module and Deploy on a synthetic data folder (never the real server's): the backup folder is
        // named after the plugin and its version, holds what a way back needs and nothing Jellyfin regenerates, and
        // is checked before Deploy runs. Deploy then stops at its file-version check (a synthetic assembly has none), the
        // step after its allowlist and reference checks, so nothing is installed. -FromWorkingTree, the rehearsal path,
        // because this synthetic repository has no git history; ReleaseInstallTests takes the release tag's path.
        using var temp = new TempDirectory();
        var (_, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(9, 0, 0, 0))]);
        var install = Path.Combine(temp.Path, "repo", "scripts", "Install-RealServer.ps1");
        File.Copy(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), install);
        var backups = Path.Combine(temp.Path, "backups");
        foreach (var (file, text) in new[]
        {
            (@"config\system.xml", "<ServerConfiguration />"), (@"config\network.xml", "<NetworkConfiguration />"), (@"root\default\Movies\Movies.mblink", @"D:\Movies"),
            (@"plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced.xml", "<PluginConfiguration />"),
            (@"plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\00000000000000000000000000000001\bookmark.json", "{}"),
            (@"plugins\Other_1.2.0.0\Other.dll", "dll"), (@"plugins\RefCheck_0.9.0.0\Jellyfin.Plugin.RefCheck.dll", "old"),
            (@"data\jellyfin.db", new string('d', 4096)), (@"data\jellyfin.db-wal", "wal"), (@"data\jellyfin.db-shm", "shm"),
            (@"data\device.txt", "device"), (@"data\library.db.old", "old"), (@"data\jellyfin.db.bak2", "bak"), (@"data\refcheck\refcheck.json", "{}"),
            (@"data\refcheck\snapshots\refcheck-20261007-031000.json", "{}"), (@"data\collections\Box\collection.xml", "<Item />"), (@"data\ScheduledTasks\x.js", "[]"),
            (@"data\SQLiteBackups\jellyfin.db.bak", "old"), (@"data\subtitles\x.srt", "1"), (@"data\trickplay\x.jpg", "1"), (@"metadata\library\x.jpg", "1"),
            (@"cache\x", "1"), (@"log\log_1.log", "1")
        })
        {
            var path = Path.Combine(data, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);
        }

        // -WhatIf: the plan, nothing made.
        var whatIf = await WindowsPowerShell.RunWithModuleAsync(
            $"& '{install}' -Plugin RefCheck -DataDir '{data}' -JellyfinDir '{server}' -BackupRoot '{backups}' -FromWorkingTree -NoBuild -WhatIf 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }}\n");
        Assert.True(whatIf.ExitCode == 0, whatIf.Output);
        Assert.Contains(whatIf.Lines, l => l.StartsWith("host: Backup to " + backups + @"\", StringComparison.Ordinal) && l.Contains("-before-RefCheck-1.0.0.0: 15 file(s)", StringComparison.Ordinal));
        Assert.Contains(@"host: Left out (old copies): data\jellyfin.db.bak2, data\library.db.old", whatIf.Lines);
        Assert.Contains(@"host: Not there, so not backed up: data\playlists, data\introskipper", whatIf.Lines);
        Assert.False(Directory.Exists(backups), "-WhatIf makes nothing");
        // CimCmdlets is loaded outside -WhatIf: no "What if: ... Set Alias" line per alias.
        Assert.DoesNotContain(whatIf.Lines, l => l.Contains("\"Set Alias\"", StringComparison.Ordinal));

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{install}' -Plugin RefCheck -DataDir '{data}' -JellyfinDir '{server}' -BackupRoot '{backups}' -FromWorkingTree -NoBuild 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }}; [Console]::Out.WriteLine('installed') }}\n"
            + "catch { [Console]::Out.WriteLine('stopped: ' + $_.Exception.Message) }\n");

        Assert.True(run.ExitCode == 0, run.Output);
        var backup = Assert.Single(Directory.GetDirectories(backups));
        Assert.Matches(@"^\d{8}-\d{6}-before-RefCheck-1\.0\.0\.0$", Path.GetFileName(backup));
        var copied = Directory.GetFiles(backup, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(backup, f)).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(
            new[]
            {
                "backup.json", @"config\network.xml", @"config\system.xml", @"data\collections\Box\collection.xml", @"data\device.txt", @"data\jellyfin.db",
                @"data\jellyfin.db-shm", @"data\jellyfin.db-wal", @"data\refcheck\refcheck.json", @"data\refcheck\snapshots\refcheck-20261007-031000.json",
                @"data\ScheduledTasks\x.js", @"plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced.xml",
                @"plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\00000000000000000000000000000001\bookmark.json", @"plugins\Other_1.2.0.0\Other.dll",
                @"plugins\RefCheck_0.9.0.0\Jellyfin.Plugin.RefCheck.dll", "RESTORE.txt", @"root\default\Movies\Movies.mblink"
            }.Order(StringComparer.OrdinalIgnoreCase),
            copied);
        Assert.Equal(new string('d', 4096), await File.ReadAllTextAsync(Path.Combine(backup, "data", "jellyfin.db"), TestContext.Current.CancellationToken));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("RefCheck", manifest.RootElement.GetProperty("plugin").GetString());
            Assert.Equal("1.0.0.0", manifest.RootElement.GetProperty("version").GetString());
            Assert.Equal(15, manifest.RootElement.GetProperty("files").GetInt32());
            Assert.Contains(@"data\refcheck", manifest.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("path").GetString()));
        }

        // RESTORE.txt: way A removes the plugin alone (its folder, its data, its settings file, the earlier
        // version put back) and never copies the databases back; way B, the whole backup, warns that everything recorded
        // since the backup's date is lost.
        var restore = await File.ReadAllTextAsync(Path.Combine(backup, "RESTORE.txt"), TestContext.Current.CancellationToken);
        var when = DateTime.ParseExact(Path.GetFileName(backup)[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Assert.StartsWith($"This backup was taken on {when} (this computer's time), before RefCheck 1.0.0.0 was installed into {data}.\r\n", restore, StringComparison.Ordinal);
        var wayA = restore[restore.IndexOf("A. Remove RefCheck 1.0.0.0 only.", StringComparison.Ordinal)..restore.IndexOf("B. Put the whole server back", StringComparison.Ordinal)];
        var wayB = restore[restore.IndexOf("B. Put the whole server back", StringComparison.Ordinal)..];
        foreach (var line in new[]
        {
            "   Everything the server recorded since the backup stays: watched states and playback positions, users and their settings, libraries, the other plugins.",
            $@"2. Delete {data}\plugins\RefCheck_1.0.0.0.",
            $@"   Copy this backup's plugins\RefCheck_0.9.0.0 folder back into {data}\plugins (the version installed before).",
            $@"3. Replace {data}\data\refcheck with this backup's data\refcheck: the plugin's data as it was before",
            $@"4. Delete {data}\plugins\configurations\Jellyfin.Plugin.RefCheck.xml, the plugin's settings. It wasn't there before.",
            "5. If you changed other plugins' settings along with it (its README says which), set them back on their settings pages, or copy only those plugins' files",
        })
        {
            Assert.Contains(line, wayA, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("jellyfin.db", wayA, StringComparison.Ordinal);
        Assert.DoesNotContain("data folder over", wayA, StringComparison.Ordinal);
        Assert.DoesNotContain("Other_1.2.0.0", restore, StringComparison.Ordinal);
        Assert.StartsWith($"B. Put the whole server back to the moment of this backup, {when}. Only when the server itself is broken.\r\n", wayB, StringComparison.Ordinal);
        Assert.Contains($@"   WARNING: this loses everything the server recorded since {when} (data\jellyfin.db is its database): watched states and playback positions,", wayB, StringComparison.Ordinal);
        Assert.Contains($@"4. Copy this backup's data folder over {data}\data.", wayB, StringComparison.Ordinal);
        Assert.Contains(@"data\refcheck was there before", wayB, StringComparison.Ordinal);
        Assert.Contains("host: Backed up and checked: 15 file(s), 0.0 MB.", run.Lines);
        Assert.Equal("stopped: Jellyfin.Plugin.RefCheck.dll has file version , but build.yaml says 1.0.0.0.", run.Lines[^1]);
        Assert.False(Directory.Exists(Path.Combine(data, "plugins", "RefCheck_1.0.0.0")));
        Assert.True(Directory.Exists(Path.Combine(data, "plugins", "RefCheck_0.9.0.0")), "Deploy stopped before it removes the earlier version");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallRealServer_ForAccessRoles_RestoreReleasesTheLocksFirst_AndMovesItsDataAside_NeverDeletesIt(bool hadData)
    {
        // Way A once deleted data\accessroles (or replaced it), and with it the ledger and the
        // PreferenceMirror records the README's recovery reads once the plugin is gone. Now it releases the locks first,
        // while the plugin runs, and moves the folder aside renamed with the time; way B does the same. The rename line
        // is run here as RESTORE.txt gives it.
        using var temp = new TempDirectory();
        var (_, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(9, 0, 0, 0))], "AccessRoles");
        var install = Path.Combine(temp.Path, "repo", "scripts", "Install-RealServer.ps1");
        File.Copy(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), install);
        var backups = Path.Combine(temp.Path, "backups");
        foreach (var file in new[] { @"config\system.xml", @"data\jellyfin.db" }.Concat(hadData ? [@"data\accessroles\accessroles.json"] : Array.Empty<string>()))
        {
            var path = Path.Combine(data, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"PreferenceMirror\":{}}", TestContext.Current.CancellationToken);
        }

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{install}' -Plugin AccessRoles -DataDir '{data}' -JellyfinDir '{server}' -BackupRoot '{backups}' -FromWorkingTree -NoBuild 6>&1 | Out-Null }} catch {{ [Console]::Out.WriteLine('stopped: ' + $_.Exception.Message) }}\n");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("stopped: Jellyfin.Plugin.AccessRoles.dll has file version , but build.yaml says 1.0.0.0.", run.Lines[^1]);
        var restore = await File.ReadAllTextAsync(Path.Combine(Assert.Single(Directory.GetDirectories(backups)), "RESTORE.txt"), TestContext.Current.CancellationToken);
        var lines = restore.Split("\r\n");
        var wayA = restore[restore.IndexOf("A. Remove AccessRoles 1.0.0.0 only.", StringComparison.Ordinal)..restore.IndexOf("B. Put the whole server back", StringComparison.Ordinal)];
        var wayB = restore[restore.IndexOf("B. Put the whole server back", StringComparison.Ordinal)..];
        var rename = $"Rename-Item -LiteralPath '{data}\\data\\accessroles' -NewName ('accessroles.removed-' + (Get-Date -Format yyyyMMdd-HHmmss))";

        Assert.True(wayA.IndexOf("0. First, while the plugin is still enabled and the server still starts:", StringComparison.Ordinal) is > 0 and var first && first < wayA.IndexOf("1. Exit Jellyfin", StringComparison.Ordinal), wayA);
        Assert.Contains("tick Also restore each user's original library access, press Release all locks... and then Release them.", wayA, StringComparison.Ordinal);
        Assert.Contains($@"3. Move {data}\data\accessroles aside, renamed with the time, never delete it: in PowerShell, {rename}", lines);
        Assert.Contains($@"   {data}\data\accessroles.removed-<yyyyMMdd-HHmmss> keeps the ledger (each managed user's library access from before Access Roles managed them) and the PreferenceMirror records", wayA, StringComparison.Ordinal);
        Assert.Contains(hadData ? $@"   Then copy this backup's data\accessroles folder back into {data}\data: the plugin's data as it was before." : "   It wasn't there before: nothing is copied back.", lines);
        Assert.DoesNotContain(@"elete " + data + @"\data\accessroles", restore, StringComparison.Ordinal);
        Assert.DoesNotContain(@"Replace " + data + @"\data\accessroles", restore, StringComparison.Ordinal);
        Assert.Contains("(" + rename + ")", wayB, StringComparison.Ordinal);
        Assert.Contains(hadData ? "first move the one there now aside" : "wasn't there before: move it aside", wayB, StringComparison.Ordinal);

        // The rename as given, once the data folder is there (the plugin makes it at its first start when it wasn't before).
        Directory.CreateDirectory(Path.Combine(data, "data", "accessroles"));
        await File.WriteAllTextAsync(Path.Combine(data, "data", "accessroles", "accessroles.json"), "{\"PreferenceMirror\":{\"x\":{}}}", TestContext.Current.CancellationToken);
        var moved = await WindowsPowerShell.RunWithModuleAsync(rename + "\n");
        Assert.True(moved.ExitCode == 0, moved.Output);
        Assert.False(Directory.Exists(Path.Combine(data, "data", "accessroles")));
        var aside = Assert.Single(Directory.GetDirectories(Path.Combine(data, "data"), "accessroles.removed-*"));
        Assert.Matches(@"^accessroles\.removed-\d{8}-\d{6}$", Path.GetFileName(aside));
        Assert.Equal("{\"PreferenceMirror\":{\"x\":{}}}", await File.ReadAllTextAsync(Path.Combine(aside, "accessroles.json"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallRealServer_ForUserRatings_ListsJellyfinEnhancedsFiles_ExportsFirst_AndMovesItsDataAside_NeverDeletesIt(bool hasJe)
    {
        // The backup holds Jellyfin Enhanced's settings file and reviews.json (plugins\configurations) and says so
        // by name, or says they aren't there; RESTORE.txt's way A starts with the exports the README's way back needs, moves
        // data\userratings (the members' ratings and reviews) aside instead of deleting it, and says how to turn JE's user
        // reviews back on; way B moves it aside too.
        using var temp = new TempDirectory();
        var (_, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(9, 0, 0, 0))], "UserRatings");
        var install = Path.Combine(temp.Path, "repo", "scripts", "Install-RealServer.ps1");
        File.Copy(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), install);
        var backups = Path.Combine(temp.Path, "backups");
        var je = new[] { @"plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced.xml", @"plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\reviews.json" };
        foreach (var file in new[] { @"config\system.xml", @"data\jellyfin.db", @"data\userratings\userratings.db" }.Concat(hasJe ? je : []))
        {
            var path = Path.Combine(data, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"Reviews\":{}}", TestContext.Current.CancellationToken);
        }

        var whatIf = await WindowsPowerShell.RunWithModuleAsync(
            $"& '{install}' -Plugin UserRatings -DataDir '{data}' -JellyfinDir '{server}' -BackupRoot '{backups}' -FromWorkingTree -NoBuild -WhatIf 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }}\n");
        Assert.True(whatIf.ExitCode == 0, whatIf.Output);
        Assert.Contains(
            hasJe ? @"host: Jellyfin Enhanced's files, in plugins: plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced.xml, plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\reviews.json"
                : @"host: Jellyfin Enhanced: no settings file or reviews.json in plugins\configurations, so none to back up",
            whatIf.Lines);
        Assert.Contains(whatIf.Lines, l => l.StartsWith("host: Backup to ", StringComparison.Ordinal) && l.EndsWith(hasJe ? @"in config, plugins, data\jellyfin.db, data\userratings" : @"in config, data\jellyfin.db, data\userratings", StringComparison.Ordinal));

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{install}' -Plugin UserRatings -DataDir '{data}' -JellyfinDir '{server}' -BackupRoot '{backups}' -FromWorkingTree -NoBuild 6>&1 | Out-Null }} catch {{ [Console]::Out.WriteLine('stopped: ' + $_.Exception.Message) }}\n");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("stopped: Jellyfin.Plugin.UserRatings.dll has file version , but build.yaml says 1.0.0.0.", run.Lines[^1]);
        var backup = Assert.Single(Directory.GetDirectories(backups));
        foreach (var file in je.Where(_ => hasJe).Append(@"data\userratings\userratings.db"))
        {
            Assert.True(File.Exists(Path.Combine(backup, file)), file);
        }

        var restore = await File.ReadAllTextAsync(Path.Combine(backup, "RESTORE.txt"), TestContext.Current.CancellationToken);
        var lines = restore.Split("\r\n");
        var wayA = restore[restore.IndexOf("A. Remove UserRatings 1.0.0.0 only.", StringComparison.Ordinal)..restore.IndexOf("B. Put the whole server back", StringComparison.Ordinal)];
        var wayB = restore[restore.IndexOf("B. Put the whole server back", StringComparison.Ordinal)..];
        var rename = $"Rename-Item -LiteralPath '{data}\\data\\userratings' -NewName ('userratings.removed-' + (Get-Date -Format yyyyMMdd-HHmmss))";

        Assert.True(wayA.IndexOf("0. First, while the plugin is still enabled and the server still starts: on its page's Data tab, Export", StringComparison.Ordinal) is > 0 and var first && first < wayA.IndexOf("1. Exit Jellyfin", StringComparison.Ordinal), wayA);
        Assert.Contains("Export for Jellyfin Enhanced after its import's dry run and import, as its README's Going back to Jellyfin Enhanced says.", wayA, StringComparison.Ordinal);
        Assert.DoesNotContain("Release all locks", restore, StringComparison.Ordinal);
        Assert.Contains($@"3. Move {data}\data\userratings aside, renamed with the time, never delete it: in PowerShell, {rename}", lines);
        Assert.Contains($@"   {data}\data\userratings.removed-<yyyyMMdd-HHmmss> keeps the members' ratings and reviews (userratings.db with its -wal), its snapshots and its archive", wayA, StringComparison.Ordinal);
        Assert.DoesNotContain("ledger", wayA, StringComparison.Ordinal);
        Assert.Contains($@"   Then copy this backup's data\userratings folder back into {data}\data: the plugin's data as it was before.", lines);
        Assert.DoesNotContain(@"elete " + data + @"\data\userratings", restore, StringComparison.Ordinal);
        Assert.True(wayA.IndexOf("   Jellyfin Enhanced (User Ratings' README, the switch-over): once Jellyfin has started, turn its user reviews back on", StringComparison.Ordinal) is > 0 and var jeLine
            && jeLine > wayA.IndexOf("5. If you changed other plugins' settings", StringComparison.Ordinal) && jeLine < wayA.IndexOf("6. Start Jellyfin.", StringComparison.Ordinal), wayA);
        Assert.Contains(@"This backup's plugins\configurations\Jellyfin.Plugin.JellyfinEnhanced\reviews.json is JE's file from before User Ratings", wayA, StringComparison.Ordinal);
        Assert.Contains("(" + rename + ")", wayB, StringComparison.Ordinal);
        Assert.Contains("first move the one there now aside", wayB, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallRealServer_RefusesAFolderThatIsntJellyfins_AndABackupInsideTheDataFolder_BeforeAnythingIsCopied()
    {
        using var temp = new TempDirectory();
        var (_, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(9, 0, 0, 0))]);
        var install = Path.Combine(temp.Path, "repo", "scripts", "Install-RealServer.ps1");
        File.Copy(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), install);
        Directory.CreateDirectory(Path.Combine(data, "config"));
        var elsewhere = Path.Combine(temp.Path, "b");

        var noData = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{install}' -Plugin RefCheck -DataDir '{data}' -JellyfinDir '{server}' -BackupRoot '{elsewhere}' -FromWorkingTree -NoBuild }} catch {{ [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }}\n");
        Assert.Contains($"refused: {data} is not a Jellyfin data folder (it has no 'data' folder).", noData.Lines);

        Directory.CreateDirectory(Path.Combine(data, "data"));
        var inside = Path.Combine(data, "backups");
        var refused = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{install}' -Plugin RefCheck -DataDir '{data}' -JellyfinDir '{server}' -BackupRoot '{inside}' -FromWorkingTree -NoBuild }} catch {{ [Console]::Out.WriteLine('refused: ' + $_.Exception.Message) }}\n");
        Assert.Contains($"refused: Refusing to put the backup in {inside}, inside the data folder it backs up. Choose another -BackupRoot.", refused.Lines);
        Assert.False(Directory.Exists(elsewhere));
        Assert.False(Directory.Exists(inside));
    }

    [Fact]
    public async Task InstallRealServer_WritesItsRefusalToStdout_AsWellAsThrowingIt()
    {
        // Earlier, the evidence files of a refusal recorded with `> file` were empty, the refusal
        // having gone to the error stream only (a thrown error isn't captured by *> or 6>&1 either). Run the way a user
        // runs it (powershell.exe -File), stdout alone now holds it, and the exit code still says it failed.
        using var temp = new TempDirectory();
        var (_, server, data) = await DeployFixtureAsync(temp.Path, [("System.Runtime", new Version(9, 0, 0, 0))]);
        var install = Path.Combine(temp.Path, "repo", "scripts", "Install-RealServer.ps1");
        File.Copy(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), install);
        var backups = Path.Combine(temp.Path, "backups");

        var run = await WindowsPowerShell.RunFileAsync(install, ["-Plugin", "RefCheck", "-DataDir", data, "-JellyfinDir", server, "-BackupRoot", backups, "-FromWorkingTree", "-NoBuild"]);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains($"Refused: {data} is not a Jellyfin data folder (it has no 'config' folder). Nothing was backed up or installed.", run.Lines);
        Assert.False(string.IsNullOrWhiteSpace(run.StandardError), "the error is still thrown");
        Assert.False(Directory.Exists(backups));

        // Every refusal goes through Stop-Refused, and a failed install after the backup is said on the host too.
        var text = await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(text.Split('\n'), l => l.TrimStart().StartsWith("throw \"", StringComparison.Ordinal));
        Assert.Contains("Write-Host (\"Refused: $Message $Outcome\").TrimEnd()", text, StringComparison.Ordinal);
        Assert.Contains("Write-Host \"Stopped: the install failed after the backup: ", text, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------- Stop-Sandbox.ps1 and dev-server.ps1

    [Fact]
    public async Task WaitProcessExit_PollsUntilTheProcessHasGone_AndCountsAReusedIdAsGone()
    {
        // Earlier, Stop-Sandbox.ps1 sometimes said "still running" right after a graceful
        // shutdown, the process gone a moment later. It now polls (Wait-ProcessExit) instead of looking once. Only this
        // test's own powershell.exe and a child that ends by itself are looked at; nothing is stopped.
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "$self = Get-CimInstance Win32_Process -Filter \"ProcessId = $PID\"\n"
            + "$sw = [Diagnostics.Stopwatch]::StartNew(); $r = Wait-ProcessExit -ProcessId $PID -StartTime $self.CreationDate -TimeoutSec 1 -PollMs 100\n"
            + "'self ' + $r + ' ' + [int]($sw.ElapsedMilliseconds -ge 900)\n"
            + "$earlier = $self.CreationDate.AddMinutes(-10)\n"
            + "'reused ' + (Wait-ProcessExit -ProcessId $PID -StartTime $earlier -TimeoutSec 5) + ' ' + (Test-ProcessRunning -ProcessId $PID -StartTime $earlier)\n"
            + "'running ' + (Test-ProcessRunning -ProcessId $PID -StartTime $self.CreationDate) + ' ' + (Test-ProcessRunning -ProcessId $PID)\n"
            + "$child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Milliseconds 2000' -WindowStyle Hidden -PassThru\n"
            + "$created = (Get-CimInstance Win32_Process -Filter \"ProcessId = $($child.Id)\").CreationDate\n"
            + "$before = Test-ProcessRunning -ProcessId $child.Id -StartTime $created\n"
            + "$sw = [Diagnostics.Stopwatch]::StartNew(); $gone = Wait-ProcessExit -ProcessId $child.Id -StartTime $created -TimeoutSec 60 -PollMs 100\n"
            + "'child ' + $before + ' ' + $gone + ' ' + [int]($sw.ElapsedMilliseconds -gt 200) + ' ' + $child.HasExited\n"
            + "'none ' + (Wait-ProcessExit -ProcessId 2147480000 -TimeoutSec 0)\n");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(["self False 1", "reused True False", "running True True", "child True True 1 True", "none True"], run.Lines);
    }

    [Fact]
    public async Task TheStartupWait_SeesTheNewStartupLine_WhenJellyfinDeletedAnOldLogAtTheStart()
    {
        // The JE 12.12 re-check: the je sandbox's first start in nine days timed out with its server up. Jellyfin keeps three
        // logs (retainedFileCountLimit), so the new day's log made it delete the oldest, whose "Startup complete" lines the
        // count taken before the start included, and the total never grew past it. dev-server.ps1 compares each log with
        // itself now (Get-StartupCompleteCounts, Test-NewStartupComplete); the total count is kept for an older measurement script.
        using var logs = new TempDirectory();
        var dir = logs.Path.Replace("'", "''", StringComparison.Ordinal);
        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"$dir = '{dir}'\n"
            + "Set-Content -LiteralPath (Join-Path $dir 'log_20261005.log') -Value @('a Main: Startup complete 0:00:08', 'b Main: Startup complete 0:00:07')\n"
            + "Set-Content -LiteralPath (Join-Path $dir 'log_20261007.log') -Value @('c Main: Startup complete 0:00:09')\n"
            + "Set-Content -LiteralPath (Join-Path $dir 'JellyfinEnhanced_2026-10-07.log') -Value @('d Startup complete')\n"
            + "$before = Get-StartupCompleteCounts -LogDir $dir\n"
            + "$total = Get-StartupCompleteCount -LogDir $dir\n"
            + "'before ' + $before.Count + ' ' + $total + ' ' + (Test-NewStartupComplete -LogDir $dir -Before $before)\n"
            + "Remove-Item -LiteralPath (Join-Path $dir 'log_20261005.log')\n"
            + "Set-Content -LiteralPath (Join-Path $dir 'log_20261016.log') -Value @('e Main: Startup complete 0:00:08')\n"
            + "'rotated ' + (Test-NewStartupComplete -LogDir $dir -Before $before) + ' ' + ((Get-StartupCompleteCount -LogDir $dir) -gt $total)\n"
            + "$again = Get-StartupCompleteCounts -LogDir $dir\n"
            + "'same ' + (Test-NewStartupComplete -LogDir $dir -Before $again)\n"
            + "Add-Content -LiteralPath (Join-Path $dir 'log_20261016.log') -Value 'f Main: Startup complete 0:00:07'\n"
            + "'appended ' + (Test-NewStartupComplete -LogDir $dir -Before $again)\n"
            + "'missing ' + (Get-StartupCompleteCounts -LogDir (Join-Path $dir 'none')).Count + ' ' + (Test-NewStartupComplete -LogDir (Join-Path $dir 'none') -Before @{})\n");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(["before 2 3 False", "rotated True False", "same False", "appended True", "missing 0 False"], run.Lines);

        var devServer = await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "dev-server.ps1"), TestContext.Current.CancellationToken);
        Assert.Contains("$startupCountsBefore = Get-StartupCompleteCounts -LogDir $sandbox.LogDir", devServer, StringComparison.Ordinal);
        Assert.Contains("-StartupCountsBefore $startupCountsBefore", devServer, StringComparison.Ordinal);
    }

    [Fact]
    public void StopSandbox_PollsForTheServersExit_BeforeItReportsAFailure()
    {
        var stop = File.ReadAllText(Path.Combine(Root, "scripts", "Stop-Sandbox.ps1"));
        Assert.Contains("[int] $ExitTimeoutSec = 30", stop, StringComparison.Ordinal);
        Assert.Contains("$startTime = $process.CreationDate", stop, StringComparison.Ordinal);
        Assert.Contains("$stopped = Wait-ProcessExit -ProcessId $processId -StartTime $startTime -TimeoutSec $TimeoutSec", stop, StringComparison.Ordinal);
        Assert.Contains("if (-not (Wait-ProcessExit -ProcessId $processId -StartTime $startTime -TimeoutSec $ExitTimeoutSec)) {", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("Wait-Process ", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Process -Id $processId", stop, StringComparison.Ordinal);

        // It still stops nothing but the sandbox's own server, by its command line, and never by name.
        Assert.Contains("Test-SandboxServerCommandLine -Process $again -Sandbox $sandbox", stop, StringComparison.Ordinal);
        Assert.DoesNotContain("Stop-Process -Name", stop, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SandboxConfiguration_IsTheParameters_ElseWithNoBuildTheOneTheSandboxLastRan_ElseDebug()
    {
        // The probes restart a sandbox with dev-server.ps1 -NoBuild, which once deployed the Debug build over a sandbox
        // started on Release.
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "$release = [pscustomobject]@{ Name = 'x'; Configuration = 'Release' }\n"
            + "$old = [pscustomobject]@{ Name = 'x' }\n"
            + "foreach ($case in @(\n"
            + "    @{ R = ''; N = $true; P = $release }, @{ R = ''; N = $false; P = $release }, @{ R = 'Debug'; N = $true; P = $release },\n"
            + "    @{ R = ''; N = $true; P = $old }, @{ R = ''; N = $true; P = $null }, @{ R = 'release'; N = $false; P = $null },\n"
            + "    @{ R = ''; N = $true; P = [pscustomobject]@{ Configuration = 'Weird' } })) {\n"
            + "    $c = Resolve-SandboxConfiguration -Requested $case.R -NoBuild:($case.N) -Previous $case.P\n"
            + "    $c.Configuration + ' ' + $c.Source\n"
            + "}\n"
            + "try { Resolve-SandboxConfiguration -Requested 'Fast' | Out-Null } catch { 'refused: ' + $_.Exception.Message }\n");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(
            ["Release sandbox.json", "Debug default", "Debug parameter", "Debug default", "Debug default", "Release parameter", "Debug default", "refused: Configuration 'Fast' is neither Debug nor Release."],
            run.Lines);
    }

    [Fact]
    public async Task DevServer_WithNoBuild_DeploysTheConfigurationTheSandboxLastRan_UnlessConfigurationIsGiven()
    {
        // The real dev-server.ps1 and module in a copy of the repository layout, a sandbox.json that says Release, and a
        // synthetic Jellyfin folder whose jellyfin.exe is an empty file: the script says which build it takes, then its
        // start of the empty file fails (nothing runs, no plugin is deployed: -Plugins None).
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
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var state = Path.Combine(repo, ".sandbox", "cfgtest", "sandbox.json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!);
        await File.WriteAllTextAsync(state, $"{{ \"Name\": \"cfgtest\", \"Port\": {port}, \"Configuration\": \"Release\", \"Pid\": null }}", token);
        var devServer = Path.Combine(repo, "scripts", "dev-server.ps1");
        string Start(string extra) => $"try {{ & '{devServer}' -Name cfgtest -Port {port} -Plugins None -Community None -Background -JellyfinDir '{server}' {extra} 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }} }}\n"
            + "catch { [Console]::Out.WriteLine('stopped: ' + ($_.Exception.Message -replace \"`r?`n\", ' | ')) }\n";
        const string Remembered = "host: Configuration: Release, the build this sandbox last ran (sandbox.json, with -NoBuild). Pass -Configuration to choose.";

        var noBuild = await WindowsPowerShell.RunWithModuleAsync(Start("-NoBuild"));
        Assert.True(noBuild.ExitCode == 0, noBuild.Output);
        Assert.Contains(Remembered, noBuild.Lines);
        Assert.Contains(noBuild.Lines, l => l.StartsWith("host: Starting sandbox 'cfgtest'", StringComparison.Ordinal));
        Assert.Single(noBuild.Lines, l => l.StartsWith("stopped: ", StringComparison.Ordinal));

        foreach (var extra in new[] { "-NoBuild -Configuration Debug", "-NoBuild -Configuration Release", string.Empty })
        {
            var other = await WindowsPowerShell.RunWithModuleAsync(Start(extra));
            Assert.True(other.ExitCode == 0, other.Output);
            Assert.DoesNotContain(other.Lines, l => l.StartsWith("host: Configuration: ", StringComparison.Ordinal));
            Assert.Contains(other.Lines, l => l.StartsWith("host: Starting sandbox 'cfgtest'", StringComparison.Ordinal));
        }

        var source = await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "dev-server.ps1"), token);
        Assert.Contains("$requestedConfiguration = if ($PSBoundParameters.ContainsKey('Configuration')) { $Configuration } else { '' }", source, StringComparison.Ordinal);
        Assert.Contains("-Configuration $Configuration -NoBuild:$NoBuild", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DevServer_ASandboxOfAnotherName_RestartedWithoutItsSettings_KeepsItsCommunityPluginsAndBaseUrl()
    {
        // The JSON store hardening's review, finding 8: a probe's restart (-Name x -Port n -Background -NoBuild) of a sandbox
        // whose name isn't a standard one took its community plugins and BaseUrl from the standard table only: none, so it
        // removed the plugin folders "(not requested)" and served at the root, although sandbox.json records both. The real
        // dev-server.ps1 in a copy of the repository layout, a synthetic Jellyfin folder whose jellyfin.exe is an empty file
        // (its start fails; nothing runs), and a sandbox.json of an earlier run with File Transformation and /jf.
        using var temp = new TempDirectory();
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        Directory.CreateDirectory(Path.Combine(repo, "plugins"));
        foreach (var file in new[] { "dev-server.ps1", "JellyfinSandbox.psm1", "Deploy-Plugin.ps1" })
        {
            File.Copy(Path.Combine(Root, "scripts", file), Path.Combine(repo, "scripts", file));
        }

        var server = Path.Combine(temp.Path, "server");
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(server, "jellyfin-web"));
        Directory.CreateDirectory(Path.Combine(server, "plugins", "File Transformation_2.5.5.0"));
        await File.WriteAllTextAsync(Path.Combine(server, "jellyfin.exe"), string.Empty, token);
        await File.WriteAllTextAsync(Path.Combine(server, "ffmpeg.exe"), string.Empty, token);
        await File.WriteAllTextAsync(Path.Combine(server, "jellyfin-web", "index.html"), "<html></html>", token);
        await File.WriteAllTextAsync(Path.Combine(server, "plugins", "File Transformation_2.5.5.0", "meta.json"), "{ \"autoUpdate\": true }", token);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var sandbox = Path.Combine(repo, ".sandbox", "c4fix");
        var copied = Path.Combine(sandbox, "data", "plugins", "File Transformation_2.5.5.0");
        Directory.CreateDirectory(copied);
        await File.WriteAllTextAsync(Path.Combine(copied, "meta.json"), "{ \"autoUpdate\": false }", token);
        await File.WriteAllTextAsync(
            Path.Combine(sandbox, "sandbox.json"),
            $"{{ \"Name\": \"c4fix\", \"Port\": {port}, \"BaseUrl\": \"/jf\", \"Community\": [\"FileTransformation\"], \"Configuration\": \"Debug\", \"Pid\": null }}",
            token);
        var devServer = Path.Combine(repo, "scripts", "dev-server.ps1");
        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"try {{ & '{devServer}' -Name c4fix -Port {port} -Plugins None -Background -NoBuild -JellyfinDir '{server}' 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }} }}\n"
            + "catch { [Console]::Out.WriteLine('stopped: ' + ($_.Exception.Message -replace \"`r?`n\", ' | ')) }\n");

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains(run.Lines, l => l.StartsWith($"host: Starting sandbox 'c4fix' on http://127.0.0.1:{port}/jf ", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Lines, l => l.Contains("(not requested)", StringComparison.Ordinal));
        Assert.True(Directory.Exists(copied), run.Output);
        Assert.Contains("<BaseUrl>/jf</BaseUrl>", await File.ReadAllTextAsync(Path.Combine(sandbox, "data", "config", "network.xml"), token), StringComparison.Ordinal);
        using var state = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sandbox, "sandbox.json"), token));
        Assert.Equal("/jf", state.RootElement.GetProperty("BaseUrl").GetString());
        Assert.Equal(["FileTransformation"], state.RootElement.GetProperty("Community").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task DevServer_CommunityFrom_CopiesThatFoldersVersion_AndARestartKeepsIt()
    {
        // -CommunityFrom takes the community plugins from another plugins folder (a sandbox's cached copy) instead of the
        // Jellyfin folder's, whose newest version may be one the sandbox must not run; sandbox.json keeps the folder, so a
        // restart without it neither copies the Jellyfin folder's newer version nor removes the copy as an older one.
        using var temp = new TempDirectory();
        var repo = Path.Combine(temp.Path, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        Directory.CreateDirectory(Path.Combine(repo, "plugins"));
        foreach (var file in new[] { "dev-server.ps1", "JellyfinSandbox.psm1", "Deploy-Plugin.ps1" })
        {
            File.Copy(Path.Combine(Root, "scripts", file), Path.Combine(repo, "scripts", file));
        }

        var token = TestContext.Current.CancellationToken;
        var server = Path.Combine(temp.Path, "server");
        Directory.CreateDirectory(Path.Combine(server, "jellyfin-web"));
        Directory.CreateDirectory(Path.Combine(server, "plugins", "Plugin Pages_3.0.0.0"));
        await File.WriteAllTextAsync(Path.Combine(server, "jellyfin.exe"), string.Empty, token);
        await File.WriteAllTextAsync(Path.Combine(server, "ffmpeg.exe"), string.Empty, token);
        await File.WriteAllTextAsync(Path.Combine(server, "jellyfin-web", "index.html"), "<html></html>", token);
        var cache = Path.Combine(temp.Path, "cache-plugins");
        Directory.CreateDirectory(Path.Combine(cache, "Plugin Pages_2.4.7.0"));
        await File.WriteAllTextAsync(Path.Combine(cache, "Plugin Pages_2.4.7.0", "meta.json"), "{ \"autoUpdate\": true }", token);
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var devServer = Path.Combine(repo, "scripts", "dev-server.ps1");
        string Start(string extra) => $"try {{ & '{devServer}' -Name cfrom -Port {port} -Plugins None -Background -NoBuild -JellyfinDir '{server}' {extra} 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }} }}\n"
            + "catch { [Console]::Out.WriteLine('stopped: ' + ($_.Exception.Message -replace \"`r?`n\", ' | ')) }\n";
        var plugins = Path.Combine(repo, ".sandbox", "cfrom", "data", "plugins");
        async Task<bool> AutoUpdate(string folder)
        {
            using var meta = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "meta.json"), token));
            return meta.RootElement.GetProperty("autoUpdate").GetBoolean();
        }

        var first = await WindowsPowerShell.RunWithModuleAsync(Start($"-Community PluginPages -CommunityFrom '{cache}'"));
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.Contains(first.Lines, l => l.StartsWith("host: Copied Plugin Pages_2.4.7.0 from ", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(plugins, "Plugin Pages_2.4.7.0")), first.Output);
        Assert.False(Directory.Exists(Path.Combine(plugins, "Plugin Pages_3.0.0.0")), first.Output);
        Assert.False(await AutoUpdate(Path.Combine(plugins, "Plugin Pages_2.4.7.0")));
        Assert.True(await AutoUpdate(Path.Combine(cache, "Plugin Pages_2.4.7.0")));

        // The synthetic jellyfin.exe never starts, so sandbox.json isn't written: write it as a started run records it.
        var source = await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "dev-server.ps1"), token);
        Assert.Contains("    CommunityFrom = $CommunityFrom\n", source.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            Path.Combine(repo, ".sandbox", "cfrom", "sandbox.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Name = "cfrom", Port = port, BaseUrl = string.Empty, Community = new[] { "PluginPages" }, CommunityFrom = cache, Configuration = "Debug", Pid = (int?)null }),
            token);

        var restart = await WindowsPowerShell.RunWithModuleAsync(Start(string.Empty));
        Assert.True(restart.ExitCode == 0, restart.Output);
        Assert.DoesNotContain(restart.Lines, l => l.Contains("Removed Plugin Pages", StringComparison.Ordinal));
        Assert.True(Directory.Exists(Path.Combine(plugins, "Plugin Pages_2.4.7.0")), restart.Output);
        Assert.False(Directory.Exists(Path.Combine(plugins, "Plugin Pages_3.0.0.0")), restart.Output);
    }

    [Fact]
    public void SyntheticAssemblies_SayWhatTheTestsMeant()
    {
        using var temp = new TempDirectory();
        var path = SyntheticAssembly.Write(Path.Combine(temp.Path, "A.dll"), "A", new Version(1, 2, 3, 4), [("B", new Version(5, 6, 7, 8))]);

        var (name, version, references) = SyntheticAssembly.Read(path);

        Assert.Equal("A", name);
        Assert.Equal(new Version(1, 2, 3, 4), version);
        Assert.Equal("B, Version=5.6.7.8", Assert.Single(references).FullName.Split(", Culture", StringSplitOptions.None)[0]);
    }

    // The real Deploy-Plugin.ps1 and module in a copy of the repository layout under root, with a synthetic RefCheck plugin
    // whose Release build output (for -NoBuild) references the given assemblies, a synthetic 10.11.5 server folder and
    // a data folder that doesn't exist yet.
    // A repository with one synthetic plugin (RefCheck unless named) and its Release build, a fake program folder and the
    // data folder's path (not made).
    private static async Task<(string Deploy, string Server, string Data)> DeployFixtureAsync(string root, (string Name, Version Version)[] references, string plugin = "RefCheck")
    {
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "scripts"));
        foreach (var file in new[] { "Deploy-Plugin.ps1", "JellyfinSandbox.psm1" })
        {
            File.Copy(Path.Combine(Root, "scripts", file), Path.Combine(repo, "scripts", file));
        }

        var assembly = "Jellyfin.Plugin." + plugin;
        var pluginFolder = Path.Combine(repo, "plugins", plugin);
        Directory.CreateDirectory(Path.Combine(pluginFolder, assembly));
        await File.WriteAllTextAsync(Path.Combine(pluginFolder, assembly, assembly + ".csproj"), "<Project />", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(pluginFolder, "build.yaml"),
            $"name: \"{plugin}\"\nguid: \"4c0d5f7e-3b1a-4e0e-9a51-1d2b3c4d5e6f\"\nversion: \"1.0.0.0\"\ntargetAbi: \"10.11.5.0\"\nframework: \"net9.0\"\n"
            + $"overview: \"Test\"\ndescription: \"Test\"\ncategory: \"General\"\nowner: \"tests\"\nartifacts:\n- \"{assembly}.dll\"\nchangelog: \"Test\"\n",
            TestContext.Current.CancellationToken);
        SyntheticAssembly.Write(Path.Combine(repo, "artifacts", "deploy", plugin, "Release", assembly + ".dll"), assembly, new Version(1, 0, 0, 0), references);
        return (Path.Combine(repo, "scripts", "Deploy-Plugin.ps1"), FakeServer(root), Path.Combine(root, "data"));
    }

    // The id Jellyfin's API gives a task (ScheduledTaskWorker.Id: the type name's MD5 in the "N" format).
    private static string TaskId(string type) => type.GetMD5().ToString("N", CultureInfo.InvariantCulture);

    // A TaskInfo as GET /ScheduledTasks returns it; LastExecutionResult is left out when null (WhenWritingNull).
    private static Dictionary<string, object> ApiTask(string type, string key, object[]? triggers = null, string? lastStart = null, string state = "Idle")
    {
        var info = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["Name"] = type[(type.LastIndexOf('.') + 1)..],
            ["State"] = state,
            ["Id"] = TaskId(type),
            ["Triggers"] = triggers ?? [],
            ["Description"] = "test",
            ["Category"] = "test",
            ["IsHidden"] = false,
            ["Key"] = key,
        };
        if (lastStart is not null)
        {
            info["LastExecutionResult"] = new { StartTimeUtc = lastStart, EndTimeUtc = lastStart, Status = "Completed", Key = key, Id = TaskId(type) };
        }

        return info;
    }

    // A Jellyfin program folder as far as the checks read it: jellyfin.dll (only its assembly version, 10.11.5.0 unless
    // given) and three shipped assemblies.
    internal static string FakeServer(string root, Version? jellyfin = null)
    {
        var server = Path.Combine(root, "server");
        Directory.CreateDirectory(server);
        SyntheticAssembly.Write(Path.Combine(server, "jellyfin.dll"), "jellyfin", jellyfin ?? new Version(10, 11, 5, 0));
        SyntheticAssembly.Write(Path.Combine(server, "System.Runtime.dll"), "System.Runtime", new Version(9, 0, 0, 0));
        SyntheticAssembly.Write(Path.Combine(server, "Microsoft.Data.Sqlite.dll"), "Microsoft.Data.Sqlite", new Version(9, 0, 11, 0));
        SyntheticAssembly.Write(Path.Combine(server, "MediaBrowser.Controller.dll"), "MediaBrowser.Controller", new Version(10, 11, 5, 0));
        File.WriteAllText(Path.Combine(server, "NotAnAssembly.dll"), "a native DLL of that name");
        return server;
    }

    [GeneratedRegex(@"\[ValidateSet\((?<list>[^)]*)\)\]\s*\r?\n\s*\[string\[\]\] \$Community", RegexOptions.CultureInvariant)]
    private static partial Regex CommunityValidateSet();

    [GeneratedRegex(@"& \(Join-Path \$PSScriptRoot 'Deploy-Plugin\.ps1'\)[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex DeployCall();
}
