using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// The review cases of the release-tag installs, each a gap an earlier version of the scripts had, on ReleaseInstallTests'
/// ReleaseRepository: the tag's build installed from a folder of the run's own and
/// checked by SHA-256 (another build landing in the shared artifacts folder, the run's own build changed before its copy),
/// the run's worktree locked with its own reason (another run that took the path keeps its worktree), PowerShell drives,
/// the scripts the real-server install runs, the plugin name in any case, two tags naming one version, and the
/// running-server refusal before the worktree and the backup. Some step into a run at a chosen moment with a PowerShell
/// breakpoint (Set-PSBreakpoint -Command ... -Action), which runs its action just before that command and lets the run go
/// on. No script runs against the real server: its spellings are asked of the guard alone, and drives on C:\ProgramData are
/// never written through.
/// </summary>
public sealed class ReleaseInstallReviewTests
{
    private const string FirstOfTheRun = ReleaseRepository.RunLockPrefix;

    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();
    [Fact]
    public async Task ReleaseInstall_TakesThePluginInAnyCase_AndNamesWhatATagsCommitLacks()
    {
        // -Plugin bookmarks was once refused as "predating this repository's layout", the tag's build.yaml being
        // looked up as plugins/bookmarks/build.yaml, while git paths are case-sensitive. The folder is matched in any case now.
        using var repo = await ReleaseRepository.CreateAsync();
        var released = await ReleaseRepository.CompileAsync(repo.Combine("released"), "1.10.0.0", "Released");
        var tagged = await repo.CommitAsync("1.10.0.0", "1.10.0", released);
        await repo.TagAsync("refcheck/v1.10.0");

        var lower = await repo.InstallAsync("-WhatIf", plugin: "refcheck");
        Assert.True(lower.ExitCode == 0, lower.Output);
        Assert.Contains($"host: Release: refcheck/v1.10.0, commit {tagged} (on main), RefCheck 1.10.0.0 by its build.yaml, the newest of 1 release tags (refcheck/v1.10.0)", lower.Lines);
        Assert.Contains(lower.Lines, l => l.StartsWith("host: Backup to " + repo.Backups, StringComparison.Ordinal) && l.Contains("-before-RefCheck-1.10.0.0: ", StringComparison.Ordinal));

        var upper = await repo.InstallAsync(string.Empty, plugin: "REFCHECK");
        Assert.True(upper.ExitCode == 0, upper.Output);
        Assert.Equal("installed", upper.Lines[^1]);
        Assert.Equal(Sha256(released), Sha256(Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0", "Jellyfin.Plugin.RefCheck.dll")));
        Assert.EndsWith("-before-RefCheck-1.10.0.0", Assert.Single(Directory.GetDirectories(repo.Backups)), StringComparison.Ordinal);

        // A tag whose commit has no folder of the plugin, and one whose commit has no plugins folder at all: each said as it is.
        await repo.GitAsync("rm", "-r", "-q", "plugins/RefCheck");
        Directory.CreateDirectory(repo.Combine("repo", "plugins", "Other"));
        await File.WriteAllTextAsync(repo.Combine("repo", "plugins", "Other", "build.yaml"), "name: \"Other\"\n", TestContext.Current.CancellationToken);
        await repo.GitAsync("add", "-A");
        await repo.GitAsync("commit", "-q", "-m", "RefCheck moved out");
        var moved = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        await repo.TagAsync("refcheck/v5.0.0");
        await repo.GitAsync("rm", "-r", "-q", "plugins");
        await repo.GitAsync("commit", "-q", "-m", "no plugins folder");
        var bare = (await repo.GitAsync("rev-parse", "HEAD")).Trim();
        await repo.TagAsync("refcheck/v6.0.0");
        var resolved = await WindowsPowerShell.RunWithModuleAsync(
            $"foreach ($v in '5.0.0', '6.0.0', '1.10.0') {{ try {{ $r = Resolve-PluginRelease -Plugin refcheck -RepoRoot '{repo.Repo}' -Version $v; 'ok|' + $r.Plugin + '|' + $r.Tag + '|' + $r.Info.Name }} catch {{ 'refused|' + $_.Exception.Message }} }}\n");
        Assert.True(resolved.ExitCode == 0, resolved.Output);
        Assert.Equal(
            [
                $"refused|refcheck/v5.0.0 (commit {moved}) has no plugins/refcheck folder (its plugins folder holds: Other).",
                $"refused|refcheck/v6.0.0 (commit {bare}) has no plugins folder, so it predates this repository's layout (Bookmarks 1.0.0 was released from its own repository) and this script can't build it.",
                "ok|RefCheck|refcheck/v1.10.0|RefCheck",
            ],
            resolved.Lines);
    }

    [Fact]
    public async Task ReleaseInstall_RefusesTwoTagsThatNameOneVersion_WhicheverWayTheVersionIsAskedFor()
    {
        // Two tags naming one version, which no test covered before. v01.2.0 and v1.2.0 are both 1.2.0 as numbers; neither
        // is taken, the newest as the default included (it isn't passed over for 1.1.0 either), until one is deleted.
        using var repo = await ReleaseRepository.CreateAsync();
        await repo.CommitAsync("1.1.0.0", "1.1.0");
        await repo.TagAsync("refcheck/v1.1.0");
        await repo.CommitAsync("1.2.0.0", "1.2.0");
        await repo.TagAsync("refcheck/v1.2.0");
        await repo.TagAsync("refcheck/v01.2.0");
        var data = repo.SnapshotOf(repo.Data);

        const string Both = "The tags refcheck/v01.2.0 and refcheck/v1.2.0 both name version 1.2.0; delete the wrong one first.";
        foreach (var arguments in new[] { string.Empty, "-Version 1.2.0", "-Version 01.2.0", "-Version 1.2.0 -WhatIf" })
        {
            var run = await repo.InstallAsync(arguments);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Equal("stopped: " + Both, run.Lines[^1]);
            Assert.Contains($"host: Refused: {Both} Nothing was backed up or installed.", run.Lines);
            Assert.DoesNotContain(run.Lines, l => l.StartsWith("host: Release: ", StringComparison.Ordinal));
        }

        // The other version is still the one -Version names.
        var older = await repo.InstallAsync("-Version 1.1.0 -WhatIf");
        Assert.True(older.ExitCode == 0, older.Output);
        Assert.Contains(older.Lines, l => l.StartsWith("host: Release: refcheck/v1.1.0, commit ", StringComparison.Ordinal));
        await repo.AssertNothingMadeAsync(data);
    }

    [Fact]
    public async Task ReleaseInstall_RefusesWhileJellyfinUsesTheDataFolder_BeforeTheWorktreeOrTheBackup()
    {
        // No test asserted before that the running-server refusal comes before the tag's worktree and the backup's
        // first copy. Get-JellyfinProcessesForDataDir answers here as for a jellyfin.exe using the data folder (a function of
        // the caller's scope, which the script finds before the module's); no process runs. Breakpoints say whether the
        // worktree, a copy or a backup file was made.
        using var repo = await ReleaseRepository.CreateAsync();
        await repo.CommitAsync("1.0.0.0", "1.0.0");
        await repo.TagAsync("refcheck/v1.0.0");
        var data = repo.SnapshotOf(repo.Data);
        var prelude = $"function Get-JellyfinProcessesForDataDir {{ param([string] $DataDir) if ($DataDir -ieq '{repo.Data}') {{ [pscustomobject]@{{ ProcessId = 4242; Name = 'jellyfin.exe' }} }} }}\n"
            + "$null = Set-PSBreakpoint -Command New-ReleaseWorktree -Action { [Console]::Out.WriteLine('ran: New-ReleaseWorktree') }\n"
            + "$null = Set-PSBreakpoint -Command Copy-Item -Action { [Console]::Out.WriteLine('ran: Copy-Item') }\n"
            + "$null = Set-PSBreakpoint -Command Write-JsonFile -Action { [Console]::Out.WriteLine('ran: Write-JsonFile') }\n";
        const string Running = "Jellyfin is still running with data folder {0} (PID 4242). Exit it from the tray icon (or stop the service) first.";

        foreach (var arguments in new[] { string.Empty, "-WhatIf" })
        {
            var run = await repo.InstallAsync(arguments, prelude);
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Equal("stopped: " + string.Format(System.Globalization.CultureInfo.InvariantCulture, Running, repo.Data), run.Lines[^1]);
            Assert.Contains($"host: Refused: {string.Format(System.Globalization.CultureInfo.InvariantCulture, Running, repo.Data)} Nothing was backed up or installed.", run.Lines);
            Assert.DoesNotContain(run.Lines, l => l.StartsWith("ran: ", StringComparison.Ordinal));
        }

        await repo.AssertNothingMadeAsync(data);

        // And in the script's text: the check comes before the worktree is made and before the backup's first copy.
        var script = await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), TestContext.Current.CancellationToken);
        var check = script.IndexOf("$running = @(Get-JellyfinProcessesForDataDir -DataDir $DataDir)", StringComparison.Ordinal);
        Assert.True(check > 0, "Install-RealServer.ps1 asks for a running jellyfin.exe.");
        Assert.True(script.IndexOf("New-ReleaseWorktree -RepoRoot", StringComparison.Ordinal) > check, "the worktree comes after the check");
        Assert.True(script.IndexOf("Copy-Item -LiteralPath $m.Item.Source", StringComparison.Ordinal) > check, "the backup comes after the check");
        Assert.True(script.IndexOf("$PSCmdlet.ShouldProcess($DataDir, $operation)", StringComparison.Ordinal) > check, "-WhatIf's plan comes after the check");
    }

