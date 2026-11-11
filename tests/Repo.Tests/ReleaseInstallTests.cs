using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Xunit;

namespace Jellyfin.Plugins.Repo.Tests;

/// <summary>
/// A real-server install builds the plugin's release tag, never the working tree (scripts/README.md,
/// "Install-RealServer.ps1"). These run Install-RealServer.ps1, Deploy-Plugin.ps1 and the module from a git repository in
/// a temporary folder: the scripts copied in, a synthetic plugin RefCheck whose commits and tags each test makes (never
/// this repository's tags), a synthetic data folder and a synthetic Jellyfin program folder. RefCheck's "project" is an
/// MSBuild file whose Publish target copies a prebuilt assembly, so a build takes no compiler and no restore. No script
/// runs against the real server here: its spellings are checked on the guard alone (Test-RealServerTarget, which only reads
/// attributes, as ScriptsTests' path-guard test does), and the script's refusals on share spellings of a temporary folder.
/// The review cases (gaps an earlier version of the scripts had) are in ReleaseInstallReviewTests, on the same
/// ReleaseRepository.
/// </summary>
public sealed class ReleaseInstallTests
{
    private const string FirstOfTheRun = ReleaseRepository.RunLockPrefix;

    private static readonly string Root = PluginIdentityChecks.FindRepositoryRoot();

    // ----------------------------------------------------------------------------------------- which tag is installed

    [Fact]
    public async Task ReleaseTags_AreOrderedByVersionAsNumbers_TheNewestIsTheDefault_AndVersionPicksOne()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        var v1_2 = await repo.CommitAsync("1.2.0.0", "1.2.0");
        await repo.TagAsync("refcheck/v1.2.0", annotated: false);
        var v1_10 = await repo.CommitAsync("1.10.0.0", "1.10.0");
        await repo.TagAsync("refcheck/v1.10.0");

        // A later commit and the last tag made, but an older version than 1.10.0.
        var v1_9 = await repo.CommitAsync("1.9.0.0", "1.9.0");
        await repo.TagAsync("refcheck/v1.9.0");
        foreach (var notARelease in new[] { "refcheck/v1.10", "refcheck/v2.0.0-rc1", "refcheck/v2.0.0.0", "refcheckx/v9.0.0", "other/v3.0.0" })
        {
            await repo.TagAsync(notARelease);
        }

        var run = await WindowsPowerShell.RunWithModuleAsync(
            $"$repo = '{repo.Repo}'\n"
            + "'tags|' + (@(Get-PluginReleaseTags -Plugin RefCheck -RepoRoot $repo | ForEach-Object { $_.Name }) -join ',')\n"
            + "'string order|' + ((@('refcheck/v1.2.0', 'refcheck/v1.10.0', 'refcheck/v1.9.0') | Sort-Object -Descending) -join ',')\n"
            + "foreach ($v in @('', '1.9.0', '1.2.0')) {\n"
            + "  $r = Resolve-PluginRelease -Plugin RefCheck -RepoRoot $repo -Version $v\n"
            + "  'release|' + $v + '|' + $r.Tag + '|' + $r.Commit + '|' + $r.Version + '|' + $r.PluginVersion + '|' + $r.Newest + '|' + ($r.Branches -join ',') + '|' + $r.WorktreePath + '|' + $r.Info.Version + '|' + $r.Info.Yaml['changelog']\n"
            + "}\n");
        Assert.True(run.ExitCode == 0, run.Output);

