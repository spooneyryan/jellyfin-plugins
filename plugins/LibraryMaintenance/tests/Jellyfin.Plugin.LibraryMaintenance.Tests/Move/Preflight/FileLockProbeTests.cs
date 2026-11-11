using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Preflight;

/// <summary>
/// The exclusive-open probe: any other handle counts, even one that shares delete; three tries,
/// only the busy files again; a missing file isn't "open".
/// </summary>
public sealed class FileLockProbeTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.ReadWrite)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public void IsOpen_ForAnyOtherHandle(FileShare share)
    {
        var path = _temp.CreateFile("movie.mkv");
        Assert.False(FileLockProbe.IsOpen(path));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, share))
        {
            Assert.True(FileLockProbe.IsOpen(path));
        }

        Assert.False(FileLockProbe.IsOpen(path));
        Assert.False(FileLockProbe.IsOpen(_temp.Combine("missing.mkv")));
        Assert.False(FileLockProbe.IsOpen(_temp.Combine("gone", "missing.mkv")));
    }

    [Fact]
    public void IsOpen_ASymbolicLink_IsOpenedItself_NeverItsTarget()
    {
        // A held link target once made the link's sidecar "in use" (and was opened through it).
        var target = _temp.CreateFile("target.srt");
        var link = _temp.Combine("link.srt");
        SymbolicLink.ToFile(link, target);
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(FileLockProbe.IsOpen(target));
            Assert.False(FileLockProbe.IsOpen(link));
        }
    }

    [Fact]
    public void IsOpen_AFileItMayNotRead_CountsAsOpen()
    {
        // Access denied isn't "free": the move would fail on it.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("ACLs are tested on Windows.");
            return;
        }

        var path = _temp.CreateFile("locked.mkv");
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        try
        {
            Assert.True(FileLockProbe.IsOpen(path));
        }
        finally
        {
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }

        Assert.False(FileLockProbe.IsOpen(path));
    }

    [Fact]
    public void IsOpen_BeyondMaxPath()
    {
        var folder = _temp.Combine(new string('a', 120), new string('b', 120));
        Directory.CreateDirectory(folder);
        var path = Path.Join(folder, new string('c', 40) + ".mkv");
        File.WriteAllBytes(path, [1]);
        Assert.True(path.Length > 260);

        Assert.False(FileLockProbe.IsOpen(path));
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(FileLockProbe.IsOpen(path));
        }

        Assert.False(FileLockProbe.IsOpen(Path.Join(folder, "missing.mkv")));
    }

    [Fact]
    public async Task FindOpenAsync_TriesThreeTimes_OnlyTheBusyFilesAgain()
    {
        var free = _temp.CreateFile("free.mkv");
        var held = _temp.CreateFile("held.mkv");
        var waits = new List<TimeSpan>();
        using var reader = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read);
        var probe = new FileLockProbe((pause, _) =>
        {
            waits.Add(pause);
            return Task.CompletedTask;
        });

        var open = await probe.FindOpenAsync([free, held], TestContext.Current.CancellationToken);

        Assert.Equal([held], open);
        Assert.Equal([FileLockProbe.Pause, FileLockProbe.Pause], waits);
    }

    [Fact]
    public async Task FindOpenAsync_AFileClosedBetweenTries_IsFree()
    {
        var path = _temp.CreateFile("movie.mkv");
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var probe = new FileLockProbe(async (_, _) => await reader.DisposeAsync());

        Assert.Empty(await probe.FindOpenAsync([path], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindOpenAsync_WithNothingOpen_NeverWaits()
    {
        var probe = new FileLockProbe((_, _) => throw new InvalidOperationException("no wait expected"));
        Assert.Empty(await probe.FindOpenAsync([_temp.CreateFile("a.mkv"), _temp.CreateFile("b.srt")], TestContext.Current.CancellationToken));
    }
}
