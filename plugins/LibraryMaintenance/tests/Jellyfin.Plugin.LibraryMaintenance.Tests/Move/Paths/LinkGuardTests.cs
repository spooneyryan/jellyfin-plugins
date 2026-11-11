using System;
using System.IO;
using System.Security.AccessControl;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Preflight;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;

/// <summary>
/// <see cref="LinkGuard"/>: the location and each folder below it, top first, the first link
/// ending the walk; a location that is a link counts; a missing folder ends it with none; one that can't be read counts.
/// Real junctions and symbolic links to folders in the test's own temp directory.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LinkGuardTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void NoLink_FromTheLocationDownToTheFolder_IsNull()
    {
        var location = Folder("Movies");
        var folder = Folder("Movies", "Action", "Heat (1995)");

        Assert.Null(LinkGuard.Find(location, folder));
        Assert.Null(LinkGuard.Find(location, location));
        Assert.Null(LinkGuard.Find(location + "\\", folder.Replace('\\', '/')));
    }

    [Fact]
    public void AFolderBetween_IsFound_TheTopMostOne_SpelledAsAsked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var location = Folder("Movies");
        var outside = Folder("Outside");
        Folder("Elsewhere");
        var linked = Path.Join(location, "Linked");
        Junction.Create(linked, outside);
        Junction.Create(Path.Join(outside, "Inner"), Path.Join(_temp.Path, "Elsewhere"));

        var found = LinkGuard.Find(location, Path.Join(location, "linked", "Inner", "Alien (1979)"));

        Assert.Equal(new FoundLink(Path.Join(location, "linked"), false, false), found);
        Assert.Equal(Path.Join(location, "Linked"), LinkGuard.Find(location, linked)?.Path);
    }

    [Fact]
    public void ALocationThatIsALink_Counts_AsTheLocation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var location = Path.Join(_temp.Path, "LinkedLibrary");
        Junction.Create(location, Folder("Target"));

        var found = LinkGuard.Find(location, Path.Join(location, "Alien (1979)"));

        Assert.Equal(new FoundLink(location, true, false), found);
        Assert.Contains("The library folder " + location, found!.Message, StringComparison.Ordinal);
        Assert.Equal(found, LinkGuard.Find(location, location));
    }

    [Fact]
    public void ADanglingJunction_IsStillALink()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var location = Folder("Movies");
        var target = Folder("Gone");
        var link = Path.Join(location, "Dangling");
        Junction.Create(link, target);
        Directory.Delete(target);

        Assert.Equal(link, LinkGuard.Find(location, Path.Join(link, "Heat (1995)"))?.Path);
    }

    [Fact]
    public void AFolderSymlink_IsALink()
    {
        var location = Folder("Movies");
        var link = Path.Join(location, "Sym");
        SymbolicLink.ToFolder(link, Folder("Outside"));

        Assert.Equal(link, LinkGuard.Find(location, Path.Join(link, "Sub"))?.Path);
    }

    [Fact]
    public void AFolderThatIsntThere_EndsTheWalk_WithNoLink()
    {
        var location = Folder("Movies");

        Assert.Null(LinkGuard.Find(location, Path.Join(location, "Gone", "Heat (1995)")));
        Assert.Null(LinkGuard.Find(Path.Join(_temp.Path, "NoLibrary"), Path.Join(_temp.Path, "NoLibrary", "x")));
    }

    [Fact]
    public void AFolderOutsideTheLocation_IsWalkedFromItsDriveRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var link = Path.Join(_temp.Path, "Linked");
        Junction.Create(link, Folder("Outside"));
        var folder = Path.Join(link, "Heat (1995)");

        Assert.Equal(link, LinkGuard.Find(Folder("Movies"), folder)?.Path);
        Assert.Equal(link, LinkGuard.Find(string.Empty, folder)?.Path);
    }

    [Fact]
    public void AFolderWhoseAttributesCantBeRead_CountsAsALink_AndNothingBelowIsRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "ACLs are tested on Windows.");
        var location = Folder("Movies");
        var hidden = Folder("Movies", "Hidden", "Inner");
        var parent = Path.GetDirectoryName(hidden)!;
        using (new DenyAccess(location, FileSystemRights.ListDirectory))
        using (new DenyAccess(parent, FileSystemRights.ReadAttributes))
        {
            try
            {
                File.GetAttributes(parent);
                Assert.Skip("This account reads the folder's attributes through the deny (a privilege).");
            }
            catch (UnauthorizedAccessException)
            {
            }

            Assert.Equal(new FoundLink(parent, false, true), LinkGuard.Find(location, hidden));
            Assert.StartsWith("Move can't read " + parent, LinkGuard.Find(location, hidden)!.Message, StringComparison.Ordinal);
        }
    }

    private string Folder(params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }
}