        // Newest first as numbers; string order would have put 1.9.0 first and 1.10.0 last.
        Assert.Equal("tags|refcheck/v1.10.0,refcheck/v1.9.0,refcheck/v1.2.0", run.Lines[0]);
        Assert.Equal("string order|refcheck/v1.9.0,refcheck/v1.2.0,refcheck/v1.10.0", run.Lines[1]);
        Assert.Equal(
            [
                $"release||refcheck/v1.10.0|{v1_10}|1.10.0|1.10.0.0|True|main|{repo.WorktreeFor("1.10.0")}|1.10.0.0|1.10.0",
                $"release|1.9.0|refcheck/v1.9.0|{v1_9}|1.9.0|1.9.0.0|False|main|{repo.WorktreeFor("1.9.0")}|1.9.0.0|1.9.0",
                $"release|1.2.0|refcheck/v1.2.0|{v1_2}|1.2.0|1.2.0.0|False|main|{repo.WorktreeFor("1.2.0")}|1.2.0.0|1.2.0",
            ],
            run.Lines.Skip(2));
    }

    [Fact]
    public async Task ReleaseInstall_WhatIf_ShowsTheTagItsCommitAndVersion_NextToTheBackupPlan_AndMakesNothing()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        await repo.CommitAsync("1.2.0.0", "1.2.0");
        await repo.TagAsync("refcheck/v1.2.0");
        var v1_10 = await repo.CommitAsync("1.10.0.0", "1.10.0");
        await repo.TagAsync("refcheck/v1.10.0");
        var v1_9 = await repo.CommitAsync("1.9.0.0", "1.9.0");
        await repo.TagAsync("refcheck/v1.9.0");
        var before = repo.Snapshot();

        var newest = await repo.InstallAsync("-WhatIf");
        var older = await repo.InstallAsync("-Version 1.9.0 -WhatIf");

        Assert.True(newest.ExitCode == 0, newest.Output);
        Assert.Equal("installed", newest.Lines[^1]);
        const string Tags = "3 release tags (refcheck/v1.10.0, refcheck/v1.9.0, refcheck/v1.2.0)";
        Assert.Contains($"host: Release: refcheck/v1.10.0, commit {v1_10} (on main), RefCheck 1.10.0.0 by its build.yaml, the newest of {Tags}", newest.Lines);
        Assert.Contains(
            $@"host: Build: Release, from a temporary detached worktree of the tag at {repo.WorktreeFor("1.10.0")} (locked for this run, removed afterwards), into a build folder of this run's own, "
            + @"installed from there and checked by SHA-256, then copied to artifacts\deploy\RefCheck\Release",
            newest.Lines);
        Assert.Contains(newest.Lines, l => l.StartsWith("host: Backup to " + repo.Backups + @"\", StringComparison.Ordinal) && l.Contains("-before-RefCheck-1.10.0.0: 3 file(s)", StringComparison.Ordinal));
        Assert.Contains(newest.Lines, l => l.StartsWith("What if: Performing the operation \"Back up to " + repo.Backups, StringComparison.Ordinal)
            && l.Contains($", then install RefCheck 1.10.0.0 built from refcheck/v1.10.0 ({v1_10[..7]})\" on target \"{repo.Data}\".", StringComparison.Ordinal));

        Assert.True(older.ExitCode == 0, older.Output);
        Assert.Contains($"host: Release: refcheck/v1.9.0, commit {v1_9} (on main), RefCheck 1.9.0.0 by its build.yaml, picked by -Version 1.9.0 from {Tags}, the newest being refcheck/v1.10.0", older.Lines);
        Assert.Contains(older.Lines, l => l.StartsWith("What if: ", StringComparison.Ordinal) && l.Contains($"then install RefCheck 1.9.0.0 built from refcheck/v1.9.0 ({v1_9[..7]})", StringComparison.Ordinal));

        // Nothing made: no worktree (not even registered), no backup, no build, the data folder and the repository as they were.
        Assert.Equal(before, repo.Snapshot());
        Assert.False(Directory.Exists(repo.WorktreeFor("1.10.0")));
        Assert.Single(await repo.WorktreesAsync());
    }

    // ------------------------------------------------------------------------------------------------- the refusals

    [Fact]
    public async Task ReleaseInstall_RefusesWithoutAReleaseTag_AnUntaggedVersion_AMismatchedBuildYaml_AndATagNoBranchContains()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        await repo.CommitAsync("1.0.0.0", "1.0.0");
        await repo.TagAsync("refcheckx/v1.0.0");
        await repo.TagAsync("refcheck/v1.0");
        var data = repo.SnapshotOf(repo.Data);

        var none = await repo.InstallAsync(string.Empty);
        var noTag = $"There is no release tag of RefCheck (refcheck/v<major>.<minor>.<patch>) in {repo.Repo}, and a real-server install builds a release tag, never the working tree. "
            + "Tag the release first, or rehearse the working tree on a stopped sandbox with -FromWorkingTree. Plugins in this checkout: RefCheck.";
        Assert.Contains($"host: Refused: {noTag} Nothing was backed up or installed.", none.Lines);
        Assert.Equal("stopped: " + noTag, none.Lines[^1]);

        await repo.TagAsync("refcheck/v1.0.0");
        var unknown = await repo.InstallAsync("-Version 2.0.0");
        Assert.Equal($"stopped: There is no release tag refcheck/v2.0.0 in {repo.Repo} (the release tags of RefCheck: refcheck/v1.0.0).", unknown.Lines[^1]);
        var malformed = await repo.InstallAsync("-Version 2.0");
        Assert.Equal("stopped: -Version 2.0 is not <major>.<minor>.<patch>, such as 1.1.1 (for the tag refcheck/v1.1.1).", malformed.Lines[^1]);

        // The newest tag on a commit whose build.yaml still says 1.0.0.0: refused, not passed over for an older tag.
        await repo.CommitAsync("1.0.0.0", "after 1.0.0, still 1.0.0.0");
        await repo.TagAsync("refcheck/v2.0.0");
        var mismatch = await repo.InstallAsync(string.Empty);
        Assert.Equal(
            "stopped: refcheck/v2.0.0's plugins/RefCheck/build.yaml says version 1.0.0.0, not 2.0.0.0, so the tag isn't the release it names: a release tag goes on the commit that sets its version.",
            mismatch.Lines[^1]);

        // A tag on a commit only the tag keeps (its branch deleted).
        await repo.GitAsync("checkout", "-q", "-b", "side");
        var dangling = await repo.CommitAsync("3.0.0.0", "3.0.0 on a branch deleted since");
        await repo.TagAsync("refcheck/v3.0.0");
        await repo.GitAsync("checkout", "-q", "main");
        await repo.GitAsync("branch", "-q", "-D", "side");
        var orphan = await repo.InstallAsync("-Version 3.0.0");
        Assert.Equal(
            $"stopped: refcheck/v3.0.0 points at commit {dangling}, which no branch of {repo.Repo} contains (git branch --all --contains {dangling} lists none). "
            + "A release is tagged on the history of the branch it was released from, so this tag is not a release.",
            orphan.Lines[^1]);

        foreach (var run in new[] { none, unknown, malformed, mismatch, orphan })
        {
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Single(run.Lines, l => l.StartsWith("host: Refused: ", StringComparison.Ordinal));
            Assert.DoesNotContain(run.Lines, l => l.StartsWith("host: Release: ", StringComparison.Ordinal));
        }

        await repo.AssertNothingMadeAsync(data);
    }

    [Fact]
    public async Task ReleaseInstall_RefusesATakenWorktreeFolder_AndLeavesWhatIsThere()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        await repo.CommitAsync("1.0.0.0", "1.0.0");
        await repo.TagAsync("refcheck/v1.0.0");
        var data = repo.SnapshotOf(repo.Data);
        var path = repo.WorktreeFor("1.0.0");

        // Someone's folder.
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, "keep.txt"), "not ours", TestContext.Current.CancellationToken);
        var folder = await repo.InstallAsync(string.Empty);
        Assert.Equal(
            $"stopped: The temporary worktree's folder {path} is already taken: something is there. Move it away (or delete it, if an earlier run left it); this script never deletes what it didn't create.",
            folder.Lines[^1]);
        Assert.Equal("not ours", await File.ReadAllTextAsync(Path.Combine(path, "keep.txt"), TestContext.Current.CancellationToken));
        ReleaseRepository.DeleteTree(path);

        // A worktree registered there (an earlier run's, or anyone's): left checked out and registered.
        await repo.GitAsync("worktree", "add", "-q", "--detach", path, "refcheck/v1.0.0");
        var registered = await repo.InstallAsync(string.Empty);
        Assert.Equal(
            $"stopped: The temporary worktree's folder {path} is already taken: git has a worktree registered there. If an earlier run left it, remove it first: "
            + $"git -C \"{repo.Repo}\" worktree remove --force \"{path}\", then git -C \"{repo.Repo}\" worktree prune.",
            registered.Lines[^1]);
        Assert.True(File.Exists(Path.Combine(path, "plugins", "RefCheck", "build.yaml")));
        Assert.Equal(2, (await repo.WorktreesAsync()).Count);

        // Locked, as a running install's is (or one a stopped run left): its lock named, and the unlock.
        await repo.GitAsync("worktree", "lock", "--reason", "another install", path);
        var locked = await repo.InstallAsync(string.Empty);
        Assert.Equal(
            $"stopped: The temporary worktree's folder {path} is already taken: git has a worktree registered there (locked: another install), another install's while it runs. "
            + $"If none is running, remove it first: git -C \"{repo.Repo}\" worktree unlock \"{path}\", git -C \"{repo.Repo}\" worktree remove --force \"{path}\", then git -C \"{repo.Repo}\" worktree prune.",
            locked.Lines[^1]);
        Assert.Contains("locked another install", await repo.GitAsync("worktree", "list", "--porcelain"), StringComparison.Ordinal);
        await repo.GitAsync("worktree", "unlock", path);

        // Its folder deleted by hand: still registered until git worktree prune, which the script leaves to the user.
        ReleaseRepository.DeleteTree(path);
        var gone = await repo.InstallAsync(string.Empty);
        Assert.Equal(
            $"stopped: The temporary worktree's folder {path} is already taken: git has a worktree registered there whose folder is gone. Clear it first: git -C \"{repo.Repo}\" worktree prune.",
            gone.Lines[^1]);
        Assert.Equal(2, (await repo.WorktreesAsync()).Count);

        foreach (var run in new[] { folder, registered, locked, gone })
        {
            Assert.True(run.ExitCode == 0, run.Output);
            Assert.Single(run.Lines, l => l.StartsWith("host: Refused: ", StringComparison.Ordinal));
        }

        await repo.GitAsync("worktree", "prune");
        await repo.AssertNothingMadeAsync(data);
    }

    [Fact]
    public async Task ReleaseInstall_RefusesNoBuildWithoutFromWorkingTree_AndVersionWithIt()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        await repo.CommitAsync("1.0.0.0", "1.0.0");
        await repo.TagAsync("refcheck/v1.0.0");
        var data = repo.SnapshotOf(repo.Data);

        var noBuild = await repo.InstallAsync("-NoBuild");
        var both = await repo.InstallAsync("-FromWorkingTree -Version 1.0.0");

        Assert.Equal("stopped: -NoBuild installs the working tree's last build, so it needs -FromWorkingTree (rehearsals only). A release is always built from its tag.", noBuild.Lines[^1]);
        Assert.Equal("stopped: -Version picks a release tag, and -FromWorkingTree builds the working tree instead. Use one of them.", both.Lines[^1]);
        await repo.AssertNothingMadeAsync(data);
    }

    [Fact]
    public async Task FromWorkingTree_IsRefusedForTheRealServersDataFolder_InEverySpellingTheGuardKnows()
    {
        // The guard Install-RealServer.ps1 asks first, on the real server's own spellings. It only reads attributes
        // (Test-ProtectedPath; the junction is made in a temporary folder and only resolved). No script runs against them.
        const string Real = @"C:\ProgramData\Jellyfin\Server";
        using var temp = new TempDirectory();
        var sandboxData = Path.Combine(temp.Path, "sandbox", "data");
        Directory.CreateDirectory(sandboxData);
        var junction = Path.Combine(temp.Path, "to-the-real-server");
        (string Path, bool Real)[] cases =
        [
            (Real, true),
            (@"c:\programdata\jellyfin\server", true),
            ("C:/ProgramData/Jellyfin/Server/", true),
            (@"C:\ProgramData\Jellyfin\Server\.", true),
            (@"C:\ProgramData\Jellyfin\Server\..\Server", true),
            (@"C:\Users\..\ProgramData\Jellyfin\Server", true),
            (@"C:\PROGRA~3\Jellyfin\Server", true),
            (@"\\?\C:\ProgramData\Jellyfin\Server", true),
            (@"\\.\C:\ProgramData\Jellyfin\Server", true),
            (@"\\localhost\C$\ProgramData\Jellyfin\Server", true),
            (@"\\?\UNC\localhost\C$\ProgramData\Jellyfin\Server", true),
            (@"C:\ProgramData\Jellyfin\Server\data", true),
            (@"Microsoft.PowerShell.Core\FileSystem::C:\ProgramData\Jellyfin\Server", true),
            (@"FileSystem::C:\ProgramData\Jellyfin\Server", true),
            (@"C:\ProgramData\JellyfinOther\data", false),
            (sandboxData, false),
            (@"\\?\" + sandboxData, false),
            (@"FileSystem::" + sandboxData, false),
        ];

        var run = await WindowsPowerShell.RunWithModuleAsync(
            string.Concat(cases.Select(c => $"[Console]::Out.WriteLine('case|' + (Test-RealServerTarget -DataDir '{c.Path}'))\n"))
            + "Push-Location 'C:\\ProgramData'\n"
            + "[Console]::Out.WriteLine('relative|' + (Test-RealServerTarget -DataDir 'Jellyfin\\Server'))\n"
            + "Pop-Location\n"
            + $"if (Test-Path -LiteralPath '{Real}' -PathType Container) {{\n"
            + $"  New-Item -ItemType Junction -Path '{junction}' -Target '{Real}' | Out-Null\n"
            + $"  try {{ [Console]::Out.WriteLine('junction|' + (Test-RealServerTarget -DataDir '{junction}\\data')) }} finally {{ [System.IO.Directory]::Delete('{junction}') }}\n"
            + "}\n");
        Assert.True(run.ExitCode == 0, run.Output);

        var results = run.Lines.Where(l => l.StartsWith("case|", StringComparison.Ordinal)).Select(l => l[5..]).ToList();
        Assert.Equal(cases.Length, results.Count);
        var wrong = cases.Zip(results).Where(p => !string.Equals(p.First.Real.ToString(), p.Second, StringComparison.OrdinalIgnoreCase)).Select(p => $"{p.First.Path}: expected {p.First.Real}, got {p.Second}").ToList();
        Assert.Empty(wrong);
        Assert.Contains("relative|True", run.Lines);
        if (Directory.Exists(Real))
        {
            Assert.Contains("junction|True", run.Lines);
        }

        Assert.False(Directory.Exists(junction));

        // The script asks it first, before it reads anything of the data folder or the checkout; its default is the real
        // server, so -FromWorkingTree without -DataDir is refused too. -DataDir is read with the script's own drives first.
        var script = await File.ReadAllTextAsync(Path.Combine(Root, "scripts", "Install-RealServer.ps1"), TestContext.Current.CancellationToken);
        var resolved = script.IndexOf("$DataDir = ConvertFrom-DevicePath (Resolve-FullPath (ConvertTo-FileSystemPath -Path $DataDir -SessionState $ExecutionContext.SessionState))", StringComparison.Ordinal);
        var guard = script.IndexOf("    if (Test-RealServerTarget -DataDir $DataDir) {", StringComparison.Ordinal);
        Assert.True(resolved > 0 && guard > resolved, "Install-RealServer.ps1 asks Test-RealServerTarget right after resolving -DataDir.");
        Assert.True(script.LastIndexOf("if ($FromWorkingTree) {", guard, StringComparison.Ordinal) > resolved, "the guard is the first thing -FromWorkingTree does");
        foreach (var later in new[]
        {
            "Get-PluginBuildInfo -Plugin $Plugin", "[System.IO.Directory]::Exists([System.IO.Path]::Combine($DataDir, $required))", "[System.IO.Path]::GetPathRoot($DataDir)", "Get-JellyfinProcessesForDataDir -DataDir $DataDir",
            "Get-ChildItem -LiteralPath $dataRoot", "Copy-Item -LiteralPath $m.Item.Source", "& (Join-Path $PSScriptRoot 'Deploy-Plugin.ps1')",
        })
        {
            Assert.True(script.IndexOf(later, StringComparison.Ordinal) > guard, later + " comes after the guard");
        }

        Assert.Contains(@"[string] $DataDir = 'C:\ProgramData\Jellyfin\Server',", script, StringComparison.Ordinal);

        // The script itself, on share spellings of a temporary data folder, which the guard can't tell apart from the real
        // server: refused before anything is read or made.
        var scripts = Path.Combine(temp.Path, "repo", "scripts");
        Directory.CreateDirectory(scripts);
        foreach (var file in new[] { "Install-RealServer.ps1", "Deploy-Plugin.ps1", "JellyfinSandbox.psm1" })
        {
            File.Copy(Path.Combine(Root, "scripts", file), Path.Combine(scripts, file));
        }

        Directory.CreateDirectory(Path.Combine(sandboxData, "config"));
        Directory.CreateDirectory(Path.Combine(sandboxData, "data"));
        var share = $@"\\localhost\{sandboxData[0]}${sandboxData[2..]}";
        var backups = Path.Combine(temp.Path, "backups");
        foreach (var spelling in new[] { share, @"\\?\UNC" + share[1..] })
        {
            var refused = await WindowsPowerShell.RunWithModuleAsync(
                $"try {{ & '{Path.Combine(scripts, "Install-RealServer.ps1")}' -Plugin RefCheck -DataDir '{spelling}' -BackupRoot '{backups}' -FromWorkingTree -NoBuild 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }}; [Console]::Out.WriteLine('installed') }}\n"
                + "catch { [Console]::Out.WriteLine('stopped: ' + $_.Exception.Message) }\n");
            Assert.True(refused.ExitCode == 0, refused.Output);
            Assert.Equal(
                $"host: Refused: -FromWorkingTree builds the working tree, which is for rehearsals on a stopped sandbox only, and {share} is the real server's data folder "
                + "(under C:\\ProgramData\\Jellyfin, or a share, device path or drive the scripts cannot tell apart from it). Leave out -FromWorkingTree to install a release tag. Nothing was backed up or installed.",
                refused.Lines[0]);
            Assert.StartsWith("stopped: -FromWorkingTree builds the working tree", refused.Lines[^1], StringComparison.Ordinal);
        }

        Assert.False(Directory.Exists(backups));
        Assert.Equal(["config", "data"], Directory.GetDirectories(sandboxData).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal));
    }

    // ----------------------------------------------------------------------------------------- the install and its clean-up

    [Fact]
    public async Task ReleaseInstall_BuildsTheTagInATemporaryWorktree_InstallsThatBuild_AndRemovesTheWorktree()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        var released = await ReleaseRepository.CompileAsync(repo.Combine("released"), "1.10.0.0", "Released");
        var unreleased = await ReleaseRepository.CompileAsync(repo.Combine("unreleased"), "1.10.0.0", "Unreleased");
        var tagged = await repo.CommitAsync("1.10.0.0", "1.10.0 as released", released);
        await repo.TagAsync("refcheck/v1.10.0");

        // main moves on under the same version number: what the working tree would have installed.
        var head = await repo.CommitAsync("1.10.0.0", "unreleased work, still 1.10.0.0", unreleased);

        // An earlier version, installed by Deploy-Plugin.ps1 once: Deploy removes it, RESTORE.txt puts it back. And a build of
        // the same version from the working tree (the trap a release install avoids): replaced in place, and RESTORE.txt puts it back too.
        var earlier = Path.Combine(repo.Data, "plugins", "RefCheck_1.9.0.0");
        Directory.CreateDirectory(earlier);
        await File.WriteAllTextAsync(Path.Combine(earlier, "meta.json"), $"{{\"guid\":\"{ReleaseRepository.PluginGuid}\",\"version\":\"1.9.0.0\"}}", TestContext.Current.CancellationToken);
        var sameVersion = Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0");
        Directory.CreateDirectory(sameVersion);
        File.Copy(unreleased, Path.Combine(sameVersion, "Jellyfin.Plugin.RefCheck.dll"));

        var run = await repo.InstallAsync(string.Empty);

        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Equal("installed", run.Lines[^1]);
        var worktree = repo.WorktreeFor("1.10.0");
        var hash = Sha256(released).ToLowerInvariant();
        string[] steps =
        [
            $"host: Release: refcheck/v1.10.0, commit {tagged} (on main), RefCheck 1.10.0.0 by its build.yaml, the newest of 1 release tags (refcheck/v1.10.0)",
            $"host: Checked out refcheck/v1.10.0 into {worktree} (detached at {tagged[..7]}, locked: {FirstOfTheRun}*",
            "host: Backed up and checked: 5 file(s), 0.0 MB.",
            $"host: Release checkout: {worktree} is refcheck/v1.10.0 (commit {tagged}), unchanged, nothing ignored or hidden in it, locked for this run.",
            "host: Removed RefCheck_1.9.0.0",
            $@"host: Deployed RefCheck 1.10.0.0 (Release) to {repo.Data}\plugins\RefCheck_1.10.0.0",
            $"host: Checked by SHA-256: Jellyfin.Plugin.RefCheck.dll {hash}",
            $@"host: Copied this build to {repo.Repo}\artifacts\deploy\RefCheck\Release, where -NoBuild finds it.",
            $"host: Recorded in backup.json and RESTORE.txt: Jellyfin.Plugin.RefCheck.dll {hash}",
            $"host: Removed the temporary worktree {worktree} (git worktree unlock, git worktree remove --force, git worktree prune).",
            "host: Done. Start Jellyfin again (Start menu > Jellyfin Server, or the tray app).",
        ];
        var at = steps.Select(s => LineIndex(run.Lines, s)).ToList();
        Assert.True(at.All(i => i >= 0) && at.SequenceEqual(at.Order()), "in this order: " + string.Join(" / ", steps.Zip(at).Where(p => p.Second < 0).Select(p => p.First)) + "\n" + run.Output);

        // The tag's build, not the working tree's: the tag's assembly and the tag's build.yaml in meta.json.
        var installed = Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0");
        Assert.Equal(Sha256(released), Sha256(Path.Combine(installed, "Jellyfin.Plugin.RefCheck.dll")));
        Assert.NotEqual(Sha256(unreleased), Sha256(Path.Combine(installed, "Jellyfin.Plugin.RefCheck.dll")));
        using (var meta = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(installed, "meta.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("1.10.0 as released", meta.RootElement.GetProperty("changelog").GetString());
            Assert.Equal("1.10.0.0", meta.RootElement.GetProperty("version").GetString());
        }

        Assert.False(Directory.Exists(earlier));

        // The build stays where -NoBuild finds it, as the working tree's build did before releases were built from tags: copied there after the install,
        // from the run's own build folder, which is gone.
        Assert.Equal(Sha256(released), Sha256(Path.Combine(repo.Repo, "artifacts", "deploy", "RefCheck", "Release", "Jellyfin.Plugin.RefCheck.dll")));
        Assert.Empty(repo.RunBuildFolders());

        // The backup says what was installed, its files by SHA-256 among it, and RESTORE.txt's way A puts the earlier version
        // back.
        var backup = Assert.Single(Directory.GetDirectories(repo.Backups));
        Assert.EndsWith("-before-RefCheck-1.10.0.0", backup, StringComparison.Ordinal);
        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "backup.json"), TestContext.Current.CancellationToken)))
        {
            Assert.Equal("refcheck/v1.10.0", manifest.RootElement.GetProperty("tag").GetString());
            Assert.Equal(tagged, manifest.RootElement.GetProperty("commit").GetString());
            Assert.Equal("1.10.0.0", manifest.RootElement.GetProperty("version").GetString());
            var files = manifest.RootElement.GetProperty("installed").GetProperty("files").EnumerateArray().ToList();
            Assert.Equal(["Jellyfin.Plugin.RefCheck.dll"], files.Select(f => f.GetProperty("name").GetString()));
            Assert.Equal(hash, files[0].GetProperty("sha256").GetString());
            Assert.Equal(new FileInfo(released).Length, files[0].GetProperty("bytes").GetInt64());
        }

        var restore = (await File.ReadAllTextAsync(Path.Combine(backup, "RESTORE.txt"), TestContext.Current.CancellationToken)).Split("\r\n");
        Assert.Equal($"What was installed: RefCheck 1.10.0.0 built from refcheck/v1.10.0 (commit {tagged}).", restore[1]);
        Assert.Equal($@"   Its files in {repo.Data}\plugins\RefCheck_1.10.0.0, checked after the copy against the build (SHA-256): Jellyfin.Plugin.RefCheck.dll {hash}.", restore[2]);
        var wayA = restore.TakeWhile(l => !l.StartsWith("B. ", StringComparison.Ordinal)).ToList();
        var delete = wayA.IndexOf($@"2. Delete {repo.Data}\plugins\RefCheck_1.10.0.0.");
        Assert.True(delete > 0, string.Join("\n", restore));
        Assert.Equal(
            [
                $@"   Copy this backup's plugins\RefCheck_1.10.0.0 folder back into {repo.Data}\plugins (the build of this version installed before, which this install replaced).",
                $@"   Copy this backup's plugins\RefCheck_1.9.0.0 folder back into {repo.Data}\plugins (the version installed before).",
            ],
            wayA.Skip(delete + 1).Take(2));
        Assert.Equal(Sha256(unreleased), Sha256(Path.Combine(backup, "plugins", "RefCheck_1.10.0.0", "Jellyfin.Plugin.RefCheck.dll")));

        // The worktree is gone, and the checkout is as it was: main at its own commit, nothing changed.
        Assert.False(Directory.Exists(worktree));
        Assert.Single(await repo.WorktreesAsync());
        Assert.Equal(head, (await repo.GitAsync("rev-parse", "HEAD")).Trim());
        Assert.Equal("refs/heads/main", (await repo.GitAsync("symbolic-ref", "HEAD")).Trim());
        Assert.Equal(string.Empty, (await repo.GitAsync("status", "--porcelain")).Trim());
    }

    [Fact]
    public async Task ReleaseInstall_RemovesTheWorktree_WhenTheTagFailsToBuild()
    {
        using var repo = await ReleaseRepository.CreateAsync();
        var tagged = await repo.CommitAsync("1.11.0.0", "1.11.0", failBuild: true);
        await repo.TagAsync("refcheck/v1.11.0");

        var run = await repo.InstallAsync(string.Empty);

        Assert.True(run.ExitCode == 0, run.Output);
        var worktree = repo.WorktreeFor("1.11.0");
        Assert.Contains(run.Lines, l => l.StartsWith($"host: Checked out refcheck/v1.11.0 into {worktree} (detached at {tagged[..7]}, locked: {FirstOfTheRun}", StringComparison.Ordinal));
        Assert.Contains(run.Lines, l => l.Contains("error : RefCheck: a forced build failure (release tests)", StringComparison.Ordinal));
        var stopped = Array.FindIndex(run.Lines, l => l.StartsWith("host: Stopped: the install failed after the backup: Build of RefCheck failed. The backup is in " + repo.Backups, StringComparison.Ordinal));
        var removed = Array.IndexOf(run.Lines, $"host: Removed the temporary worktree {worktree} (git worktree unlock, git worktree remove --force, git worktree prune).");
        Assert.True(stopped >= 0 && removed > stopped, run.Output);
        Assert.Equal("stopped: Build of RefCheck failed.", run.Lines[^1]);

        Assert.False(Directory.Exists(worktree));
        Assert.Single(await repo.WorktreesAsync());
        Assert.Empty(Directory.GetDirectories(Path.Combine(repo.Data, "plugins"), "RefCheck_*"));
        Assert.Empty(repo.RunBuildFolders());
        Assert.True(File.Exists(Path.Combine(Assert.Single(Directory.GetDirectories(repo.Backups)), "RESTORE.txt")), "the backup was taken before the build");
    }

    [Fact]
    public async Task DeployRealServer_InstallsNothingButTheReleaseWorktreeThisRunMade_AsItWasTagged()
    {
        // Deploy-Plugin.ps1 -RealServer, run on its own: it refuses everything but what Install-RealServer.ps1 hands it, the
        // worktree of the tag it made for its run (locked with the run's reason, passed as -ReleaseLock), with nothing changed,
        // added, ignored or hidden from git status in it (an ignored *.csproj.user, which MSBuild imports, and
        // assume-unchanged or skip-worktree files passed before). A synthetic data folder as the target, which takes
        // -RealServer's path all the same.
        using var repo = await ReleaseRepository.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var released = await ReleaseRepository.CompileAsync(repo.Combine("released"), "1.10.0.0", "Released");
        var tagged = await repo.CommitAsync("1.10.0.0", "1.10.0", released);
        await repo.TagAsync("refcheck/v1.10.0");
        var head = await repo.CommitAsync("1.10.0.0", "unreleased work, still 1.10.0.0");
        const string Lock = FirstOfTheRun + "0123456789abcdef0123456789abcdef (PID 1, started 2026-10-17T00:00:00Z)";
        var lockArgument = $"-ReleaseLock '{Lock}'";
        var checkout = repo.Combine("checkout");
        await repo.GitAsync("worktree", "add", "-q", "--lock", "--reason", Lock, "--detach", checkout, "refcheck/v1.10.0");
        var later = repo.Combine("later");
        await repo.GitAsync("worktree", "add", "-q", "--lock", "--reason", Lock, "--detach", later, "main");
        var plain = repo.Combine("plain", "plugins", "RefCheck");
        Directory.CreateDirectory(plain);
        File.Copy(Path.Combine(checkout, "plugins", "RefCheck", "build.yaml"), Path.Combine(plain, "build.yaml"));
        var data = repo.SnapshotOf(repo.Data);

        const string OnlyInstall = "-RealServer installs only a release tag's build: run Install-RealServer.ps1, which checks the tag out into a temporary worktree locked for its run "
            + "and passes it here as -SourceRoot, with -ReleaseLock, the lock's reason.";
        (string Arguments, string Refusal)[] cases =
        [
            ("-RealServer", OnlyInstall),
            ($"-RealServer -SourceRoot '{checkout}'", OnlyInstall),
            ($"-RealServer {lockArgument}", OnlyInstall),
            ($"-RealServer -SourceRoot '{repo.Repo}' {lockArgument}", $"{repo.Repo} has 1 ignored file(s) or folder(s) (!! scripts/), and a build reads those too (MSBuild imports "
                + "an ignored *.csproj.user into its project): a release is built from a checkout of its tag that nothing was added to."),
            ($"-RealServer -SourceRoot '{later}' {lockArgument}", $"{later} is at commit {head}, but refcheck/v1.10.0 is commit {tagged}: it isn't that release's checkout."),
            ($"-RealServer -SourceRoot '{repo.Combine("plain")}' {lockArgument}", $"{repo.Combine("plain")} is not the top of a git checkout."),
            ($"-RealServer -SourceRoot '{checkout}' {lockArgument} -NoBuild", "-SourceRoot builds that checkout, and -NoBuild would install this repository's last build instead. Use one of them."),
            ($"-RealServer -SourceRoot '{checkout}' {lockArgument} -Configuration Debug", "-RealServer installs a release, which is built in Release, not Debug."),
            ($"-SourceRoot '{checkout}' {lockArgument}", "-ReleaseLock names the release worktree of a -RealServer install; without -RealServer it means nothing."),
            ($"-RealServer -SourceRoot '{checkout}' -ReleaseLock 'another run'", $"{checkout} is refcheck/v1.10.0 as tagged, but not the worktree Install-RealServer.ps1 made of it for this run "
                + $"(git has it locked: {Lock}; this run's is locked with the run's own reason): -RealServer installs only that one, so run Install-RealServer.ps1."),
        ];
        foreach (var (arguments, refusal) in cases)
        {
            var refused = await repo.DeployAsync(arguments);
            Assert.True(refused.ExitCode == 0, refused.Output);
            Assert.Equal("stopped: " + refusal, refused.Lines[^1]);
        }

        // A change in the tag's checkout: an untracked file is one too.
        var stray = Path.Combine(checkout, "stray.txt");
        await File.WriteAllTextAsync(stray, "x", token);
        var changed = await repo.DeployAsync($"-RealServer -SourceRoot '{checkout}' {lockArgument}");
        Assert.Equal($"stopped: {checkout} has 1 change(s) (?? stray.txt), and a release is installed exactly as it was tagged.", changed.Lines[^1]);
        File.Delete(stray);

        // An ignored file that the build would read: MSBuild imports <project>.user into the project.
        var project = Path.Combine(checkout, "plugins", "RefCheck", "Jellyfin.Plugin.RefCheck");
        var user = Path.Combine(project, "Jellyfin.Plugin.RefCheck.csproj.user");
        await File.WriteAllTextAsync(user, "<Project />", token);
        var ignored = await repo.DeployAsync($"-RealServer -SourceRoot '{checkout}' {lockArgument}");
        Assert.Equal(
            $"stopped: {checkout} has 1 ignored file(s) or folder(s) (!! plugins/RefCheck/Jellyfin.Plugin.RefCheck/Jellyfin.Plugin.RefCheck.csproj.user), and a build reads those too "
            + "(MSBuild imports an ignored *.csproj.user into its project): a release is built from a checkout of its tag that nothing was added to.",
            ignored.Lines[^1]);
        File.Delete(user);

        // A change git status is told not to see: assume-unchanged, then skip-worktree.
        const string Csproj = "plugins/RefCheck/Jellyfin.Plugin.RefCheck/Jellyfin.Plugin.RefCheck.csproj";
        var csproj = Path.Combine(project, "Jellyfin.Plugin.RefCheck.csproj");
        var original = await File.ReadAllTextAsync(csproj, token);
        await File.AppendAllTextAsync(csproj, "<!-- not the tag's -->\n", token);
        foreach (var (flag, unflag, letter) in new[] { ("--assume-unchanged", "--no-assume-unchanged", "h"), ("--skip-worktree", "--no-skip-worktree", "S") })
        {
            await repo.GitInAsync(checkout, "update-index", flag, Csproj);
            Assert.Equal(string.Empty, (await repo.GitInAsync(checkout, "status", "--porcelain", "--untracked-files=all")).Trim());
            var hidden = await repo.DeployAsync($"-RealServer -SourceRoot '{checkout}' {lockArgument}");
            Assert.Equal(
                $"stopped: {checkout} has 1 file(s) git is told not to look at ({letter} {Csproj}; in git ls-files -v lower case is assume-unchanged and S skip-worktree), "
                + "so git status can't vouch for them, and a release is installed exactly as it was tagged.",
                hidden.Lines[^1]);
            await repo.GitInAsync(checkout, "update-index", unflag, Csproj);
        }

        await File.WriteAllTextAsync(csproj, original, token);

        // The tag's clean checkout, but not locked for this run: refused too.
        await repo.GitAsync("worktree", "unlock", checkout);
        var unlocked = await repo.DeployAsync($"-RealServer -SourceRoot '{checkout}' {lockArgument}");
        Assert.Equal(
            $"stopped: {checkout} is refcheck/v1.10.0 as tagged, but not the worktree Install-RealServer.ps1 made of it for this run (git has it unlocked; this run's is locked with the run's own reason): "
            + "-RealServer installs only that one, so run Install-RealServer.ps1.",
            unlocked.Lines[^1]);
        await repo.GitAsync("worktree", "lock", "--reason", Lock, checkout);
        await repo.AssertNothingMadeAsync(data, worktrees: 3);
        Assert.Empty(repo.RunBuildFolders());

        // The worktree of the tag, as tagged and locked with the run's reason: built in a folder of its own, installed from
        // there, checked by SHA-256, and copied to the shared folder afterwards.
        var deployed = await repo.DeployAsync($"-RealServer -SourceRoot '{checkout}' {lockArgument}");
        Assert.True(deployed.ExitCode == 0, deployed.Output);
        Assert.Contains($"host: Release checkout: {checkout} is refcheck/v1.10.0 (commit {tagged}), unchanged, nothing ignored or hidden in it, locked for this run.", deployed.Lines);
        Assert.Contains($@"host: Deployed RefCheck 1.10.0.0 (Release) to {repo.Data}\plugins\RefCheck_1.10.0.0", deployed.Lines);
        Assert.Contains($"host: Checked by SHA-256: Jellyfin.Plugin.RefCheck.dll {Sha256(released).ToLowerInvariant()}", deployed.Lines);
        Assert.Equal(Sha256(released), Sha256(Path.Combine(repo.Data, "plugins", "RefCheck_1.10.0.0", "Jellyfin.Plugin.RefCheck.dll")));
        Assert.Equal(Sha256(released), Sha256(Path.Combine(repo.Repo, "artifacts", "deploy", "RefCheck", "Release", "Jellyfin.Plugin.RefCheck.dll")));
        Assert.Empty(repo.RunBuildFolders());
        foreach (var worktree in new[] { checkout, later })
        {
            await repo.GitAsync("worktree", "remove", "--force", "--force", worktree);
        }
    }

    [Fact]
    public async Task ThisRepositorysReleaseTags_EachNameTheVersionItsBuildYamlSays()
    {
        // Read only, on this checkout's own tags: each release tag of each plugin that Install-RealServer.ps1 can resolve
        // names the version its build.yaml says, so a tag on the wrong commit fails here before anyone installs it. A tag
        // may be refused only as predating this repository's layout (bookmarks/v1.0.0), or as on no branch of this
        // checkout: the release workflow checks out a tag that is pushed before the branch it was made on.
        var run = await WindowsPowerShell.RunWithModuleAsync(
            "foreach ($plugin in @(Get-PluginFolders)) {\n"
            + "  foreach ($tag in @(Get-PluginReleaseTags -Plugin $plugin -RepoRoot (Get-RepoRoot))) {\n"
            + "    try { $r = Resolve-PluginRelease -Plugin $plugin -RepoRoot (Get-RepoRoot) -Version $tag.Text; [Console]::Out.WriteLine('ok|' + $r.Tag + '|' + $r.PluginVersion + '|' + $r.Info.Version) }\n"
            + "    catch { [Console]::Out.WriteLine('refused|' + $tag.Name + '|' + $_.Exception.Message) }\n"
            + "  }\n"
            + "}\n");
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.SkipWhen(run.Lines.Length == 0, "This checkout has no release tags.");

        Assert.All(
            run.Lines.Where(l => l.StartsWith("refused|", StringComparison.Ordinal)),
            l => Assert.True(
                l.Contains("predates this repository's layout", StringComparison.Ordinal) || l.Contains("which no branch of", StringComparison.Ordinal),
                l));
        Assert.All(run.Lines.Where(l => l.StartsWith("ok|", StringComparison.Ordinal)), l => Assert.Equal(l.Split('|')[2], l.Split('|')[3]));

        // In a checkout with the optional local private/ folder (the full history), an older release resolves too.
        if (Directory.Exists(Path.Combine(Root, "private")))
        {
            Assert.Contains(run.Lines, l => l.StartsWith("ok|bookmarks/v1.1.1|1.1.1.0|", StringComparison.Ordinal));
        }
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // The line's index, or for an expected line ending in "*" the first line starting with what comes before it.
    private static int LineIndex(string[] lines, string expected) => expected.EndsWith('*')
        ? Array.FindIndex(lines, l => l.StartsWith(expected[..^1], StringComparison.Ordinal))
        : Array.IndexOf(lines, expected);
}