    [Fact]
    public async Task ReleaseInstall_LeavesAnotherRunsWorktree_WhenThatRunTookThePathAfterTheCheck()
    {
        // The clean-up once removed whatever was at the worktree's path once this run had tried to make it there, so
        // a second run that took ..\jfp-rel-<plugin>-<x.y.z> between this run's last check and its git worktree add lost its
        // worktree to this run's finally block. Here that second run takes it exactly then (a breakpoint on New-ReleaseWorktree),
        // locked with its own reason as runs lock theirs now, and unlocked as a run of the earlier script would have it.
        using var repo = await ReleaseRepository.CreateAsync();
        await repo.CommitAsync("1.0.0.0", "1.0.0");
        await repo.TagAsync("refcheck/v1.0.0");
        var data = repo.SnapshotOf(repo.Data);
        var path = repo.WorktreeFor("1.0.0");

        foreach (var (lockArguments, lockText) in new[] { ("'--lock', '--reason', 'another run of the script', ", "locked: another run of the script"), (string.Empty, "unlocked") })
        {
            var run = await repo.InstallAsync(
                string.Empty,
                "$null = Set-PSBreakpoint -Command New-ReleaseWorktree -Action {\n"
                + $"  $r = Invoke-Git -Repo '{repo.Repo}' -Arguments @('worktree', 'add', {lockArguments}'--detach', '{path}', 'refcheck/v1.0.0')\n"
                + "  [Console]::Out.WriteLine('another run took the path: ' + $r.ExitCode)\n"
                + "}\n");

            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Contains("another run took the path: 0", run.Lines);
            Assert.Contains(run.Lines, l => l.StartsWith("host: Refused: Checking ", StringComparison.Ordinal) && l.Contains("already exists", StringComparison.Ordinal));
            Assert.Contains($"host: Left {path} as it is: the worktree registered there isn't this run's ({lockText}).", run.Lines);
            Assert.DoesNotContain(run.Lines, l => l.StartsWith("host: Removed the temporary worktree", StringComparison.Ordinal));
            Assert.StartsWith("stopped: Checking ", run.Lines[^1], StringComparison.Ordinal);

            // The other run's worktree: still checked out and registered, its lock as it was; nothing of this run's.
            Assert.True(File.Exists(Path.Combine(path, "plugins", "RefCheck", "build.yaml")));
            var list = await repo.GitAsync("worktree", "list", "--porcelain");
            Assert.Contains("worktree " + path.Replace('\\', '/'), list, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(lockText.StartsWith("locked", StringComparison.Ordinal), list.Contains("locked another run of the script", StringComparison.Ordinal));
            Assert.False(Directory.Exists(repo.Backups), "no backup");
            Assert.Equal(data, repo.SnapshotOf(repo.Data));

            await repo.GitAsync("worktree", "remove", "--force", "--force", path);
            await repo.GitAsync("worktree", "prune");
        }

        await repo.AssertNothingMadeAsync(data);
    }

    [Fact]
    public async Task PathGuard_FollowsPowerShellDrives_AndCountsADriveItCantSeeAsTheRealServer()
    {
        // A PowerShell drive (New-PSDrive Q -Root C:\ProgramData\Jellyfin, then Q:\Server) once wasn't recognised:
        // .NET doesn't know such drives, while Test-Path and Copy-Item follow them. The guard reads the drives the module sees
        // (global ones), counts a drive letter Windows doesn't know as the real server's (a script's own drive, which the
        // module can't see, or none at all), and a script reads its own drives with ConvertTo-FileSystemPath first. Drives on
        // C:\ProgramData are only asked about, never written through; the writes go to drives on a temporary folder (below).
        using var temp = new TempDirectory();
        var sandbox = Path.Combine(temp.Path, "sandbox", "data");
        Directory.CreateDirectory(sandbox);
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var free = "QRSTUVWXYJKLMNOP".First(c => !used.Contains(c));
        var run = await WindowsPowerShell.RunWithModuleAsync(
            $$"""
            New-PSDrive -Name R1PD -PSProvider FileSystem -Root 'C:\ProgramData' -Scope Global | Out-Null
            New-PSDrive -Name R1TD -PSProvider FileSystem -Root '{{temp.Path}}' -Scope Global | Out-Null
            [Console]::Out.WriteLine('global real|' + (Test-RealServerTarget -DataDir 'R1PD:\Jellyfin\Server') + '|' + (Resolve-FullPath 'R1PD:\Jellyfin\Server'))
            [Console]::Out.WriteLine('global temp|' + (Test-RealServerTarget -DataDir 'R1TD:\sandbox\data') + '|' + (Resolve-FullPath 'R1TD:\sandbox\data'))
            try { $null = Assert-SafePath -Path 'R1PD:\Jellyfin\Server\data' -What 'data folder'; [Console]::Out.WriteLine('safe real|accepted') } catch { [Console]::Out.WriteLine('safe real|' + $_.Exception.Message) }
            [Console]::Out.WriteLine('safe temp|' + (Assert-SafePath -Path 'R1TD:\sandbox\data' -What 'data folder'))
            & {
                New-PSDrive -Name R1PL -PSProvider FileSystem -Root 'C:\ProgramData' | Out-Null
                New-PSDrive -Name R1TL -PSProvider FileSystem -Root '{{temp.Path}}' | Out-Null
                $real = ConvertTo-FileSystemPath -Path 'R1PL:\Jellyfin\Server' -SessionState $ExecutionContext.SessionState
                $mine = ConvertTo-FileSystemPath -Path 'R1TL:\sandbox\data' -SessionState $ExecutionContext.SessionState
                [Console]::Out.WriteLine('local real|' + (Test-RealServerTarget -DataDir 'R1PL:\Jellyfin\Server') + '|' + (Test-RealServerTarget -DataDir $real) + '|' + $real)
                [Console]::Out.WriteLine('local temp|' + (Test-RealServerTarget -DataDir 'R1TL:\sandbox\data') + '|' + (Test-RealServerTarget -DataDir $mine) + '|' + $mine)
            }
            [Console]::Out.WriteLine('unknown drive|' + (Test-RealServerTarget -DataDir '{{free}}:\sandbox\data'))
            try { $null = Resolve-FullPath 'HKLM:\SOFTWARE'; [Console]::Out.WriteLine('registry|accepted') } catch { [Console]::Out.WriteLine('registry|' + $_.Exception.Message) }

            """);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal(
            [
                @"global real|True|C:\ProgramData\Jellyfin\Server",
                $"global temp|False|{sandbox}",
                @"safe real|Refusing to continue: the data folder (C:\ProgramData\Jellyfin\Server\data) is under C:\ProgramData\Jellyfin, which belongs to the real Jellyfin server.",
                $"safe temp|{sandbox}",
                @"local real|True|True|C:\ProgramData\Jellyfin\Server",
                $"local temp|True|False|{sandbox}",
                "unknown drive|True",
                @"registry|HKLM:\SOFTWARE is not a file system path (PowerShell reads it as a path of Registry).",
            ],
            run.Lines);
    }

    [Fact]
    public async Task ReleaseInstall_AndDeploy_ReadTheirPathsWithTheCallersOwnPowerShellDrives()
    {
        // The scripts read -DataDir, -BackupRoot and -SourceRoot with the drives of the scope that runs them (a drive of the
        // caller's own here, which the module alone can't see): the plan, the backup and the install name the folder the drive
        // leads to. The drive is on this test's temporary folder.
        using var repo = await ReleaseRepository.CreateAsync();
        var released = await ReleaseRepository.CompileAsync(repo.Combine("released"), "1.10.0.0", "Released");
        var tagged = await repo.CommitAsync("1.10.0.0", "1.10.0", released);
        await repo.TagAsync("refcheck/v1.10.0");
        var drive = $"New-PSDrive -Name R1D -PSProvider FileSystem -Root '{repo.Root}' | Out-Null\n";

        var run = await repo.InstallAsync(string.Empty, drive, dataDir: @"R1D:\data", backupRoot: @"R1D:\backups");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("installed", run.Lines[^1]);
        Assert.Contains($"host: Data folder: {repo.Data} (no jellyfin.exe uses it)", run.Lines);
        Assert.Equal(Sha256(released), Sha256(Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0", "Jellyfin.Plugin.RefCheck.dll")));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Assert.Single(Directory.GetDirectories(repo.Backups)), "backup.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal(repo.Data, manifest.RootElement.GetProperty("dataDir").GetString());
            Assert.Equal($@"{repo.Data}\plugins\RefCheck_1.10.0.0", manifest.RootElement.GetProperty("installed").GetProperty("folder").GetString());
        }

        // Deploy-Plugin.ps1 on its own, its -SourceRoot and -DataDir through such a drive.
        const string Lock = FirstOfTheRun + "00000000000000000000000000000001 (PID 1, started 2026-10-17T00:00:00Z)";
        var checkout = repo.Combine("checkout");
        await repo.GitAsync("worktree", "add", "-q", "--lock", "--reason", Lock, "--detach", checkout, "refcheck/v1.10.0");
        var deployed = await repo.DeployAsync($"-RealServer -SourceRoot 'R1D:\\checkout' -ReleaseLock '{Lock}'", drive, dataDir: @"R1D:\data");
        Assert.True(deployed.ExitCode == 0, deployed.Output);
        Assert.Contains($"host: Release checkout: {checkout} is refcheck/v1.10.0 (commit {tagged}), unchanged, nothing ignored or hidden in it, locked for this run.", deployed.Lines);
        Assert.Contains($@"host: Deployed RefCheck 1.10.0.0 (Release) to {repo.Data}\plugins\RefCheck_1.10.0.0", deployed.Lines);
        await repo.GitAsync("worktree", "remove", "--force", "--force", checkout);
    }

    [Fact]
    public async Task ReleaseInstall_InstallsTheTagsBuild_WhenAnotherBuildLandsInTheSharedArtifactsFolderMeanwhile()
    {
        // The tag's build was once published into the checkout-wide artifacts\deploy\<Plugin>\Release
        // and installed from there after the checks, so any other Release build of the plugin landing there in between
        // (Deploy-Plugin.ps1's default, dev-server.ps1 -Configuration Release, a rehearsal, another session) went onto the server
        // while RESTORE.txt said the tag was installed. Here the working tree's build (the same version) lands there exactly
        // then: a breakpoint on the reference check, which Deploy runs after the build and before the copy. The same breakpoint
        // reads the worktree's lock while Deploy runs.
        using var repo = await ReleaseRepository.CreateAsync();
        var released = await ReleaseRepository.CompileAsync(repo.Combine("released"), "1.10.0.0", "Released");
        var unreleased = await ReleaseRepository.CompileAsync(repo.Combine("unreleased"), "1.10.0.0", "Unreleased");
        await repo.CommitAsync("1.10.0.0", "1.10.0 as released", released);
        await repo.TagAsync("refcheck/v1.10.0");
        await repo.CommitAsync("1.10.0.0", "unreleased work, still 1.10.0.0", unreleased);
        var shared = Path.Combine(repo.Repo, "artifacts", "deploy", "RefCheck", "Release");
        var worktree = repo.WorktreeFor("1.10.0");

        var run = await repo.InstallAsync(
            string.Empty,
            $$"""
            $null = Set-PSBreakpoint -Command Assert-AssemblyReferenceVersions -Action {
                New-Item -ItemType Directory -Force -Path '{{shared}}' | Out-Null
                [System.IO.File]::Copy('{{unreleased}}', '{{Path.Combine(shared, "Jellyfin.Plugin.RefCheck.dll")}}', $true)
                [Console]::Out.WriteLine('race: the working tree''s build landed in the shared folder')
                $entry = @(Get-GitWorktrees -RepoRoot '{{repo.Repo}}' | Where-Object { $_.Path -ieq '{{worktree}}' })
                [Console]::Out.WriteLine('during the deploy: ' + $entry.Count + '|' + $entry[0].Locked + '|' + $entry[0].LockReason)
            }

            """);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("installed", run.Lines[^1]);
        Assert.Single(run.Lines, l => l == "race: the working tree's build landed in the shared folder");

        // The tag's assembly is what was installed and recorded, not the one that landed in the shared folder.
        var installed = Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0", "Jellyfin.Plugin.RefCheck.dll");
        Assert.Equal(Sha256(released), Sha256(installed));
        Assert.NotEqual(Sha256(unreleased), Sha256(installed));
        var backup = Assert.Single(Directory.GetDirectories(repo.Backups));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal(Sha256(released).ToLowerInvariant(), manifest.RootElement.GetProperty("installed").GetProperty("files")[0].GetProperty("sha256").GetString());
        }

        // The shared folder got the tag's build after the install, over what had landed there.
        Assert.Equal(Sha256(released), Sha256(Path.Combine(shared, "Jellyfin.Plugin.RefCheck.dll")));
        Assert.Empty(repo.RunBuildFolders());

        // While Deploy ran, the worktree was locked with this run's reason, the one the checkout line names.
        var during = Assert.Single(run.Lines, l => l.StartsWith("during the deploy: ", StringComparison.Ordinal));
        var checkedOut = Assert.Single(run.Lines, l => l.StartsWith($"host: Checked out refcheck/v1.10.0 into {worktree} (detached at ", StringComparison.Ordinal));
        var reason = checkedOut[(checkedOut.IndexOf("locked: ", StringComparison.Ordinal) + 8)..^1];
        Assert.StartsWith(FirstOfTheRun, reason, StringComparison.Ordinal);
        Assert.Equal($"during the deploy: 1|True|{reason}", during);
        Assert.False(Directory.Exists(worktree));
    }

    [Fact]
    public async Task ReleaseInstall_InstallsNothingOfABuildThatChangedAfterItWasChecked()
    {
        // Every installed DLL by its SHA-256 against the build that was checked, refused otherwise. Here the
        // run's own build folder (artifacts\builds\RefCheck-Release-<time>-<id>) gets another assembly of the same version
        // after the build, before the copy (a breakpoint on the reference check): the copy isn't the build that was checked,
        // so the plugin folder is removed again and the run stops; the backup stays, its backup.json without "installed".
        using var repo = await ReleaseRepository.CreateAsync();
        var released = await ReleaseRepository.CompileAsync(repo.Combine("released"), "1.10.0.0", "Released");
        var unreleased = await ReleaseRepository.CompileAsync(repo.Combine("unreleased"), "1.10.0.0", "Unreleased");
        await repo.CommitAsync("1.10.0.0", "1.10.0 as released", released);
        await repo.TagAsync("refcheck/v1.10.0");
        var builds = Path.Combine(repo.Repo, "artifacts", "builds");

        var run = await repo.InstallAsync(
            string.Empty,
            $$"""
            $null = Set-PSBreakpoint -Command Assert-AssemblyReferenceVersions -Action {
                foreach ($dll in @(Get-ChildItem -LiteralPath '{{builds}}' -Recurse -Filter 'Jellyfin.Plugin.RefCheck.dll')) {
                    [System.IO.File]::Copy('{{unreleased}}', $dll.FullName, $true)
                    [Console]::Out.WriteLine('changed: ' + $dll.Directory.Name)
                }
            }

            """);

        Assert.True(run.ExitCode == 0, run.Output);
        var changed = Assert.Single(run.Lines, l => l.StartsWith("changed: ", StringComparison.Ordinal));
        Assert.Matches(@"^changed: RefCheck-Release-\d{8}-\d{6}-[0-9a-f]{8}$", changed);
        var target = Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0");
        var mismatch = $"The installed files aren't the build that was checked (SHA-256: Jellyfin.Plugin.RefCheck.dll is {Sha256(unreleased).ToLowerInvariant()}, the build was {Sha256(released).ToLowerInvariant()}): "
            + $@"something changed {builds}\{changed[9..]} during the install. {target} was removed again, so nothing of that build is installed.";
        Assert.Equal("stopped: " + mismatch, run.Lines[^1]);
        Assert.Contains(run.Lines, l => l.StartsWith($"host: Stopped: the install failed after the backup: {mismatch} The backup is in {repo.Backups}", StringComparison.Ordinal));
        Assert.False(Directory.Exists(target));
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("host: Deployed ", StringComparison.Ordinal));

        var backup = Assert.Single(Directory.GetDirectories(repo.Backups));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("installed").ValueKind);
        }

        Assert.DoesNotContain("Its files in", await File.ReadAllTextAsync(Path.Combine(backup, "RESTORE.txt"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(repo.Repo, "artifacts", "deploy", "RefCheck", "Release")), "nothing copied to the shared folder");
        Assert.Empty(repo.RunBuildFolders());
        Assert.False(Directory.Exists(repo.WorktreeFor("1.10.0")));
        Assert.Single(await repo.WorktreesAsync());
    }

    [Fact]
    public async Task ReleaseInstall_InstallsNothingWrittenIntoTheRunsBuildFolder_BeforeItsChecks_ItsBaselineIsTheCompilersOutput()
    {
        // The SHA-256 baseline was once read from the run's own folder (artifacts\builds\
        // RefCheck-Release-<time>-<id>, a name anyone can predict) after dotnet publish, so a file written there in that gap
        // was checked, installed and recorded in backup.json as the tag's build. The baseline is the compiler's output in the
        // locked release worktree now (obj\Release\net9.0), and a run folder's file that isn't it is refused before the install.
        // Here another assembly of the same version lands in the run folder right after the publish (a breakpoint on the
        // check itself, which Deploy runs first after the build).
        using var repo = await ReleaseRepository.CreateAsync();
        var released = await ReleaseRepository.CompileAsync(repo.Combine("released"), "1.10.0.0", "Released");
        var planted = await ReleaseRepository.CompileAsync(repo.Combine("planted"), "1.10.0.0", "Planted");
        await repo.CommitAsync("1.10.0.0", "1.10.0 as released", released);
        await repo.TagAsync("refcheck/v1.10.0");
        var builds = Path.Combine(repo.Repo, "artifacts", "builds");
        var worktree = repo.WorktreeFor("1.10.0");
        var compiled = Path.Combine(worktree, "plugins", "RefCheck", "Jellyfin.Plugin.RefCheck", "obj", "Release", "net9.0", "Jellyfin.Plugin.RefCheck.dll");

        var run = await repo.InstallAsync(
            string.Empty,
            $$"""
            $null = Set-PSBreakpoint -Command Assert-BuildIsCompilerOutput -Action {
                foreach ($dll in @(Get-ChildItem -LiteralPath '{{builds}}' -Recurse -Filter 'Jellyfin.Plugin.RefCheck.dll')) {
                    [System.IO.File]::Copy('{{planted}}', $dll.FullName, $true)
                    [Console]::Out.WriteLine('planted: ' + $dll.Directory.Name)
                }

                [Console]::Out.WriteLine('compiler output: ' + (Test-Path -LiteralPath '{{compiled}}'))
            }

            """);

        Assert.True(run.ExitCode == 0, run.Output);
        var folder = Assert.Single(run.Lines, l => l.StartsWith("planted: ", StringComparison.Ordinal))[9..];
        Assert.Matches(@"^RefCheck-Release-\d{8}-\d{6}-[0-9a-f]{8}$", folder);
        Assert.Contains("compiler output: True", run.Lines);
        var refusal = $@"Jellyfin.Plugin.RefCheck.dll in {builds}\{folder} isn't what the compiler wrote (SHA-256 {Sha256(planted).ToLowerInvariant()}; the compiler's output "
            + $"{compiled} is {Sha256(released).ToLowerInvariant()}): something wrote into the run's build folder after the build, so nothing of it was installed.";
        Assert.Equal("stopped: " + refusal, run.Lines[^1]);
        Assert.False(Directory.Exists(Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0")));
        Assert.DoesNotContain(run.Lines, l => l.StartsWith("host: Deployed ", StringComparison.Ordinal));
        var backup = Assert.Single(Directory.GetDirectories(repo.Backups));
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("installed").ValueKind);
        }

        Assert.Empty(repo.RunBuildFolders());
        Assert.False(Directory.Exists(worktree));

        // Without the plant the same install puts the tag's build in place, recorded by the compiler output's SHA-256.
        ReleaseRepository.DeleteTree(repo.Backups);
        var clean = await repo.InstallAsync(string.Empty);
        Assert.True(clean.ExitCode == 0, clean.Output);
        Assert.Equal("installed", clean.Lines[^1]);
        Assert.Equal(Sha256(released), Sha256(Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0", "Jellyfin.Plugin.RefCheck.dll")));
        Assert.Contains($"host: Checked by SHA-256: Jellyfin.Plugin.RefCheck.dll {Sha256(released).ToLowerInvariant()}", clean.Lines);
    }

    [Fact]
    public async Task RealServerInstall_RefusesADataFolderOnADriveLetterThatIsntThere_WithARefusedLine()
    {
        // Step 1 once asked Join-Path for <data folder>\config, which stops with PowerShell's own
        // "Cannot find drive" for a drive letter that isn't there, instead of the script's "Refused: ..." line. Such a folder
        // takes the real server's path (the guard can't tell it apart from it), so the scripts must be committed ones here;
        // nothing is made, with -WhatIf or without.
        var letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(c => (char)c).Reverse()
            .First(c => !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c));
        var nowhere = $@"{letter}:\jellyfin-data-c2-verification";
        using var repo = await ReleaseRepository.CreateAsync(trackScripts: true);
        await repo.CommitAsync("1.0.0.0", "1.0.0");
        await repo.TagAsync("refcheck/v1.0.0");
        var data = repo.SnapshotOf(repo.Data);

        foreach (var arguments in new[] { "-WhatIf", string.Empty })
        {
            var run = await repo.InstallAsync(arguments, dataDir: nowhere);

            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Equal($@"stopped: {nowhere} is not a Jellyfin data folder (its drive or share {letter}:\ isn't there).", run.Lines[^1]);
            Assert.Contains($@"host: Refused: {nowhere} is not a Jellyfin data folder (its drive or share {letter}:\ isn't there). Nothing was backed up or installed.", run.Lines);
            Assert.DoesNotContain(run.Lines, l => l.Contains("Cannot find drive", StringComparison.OrdinalIgnoreCase));
        }

        await repo.AssertNothingMadeAsync(data);
    }

    [Fact]
    public async Task RealServerInstall_RefusesScriptsThatArentTheCheckoutsCommittedOnes()
    {
        // The install builds a tag, but its own scripts run as the checkout has them, so "run it from the main
        // checkout, whatever it holds" wasn't true of the scripts. For the real server the checkout's PowerShell scripts must be
        // its HEAD's: Get-RepoScriptsProblem, here on a repository with its scripts committed, changed in each way git can see
        // or be told not to see. A Node probe being written beside them doesn't count.
        using var repo = await ReleaseRepository.CreateAsync(trackScripts: true);
        await repo.CommitAsync("1.0.0.0", "1.0.0");
        await repo.TagAsync("refcheck/v1.0.0");
        var running = string.Join(", ", new[] { "Install-RealServer.ps1", "Deploy-Plugin.ps1", "JellyfinSandbox.psm1" }.Select(f => $"'{Path.Combine(repo.Repo, "scripts", f)}'"));
        var check = await WindowsPowerShell.RunWithModuleAsync(
            $$"""
            $repo = '{{repo.Repo}}'
            $running = @({{running}})
            function Show([string] $label) { [Console]::Out.WriteLine($label + '|' + [string](Get-RepoScriptsProblem -RepoRoot $repo -Running $running)) }
            function G([string[]] $arguments) { $run = Invoke-Git -Repo $repo -Arguments $arguments; if ($run.ExitCode -ne 0) { throw $run.Error } }
            $deploy = Join-Path $repo 'scripts\Deploy-Plugin.ps1'
            Show 'clean'
            [System.IO.File]::WriteAllText((Join-Path $repo 'scripts\new-probe.mjs'), 'x')
            Show 'probe'
            [System.IO.File]::AppendAllText($deploy, "# changed`n")
            Show 'changed'
            G @('add', 'scripts/Deploy-Plugin.ps1')
            Show 'staged'
            G @('reset', '-q')
            G @('update-index', '--assume-unchanged', 'scripts/Deploy-Plugin.ps1')
            Show 'assume-unchanged'
            G @('update-index', '--no-assume-unchanged', 'scripts/Deploy-Plugin.ps1')
            G @('update-index', '--skip-worktree', 'scripts/Deploy-Plugin.ps1')
            Show 'skip-worktree'
            G @('update-index', '--no-skip-worktree', 'scripts/Deploy-Plugin.ps1')
            G @('checkout', '--', 'scripts/Deploy-Plugin.ps1')
            New-Item -ItemType Directory -Path (Join-Path $repo 'scripts\sub') | Out-Null
            [System.IO.File]::WriteAllText((Join-Path $repo 'scripts\sub\extra.ps1'), 'x')
            Show 'added'

            """);
        Assert.True(check.ExitCode == 0, check.Output);
        var results = check.Lines.Select(l => l.Split('|', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
        Assert.Equal(string.Empty, results["clean"]);
        Assert.Equal(string.Empty, results["probe"]);
        string Problem(string what) => $"The scripts this install runs aren't the committed ones of {repo.Repo}: Install-RealServer.ps1, Deploy-Plugin.ps1 and JellyfinSandbox.psm1 run as they are "
            + $"in its scripts folder, whichever tag is built, and {what}. Commit those changes or put them aside first, or run the install from a checkout whose scripts are committed, "
            + $"such as a new worktree: git -C \"{repo.Repo}\" worktree add --detach <folder> main, then <folder>\\scripts\\Install-RealServer.ps1.";
        Assert.Equal(Problem("git status lists 1 change(s) to them ( M scripts/Deploy-Plugin.ps1)"), results["changed"]);
        Assert.Equal(Problem("git status lists 1 change(s) to them (M  scripts/Deploy-Plugin.ps1)"), results["staged"]);
        Assert.Equal(Problem("git is told not to look at 1 of them (h scripts/Deploy-Plugin.ps1; lower case is assume-unchanged, S skip-worktree)"), results["assume-unchanged"]);
        Assert.Equal(Problem("git is told not to look at 1 of them (S scripts/Deploy-Plugin.ps1; lower case is assume-unchanged, S skip-worktree)"), results["skip-worktree"]);
        Assert.Equal(Problem("git status lists 1 change(s) to them (?? scripts/sub/extra.ps1)"), results["added"]);

        // The script asks it for the real server only, before it resolves the release or makes anything: here on a share
        // spelling of the temporary data folder, which the guard can't tell apart from the real server's, with a script
        // changed. A stopped sandbox's folder takes changed scripts (a rehearsal of the changes): its plan.
        var share = $@"\\localhost\{repo.Data[0]}${repo.Data[2..]}";
        var data = repo.SnapshotOf(repo.Data);
        var real = await repo.InstallAsync("-WhatIf", dataDir: share);
        Assert.True(real.ExitCode == 0, real.Output);
        Assert.Equal("stopped: " + Problem("git status lists 1 change(s) to them (?? scripts/sub/extra.ps1)"), real.Lines[^1]);
        Assert.Single(real.Lines, l => l.StartsWith("host: Refused: The scripts this install runs aren't the committed ones", StringComparison.Ordinal));
        Assert.DoesNotContain(real.Lines, l => l.StartsWith("host: Release: ", StringComparison.Ordinal));
        var sandbox = await repo.InstallAsync("-WhatIf");
        Assert.True(sandbox.ExitCode == 0, sandbox.Output);
        Assert.Contains(sandbox.Lines, l => l.StartsWith("host: Release: refcheck/v1.0.0, commit ", StringComparison.Ordinal));
        await repo.AssertNothingMadeAsync(data);

        // Scripts that aren't committed at all (the other tests' repositories ignore scripts/): each file the install runs named.
        using var untracked = await ReleaseRepository.CreateAsync();
        await untracked.CommitAsync("1.0.0.0", "1.0.0");
        await untracked.TagAsync("refcheck/v1.0.0");
        var none = await untracked.InstallAsync("-WhatIf", dataDir: $@"\\localhost\{untracked.Data[0]}${untracked.Data[2..]}");
        Assert.True(none.ExitCode == 0, none.Output);
        Assert.Contains("3 of the files it runs aren't committed at all (scripts/Install-RealServer.ps1, scripts/Deploy-Plugin.ps1, scripts/JellyfinSandbox.psm1)", none.Lines[^1], StringComparison.Ordinal);

        var script = await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), TestContext.Current.CancellationToken);
        var asks = script.IndexOf("$scriptsProblem = Get-RepoScriptsProblem -RepoRoot $repo -Running @($PSCommandPath, (Join-Path $PSScriptRoot 'Deploy-Plugin.ps1'), (Join-Path $PSScriptRoot 'JellyfinSandbox.psm1'))", StringComparison.Ordinal);
        Assert.True(asks > 0 && asks < script.IndexOf("$release = Resolve-PluginRelease", StringComparison.Ordinal), "the scripts are checked before the release is resolved");
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
