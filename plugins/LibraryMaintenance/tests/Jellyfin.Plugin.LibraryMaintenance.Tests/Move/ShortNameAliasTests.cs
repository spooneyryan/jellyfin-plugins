using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using MediaBrowser.Controller.Session;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// 8.3 short names (regression probes, inverted). A listing has long names only, so
/// <c>FOLDER~1</c>, another folder's short name, looked free: as a new folder or a wrap folder it named that folder (another
/// movie's own, or a junction). Now every name Move would make, move to or open is refused when Windows opens something by
/// it already. The short names are the volume's own (<c>GetShortPathNameW</c>); without 8.3 names the tests skip.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ShortNameAliasTests : IDisposable
{
    private readonly PlannerWorld _w = new();
    private readonly List<string> _probed = [];
    private readonly List<string> _accessChecked = [];

    public void Dispose() => _w.Dispose();

    [Fact]
    public void FindEntry_TellsAnAliasFromALongName()
    {
        var folder = _w.Folder("Cult", "Folder Movie (2008)");
        var alias = ShortName.Of(folder);

        Assert.Equal(new FolderEntry(alias, 0, true), DestinationResolver.FindEntry(_w.Cult, alias));
        var longName = DestinationResolver.FindEntry(_w.Cult, "FOLDER MOVIE (2008)");
        Assert.Equal(("Folder Movie (2008)", false, true), (longName?.Name, longName?.IsAlias, longName?.Attributes.HasFlag(FileAttributes.Directory)));
        Assert.Null(DestinationResolver.FindEntry(_w.Cult, "Folder Movie (2009)"));
    }

    [Fact]
    public void ANewFolderOrAPathSegment_NamedByAnotherFoldersShortName_IsRefused()
    {
        var folder = _w.Folder("Cult", "Folder Movie (2008)");
        var alias = ShortName.Of(folder);
        var resolver = new DestinationResolver(static _ => false);

        var made = Assert.Throws<PluginException>(() => resolver.Resolve(_w.CultLibrary, 0, null, alias));
        Assert.Equal((PluginErrorKind.Invalid, MoveErrorCodes.DestinationInvalid), (made.Kind, made.Code));
        Assert.Equal(Path.Join(_w.Cult, alias) + " exists already under another name (\"" + alias + "\" is another name, an 8.3 short name, of a folder or file there); choose another name.", made.Message);

        var opened = Assert.Throws<PluginException>(() => resolver.Resolve(_w.CultLibrary, 0, alias, null));
        Assert.Equal(MoveErrorCodes.DestinationInvalid, opened.Code);
        Assert.EndsWith("; choose the folder by its own name.", opened.Message, StringComparison.Ordinal);
        Assert.Equal(folder, resolver.Resolve(_w.CultLibrary, 0, "folder movie (2008)", null).Directory);
    }

    [Fact]
    public async Task AnotherMoviesFolder_ByItsShortName_IsNeitherANewFolderNorAWrapFolder()
    {
        // A new folder named by a short name: NewFolderName "RONIN(~1" planned with no blocker, into Ronin's own folder.
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        var ronin = _w.Movie(Path.Join(_w.Cult, "Ronin (1998)", "Ronin (1998).mkv"), "Ronin", 1998, false, "movie.nfo");
        var roninFolder = Path.GetDirectoryName(ronin.Path)!;
        var alias = ShortName.Of(roninFolder);

        var made = Assert.Throws<PluginException>(() => _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary, newFolder: alias, wrap: false));
        Assert.Equal(MoveErrorCodes.DestinationInvalid, made.Code);

        _w.Library.PathLookups.Clear();
        _w.Volumes.Asked.Clear();
        var wrapped = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary, wrap: true, wrapName: alias);
        await Preflight(wrapped);

        var blocker = Assert.Single(wrapped.Blockers);
        Assert.Equal((MoveErrorCodes.Collision, Path.Join(_w.Cult, alias)), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.EndsWith("; choose another folder name.", blocker.Message, StringComparison.Ordinal);
        Assert.Empty(wrapped.CreatedDirectories);
        Assert.Equal(_w.Cult, wrapped.TargetDirectory);
        AssertNothingReadAt(Path.Join(_w.Cult, alias), roninFolder);
    }

    [Fact]
    public async Task AJunctionsShortName_IsNeitherANewFolderNorAWrapFolder_AndNothingBehindItIsRead()
    {
        // A junction's short name: "HEAT(1~1" planned Rename with nothing in Links, and the access
        // check and the volume reached through it.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        var outside = _w.Folder("Outside", "w83");
        var link = Path.Join(_w.Cult, "Heat (1995)");
        Junction.Create(link, outside);
        var alias = ShortName.Of(link);

        Assert.Equal(MoveErrorCodes.DestinationInvalid, Assert.Throws<PluginException>(() => _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary, newFolder: alias, wrap: false)).Code);

        _w.Library.PathLookups.Clear();
        _w.Volumes.Asked.Clear();
        var wrapped = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary, wrap: true, wrapName: alias);
        await Preflight(wrapped);

        Assert.Equal([MoveErrorCodes.Collision], wrapped.Blockers.Select(b => b.Code));
        Assert.Empty(wrapped.CreatedDirectories);
        AssertNothingReadAt(Path.Join(_w.Cult, alias), outside);
        Assert.DoesNotContain(_w.Volumes.Asked, p => PathRules.IsSameOrUnder(p, link));
    }

    [Fact]
    public void AFolderUnit_WhoseNameIsAnEntrysShortNameAtTheTarget_IsACollision()
    {
        var taken = _w.Folder("Cult", "Heat (1995)");
        var alias = ShortName.Of(taken);
        var movie = _w.Movie(Path.Join(_w.Movies, alias, alias + ".mkv"), "Heat", 1995, false);

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.Collision, Path.Join(_w.Cult, alias)), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.StartsWith(Path.Join(_w.Cult, alias) + " exists already under another name", blocker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileSetMember_WhoseNameIsAFilesShortNameAtTheTarget_IsACollision()
    {
        var taken = _w.File(Path.Join(_w.Cult, "Heat.1995.Directors.Cut.mkv"));
        var alias = ShortName.Of(taken);
        var movie = _w.Movie(Path.Join(_w.Movies, alias), "Heat", 1995, true);

        var unit = _w.Plan(movie, _w.MoviesLibrary, _w.CultLibrary, wrap: false);

        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.Collision, Path.Join(_w.Cult, alias)), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.EndsWith("; Move never overwrites.", blocker.Message, StringComparison.Ordinal);
        Assert.Contains("exists already under another name", blocker.Message, StringComparison.Ordinal);
    }

    private async Task Preflight(MoveUnit unit)
    {
        var sessions = new Mock<ISessionManager>();
        sessions.SetupGet(s => s.Sessions).Returns([]);
        using var gate = new LibraryOperationGate(_w.Library.Object);
        var locks = new FileLockProbe(static (_, _) => Task.CompletedTask, p =>
        {
            _probed.Add(p);
            return false;
        });
        await new MovePreflight(sessions.Object, gate, locks, new RecordingAccess(_accessChecked)).CheckAsync(unit, DestinationWriteCheck.Access, "test", null, TestContext.Current.CancellationToken);
    }

    // Nothing by the alias, nor in what it names, was asked for its volume, looked up, lock-probed or access-checked.
    private void AssertNothingReadAt(string alias, string named)
    {
        bool At(string p) => PathRules.IsSameOrUnder(p, alias) || PathRules.IsSameOrUnder(p, named);
        Assert.DoesNotContain(_w.Volumes.Asked, At);
        Assert.DoesNotContain(_w.Library.PathLookups, At);
        Assert.DoesNotContain(_probed, At);
        Assert.DoesNotContain(_accessChecked, At);
    }

    private sealed class RecordingAccess(List<string> asked) : IFolderWriteAccess
    {
        public bool CanAdd(string directory, bool files, bool folders)
        {
            asked.Add(directory);
            return true;
        }
    }
}