/// <summary>
/// A git repository in a temporary folder laid out like this one for Install-RealServer.ps1's release path: the
/// scripts (gitignored, as artifacts are, unless trackScripts commits them with the plugin), the synthetic plugin RefCheck
/// committed and tagged by the tests, a synthetic data folder and Jellyfin program folder next to it. Its release worktrees
/// land next to it, inside the temporary folder.
/// </summary>
internal sealed class ReleaseRepository : IDisposable
{
    public const string PluginGuid = "4c0d5f7e-3b1a-4e0e-9a51-1d2b3c4d5e6f";

    // How each run's lock reason starts (New-ReleaseLockReason: "Install-RealServer.ps1 run <id> (PID ..., started ...)").
    public const string RunLockPrefix = "Install-RealServer.ps1 run ";

    // A plugin "build" without a compiler or a restore: Publish copies the prebuilt assembly where the compiler writes its
    // output (obj\<Configuration>\net9.0, which Deploy-Plugin.ps1 checks a run folder's build against), then
    // from there into dotnet publish's -o, as dotnet publish does.
    private const string CopyingProject = "<Project>\n  <Target Name=\"Restore\" />\n  <Target Name=\"Publish\">\n"
        + "    <Copy SourceFiles=\"$(MSBuildThisFileDirectory)prebuilt\\Jellyfin.Plugin.RefCheck.dll\" DestinationFolder=\"$(MSBuildThisFileDirectory)obj\\$(Configuration)\\net9.0\" />\n"
        + "    <Copy SourceFiles=\"$(MSBuildThisFileDirectory)obj\\$(Configuration)\\net9.0\\Jellyfin.Plugin.RefCheck.dll\" DestinationFolder=\"$(PublishDir)\" />\n  </Target>\n</Project>\n";

