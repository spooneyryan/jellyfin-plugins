using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Kit.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The free-space probe: the nearest existing folder is probed, with a
/// trailing separator; a per-user folder that doesn't exist yet and a missing clip folder are measured by the folder above
/// them; no existing folder at all is <see cref="DriveSpaceStatus.Unavailable"/> (refused, never waved through); a failing
/// call on an existing folder is <see cref="DriveSpaceStatus.Unknown"/> (the guard off), unless its error says the path or
/// its volume is gone (a dangling junction, as measured on Windows), which is Unavailable. First over fake calls, then on this
/// machine's file system (Windows).
/// </summary>
public sealed class DriveSpaceProbeTests : IDisposable
{
    private readonly TempDirectory _directory = new();

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void AnExistingFolder_IsProbedItself_WithATrailingSeparator()
    {
        var calls = new FakeCalls(@"C:\data\clips") { Volume = @"C:\" };

        var space = new DriveSpaceProbe(calls).Get(@"C:\data\clips");

        Assert.Equal(DriveSpaceStatus.Ok, space.Status);
        Assert.Equal([@"C:\data\clips\"], calls.FreeSpaceCalls);
        Assert.Equal(@"C:\data\clips", space.ProbedPath);
        Assert.Equal(@"C:\", space.Volume);
        Assert.Equal(FakeCalls.Available, space.AvailableBytes);
        Assert.Equal(FakeCalls.Total, space.TotalBytes);
        Assert.Null(space.Error);
    }

    [Fact]
    public void APerUserFolderNotCreatedYet_IsMeasuredByItsClipFolder()
    {
        // A per-user folder that doesn't exist yet under an existing root: the root is probed.
        var calls = new FakeCalls(@"C:\", @"C:\data", @"C:\data\clips");

        var space = new DriveSpaceProbe(calls).Get(@"C:\data\clips\alice");

        Assert.Equal(DriveSpaceStatus.Ok, space.Status);
        Assert.Equal(@"C:\data\clips", space.ProbedPath);
        Assert.Equal(@"C:\data\clips\alice", space.Folder);
        Assert.Equal([@"C:\data\clips\"], calls.FreeSpaceCalls);
    }

    [Fact]
    public void AClipFolderThatDoesntExist_OnAnExistingDrive_IsMeasuredByTheDrive()
    {
        // A root that doesn't exist on an existing drive: the drive root is probed (a new ClipOutputPath on Q: still
        // gets LowFreeSpace).
        var calls = new FakeCalls(@"Q:\");

        var space = new DriveSpaceProbe(calls).Get(@"Q:\Clips\new\alice");

        Assert.Equal(DriveSpaceStatus.Ok, space.Status);
        Assert.Equal(@"Q:\", space.ProbedPath);
        Assert.Equal([@"Q:\"], calls.FreeSpaceCalls);
        Assert.Equal([@"Q:\Clips\new\alice", @"Q:\Clips\new", @"Q:\Clips", @"Q:\"], calls.ExistsCalls);
    }

    [Fact]
    public void NoExistingFolderAtAll_IsUnavailable_AndNothingIsProbed()
    {
        // A drive letter that isn't there: refused, not waved through.
        var calls = new FakeCalls();

        var space = new DriveSpaceProbe(calls).Get(@"R:\clips\alice");

        Assert.Equal(DriveSpaceStatus.Unavailable, space.Status);
        Assert.Null(space.ProbedPath);
        Assert.Null(space.AvailableBytes);
        Assert.Empty(calls.FreeSpaceCalls);
        Assert.Equal(@"R:\", space.VolumeName);
    }

    [Theory]
    [InlineData(5)] // ERROR_ACCESS_DENIED
    [InlineData(50)] // ERROR_NOT_SUPPORTED
    [InlineData(1117)] // ERROR_IO_DEVICE
    public void AFailingCallOnAnExistingFolder_IsUnknown(int error)
    {
        var calls = new FakeCalls(@"C:\", @"C:\data") { Failure = error };

        var space = new DriveSpaceProbe(calls).Get(@"C:\data\clips");

        Assert.Equal(DriveSpaceStatus.Unknown, space.Status);
        Assert.Equal(error, space.Error);
        Assert.Equal(@"C:\data", space.ProbedPath);
        Assert.Null(space.AvailableBytes);
    }

    [Theory]
    [MemberData(nameof(GoneErrors))]
    public void AFailingCallWhoseErrorSaysThePathIsGone_IsUnavailable(int error)
    {
        // Measured on Windows: a junction or mount point whose volume is gone still "exists" (Directory.Exists), and the call fails
        // with error 3; the guard must not switch off for it.
        var calls = new FakeCalls(@"C:\", @"C:\data", @"C:\data\j-dangling") { Failure = error };

        var space = new DriveSpaceProbe(calls).Get(@"C:\data\j-dangling\alice");

        Assert.Equal(DriveSpaceStatus.Unavailable, space.Status);
        Assert.Equal(error, space.Error);
        Assert.Equal(@"C:\data\j-dangling", space.ProbedPath);
    }

    public static TheoryData<int> GoneErrors()
    {
        var data = new TheoryData<int>();
        foreach (var error in new[] { 2, 3, 15, 21, 53, 55, 67, 1231, 1920, 2250 })
        {
            data.Add(error);
        }

        return data;
    }

    [Fact]
    public void TheGoneErrors_AreThePathOrVolumeGoneErrors()
        => Assert.Equal([2, 3, 15, 21, 53, 55, 67, 1231, 1920, 2250], DriveSpaceProbe.GoneErrors.Order());

    [Fact]
    public void AUncPath_IsProbedWithATrailingBackslash()
    {
        // A UNC path gets a trailing backslash; GetDiskFreeSpaceExW documents it for share roots.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths are Windows paths.");
        var calls = new FakeCalls(@"\\server\share");

        var space = new DriveSpaceProbe(calls).Get(@"\\server\share\clips\alice");

        Assert.Equal(DriveSpaceStatus.Ok, space.Status);
        Assert.Equal([@"\\server\share\"], calls.FreeSpaceCalls);
        Assert.Equal(@"\\server\share", space.ProbedPath);
    }

    [Fact]
    public void AMissingShare_IsUnavailable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths are Windows paths.");
        var calls = new FakeCalls();

        Assert.Equal(DriveSpaceStatus.Unavailable, new DriveSpaceProbe(calls).Get(@"\\no-such-host.invalid\share\clips").Status);
    }

    [Fact]
    public void AFolderWithASeparatorAlready_IsProbedAsItIs()
        => Assert.Equal(@"C:\", DriveSpaceProbe.WithTrailingSeparator(@"C:\"));

    [Theory]
    [InlineData("")]
    [InlineData("C:\\bad\0name")]
    public void SomethingThatIsntAPath_IsUnavailable(string folder)
    {
        var calls = new FakeCalls(@"C:\");

        Assert.Equal(DriveSpaceStatus.Unavailable, new DriveSpaceProbe(calls).Get(folder).Status);
        Assert.Empty(calls.FreeSpaceCalls);
    }

    // On this machine (integration: temporary folders on C:).

    [Fact]
    public void OnThisMachine_AFolder_HasItsVolumesFreeSpace()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GetDiskFreeSpaceExW.");
        var space = new DriveSpaceProbe().Get(_directory.Path);

        Assert.Equal(DriveSpaceStatus.Ok, space.Status);
        Assert.Equal(_directory.Path, space.ProbedPath);
        Assert.True(space.AvailableBytes > 0, "available");
        Assert.True(space.TotalBytes >= space.AvailableBytes, "total");
        Assert.NotNull(space.Volume);
        Assert.StartsWith(space.Volume, _directory.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"\", space.Volume, StringComparison.Ordinal);
    }

    [Fact]
    public void OnThisMachine_APerUserFolderNotCreatedYet_AndAMissingClipFolder_AreMeasuredByTheFolderAbove()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "GetDiskFreeSpaceExW.");
        var probe = new DriveSpaceProbe();

        var perUser = probe.Get(_directory.Combine("alice"));
        var missingRoot = probe.Get(_directory.Combine("no-such-clips", "sub", "alice"));

        Assert.Equal(DriveSpaceStatus.Ok, perUser.Status);
        Assert.Equal(_directory.Path, perUser.ProbedPath);
        Assert.Equal(DriveSpaceStatus.Ok, missingRoot.Status);
        Assert.Equal(_directory.Path, missingRoot.ProbedPath);
        Assert.False(Directory.Exists(_directory.Combine("alice")), "the probe creates nothing");
        Assert.False(Directory.Exists(_directory.Combine("no-such-clips")), "the probe creates nothing");
    }

    [Fact]
    public void OnThisMachine_ADriveLetterThatIsntThere_IsUnavailable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters.");
        var present = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var missing = "RQPONMLKJIHGWVUTS".FirstOrDefault(letter => !present.Contains(letter) && !Directory.Exists(letter + @":\"));
        Assert.SkipWhen(missing == default, "Every drive letter is in use.");

        var space = new DriveSpaceProbe().Get(missing + @":\clips\alice");

        Assert.Equal(DriveSpaceStatus.Unavailable, space.Status);
        Assert.Null(space.ProbedPath);
    }

    [Fact]
    public void OnThisMachine_AJunctionWhoseTargetIsGone_IsUnavailable_AndAWorkingOneIsMeasuredThroughIt()
    {
        // The same with a real junction (mklink /J needs no administrator rights): Directory.Exists is true for it, the
        // call fails with error 3, so the clip folder is Unavailable (a junction or mount point whose volume is offline),
        // never Unknown.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions.");
        var target = Directory.CreateDirectory(_directory.Combine("target")).FullName;
        var link = _directory.Combine("j-clips");
        Assert.SkipUnless(MakeJunction(link, target), "mklink /J failed here.");
        try
        {
            var through = new DriveSpaceProbe().Get(Path.Combine(link, "alice"));
            Assert.Equal(DriveSpaceStatus.Ok, through.Status);
            Assert.Equal(link, through.ProbedPath);

            Directory.Delete(target);
            Assert.True(Directory.Exists(link), "the dangling junction still reads as a folder");

            var dangling = new DriveSpaceProbe().Get(Path.Combine(link, "alice"));
            Assert.Equal(DriveSpaceStatus.Unavailable, dangling.Status);
            Assert.Equal(link, dangling.ProbedPath);
            Assert.True(DriveSpaceProbe.GoneErrors.Contains(dangling.Error!.Value), $"error {dangling.Error}");
        }
        finally
        {
            // Removes the link only, never its target.
            Directory.Delete(link);
        }
    }

    [Fact]
    public void OnThisMachine_AUncPathToThisMachine_IsMeasured()
    {
        // Measured on Windows: GetDiskFreeSpaceExW works through \\localhost\C$ (DriveInfo refuses UNC paths). Skipped when the
        // administrative share can't be read.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths.");
        var root = Path.GetPathRoot(_directory.Path)!;
        Assert.SkipUnless(root.Length == 3 && root[1] == ':', "The temporary folder isn't on a drive letter.");
        var unc = $@"\\localhost\{root[0]}$\{_directory.Path[3..]}";
        Assert.SkipUnless(Directory.Exists(unc), "The administrative share can't be read here.");

        var space = new DriveSpaceProbe().Get(Path.Combine(unc, "not-yet", "alice"));

        Assert.Equal(DriveSpaceStatus.Ok, space.Status);
        Assert.Equal(unc, space.ProbedPath);
        Assert.True(space.AvailableBytes > 0);
    }

    private static bool MakeJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            process.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Folders that exist, the free-space calls made, and one answer for all of them.
    private sealed class FakeCalls(params string[] existing) : IDriveSpaceCalls
    {
        public const long Available = 300L * 1024 * 1024 * 1024;
        public const long Total = 500L * 1024 * 1024 * 1024;

        private readonly HashSet<string> _existing = new(existing, StringComparer.OrdinalIgnoreCase);

        public int? Failure { get; init; }

        public string? Volume { get; init; }

        public List<string> FreeSpaceCalls { get; } = [];

        public List<string> ExistsCalls { get; } = [];

        public bool FolderExists(string path)
        {
            ExistsCalls.Add(path);
            return _existing.Contains(Path.TrimEndingDirectorySeparator(path)) || _existing.Contains(path);
        }

        public DriveSpaceCall GetFreeSpace(string folder)
        {
            FreeSpaceCalls.Add(folder);
            return Failure is int error ? DriveSpaceCall.Failed(error) : DriveSpaceCall.Worked(Available, Total);
        }

        public string? GetVolume(string folder) => Volume;
    }
}
