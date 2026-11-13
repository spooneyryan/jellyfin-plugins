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
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// An approval covers exactly the removals a manager reviewed: a hold that grows is a new version an old Apply can't
/// name, a run that would remove more or empty the collection is held again, and any run's end uses the approval up.
/// Keep pins every held item or refuses; the writer through the server's calls saves the server's current copy; what
/// stopped a run is told to managers without the details; edits beyond what a state keeps stop the runs.
/// </summary>
public sealed class GuardApprovalTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";
    private const string Recent = """{"Field":"Year","Op":"Ge","Value":2006}""";

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();
    private readonly User _manager = PlannerWorld.NewUser("manager");
    private readonly AutoCollectionHost _host;
    private readonly List<FakeRow> _movies = [];

    public GuardApprovalTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
        _manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        _manager.SetPermission(PermissionKind.EnableAllFolders, true);
        var users = new Mock<IUserManager>();
        var all = new[] { _world.Admin, _world.Kid, _manager };
        users.SetupUsers(all);
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
    public async Task AHoldThatGrows_IsANewVersion_AnApplyOfTheSmallerOneIsStale_AndTheCollectionKeepsItsMembers()
    {
        var created = await CreateAsync();
        _host.ScanRunning = true;
        Gone(5);
        var small = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal(5, small.State.PendingGuard!.WouldRemoveCount);

        // The same removals again: the same version, so a manager's read stays good.
        var same = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal(small.State.PendingGuard.DetectedUtc, same.State.PendingGuard!.DetectedUtc);

        // Every member gone: a new version, held by the Empty rule.
        Gone(10);
        var grown = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal(10, grown.State.PendingGuard!.WouldRemoveCount);
        Assert.True(grown.State.PendingGuard.DetectedUtc > small.State.PendingGuard.DetectedUtc);
        Assert.Contains(GuardReasons.Empty, grown.State.PendingGuard.Reasons);

        var stale = await Assert.ThrowsAsync<PluginException>(() => _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(small.State.PendingGuard.DetectedUtc), Ct));
        Assert.Equal("PreviewStale", stale.Code);
        await _host.Queue.WhenIdleAsync();
        Assert.Equal(10, Box(created).LinkedChildren.Length);
        Assert.Equal("Guarded", (await _host.Service.GetAsync(Admin, true, created.Id, Ct)).State.Status);
    }

    [Fact]
    public async Task AnApprovedSmallHold_FollowedByALargerRemoval_IsHeldAgain()
    {
        var created = await CreateAsync();
        _host.ScanRunning = true;
        Gone(5);
        var small = await _host.RefreshAsync(Admin, true, created.Id);

        // More go before the approved run: it would remove 8, not the 5 approved.
        Gone(8);
        await _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(small.State.PendingGuard!.DetectedUtc), Ct);
        await _host.Queue.WhenIdleAsync();

        var after = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        Assert.Equal("Guarded", after.State.Status);
        Assert.Equal(8, after.State.PendingGuard!.WouldRemoveCount);
        Assert.False(after.State.PendingGuard.Approved);
        Assert.NotEqual(small.State.PendingGuard.DetectedUtc, after.State.PendingGuard.DetectedUtc);
        Assert.Equal(10, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task AnApprovedSmallHold_FollowedByAnEmptyingRun_IsHeldAgain()
    {
        var created = await CreateAsync();
        _host.ScanRunning = true;
        Gone(5);
        var small = await _host.RefreshAsync(Admin, true, created.Id);

        Gone(10);
        await _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(small.State.PendingGuard!.DetectedUtc), Ct);
        await _host.Queue.WhenIdleAsync();

        var after = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        Assert.Equal("Guarded", after.State.Status);
        Assert.Equal(GuardReasons.Empty, after.State.Reason);
        Assert.False(after.State.PendingGuard!.Approved);
        Assert.Equal(10, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task AnApprovedEmptyHold_EmptiesTheCollection()
    {
        var created = await CreateAsync();
        Gone(10);
        var held = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal(GuardReasons.Empty, held.State.Reason);

        await _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(held.State.PendingGuard!.DetectedUtc), Ct);
        await _host.Queue.WhenIdleAsync();

        Assert.Empty(Box(created).LinkedChildren);
        Assert.Equal("Ok", (await _host.Service.GetAsync(Admin, true, created.Id, Ct)).State.Status);
    }

    [Fact]
    public async Task ARunThatStops_UsesTheApprovalUp_SoTheNextRunIsHeldAgain()
    {
        var created = await CreateAsync();
        _host.ScanRunning = true;
        Gone(5);
        var held = await _host.RefreshAsync(Admin, true, created.Id);

        var current = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        await _host.UpdateAsync(Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { Enabled = false }, current.UpdatedUtc);
        await _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(held.State.PendingGuard!.DetectedUtc), Ct);
        await _host.Queue.WhenIdleAsync();

        var stopped = _host.State.Find(created.Id)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.Disabled, stopped.Status.Value);
        Assert.NotNull(stopped.PendingGuard);
        Assert.Null(stopped.PendingGuard!.ApprovedUtc);

        // Later, every member gone and the definition on again: held, nothing removed.
        Gone(10);
        current = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        var on = await _host.UpdateAsync(Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { Enabled = true }, current.UpdatedUtc);
        Assert.Equal("Guarded", on.State.Status);
        Assert.Equal(10, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task ARunThatStops_ClearsTheAcceptedPreviewsRemovals()
    {
        var created = await CreateAsync();
        await _host.State.UpdateAsync(created.Id, s => s! with { AcceptedRemovals = 6, AcceptedResultHash = "sha256:x" }, Ct);
        var current = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        await _host.UpdateAsync(Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { Enabled = false }, current.UpdatedUtc);
        await _host.Runner.RunAsync(created.Id, SmartSearchAutoCollectionRunReason.Manual, Ct);

        var stopped = _host.State.Find(created.Id)!;
        Assert.Null(stopped.AcceptedRemovals);
        Assert.Null(stopped.AcceptedResultHash);
    }

    [Fact]
    public async Task KeepOfMoreThan500HeldItems_PinsThemAll()
    {
        AddMovies(600);
        var created = await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", QueryPlannerTests.Q(Keep, "\"Movie\"")));
        _host.ScanRunning = true;
        var changed = await _host.UpdateAsync(Admin, true, created.Id, AutoCollectionHost.Def(null, QueryPlannerTests.Q(Keep + "," + Recent, "\"Movie\"")), created.UpdatedUtc);
        Assert.Equal(606, changed.State.PendingGuard!.WouldRemoveCount);
        Assert.Equal(606, changed.State.PendingGuard.WouldRemove.Count);

        _host.ScanRunning = false;
        await _host.Service.KeepGuardAsync(_manager, false, created.Id, Guard(changed.State.PendingGuard.DetectedUtc), Ct);
        await _host.Queue.WhenIdleAsync();

        var kept = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        Assert.Equal("Ok", kept.State.Status);
        Assert.Equal(606, kept.ManualEdits!.Pinned!.Count);
        Assert.Equal(610, Box(created).LinkedChildren.Length);
        Assert.Equal(0, kept.State.LastRemoved);
    }

    [Fact]
    public async Task KeepOfMoreHeldItemsThanCanBePinned_IsRefused_AndTheHoldStays_WhileApplyStillWorks()
    {
        AddMovies(1000);
        var created = await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", QueryPlannerTests.Q(Keep, "\"Movie\"")));
        _host.ScanRunning = true;
        var changed = await _host.UpdateAsync(Admin, true, created.Id, AutoCollectionHost.Def(null, QueryPlannerTests.Q(Keep + "," + Recent, "\"Movie\"")), created.UpdatedUtc);
        var held = changed.State.PendingGuard!;
        Assert.Equal(1006, held.WouldRemoveCount);

        var refused = await Assert.ThrowsAsync<PluginException>(() => _host.Service.KeepGuardAsync(_manager, false, created.Id, Guard(held.DetectedUtc), Ct));
        Assert.Equal("TooManyToKeep", refused.Code);
        Assert.Equal(PluginErrorKind.Conflict, refused.Kind);
        var after = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        Assert.Equal(held.DetectedUtc, after.State.PendingGuard!.DetectedUtc);
        Assert.Empty(after.ManualEdits!.Pinned!);
        Assert.Equal(1010, Box(created).LinkedChildren.Length);

        // Too many to list, but the same set: the approval covers it.
        await _host.Service.ApplyGuardAsync(_manager, false, created.Id, Guard(held.DetectedUtc), Ct);
        await _host.Queue.WhenIdleAsync();
        Assert.Equal(4, Box(created).LinkedChildren.Length);
    }

    [Fact]
    public async Task ManagerApiWriter_SavesThePresentation_OnTheServersCurrentCopy()
    {
        var a = Movie("A", 2001);
        var b = Movie("B", 2002);
        var c = Movie("C", 2003);
        var d = Movie("D", 2004);
        var definition = new AutoCollectionDefinition { Id = Guid.NewGuid(), Name = "Box" };
        var server = new CacheReloadingPersistence(definition.Tether);
        var stale = server.Make([a.Id, b.Id, c.Id]);
        var direct = new DirectReplaceWriter(_world.Core.Library.Object, server, new CollectionLocator(_world.Core.Library.Object, _world.Core.Access()), NullLogger.Instance);
        var writer = new ManagerApiWriter(server, direct, new CollectionWriteSuppression(), NullLogger.Instance);

        await writer.WriteAsync(stale, new CollectionDiff([d.Id], [c.Id], 0, 0, false, []), [a.Id, b.Id, d.Id], definition, Ct);

        Assert.Equal([a.Id, b.Id, d.Id], server.Row);
        Assert.Equal(3, server.Saves);
        Assert.NotSame(stale, server.LastSaved);
    }

    [Fact]
    public async Task ManagerApiWriter_LeavesThePresentation_WhenTheCollectionIsntTheDefinitionsAnyMore()
    {
        var a = Movie("A", 2001);
        var b = Movie("B", 2002);
        var definition = new AutoCollectionDefinition { Id = Guid.NewGuid(), Name = "Box" };
        var server = new CacheReloadingPersistence("someone-else");
        var stale = server.Make([a.Id, b.Id]);
        var direct = new DirectReplaceWriter(_world.Core.Library.Object, server, new CollectionLocator(_world.Core.Library.Object, _world.Core.Access()), NullLogger.Instance);
        var writer = new ManagerApiWriter(server, direct, new CollectionWriteSuppression(), NullLogger.Instance);

        await writer.WriteAsync(stale, new CollectionDiff([], [b.Id], 0, 0, false, []), [a.Id], definition, Ct);

        Assert.Equal([a.Id], server.Row);
        Assert.Equal(1, server.Saves);
    }

    [Fact]
    public async Task AWriterThatFindsAMemberGone_AsksBeforeItWrites_AndWritesNothingWhenRefused()
    {
        var created = await CreateAsync();
        var box = Box(created);
        var links = new LinkResolver(_world.Core.Library.Object, _world.Core.Access()).Resolve(box);
        var direct = new DirectReplaceWriter(_world.Core.Library.Object, _persistence, _host.Locator, NullLogger.Instance);
        var definition = _host.Store.Find(created.Id)!;
        _world.Core.Remove(_movies[0]);
        IReadOnlyCollection<Guid>? missing = null;

        var written = await direct.WriteAsync(box, links, _movies.Select(m => m.Id).ToList(), definition, Ct, accept: m => { missing = m; return false; });

        Assert.Null(written);
        Assert.Equal([_movies[0].Id], missing);
        Assert.Empty(_persistence.Calls);
        Assert.Equal(10, box.LinkedChildren.Length);
    }

    [Fact]
    public async Task WhatStoppedARun_IsToldToManagers_WithoutTheDetails_WhichAdministratorsSee()
    {
        var created = await CreateAsync();
        _persistence.SaveFails = _ => true;
        Movie("New", 2010);
        await _host.Service.RefreshAsync(Admin, true, created.Id, Ct);
        await _host.Queue.WhenIdleAsync();

        var byManager = await _host.Service.GetAsync(_manager, false, created.Id, Ct);
        Assert.Equal("Error", byManager.State.Status);
        Assert.Equal(AutoCollectionReasons.SafeText(AutoCollectionReasons.RunFailed), byManager.State.LastError);
        Assert.DoesNotContain("disk", byManager.State.LastError, StringComparison.Ordinal);
        var byAdmin = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        Assert.Equal("the disk is full", byAdmin.State.LastError);
    }

    [Fact]
    public async Task EditsBeyondWhatAStateKeeps_StopTheRuns_UntilAManagerSaves()
    {
        var created = await CreateAsync();
        var edits = Enumerable.Range(0, AutoCollectionState.MaxPendingEdits + 1).Select(_ => Guid.NewGuid()).ToList();
        _host.Listener.OnCollectionEdited(Box(created), edits, added: true);
        await _host.Listener.WhenIdleAsync();
        await _host.Queue.WhenIdleAsync();

        var state = _host.State.Find(created.Id)!;
        Assert.True(state.PendingEditsOverflow);
        Assert.Equal(AutoCollectionState.MaxPendingEdits, state.PendingEdits.Count);
        Assert.Equal(edits[0], state.PendingEdits[0].ItemId);
        var stopped = await _host.RefreshAsync(Admin, true, created.Id);
        Assert.Equal("Guarded", stopped.State.Status);
        Assert.Equal(AutoCollectionReasons.TooManyManualEdits, stopped.State.Reason);
        Assert.Equal(10, Box(created).LinkedChildren.Length);

        // A save is the manager's answer: the runs go on.
        var current = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        await _host.UpdateAsync(Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { ManualEdits = new SmartSearchAutoCollectionManualEditsDto { Mode = "Overwrite" } }, current.UpdatedUtc);
        var after = _host.State.Find(created.Id)!;
        Assert.False(after.PendingEditsOverflow);
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, after.Status.Value);
    }

    [Fact]
    public async Task PinsSavedByAManager_AndByKeep_RecordWhatTheItemIs()
    {
        var created = await CreateAsync();
        var current = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        await _host.UpdateAsync(Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { ManualEdits = new SmartSearchAutoCollectionManualEditsDto { PinnedItemIds = [_movies[9].Id] } }, current.UpdatedUtc);
        Assert.NotNull(Assert.Single(_host.Store.Find(created.Id)!.ManualEdits.Pinned).Identity);

        _host.ScanRunning = true;
        current = await _host.Service.GetAsync(Admin, true, created.Id, Ct);
        var changed = await _host.UpdateAsync(Admin, true, created.Id, AutoCollectionHost.Def(null, QueryPlannerTests.Q(Keep + "," + Recent, "\"Movie\"")), current.UpdatedUtc);
        await _host.Service.KeepGuardAsync(_manager, false, created.Id, Guard(changed.State.PendingGuard!.DetectedUtc), Ct);
        var pins = _host.Store.Find(created.Id)!.ManualEdits.Pinned;
        Assert.Equal(7, pins.Count);
        Assert.All(pins, p => Assert.NotNull(p.Identity));
    }

    [Fact]
    public async Task AStartupPassServingAManualRequest_KeepsTheStartupSignal()
    {
        var created = await CreateAsync();
        Gone(5);
        _host.ScanRunning = true;

        // The startup pass waits for the scan; a manual pass asked meanwhile starts it now.
        _ = _host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Startup);
        await _host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.Manual);

        var state = _host.State.Find(created.Id)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.Guarded, state.Status.Value);
        Assert.Contains(GuardReasons.StartupPass, state.PendingGuard!.Reasons);
        Assert.Equal(10, Box(created).LinkedChildren.Length);
    }

    private static SmartSearchAutoCollectionGuardRequestDto Guard(DateTime detected) => new() { DetectedUtc = detected };

    private async Task<SmartSearchAutoCollectionDto> CreateAsync()
    {
        var created = await _host.CreateAsync(Admin, true, AutoCollectionHost.Def("Keepers", QueryPlannerTests.Q(Keep, "\"Movie\"")));
        Assert.Equal(10, Box(created).LinkedChildren.Length);
        _persistence.Calls.Clear();
        return created;
    }

    private void AddMovies(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Movie("X" + i, 2000);
        }
    }

    // The first n movies leave the library: their links go dead.
    private void Gone(int n)
    {
        foreach (var movie in _movies.Take(n))
        {
            if (_world.Core.Rows.Contains(movie))
            {
                _world.Core.Remove(movie);
            }
        }
    }

    private BoxSet Box(SmartSearchAutoCollectionDto dto) => _persistence.Boxes.Single(b => b.Id.Equals(dto.CollectionId));

    private FakeRow Movie(string name, int year)
        => _world.Add(BaseItemKind.Movie, name, _world.Movies, s =>
        {
            s.Year = year;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });

    // The server as seen after its cache dropped the collection: each call loads a fresh copy from the row, changes and
    // saves it; a save of any copy writes that copy's links to the row.
    private sealed class CacheReloadingPersistence(string tether) : ICollectionPersistence
    {
        private Guid _id;

        public List<Guid> Row { get; private set; } = [];

        public BoxSet? LastSaved { get; private set; }

        public int Saves { get; private set; }

        public BoxSet Make(IReadOnlyList<Guid> ids)
        {
            _id = Guid.NewGuid();
            Row = [.. ids];
            return Load();
        }

        public Task<BoxSet> CreateAsync(string name, bool isLocked, IReadOnlyDictionary<string, string> providerIds, IReadOnlyList<Guid> itemIds) => throw new NotSupportedException();

        public void UpdateRating(BoxSet box, IReadOnlyList<BaseItem> members)
        {
        }

        public Task SaveAsync(BoxSet box, CancellationToken cancellationToken)
        {
            Row = box.LinkedChildren.Select(l => l.ItemId!.Value).ToList();
            LastSaved = box;
            Saves++;
            return Task.CompletedTask;
        }

        public void QueueRefresh(BoxSet box)
        {
        }

        public BoxSet? FindDeletable(Guid id, User user) => null;

        public Task DeleteAsync(BoxSet box) => Task.CompletedTask;

        public BoxSet? Reload(Guid collectionId) => collectionId.Equals(_id) ? Load() : null;

        public Task AddAsync(Guid collectionId, IReadOnlyList<Guid> itemIds)
        {
            var fresh = Load();
            fresh.LinkedChildren = [.. fresh.LinkedChildren, .. itemIds.Select(id => new LinkedChild { ItemId = id, Type = LinkedChildType.Manual })];
            return SaveAsync(fresh, CancellationToken.None);
        }

        public Task RemoveAsync(Guid collectionId, IReadOnlyList<Guid> itemIds)
        {
            var fresh = Load();
            var gone = itemIds.ToHashSet();
            fresh.LinkedChildren = fresh.LinkedChildren.Where(l => !gone.Contains(l.ItemId!.Value)).ToArray();
            return SaveAsync(fresh, CancellationToken.None);
        }

        private BoxSet Load() => new()
        {
            Id = _id,
            Name = "Box",
            IsLocked = true,
            ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [AutoCollectionDefinition.TetherKey] = tether },
            LinkedChildren = Row.Select(id => new LinkedChild { ItemId = id, Type = LinkedChildType.Manual }).ToArray()
        };
    }
}