    private const string FailingProject = "<Project>\n  <Target Name=\"Restore\" />\n  <Target Name=\"Publish\">\n"
        + "    <Error Text=\"RefCheck: a forced build failure (release tests)\" />\n  </Target>\n</Project>\n";

    private static readonly string Scripts = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "scripts");

    private readonly TempDirectory temp = new();

    private ReleaseRepository()
    {
    }

    public string Root => temp.Path;

    public string Repo => Path.Combine(Root, "repo");

    public string Data => Path.Combine(Root, "data");

    public string Backups => Path.Combine(Root, "backups");

    public string Server { get; private set; } = string.Empty;

    public static async Task<ReleaseRepository> CreateAsync(bool trackScripts = false)
    {
        var repo = new ReleaseRepository();
        var token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.Combine(repo.Repo, "scripts"));
        foreach (var file in new[] { "Install-RealServer.ps1", "Deploy-Plugin.ps1", "JellyfinSandbox.psm1" })
        {
            File.Copy(Path.Combine(Scripts, file), Path.Combine(repo.Repo, "scripts", file));
        }

        repo.Server = SandboxToolingTests.FakeServer(repo.Root);
        foreach (var (file, text) in new[] { (@"config\system.xml", "<ServerConfiguration />"), (@"data\jellyfin.db", "db"), (@"plugins\Other_1.0.0.0\Other.dll", "dll") })
        {
            var path = Path.Combine(repo.Data, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text, token);
        }

        await repo.GitAsync("init", "-q", "-b", "main");
        await repo.GitAsync("config", "user.name", "release tests");
        await repo.GitAsync("config", "user.email", "release-tests@example.invalid");
        await repo.GitAsync("config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repo.Repo, ".gitignore"), "artifacts/\nbin/\nobj/\n*.user\n" + (trackScripts ? string.Empty : "scripts/\n"), token);
        return repo;
    }

    public static async Task<string> CompileAsync(string folder, string version, string className)
    {
        // A real assembly with a file version (Deploy checks it against build.yaml), compiled by Windows PowerShell's Add-Type.
        var path = Path.Combine(folder, "Jellyfin.Plugin.RefCheck.dll");
        Directory.CreateDirectory(folder);
        var source = $"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] [assembly: System.Reflection.AssemblyFileVersion(\"{version}\")] "
            + $"namespace RefCheck {{ public sealed class {className} {{ }} }}";
        var run = await WindowsPowerShell.RunWithModuleAsync($"Add-Type -TypeDefinition '{source}' -OutputAssembly '{path}' -OutputType Library\n");
        Assert.True(run.ExitCode == 0 && File.Exists(path), run.Output);
        return path;
    }

    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // git writes its objects read-only.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    public string Combine(params string[] parts) => temp.Combine(parts);

    public string WorktreeFor(string version) => Path.Combine(Root, "jfp-rel-refcheck-" + version);

    // What is left of the deploys' own build folders (artifacts\builds\<Plugin>-<Configuration>-<time>-<id>): nothing, after a run.
    public string[] RunBuildFolders()
    {
        var builds = Path.Combine(Repo, "artifacts", "builds");
        return Directory.Exists(builds) ? Directory.GetFileSystemEntries(builds) : [];
    }

    public async Task<string> CommitAsync(string version, string changelog, string? assembly = null, bool failBuild = false)
    {
        var token = TestContext.Current.CancellationToken;
        var plugin = Path.Combine(Repo, "plugins", "RefCheck");
        var project = Path.Combine(plugin, "Jellyfin.Plugin.RefCheck");
        var prebuilt = Path.Combine(project, "prebuilt", "Jellyfin.Plugin.RefCheck.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(prebuilt)!);
        await File.WriteAllTextAsync(
            Path.Combine(plugin, "build.yaml"),
            $"name: \"RefCheck\"\nguid: \"{PluginGuid}\"\nversion: \"{version}\"\ntargetAbi: \"10.11.5.0\"\nframework: \"net9.0\"\noverview: \"Test\"\ndescription: \"Test\"\n"
            + $"category: \"General\"\nowner: \"tests\"\nartifacts:\n- \"Jellyfin.Plugin.RefCheck.dll\"\nchangelog: \"{changelog}\"\n",
            token);
        await File.WriteAllTextAsync(Path.Combine(project, "Jellyfin.Plugin.RefCheck.csproj"), failBuild ? FailingProject : CopyingProject, token);
        if (assembly is null)
        {
            SyntheticAssembly.Write(prebuilt, "Jellyfin.Plugin.RefCheck", new Version(version));
        }
        else
        {
            File.Copy(assembly, prebuilt, overwrite: true);
        }

        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", $"RefCheck {version}: {changelog}");
        return (await GitAsync("rev-parse", "HEAD")).Trim();
    }

    public Task TagAsync(string name, bool annotated = true) => annotated ? GitAsync("tag", "-a", name, "-m", name) : GitAsync("tag", name);

    public Task<string> GitAsync(params string[] arguments) => GitInAsync(Repo, arguments);

    public async Task<string> GitInAsync(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(folder);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start)!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        var errors = process.StandardError.ReadToEndAsync(limit.Token);
        var output = await process.StandardOutput.ReadToEndAsync(limit.Token);
        var errorText = await errors;
        await process.WaitForExitAsync(limit.Token);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed ({process.ExitCode}): {errorText}");
        return output;
    }

    public async Task<IReadOnlyList<string>> WorktreesAsync() =>
        [.. (await GitAsync("worktree", "list", "--porcelain")).Split('\n').Where(l => l.StartsWith("worktree ", StringComparison.Ordinal))];

    // Install-RealServer.ps1 with this repository's folders; a prelude (PowerShell run first, in a scope of its own that the
    // script runs inside: a function or drive of the caller's, a breakpoint) and other spellings of -Plugin, -DataDir and
    // -BackupRoot when given.
    public Task<PowerShellRun> InstallAsync(string arguments, string? prelude = null, string plugin = "RefCheck", string? dataDir = null, string? backupRoot = null) => RunAsync(
        $"& '{Path.Combine(Repo, "scripts", "Install-RealServer.ps1")}' -Plugin {plugin} -DataDir '{dataDir ?? Data}' -JellyfinDir '{Server}' -BackupRoot '{backupRoot ?? Backups}' {arguments}",
        prelude);

    public Task<PowerShellRun> DeployAsync(string arguments, string? prelude = null, string? dataDir = null) => RunAsync(
        $"& '{Path.Combine(Repo, "scripts", "Deploy-Plugin.ps1")}' -Plugin RefCheck -DataDir '{dataDir ?? Data}' -JellyfinDir '{Server}' {arguments}",
        prelude);

    // Every file and folder under the temporary folder, with sizes and write times: what -WhatIf must leave as it was.
    public string[] Snapshot() => SnapshotOf(Root);

    public string[] SnapshotOf(string folder) =>
        [.. Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .Select(p => Directory.Exists(p) ? Path.GetRelativePath(Root, p) + @"\" : $"{Path.GetRelativePath(Root, p)}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}")
            .Order(StringComparer.OrdinalIgnoreCase)];

    // No backup, no worktree (only the given number registered: the repository and the test's own), the data folder as it was.
    public async Task AssertNothingMadeAsync(string[] data, int worktrees = 1)
    {
        Assert.False(Directory.Exists(Backups), "no backup");
        Assert.Empty(Directory.GetDirectories(Root, "jfp-rel-*"));
        Assert.Equal(data, SnapshotOf(Data));
        Assert.Equal(worktrees, (await WorktreesAsync()).Count);
    }

    public void Dispose()
    {
        try
        {
            DeleteTree(Root);
        }
        catch (IOException)
        {
            // Best effort, as TempDirectory's own.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort, as above.
        }

        temp.Dispose();
    }

    private static Task<PowerShellRun> RunAsync(string command, string? prelude) => WindowsPowerShell.RunWithModuleAsync(
        $"try {{ {(prelude is null ? command : $"& {{\n{prelude}\n{command}\n}}")} 6>&1 | ForEach-Object {{ [Console]::Out.WriteLine('host: ' + $_) }}; [Console]::Out.WriteLine('installed') }}\n"
        + "catch { [Console]::Out.WriteLine('stopped: ' + ($_.Exception.Message -replace \"`r?`n\", ' | ')) }\n",
        TimeSpan.FromSeconds(240));
}
