using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The guard in whole runs: adds go and removals (dead links too) are held while a signal stands; the hold clears on its
/// own once the signal is gone; Apply lets them go once, Keep pins them; a change saved from its preview isn't held; the
/// preview says what a run now would hold; the startup pass holds, the pass after a scan doesn't; a missing library
/// folder holds and is named to administrators only.
/// </summary>
public sealed class GuardFlowTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";
    private const string Since2000 = """{"Field":"Year","Op":"Ge","Value":2000}""";
    private const string Recent = """{"Field":"Year","Op":"Ge","Value":2006}""";

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();
    private readonly User _manager = PlannerWorld.NewUser("manager");
    private readonly AutoCollectionHost _host;
    private readonly List<FakeRow> _movies = [];

    public GuardFlowTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
        _manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        _manager.SetPermission(PermissionKind.EnableAllFolders, true);
        var users = new Mock<IUserManager>();
        var all = new[] { _world.Admin, _world.Kid, _manager };
        users.SetupGet(m => m.Users).Returns(all);
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => all.FirstOrDefault(u => u.Id.Equals(id)));
        _host = new AutoCollectionHost(_world, _harness, _folder.Combine("data"), _persistence, users.Object);
        for (var i = 0; i < 10; i++)
        {
            _movies.Add(Movie("M" + i, 2000 + i));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private User Admin => _world.Admin;

    public void Dispose()
    {
        _host.Dispose();
        _harness.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task DuringAScan_AddsGo_RemovalsAreHeld_UntilApplied()
    {
        var created = await CreateAsync();
        _host.ScanRunning = true;
        Gone(6);
        Movie("New", 2010);

        var held = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal("Guarded", held.State.Status);
        Assert.Equal(GuardReasons.ScanRunning, held.State.Reason);
        Assert.Equal(6, held.State.PendingGuard!.WouldRemoveCount);
        Assert.Equal([GuardReasons.ScanRunning], held.State.PendingGuard.Reasons);
        Assert.Equal(11, Box(created).LinkedChildren.Length);
        Assert.Equal(["Rating", "Save", "Refresh"], Take());

        // Still held: nothing more to write, the same review.
        var again = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal("Guarded", again.State.Status);
        Assert.Equal(held.State.PendingGuard.DetectedUtc, again.State.PendingGuard!.DetectedUtc);
        Assert.Empty(Take());

        // Apply needs the review as read: another version is stale.
        var stale = await Assert.ThrowsAsync<PluginException>(() => _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(held.State.PendingGuard.DetectedUtc.AddSeconds(-1)), Ct));
        Assert.Equal("PreviewStale", stale.Code);
        var applied = await _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(held.State.PendingGuard.DetectedUtc), Ct);
        Assert.True(applied.State.PendingGuard!.Approved);
        await _host.Queue.WhenIdleAsync();

        var after = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        Assert.Equal("Ok", after.State.Status);
        Assert.Null(after.State.PendingGuard);
        Assert.Equal(5, Box(created).LinkedChildren.Length);
        var none = await Assert.ThrowsAsync<PluginException>(() => _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(held.State.PendingGuard.DetectedUtc), Ct));
        Assert.Equal("GuardNotPending", none.Code);
    }

    [Fact]
    public async Task AHold_ClearsOnItsOwn_OnceTheSignalIsGone()
    {
        var created = await CreateAsync();
        _host.ScanRunning = true;
        Gone(5);
        Assert.Equal("Guarded", (await _host.RefreshAsync(Admin, true, created.Id)).State.Status);

        _host.ScanRunning = false;
        var cleared = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal("Ok", cleared.State.Status);
        Assert.Equal(5, cleared.State.LastDeadLinks);
        Assert.Equal(5, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task ACandidateDrop_HoldsEveryRun_UntilTheItemsComeBack()
    {
        var created = await CreateAsync();
        Gone(6);

        var first = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal([GuardReasons.CandidateDrop], first.State.PendingGuard!.Reasons);
        var second = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal("Guarded", second.State.Status);
        Assert.Equal(first.State.PendingGuard.DetectedUtc, second.State.PendingGuard!.DetectedUtc);
        Assert.Equal(10, Box(created).LinkedChildren.Length);

        foreach (var movie in _movies.Take(6))
        {
            _world.Core.Restore(movie);
        }

        var back = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal("Ok", back.State.Status);
        Assert.Null(back.State.PendingGuard);
        Assert.Equal(10, back.State.LastCandidateCount);
        Assert.Equal(10, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task ALargeLegitimateRemoval_AfterAScan_IsApplied_TheScopeIsWhatDrops()
    {
        // Ten older films outside the query: the scope (films in the library) stays steady while the query's own rule,
        // pushed into SQL, finds 6 fewer.
        for (var i = 0; i < 10; i++)
        {
            Movie("Old" + i, 1990);
        }

        var created = await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Since 2000", QueryPlannerTests.Q(Since2000, "\"Movie\"")));
        Assert.Equal(10, Box(created).LinkedChildren.Length);
        var scope = _host.State.Find(created.Id)!.LastCandidateCount;
        Assert.True(scope >= 20, scope.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Gone(6);

        await _host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.PostScan);
        var after = _host.State.Find(created.Id)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, after.Status.Value);
        Assert.Null(after.PendingGuard);
        Assert.Equal(scope - 6, after.LastCandidateCount);
        Assert.Equal(4, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task Keep_PinsTheHeldMembers()
    {
        var created = await CreateAsync();
        _host.ScanRunning = true;

        // A change saved without its preview, during a scan: 6 of 10 would go.
        var changed = await _host.UpdateAsync(Admin, true, created.Id, AutoCollectionHost.Def(null, QueryPlannerTests.Q(Keep + "," + Recent, "\"Movie\"")), created.UpdatedUtc);
        Assert.Equal("Guarded", changed.State.Status);
        var held = changed.State.PendingGuard!;
        Assert.Equal(_movies.Take(6).Select(m => m.Id).Order(), held.WouldRemove.Order());
        Assert.Equal(10, Box(created).LinkedChildren.Length);

        await _host.Service.KeepGuardAsync(_manager, false, created.Id, Guard(held.DetectedUtc), Ct);
        await _host.Queue.WhenIdleAsync();

        var kept = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        Assert.Equal("Ok", kept.State.Status);
        Assert.Equal(_movies.Take(6).Select(m => m.Id).Order(), kept.ManualEdits!.Pinned!.Select(p => p.ItemId).Order());
        Assert.Equal(10, Box(created).LinkedChildren.Length);
        Assert.Equal(_manager.Id, kept.UpdatedByUserId);
    }

    [Fact]
    public async Task AChangeSavedFromItsPreview_IsntHeld_AndThePreviewSaysWhatARunNowWouldHold()
    {
        var created = await CreateAsync();
        _host.ScanRunning = true;
        var change = AutoCollectionHost.Def(null, QueryPlannerTests.Q(Keep + "," + Recent, "\"Movie\""));

        var preview = await _host.Service.PreviewAsync(Admin, true, new SmartSearchAutoCollectionPreviewRequestDto { DefinitionId = created.Id, Definition = change }, Ct);
        Assert.Equal(6, preview.WouldRemove);
        Assert.True(preview.Guard.WouldTrip);
        Assert.Equal([GuardReasons.ScanRunning], preview.Guard.Reasons);

        var saved = await _host.UpdateAsync(Admin, true, created.Id, change, created.UpdatedUtc, preview.PreviewHash);
        Assert.Equal("Ok", saved.State.Status);
        Assert.Equal(4, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task TheStartupPass_Holds_ThePassAfterAScan_DoesNt()
    {
        var created = await CreateAsync();
        Gone(5);

        await _host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Startup);
        var startup = _host.State.Find(created.Id)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.Guarded, startup.Status.Value);
        Assert.Equal([GuardReasons.StartupPass], startup.PendingGuard!.Reasons);

        await _host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.PostScan);
        var after = _host.State.Find(created.Id)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, after.Status.Value);
        Assert.Equal(5, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task AMissingLibraryFolder_Holds_AndIsNamedToAdministratorsOnly()
    {
        var created = await CreateAsync();
        _world.Core.Library.Setup(l => l.GetVirtualFolders()).Returns(
            [new VirtualFolderInfo { Name = "Movies", ItemId = _world.Movies.Id.ToString("N"), Locations = ["/media/movies-a", "/media/movies-b"] }]);
        _host.MissingFolders.Add("/media/movies-b");
        Gone(5);

        var held = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal([GuardReasons.LocationUnreachable], held.State.PendingGuard!.Reasons);
        Assert.Equal(["/media/movies-b"], held.State.PendingGuard.Locations);
        var byManager = await _host.Service.GetAsync(_manager, false, created.Id, Ct);
        Assert.Null(byManager.State.PendingGuard!.Locations);
    }

    private static SmartSearchAutoCollectionGuardRequestDto Guard(DateTime detected) => new() { DetectedUtc = detected };

    private async Task<SmartSearchAutoCollectionDto> CreateAsync()
    {
        var created = await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", QueryPlannerTests.Q(Keep, "\"Movie\"")));
        Assert.Equal(10, Box(created).LinkedChildren.Length);
        Take();
        return created;
    }

    // The first n movies leave the library: their links go dead.
    private void Gone(int n)
    {
        foreach (var movie in _movies.Take(n))
        {
            _world.Core.Remove(movie);
        }
    }

    private BoxSet Box(SmartSearchAutoCollectionDto dto) => _persistence.Boxes.Single(b => b.Id.Equals(dto.CollectionId));

    private List<string> Take()
    {
        var calls = _persistence.Calls.ToList();
        _persistence.Calls.Clear();
        return calls;
    }

    private FakeRow Movie(string name, int year, bool keep = true)
        => _world.Add(BaseItemKind.Movie, name, _world.Movies, s =>
        {
            s.Year = year;
            s.Overview = keep ? "keep" : "other";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });
}
