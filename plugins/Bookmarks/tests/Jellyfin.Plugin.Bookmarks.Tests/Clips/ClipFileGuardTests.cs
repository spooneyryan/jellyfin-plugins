using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Clips;

/// <summary>
/// The file-operation tests for <see cref="ClipFileGuard"/>: a path is a clip
/// file's only when it resolves strictly under a recorded clip folder, separator-aware and without regard to case on
/// Windows, with a clip file's name, and without a reparse point between the clip folder and the file. Everything else is
/// refused, and nothing the guard refuses is ever touched.
/// </summary>
public sealed class ClipFileGuardTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly FakeClipFileSystem _fileSystem = new();
    private readonly string _root;

    public ClipFileGuardTests()
    {
        _root = _directory.Combine("clips");
        Directory.CreateDirectory(Path.Combine(_root, "alice"));
    }

    [Fact]
    public void AClipFileInAClipFolder_Passes_WithItsFullPathAndFolder_WhetherOrNotItExists()
    {
        var file = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) - Bank job [00.01.00-00.01.30].mp4"));

        var exists = Check(file);
        var missing = Check(Path.Combine(_root, "alice", "Heat (1995) [00.01.00-00.01.30].mkv"));
        var missingFolder = Check(Path.Combine(_root, "bob", "Heat (1995) [00.01.00-00.01.30].mkv"));

        Assert.Equal(ClipPathVerdict.Exists, exists.Verdict);
        Assert.Equal(file, exists.FullPath);
        Assert.Equal(_root, exists.Root);
        Assert.True(exists.IsInClipFolders);
        Assert.False(exists.IsRefused);
        Assert.Equal(ClipPathVerdict.Missing, missing.Verdict);
        Assert.True(missing.IsInClipFolders);
        Assert.Equal(ClipPathVerdict.Missing, missingFolder.Verdict);
        Assert.Equal(_root, missingFolder.Root);
    }

    [Fact]
    public void AClipFileOutsideEveryClipFolder_IsRefused()
    {
        var elsewhere = _directory.CreateFile(Path.Combine("library", "Heat (1995).mkv"));

        var check = Check(elsewhere);

        Assert.Equal(ClipPathVerdict.OutsideClipFolders, check.Verdict);
        Assert.True(check.IsRefused);
        Assert.Null(check.Root);
        Assert.Equal(ClipPathVerdict.OutsideClipFolders, ClipFileGuard.Check(elsewhere, [], _fileSystem).Verdict);
    }

    [Fact]
    public void DotDotEscapes_AreResolvedFirst_AndRefused()
    {
        var outside = _directory.CreateFile(Path.Combine("library", "Heat (1995).mkv"));
        var escape = _root + Path.DirectorySeparatorChar + "alice" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "library" + Path.DirectorySeparatorChar + "Heat (1995).mkv";
        var staysInside = _root + Path.DirectorySeparatorChar + "bob" + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "alice" + Path.DirectorySeparatorChar + "x.mp4";

        var refused = Check(escape);
        var inside = Check(staysInside);

        Assert.Equal(ClipPathVerdict.OutsideClipFolders, refused.Verdict);
        Assert.Equal(outside, refused.FullPath);
        Assert.Equal(Path.Combine(_root, "alice", "x.mp4"), inside.FullPath);
        Assert.Equal(ClipPathVerdict.Missing, inside.Verdict);
    }

    [Fact]
    public void ASiblingFolderSharingThePrefix_IsNotInside()
    {
        // "clips2" starts with "clips", but is another folder (separator-aware prefix).
        var sibling = _directory.CreateFile(Path.Combine("clips2", "Heat (1995) [00.01.00-00.01.30].mp4"));

        Assert.Equal(ClipPathVerdict.OutsideClipFolders, Check(sibling).Verdict);
    }

    [Fact]
    public void TheClipFolderItself_IsNoClipFile()
    {
        // A clip folder named like a clip file passes the name check, but a folder is never a file of its own.
        var folder = _directory.Combine("odd.mkv");
        Directory.CreateDirectory(folder);

        Assert.Equal(ClipPathVerdict.OutsideClipFolders, ClipFileGuard.Check(folder, [folder], _fileSystem).Verdict);
        Assert.True(ClipFileGuard.Check(folder + Path.DirectorySeparatorChar, [folder], _fileSystem).IsRefused);
    }

    [Fact]
    public void AFolderWhereTheFileShouldBe_IsNotAFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "alice", "not a file.mp4"));

        Assert.Equal(ClipPathVerdict.NotAFile, Check(Path.Combine(_root, "alice", "not a file.mp4")).Verdict);
    }

    [Fact]
    public void CaseDoesNotMatter_OnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows compares paths without regard to case.");
        var file = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mp4"));

        var check = ClipFileGuard.Check(file.ToUpperInvariant(), [_root.ToLowerInvariant() + Path.DirectorySeparatorChar], _fileSystem);

        Assert.Equal(ClipPathVerdict.Exists, check.Verdict);
    }

    [Fact]
    public void TheLongestClipFolderWins()
    {
        var nested = Path.Combine(_root, "alice");
        var file = _directory.CreateFile(Path.Combine("clips", "alice", "x.mkv"));

        Assert.Equal(nested, ClipFileGuard.Check(file, [_root, nested], _fileSystem).Root);
        Assert.Equal(nested, ClipFileGuard.Check(file, [nested, _root], _fileSystem).Root);
    }

    [Fact]
    public void RecordedFoldersAreNormalised_AndOnesThatArentPathsAreSkipped()
    {
        var file = _directory.CreateFile(Path.Combine("clips", "alice", "x.mkv"));

        var check = ClipFileGuard.Check(file, ["   ", "C:\\bad\0path", _root + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar], _fileSystem);

        Assert.Equal(ClipPathVerdict.Exists, check.Verdict);
        Assert.Equal(_root, check.Root);
    }

    [Theory]
    [InlineData("x.txt")]
    [InlineData("bookmarks.json")]
    [InlineData(".mp4")]
    [InlineData("x.mp4.partial")]
    [InlineData("x.mkv.bak")]
    [InlineData("x.avi")]
    public void OnlyClipFileNames_Pass(string name)
    {
        // Bookmarks writes only .mp4 and .mkv clips (ClipFileNaming.GetExtension); a clip folder can hold other files (a
        // broad folder, a library), and the guard never reaches them.
        _directory.CreateFile(Path.Combine("clips", "alice", name));

        Assert.Equal(ClipPathVerdict.NotAClipFile, Check(Path.Combine(_root, "alice", name)).Verdict);
    }

    [Fact]
    public void APartialFile_PassesOnlyWhereAllowed()
    {
        var partial = _directory.CreateFile(Path.Combine("clips", "alice", "Heat (1995) [00.01.00-00.01.30].mp4.partial"));

        Assert.Equal(ClipPathVerdict.NotAClipFile, Check(partial).Verdict);
        Assert.Equal(ClipPathVerdict.Exists, ClipFileGuard.Check(partial, [_root], _fileSystem, allowPartial: true).Verdict);
        Assert.Equal(ClipPathVerdict.NotAClipFile, ClipFileGuard.Check(Path.Combine(_root, "alice", "notes.txt.partial"), [_root], _fileSystem, allowPartial: true).Verdict);
        Assert.True(ClipFileGuard.IsClipFileName("A.MKV", allowPartial: false));
        Assert.True(ClipFileGuard.IsClipFileName("A.mp4.PARTIAL", allowPartial: true));
        Assert.False(ClipFileGuard.IsClipFileName(null, allowPartial: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoPath_IsNotAPath(string? path)
        => Assert.Equal(ClipPathVerdict.NotAPath, Check(path).Verdict);

    [Fact]
    public void DevicePaths_AreRefused()
    {
        // \\?\ skips the normalisation that removes "..", so a device path could name a file outside while it looks inside.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows device paths.");
        var file = _directory.CreateFile(Path.Combine("clips", "alice", "x.mp4"));

        Assert.Equal(ClipPathVerdict.NotAPath, Check(@"\\?\" + file).Verdict);
        Assert.Equal(ClipPathVerdict.NotAPath, Check(@"\\.\" + file).Verdict);
        Assert.Equal(ClipPathVerdict.NotAPath, Check(@"//?/" + file).Verdict);
        Assert.Equal(ClipPathVerdict.NotAPath, ClipFileGuard.Check(@"\\?\" + file, [@"\\?\" + _root], _fileSystem).Verdict);
    }

    [Fact]
    public void AnAlternateDataStream_IsNotAClipFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "NTFS streams.");

        Assert.Equal(ClipPathVerdict.NotAClipFile, Check(Path.Combine(_root, "alice", "x.mp4:hidden")).Verdict);
    }

    [Fact]
    public void ARelativePath_ResolvesAgainstTheWorkingDirectory_AsTheFileOperationsWould()
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_root, "alice", "x.mp4"));
        Assert.SkipWhen(Path.IsPathRooted(relative), "The temporary folder is on another drive than the working directory.");

        var check = Check(relative);

        Assert.Equal(ClipPathVerdict.Missing, check.Verdict);
        Assert.Equal(Path.Combine(_root, "alice", "x.mp4"), check.FullPath);
    }

    [Fact]
    public void AReparsePointBetweenTheClipFolderAndTheFile_IsRefused_ButTheClipFolderMayBeOne()
    {
        var file = _directory.CreateFile(Path.Combine("clips", "alice", "x.mp4"));

        _fileSystem.ReparsePoints.Add(Path.Combine(_root, "alice"));
        Assert.Equal(ClipPathVerdict.ReparsePoint, Check(file).Verdict);
        Assert.True(Check(file).IsRefused);

        _fileSystem.ReparsePoints.Clear();
        _fileSystem.ReparsePoints.Add(file);
        Assert.Equal(ClipPathVerdict.ReparsePoint, Check(file).Verdict);

        // The clip folder itself (an admin's folder on another drive, say) is not checked.
        _fileSystem.ReparsePoints.Clear();
        _fileSystem.ReparsePoints.Add(_root);
        Assert.Equal(ClipPathVerdict.Exists, Check(file).Verdict);
    }

    [Fact]
    public void ARealJunctionInsideTheClipFolder_IsNotFollowed()
    {
        // A junction made with mklink /J (skipped when that fails). A clip path through it points at a file
        // outside the clip folders, and the guard refuses it because the junction is a reparse point.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows junctions.");
        var target = _directory.Combine("elsewhere");
        var outside = _directory.CreateFile(Path.Combine("elsewhere", "Heat (1995).mkv"));
        var junction = Path.Combine(_root, "linked");
        MakeJunction(junction, target);

        try
        {
            var through = Path.Combine(junction, "Heat (1995).mkv");
            Assert.True(File.Exists(through));

            var check = ClipFileGuard.Check(through, [_root], PhysicalClipFileSystem.Instance);

            Assert.Equal(ClipPathVerdict.ReparsePoint, check.Verdict);
            Assert.True(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [Fact]
    public void APathThatCantBeInspected_IsUnreadable_NotRefused()
    {
        // A drive that isn't ready, a folder the server may not read: nothing is touched, and the caller answers 503.
        var file = Path.Combine(_root, "alice", "x.mp4");
        var error = new IOException("The device is not ready.");
        _fileSystem.AttributesFailure = p => p.EndsWith("alice", StringComparison.OrdinalIgnoreCase) ? error : null;

        var check = Check(file);

        Assert.Equal(ClipPathVerdict.Unreadable, check.Verdict);
        Assert.Same(error, check.Error);
        Assert.False(check.IsRefused);
        Assert.False(check.IsInClipFolders);
        Assert.Equal(_root, check.Root);
    }

    [Fact]
    public void AFileWhereAFolderShouldBe_MeansTheFileIsMissing()
    {
        _directory.CreateFile(Path.Combine("clips", "carol"));

        Assert.Equal(ClipPathVerdict.Missing, Check(Path.Combine(_root, "carol", "x.mp4")).Verdict);
    }

    [Fact]
    public void AClipFolderThatIsntThere_IsFolderUnavailable_NeverMissing()
    {
        // The file's root folder does not exist (drive offline). On the real file system, the clip folder gone
        // after its clip was written (its drive unplugged, the folder moved or removed): every path under it would read as
        // missing, and a missing file counts as deleted.
        var usb = _directory.Combine("usb");
        var file = _directory.CreateFile(Path.Combine("usb", "alice", "Heat (1995) [00.01.00-00.01.30].mp4"));
        ClipFileSupport.Unplug(usb);

        var check = ClipFileGuard.Check(file, [_root, usb], PhysicalClipFileSystem.Instance);

        Assert.Equal(ClipPathVerdict.FolderUnavailable, check.Verdict);
        Assert.Equal(file, check.FullPath);
        Assert.Equal(usb, check.Root);
        Assert.True(check.IsUnreachable);
        Assert.False(check.IsRefused);
        Assert.False(check.IsInClipFolders);

        // A file where the clip folder should be is no clip folder either.
        File.WriteAllText(usb, "not a folder");
        Assert.Equal(ClipPathVerdict.FolderUnavailable, ClipFileGuard.Check(file, [usb], PhysicalClipFileSystem.Instance).Verdict);
        File.Delete(usb);

        // Plugged in again: the file is there, and a folder missing inside a clip folder that is there means a missing file.
        ClipFileSupport.PlugIn(usb);
        Assert.Equal(ClipPathVerdict.Exists, ClipFileGuard.Check(file, [usb], PhysicalClipFileSystem.Instance).Verdict);
        Assert.Equal(ClipPathVerdict.Missing, ClipFileGuard.Check(Path.Combine(usb, "bob", "x.mp4"), [usb], PhysicalClipFileSystem.Instance).Verdict);
    }

    [Fact]
    public void AClipFolderOnADriveLetterThatIsGone_IsFolderUnavailable()
    {
        // An unplugged USB drive takes its letter with it; .NET reads its paths as it reads a folder that doesn't exist.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive letters.");
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        var letter = "QRSTUVWXY".FirstOrDefault(c => !used.Contains(c));
        Assert.SkipWhen(letter == default, "No free drive letter.");

        foreach (var root in new[] { $@"{letter}:\Clips", $@"{letter}:\" })
        {
            var check = ClipFileGuard.Check(Path.Combine(root, "alice", "Heat (1995) [00.01.00-00.01.30].mp4"), [root], PhysicalClipFileSystem.Instance);

            Assert.Equal(ClipPathVerdict.FolderUnavailable, check.Verdict);
            Assert.Equal(root, check.Root);
        }
    }

    [Fact]
    public void AClipFolderThatIsALinkToAFolderThatIsGone_IsFolderUnavailable()
    {
        // The clip folder may be a link (an admin's folder on another drive): with that drive unplugged the link stays, and
        // its target doesn't. A real junction made with mklink /J (skipped when that fails).
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows junctions.");
        var drive = _directory.Combine("drive");
        _directory.CreateFile(Path.Combine("drive", "alice", "x.mp4"));
        var linked = _directory.Combine("linked-clips");
        MakeJunction(linked, drive);

        try
        {
            var through = Path.Combine(linked, "alice", "x.mp4");
            Assert.Equal(ClipPathVerdict.Exists, ClipFileGuard.Check(through, [linked], PhysicalClipFileSystem.Instance).Verdict);

            ClipFileSupport.Unplug(drive);
            Assert.Equal(ClipPathVerdict.FolderUnavailable, ClipFileGuard.Check(through, [linked], PhysicalClipFileSystem.Instance).Verdict);

            ClipFileSupport.PlugIn(drive);
            Assert.Equal(ClipPathVerdict.Exists, ClipFileGuard.Check(through, [linked], PhysicalClipFileSystem.Instance).Verdict);
        }
        finally
        {
            Directory.Delete(linked);
        }
    }

    [Fact]
    public void AClipFolderThatCantBeAskedAbout_IsUnreadable()
    {
        var error = new IOException("The device is not ready.");
        _fileSystem.FolderFailure = _ => error;

        var check = Check(Path.Combine(_root, "alice", "x.mp4"));

        Assert.Equal(ClipPathVerdict.Unreadable, check.Verdict);
        Assert.Same(error, check.Error);
        Assert.True(check.IsUnreachable);
    }

    public void Dispose() => _directory.Dispose();

    // A junction made with mklink /J; the test is skipped when that fails.
    private static void MakeJunction(string junction, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        mklink.WaitForExit();
        Assert.SkipUnless(mklink.ExitCode == 0 && Directory.Exists(junction), "mklink /J failed.");
    }

    private ClipPathCheck Check(string? path) => ClipFileGuard.Check(path, [_root], _fileSystem);
}
