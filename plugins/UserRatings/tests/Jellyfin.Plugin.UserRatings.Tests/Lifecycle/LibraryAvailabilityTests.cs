using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Lifecycle;

/// <summary>
/// The lifecycle's data-safety rule on its own (<see cref="LibraryAvailability"/>): a rating whose item is gone may detach
/// only when its library folder is there, readable and not empty, and its file isn't on disk; with no library folder known, or a
/// path outside every library folder while one is unavailable, it stays attached.
/// </summary>
public sealed class LibraryAvailabilityTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly Mock<ILibraryManager> _library = new();
    private readonly ListLogger _logger = new();
    private readonly List<string> _locations = [];

    public LibraryAvailabilityTests()
    {
        _library.Setup(l => l.GetVirtualFolders()).Returns(() => _locations.Select(l => new VirtualFolderInfo { Name = Path.GetFileName(l), Locations = [l] }).ToList());
    }

    public void Dispose() => _directory.Dispose();

    [Fact]
    public void InAnAvailableFolder_AMissingPathMayDetach_AndOneStillOnDiskStaysAttached()
    {
        var movies = Folder("Movies");
        var gone = Path.Combine(movies, "Heat (1995)", "Heat (1995).mkv");
        var there = Path.Combine(movies, "Ronin (1998)", "Ronin (1998).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(there)!);
        File.WriteAllText(there, "synthetic");

        var check = Read();

        Assert.Null(check.HoldReason(gone));
        Assert.Equal(LibraryAvailability.StillOnDisk, check.HoldReason(there));
        Assert.Equal(LibraryAvailability.StillOnDisk, check.HoldReason(Path.GetDirectoryName(there)));
        Assert.False(check.AnyUnavailable);
    }

    [Fact]
    public void AMissingFolder_AnEmptyMountPoint_KeepEveryRatingInThemAttached()
    {
        var unmounted = _directory.Combine("Z", "Movies");
        _locations.Add(unmounted);
        var empty = _directory.Combine("Mount", "Shows");
        Directory.CreateDirectory(empty);
        _locations.Add(empty);

        var check = Read();

        Assert.Equal(LibraryAvailability.FolderUnavailable, check.HoldReason(Path.Combine(unmounted, "Heat (1995)", "Heat (1995).mkv")));
        Assert.Equal(LibraryAvailability.FolderUnavailable, check.HoldReason(Path.Combine(empty, "Test Series")));
        Assert.True(check.AnyUnavailable);
    }

    [Fact]
    public void APathInNoLibraryFolder_OrNoPath_StaysAttachedWhileAnyFolderIsUnavailable()
    {
        var movies = Folder("Movies");
        var outside = _directory.Combine("elsewhere", "Heat.mkv");

        Assert.Null(Read().HoldReason(outside));
        Assert.Null(Read().HoldReason(null));

        _locations.Add(_directory.Combine("Z", "Music"));
        var check = Read();
        Assert.Equal(LibraryAvailability.FolderUnavailable, check.HoldReason(outside));
        Assert.Equal(LibraryAvailability.FolderUnavailable, check.HoldReason(null));
        Assert.Null(check.HoldReason(Path.Combine(movies, "Gone", "Gone.mkv")));
    }

    [Fact]
    public void NoLibraryFolderKnown_OrTheirListUnreadable_KeepsEveryRatingAttached()
    {
        Assert.Equal(LibraryAvailability.NoLibraryFolders, Read().HoldReason(_directory.Combine("Movies", "Heat.mkv")));

        _library.Setup(l => l.GetVirtualFolders()).Throws(new IOException("synthetic"));
        Assert.Equal(LibraryAvailability.NoLibraryFolders, Read().HoldReason(null));
        Assert.Contains(_logger.At(LogLevel.Warning), e => e.Message.Contains("ratings whose items are gone stay attached", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDeepestFolder_Decides_AndCaseDoesNotMatterOnWindows()
    {
        var movies = Folder("Movies");
        var kids = _directory.Combine("Movies", "Kids");
        Directory.CreateDirectory(kids);
        _locations.Add(kids);

        // Movies has an entry (Kids), so it is available; Kids is empty: its items stay attached, Movies' may detach.
        var check = Read();
        Assert.Equal(LibraryAvailability.FolderUnavailable, check.HoldReason(Path.Combine(kids, "Up", "Up.mkv")));
        Assert.Null(check.HoldReason(Path.Combine(movies, "Heat", "Heat.mkv")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(LibraryAvailability.FolderUnavailable, check.HoldReason(Path.Combine(kids.ToUpperInvariant(), "Up", "Up.mkv")));
        }

        Assert.Equal(new[] { kids, movies }.Select(Path.GetFullPath), check.Folders);
    }

    [Fact]
    public void IsUnder_NeedsASeparatorAfterTheFolder()
    {
        Assert.True(LibraryAvailability.IsUnder(@"C:\media\Movies\Heat.mkv", @"C:\media\Movies"));
        Assert.False(LibraryAvailability.IsUnder(@"C:\media\Movies 2\Heat.mkv", @"C:\media\Movies"));
        Assert.False(LibraryAvailability.IsUnder(@"C:\media\Movies", @"C:\media\Movies"));
        Assert.True(LibraryAvailability.IsUnder(@"Q:\Heat.mkv", @"Q:\"));
    }

    // ---------------------------------------------------------------------------------------------- minor findings, fixed

    [Fact]
    public void APathBehindADanglingJunction_IsHeld_AsFolderUnavailable()
    {
        // A junction (or share) inside the library folder whose target is offline. A real junction made with mklink /J
        // (skipped when that fails): the item may well be behind it, so it isn't "gone".
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows junctions.");
        var movies = Folder("Movies");
        var target = _directory.Combine("other drive", "Films");
        Directory.CreateDirectory(Path.Combine(target, "Film (2020)"));
        File.WriteAllText(Path.Combine(target, "Film (2020)", "Film (2020).mkv"), "synthetic");
        var junction = Path.Combine(movies, "Films");
        MakeJunction(junction, target);
        var file = Path.Combine(junction, "Film (2020)", "Film (2020).mkv");
        Assert.Equal(LibraryAvailability.StillOnDisk, Read().HoldReason(file));

        Directory.Delete(target, recursive: true);
        Assert.Equal(LibraryAvailability.FolderUnavailable, Read().HoldReason(file));

        // The film's own folder is the junction.
        var target2 = _directory.Combine("other drive", "Other (2021)");
        Directory.CreateDirectory(target2);
        File.WriteAllText(Path.Combine(target2, "Other (2021).mkv"), "synthetic");
        var own = Path.Combine(movies, "Other (2021)");
        MakeJunction(own, target2);
        Directory.Delete(target2, recursive: true);
        Assert.Equal(LibraryAvailability.FolderUnavailable, Read().HoldReason(Path.Combine(own, "Other (2021).mkv")));

        // A folder deleted in the ordinary way still lets it detach: the first folder that is there decides, and it reads.
        Assert.Null(Read().HoldReason(Path.Combine(movies, "Gone (2019)", "Gone (2019).mkv")));
    }

    [Fact]
    public void APathBehindAFolderThatCantBeListed_IsHeld_AsFolderUnavailable()
    {
        // An ACL that denies the folder between the library folder and the item (icacls, removed afterwards). The file is
        // there, but nothing can tell: held, never detached.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACLs.");
        var movies = Folder("Movies");
        var locked = Path.Combine(movies, "Locked");
        Directory.CreateDirectory(Path.Combine(locked, "Film (2020)"));
        var file = Path.Combine(locked, "Film (2020)", "Film (2020).mkv");
        File.WriteAllText(file, "synthetic");
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        Icacls($"\"{locked}\" /deny \"{user}:(OI)(CI)(F)\"");
        try
        {
            Assert.Equal(LibraryAvailability.FolderUnavailable, Read().HoldReason(file));
            Assert.Equal(LibraryAvailability.FolderUnavailable, Read().HoldReason(Path.Combine(locked, "Gone (2019)", "Gone (2019).mkv")));
        }
        finally
        {
            Icacls($"\"{locked}\" /remove:d \"{user}\" /T /C");
        }

        Assert.Equal(LibraryAvailability.StillOnDisk, Read().HoldReason(file));
    }

    [Fact]
    public void ALibraryRootWithOnlyHiddenSystemFolders_IsUnavailable_AndOneEntryMoreMakesItAvailable()
    {
        // A drive letter given to another, empty volume: only $RECYCLE.BIN and System Volume Information at its root.
        var root = _directory.Combine("Volume");
        Directory.CreateDirectory(root);
        _locations.Add(root);
        foreach (var name in new[] { "System Volume Information", "$RECYCLE.BIN" })
        {
            var system = Directory.CreateDirectory(Path.Combine(root, name));
            system.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        }

        var gone = Path.Combine(root, "Film (2020)", "Film (2020).mkv");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(LibraryAvailability.FolderUnavailable, Read().HoldReason(gone));
            Assert.True(Read().AnyUnavailable);
        }

        // Hidden alone, or System alone, is an entry like any other.
        var hidden = Directory.CreateDirectory(Path.Combine(root, ".hidden"));
        hidden.Attributes |= FileAttributes.Hidden;
        Assert.Null(Read().HoldReason(gone));
        Assert.False(Read().AnyUnavailable);
    }

    [Fact]
    public void FolderOf_IsTheDeepestLibraryFolderOfAPath_OrNull()
    {
        var movies = Folder("Movies");
        var kids = _directory.Combine("Movies", "Kids");
        Directory.CreateDirectory(kids);
        _locations.Add(kids);
        var check = Read();

        Assert.Equal(Path.GetFullPath(kids), check.FolderOf(Path.Combine(kids, "Up", "Up.mkv")));
        Assert.Equal(Path.GetFullPath(movies), check.FolderOf(Path.Combine(movies, "Heat", "Heat.mkv")));
        Assert.Null(check.FolderOf(_directory.Combine("elsewhere", "Heat.mkv")));
        Assert.Null(check.FolderOf(null));
        Assert.Null(check.FolderOf("  "));
    }

    // A junction made with mklink /J; the test is skipped when that fails.
    private static void MakeJunction(string junction, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        mklink.WaitForExit();
        Assert.SkipUnless(mklink.ExitCode == 0 && Directory.Exists(junction), "mklink /J failed.");
    }

    private static void Icacls(string arguments)
    {
        using var icacls = Process.Start(new ProcessStartInfo("icacls.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        icacls.WaitForExit();
        Assert.SkipUnless(icacls.ExitCode == 0, "icacls failed.");
    }

    private string Folder(string name)
    {
        var path = _directory.Combine(name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "placeholder.txt"), "synthetic");
        _locations.Add(path);
        return path;
    }

    private LibraryAvailability Read() => LibraryAvailability.Read(_library.Object, _logger);
}
