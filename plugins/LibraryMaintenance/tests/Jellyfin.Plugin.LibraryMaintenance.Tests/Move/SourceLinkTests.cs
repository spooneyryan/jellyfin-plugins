using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move;

/// <summary>
/// Links on the source side (regression probes, inverted): a link anywhere from the
/// source location down to the unit, the location itself included, blocks the plan or refuses the route, and nothing at or
/// below it is listed, looked up, lock-probed, access-checked or asked for its volume. A destination location that is a
/// link is refused too. Real junctions and symbolic links to folders in the test's temp directory.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SourceLinkTests : IDisposable
{
    private readonly PlannerWorld _w = new();
    private readonly List<string> _probed = [];
    private readonly List<string> _accessChecked = [];

    public void Dispose() => _w.Dispose();

    [Fact]
    public async Task AFileSet_WhoseFolderIsAJunction_IsBlocked_AndNothingInItIsRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var outside = _w.Folder("Outside", "Mixed");
        var mixed = Path.Join(_w.Movies, "Mixed");
        Junction.Create(mixed, outside);
        var heat = _w.Movie(Path.Join(mixed, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt", "Heat.1995.nfo");
        _w.Movie(Path.Join(mixed, "Ronin.1998.mkv"), "Ronin", 1998, true);

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);
        await Preflight(unit);

        AssertBlockedAt(unit, mixed);
        Assert.Contains("is a junction or symbolic link", unit.Blockers[0].Message, StringComparison.Ordinal);
        Assert.Equal(PlannerWorld.IdOf(Path.Join(_w.Cult, "Heat.1995.mkv"), heat.GetType()), Assert.Single(unit.Items).PredictedId);
        AssertNothingReadBelow(mixed, outside);
    }

    [Fact]
    public async Task AFolderUnit_BelowAJunction_IsBlocked_AndNothingBelowTheJunctionIsRead()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var outside = _w.Folder("Outside", "Linked");
        var linked = Path.Join(_w.Movies, "Linked");
        Junction.Create(linked, outside);
        var alien = _w.Movie(Path.Join(linked, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false, "movie.nfo");
        _w.Movie(Path.Join(linked, "Alien (1979)", "Ronin (1998).mkv"), "Ronin", 1998, false);

        var unit = _w.Plan(alien, _w.MoviesLibrary, _w.CultLibrary);
        await Preflight(unit);

        AssertBlockedAt(unit, linked);
        AssertNothingReadBelow(linked, outside);
    }

    [Fact]
    public async Task AFileSet_BelowAFolderSymlink_IsBlocked()
    {
        var outside = _w.Folder("Outside", "Sym");
        var linked = Path.Join(_w.Movies, "Sym");
        SymbolicLink.ToFolder(linked, outside);
        var heat = _w.Movie(Path.Join(linked, "Sub", "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");

        var unit = _w.Plan(heat, _w.MoviesLibrary, _w.CultLibrary);
        await Preflight(unit);

        AssertBlockedAt(unit, linked);
        AssertNothingReadBelow(linked, outside);
    }

    [Fact]
    public async Task ASourceLocationThatIsAJunction_BlocksThePlan_NamingTheLocation()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var outside = _w.Folder("Outside", "LinkedLib");
        var location = Path.Join(_w.Root, "LinkedLib");
        Junction.Create(location, outside);
        var library = new CatalogLibrary(new Guid("c0000000-0000-0000-0000-0000000000d1"), "LinkedLib", CollectionTypeOptions.movies, [location], null);
        var heat = _w.Movie(Path.Join(location, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        var alien = _w.Movie(Path.Join(location, "Alien (1979)", "Alien (1979).mkv"), "Alien", 1979, false);

        foreach (var item in new[] { heat, alien })
        {
            var unit = _w.Plan(item, library, _w.CultLibrary);
            await Preflight(unit);

            AssertBlockedAt(unit, location);
            Assert.StartsWith("The library folder " + location + " is a junction or symbolic link", unit.Blockers[0].Message, StringComparison.Ordinal);
        }

        AssertNothingReadBelow(location, outside);
    }

    [Fact]
    public async Task AnItemInALibraryFolderThatIsAJunction_IsNotEligible_AndEveryRouteRefusesIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var outside = _w.Folder("Outside", "PrivateTarget");
        Directory.Delete(_w.Private);
        Junction.Create(_w.Private, outside);
        var heat = _w.Movie(Path.Join(_w.Private, "Heat.1995.mkv"), "Heat", 1995, true);
        using var s = new PlanServices(_w);

        var result = s.Eligibility.Check(heat.Id, null);

        Assert.Equal((MoveErrorCodes.ReparsePoint, false), (result.Code, result.Eligible));
        Assert.Equal("The library folder " + _w.Private + " is a junction or symbolic link; Move doesn't follow links, so it doesn't move media in or out of it.", result.Reason);
        await AssertRefusedEverywhere(s, heat.Id);
        AssertNothingReadBelow(_w.Private, outside);
    }

    [Fact]
    public async Task AnItemWhoseFolderIsAJunction_IsNotEligible_AndSourceDestinationsFoldersAndPlanRefuseIt()
    {
        // Destinations once asked the volume of a movie inside a linked folder unit.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var outside = _w.Folder("Outside", "a");
        var root = Path.Join(_w.Movies, "Alien (1979)");
        Junction.Create(root, outside);
        var movie = _w.Movie(Path.Join(root, "Alien (1979).mkv"), "Alien", 1979, false);
        using var s = new PlanServices(_w);
        _w.Volumes.Asked.Clear();

        var result = s.Eligibility.Check(movie.Id, null);

        Assert.Equal(MoveErrorCodes.ReparsePoint, result.Code);
        Assert.Equal(root + " is a junction or symbolic link; Move doesn't follow links, so it doesn't move what is below it.", result.Reason);
        await AssertRefusedEverywhere(s, movie.Id);
        AssertNothingReadBelow(root, outside);
    }

    [Fact]
    public void Destinations_WalkAgain_BeforeAskingTheSourcesVolume()
    {
        // As if the link appeared after eligibility: MoveDestinations refuses on its own and asks nothing below it.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var heat = _w.Movie(Path.Join(_w.Movies, "Linked", "Heat.1995.mkv"), "Heat", 1995, true);
        using var s = new PlanServices(_w);
        var eligible = s.Eligibility.Check(heat.Id, null);
        Assert.True(eligible.Eligible);
        var outside = _w.Folder("Outside", "late");
        Directory.Move(Path.Join(_w.Movies, "Linked"), Path.Join(outside, "moved"));
        Junction.Create(Path.Join(_w.Movies, "Linked"), Path.Join(outside, "moved"));
        _w.Volumes.Asked.Clear();

        var e = Assert.Throws<PluginException>(() => s.Destinations.GetDestinations(eligible, null));

        Assert.Equal((PluginErrorKind.Conflict, MoveErrorCodes.ReparsePoint), (e.Kind, e.Code));
        Assert.DoesNotContain(_w.Volumes.Asked, p => PathRules.IsSameOrUnder(p, Path.Join(_w.Movies, "Linked")));
        Assert.Empty(_w.Volumes.Asked);
    }

    [Fact]
    public void Source_RefusesALinkThePlannerFinds_AfterEligibilityFoundNone()
    {
        // A junction made between Eligibility's walk and the planner's (here when the planner looks up the item's extra,
        // which it does before it walks): Source answers 409 from the planner's own walk, with nothing below it listed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var linked = Path.Join(_w.Movies, "Linked");
        var heat = _w.Movie(Path.Join(linked, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        var outside = _w.Folder("Outside", "late");
        var extraId = new Guid("e0000000-0000-0000-0000-0000000000e1");
        heat.ExtraIds = [extraId];
        var made = false;
        _w.Library.Mock.Setup(l => l.GetItemById(extraId)).Returns(() =>
        {
            if (!made)
            {
                made = true;
                Directory.Move(linked, Path.Join(outside, "moved"));
                Junction.Create(linked, Path.Join(outside, "moved"));
            }

            return null!;
        });
        using var s = new PlanServices(_w);
        Assert.True(s.Eligibility.Check(heat.Id, null).Eligible);
        Assert.False(made);
        _w.Library.PathLookups.Clear();
        _w.Volumes.Asked.Clear();

        var e = Assert.Throws<PluginException>(() => s.Plans.GetSource(heat.Id, null));

        Assert.True(made);
        Assert.Equal((PluginErrorKind.Conflict, MoveErrorCodes.ReparsePoint), (e.Kind, e.Code));
        Assert.StartsWith(linked + " is a junction or symbolic link", e.Message, StringComparison.Ordinal);
        AssertNothingReadBelow(linked, outside);
    }

    [Fact]
    public void Destinations_AskTheVolumeOfTheUnitsFolder_NotOfTheFile()
    {
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true);
        using var s = new PlanServices(_w);
        _w.Volumes.Asked.Clear();

        s.Plans.GetDestinations(heat.Id, null);

        Assert.Contains(_w.Movies, _w.Volumes.Asked);
        Assert.DoesNotContain(heat.Path, _w.Volumes.Asked);
    }

    [Fact]
    public async Task ADestinationLocationThatIsAJunction_IsRefused_AndListedButNeverOpened()
    {
        // A destination location that is a link was once followed.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Junctions are a Windows feature.");
        var outside = _w.Folder("Outside", "PrivateTarget");
        _w.Folder("Outside", "PrivateTarget", "Crime");
        _w.File(Path.Join(outside, ".ignore"));
        Directory.Delete(_w.Private);
        Junction.Create(_w.Private, outside);
        var heat = _w.Movie(Path.Join(_w.Movies, "Heat.1995.mkv"), "Heat", 1995, true, "Heat.1995.srt");
        using var s = new PlanServices(_w);
        _w.Volumes.Asked.Clear();

        var resolve = Assert.Throws<PluginException>(() => s.Resolver.Resolve(_w.PrivateLibrary, 0, "Crime", null));
        Assert.Equal((PluginErrorKind.Invalid, MoveErrorCodes.ReparsePoint), (resolve.Kind, resolve.Code));
        Assert.StartsWith("The library folder " + _w.Private + " is a junction", resolve.Message, StringComparison.Ordinal);

        var plan = await Assert.ThrowsAsync<PluginException>(() => s.Plans.PlanAsync(Request(heat.Id, _w.PrivateLibrary.Id, "Crime"), null, TestContext.Current.CancellationToken));
        Assert.Equal(MoveErrorCodes.ReparsePoint, plan.Code);
        Assert.Equal(MoveErrorCodes.ReparsePoint, Assert.Throws<PluginException>(() => s.Destinations.GetFolders(_w.PrivateLibrary.Id, 0, null, null, 0, 200, null, null)).Code);

        var row = s.Plans.GetDestinations(heat.Id, null).Single(l => l.LibraryId == _w.PrivateLibrary.Id).Locations.Single();
        Assert.Equal((false, false, "reparse-point", null, null), (row.Reachable, row.Selectable, row.Reason, row.FreeBytes, row.Label));
        Assert.DoesNotContain(_w.Volumes.Asked, p => PathRules.IsSameOrUnder(p, _w.Private) || PathRules.IsSameOrUnder(p, outside));
        Assert.DoesNotContain(_w.Library.PathLookups, p => PathRules.IsSameOrUnder(p, _w.Private) || PathRules.IsSameOrUnder(p, outside));
    }

    private static LibraryMaintenanceMovePlanRequestDto Request(Guid itemId, Guid libraryId, string? relativePath)
        => new() { ItemIds = [itemId], Destination = new LibraryMaintenanceMovePlanDestinationDto { LibraryId = libraryId, LocationIndex = 0, RelativePath = relativePath } };

    private static void AssertBlockedAt(MoveUnit unit, string link)
    {
        var blocker = Assert.Single(unit.Blockers);
        Assert.Equal((MoveErrorCodes.ReparsePoint, link), (blocker.Code, Assert.Single(blocker.Paths!)));
        Assert.Equal([link], unit.Links);
        Assert.Empty(unit.Entries);
        Assert.True(unit.IsLinked(unit.SourceRoot));
        Assert.Null(unit.SourceVolumeId);
    }

    private static async Task AssertRefusedEverywhere(PlanServices s, Guid itemId)
    {
        void Refused(Action route)
        {
            var e = Assert.Throws<PluginException>(route);
            Assert.Equal((PluginErrorKind.Conflict, MoveErrorCodes.ReparsePoint), (e.Kind, e.Code));
        }

        Refused(() => s.Plans.GetSource(itemId, null));
        Refused(() => s.Plans.GetDestinations(itemId, null));
        Refused(() => s.Plans.RequireEligible(itemId, null));
        var plan = await Assert.ThrowsAsync<PluginException>(() => s.Plans.PlanAsync(Request(itemId, s.World.CultLibrary.Id, null), null, TestContext.Current.CancellationToken));
        Assert.Equal((PluginErrorKind.Conflict, MoveErrorCodes.ReparsePoint), (plan.Kind, plan.Code));
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

    // Nothing at or below the link, nor in its target, was asked for its volume, looked up, lock-probed or access-checked.
    private void AssertNothingReadBelow(string link, string target)
    {
        bool Below(string p) => PathRules.IsSameOrUnder(p, link) || PathRules.IsSameOrUnder(p, target);
        Assert.DoesNotContain(_w.Volumes.Asked, Below);
        Assert.DoesNotContain(_w.Library.PathLookups, Below);
        Assert.DoesNotContain(_probed, Below);
        Assert.DoesNotContain(_accessChecked, Below);
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
