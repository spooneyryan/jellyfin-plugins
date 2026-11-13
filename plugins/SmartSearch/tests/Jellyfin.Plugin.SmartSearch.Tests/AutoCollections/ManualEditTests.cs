using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// Edits made to an auto-collection outside the plugin (the server's collection editor, the server's own adds, other
/// plugins): pins and excludes in Respect mode, reverts in Overwrite mode, edits during a scan, the limit, the plugin's
/// own writes ignored; pins and excludes on removed items detached, and on moved items followed through the journal.
/// </summary>
public sealed class ManualEditTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";
    private const string JournalId = "5c1d2e3f4a5b6c7d8e9f0a1b2c3d4e5f";

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();

    public ManualEditTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _harness.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task AnAddOutsideTheRules_IsPinned_AndARemovedMatch_IsExcluded_AndBothStay()
    {
        var a = Movie("A");
        Movie("B");
        var c = Movie("C", "other");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Q(Keep)));

        await EditAsync(host, created.Id, [c.Id], added: true);
        await EditAsync(host, created.Id, [a.Id], added: false);

        var definition = host.Store.Find(created.Id)!;
        Assert.Equal([c.Id], definition.ManualEdits.Pinned.Select(e => e.ItemId));
        Assert.Equal([a.Id], definition.ManualEdits.Excluded.Select(e => e.ItemId));
        Assert.NotNull(definition.ManualEdits.Pinned[0].Identity);
        Assert.DoesNotContain(a.Id, Members());
        Assert.Contains(c.Id, Members());
        var state = host.State.Find(created.Id)!;
        Assert.Empty(state.PendingEdits);
        Assert.Equal(SmartSearchAutoCollectionRunReason.ManualEdit, state.LastReason!.Value.Value);
        Assert.Equal(1, state.History[0].Excluded);
        Assert.Equal(1, state.History[1].Pinned);

        // A refresh keeps them.
        var refreshed = await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Equal("Ok", refreshed.State.Status);
        Assert.Contains(c.Id, Members());
        Assert.DoesNotContain(a.Id, Members());

        // An excluded match added back stops being excluded (and isn't pinned: the query has it).
        await EditAsync(host, created.Id, [a.Id], added: true);
        definition = host.Store.Find(created.Id)!;
        Assert.Empty(definition.ManualEdits.Excluded);
        Assert.Equal([c.Id], definition.ManualEdits.Pinned.Select(e => e.ItemId));
        Assert.Contains(a.Id, Members());

        // A pin removed again stops being a pin (and isn't excluded: the query doesn't have it).
        await EditAsync(host, created.Id, [c.Id], added: false);
        definition = host.Store.Find(created.Id)!;
        Assert.Empty(definition.ManualEdits.Pinned);
        Assert.Empty(definition.ManualEdits.Excluded);
        Assert.DoesNotContain(c.Id, Members());
    }

    [Fact]
    public async Task TwentyFiveFilmsAddedAtOnce_AllBecomePins()
    {
        Movie("A");
        var others = Enumerable.Range(0, 25).Select(i => Movie("Other " + i, "other")).ToList();
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Q(Keep)));

        await EditAsync(host, created.Id, [.. others.Select(o => o.Id)], added: true);

        Assert.Equal(25, host.Store.Find(created.Id)!.ManualEdits.Pinned.Count);
        Assert.Equal(26, Members().Count);
        Assert.Equal(25, host.State.Find(created.Id)!.History[0].Pinned);
    }

    [Fact]
    public async Task InOverwriteMode_AnEditIsReverted_AndNothingIsKept()
    {
        var a = Movie("A");
        var c = Movie("C", "other");
        using var host = Host();
        var definition = AutoCollectionHost.Def("Keepers", Q(Keep));
        definition.ManualEdits = new SmartSearchAutoCollectionManualEditsDto { Mode = "Overwrite" };
        var created = await host.CreateAsync(_world.Admin, true, definition);

        await EditAsync(host, created.Id, [c.Id], added: true);
        await EditAsync(host, created.Id, [a.Id], added: false);

        var stored = host.Store.Find(created.Id)!;
        Assert.Empty(stored.ManualEdits.Pinned);
        Assert.Empty(stored.ManualEdits.Excluded);
        Assert.Equal([a.Id], Members());
        Assert.Equal(1, host.State.Find(created.Id)!.History[0].Reverted);
    }

    [Fact]
    public async Task TheServersOwnAdd_IsReverted_OnlyWhenTheLibraryAddsAndTheNameMatches()
    {
        Movie("A");
        var tmdb = Movie("T", "other");
        var named = Movie("N", "other");
        ((Movie)tmdb.Item).CollectionName = "Keepers";
        ((Movie)named.Item).CollectionName = "Something else";
        _world.Core.Library.Setup(l => l.GetLibraryOptions(It.IsAny<BaseItem>())).Returns(new LibraryOptions { AutomaticallyAddToCollection = true });
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Q(Keep)));

        await EditAsync(host, created.Id, [tmdb.Id, named.Id], added: true);

        var stored = host.Store.Find(created.Id)!;
        Assert.Equal([named.Id], stored.ManualEdits.Pinned.Select(e => e.ItemId));
        Assert.DoesNotContain(tmdb.Id, Members());
        Assert.Equal(1, host.State.Find(created.Id)!.History[0].Reverted);
    }

    [Fact]
    public async Task AnEditDuringAScan_WaitsForThePassAfterIt_AndIsThenPinned()
    {
        Movie("A");
        var c = Movie("C", "other");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Q(Keep)));
        var requests = host.Queue.Requests.Count;

        host.ScanRunning = true;
        Box().LinkedChildren = [.. Box().LinkedChildren, new LinkedChild { ItemId = c.Id, Type = MediaBrowser.Controller.Entities.LinkedChildType.Manual }];
        host.Listener.OnCollectionEdited(Box(), [c.Id], added: true);
        await host.Listener.WhenIdleAsync();
        Assert.Equal(requests, host.Queue.Requests.Count);
        Assert.Single(host.State.Find(created.Id)!.PendingEdits);

        host.ScanRunning = false;
        await host.Worker.RequestPassAsync(SmartSearchAutoCollectionRunReason.PostScan);
        Assert.Equal([c.Id], host.Store.Find(created.Id)!.ManualEdits.Pinned.Select(e => e.ItemId));
        Assert.Contains(c.Id, Members());
        Assert.Empty(host.State.Find(created.Id)!.PendingEdits);
    }

    [Fact]
    public async Task EditsOverTheLimit_HoldTheRun_AndNothingIsReverted()
    {
        Movie("A");
        var c = Movie("C", "other");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Q(Keep)));
        var live = Enumerable.Range(0, AutoCollectionManualEdits.MaxEntries).Select(i => Movie("Pin " + i, "other")).ToList();
        await host.Store.UpdateDefinitionAsync(created.Id, d => d with { ManualEdits = d.ManualEdits with { Pinned = live.Select(r => new AutoCollectionItemEntry { ItemId = r.Id, AddedUtc = DateTime.UtcNow }).ToList() } }, Ct);
        await host.RefreshAsync(_world.Admin, true, created.Id);
        var before = Members();
        Assert.Equal(1001, before.Count);

        await EditAsync(host, created.Id, [c.Id], added: true);

        var state = host.State.Find(created.Id)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.Guarded, state.Status.Value);
        Assert.Equal(AutoCollectionReasons.TooManyManualEdits, state.Reason);
        Assert.Single(state.PendingEdits);
        Assert.Contains(c.Id, Members());
        Assert.Equal(AutoCollectionManualEdits.MaxEntries, host.Store.Find(created.Id)!.ManualEdits.Pinned.Count);

        // Overwrite settles it: the edit is reverted.
        var current = await host.Service.GetAsync(_world.Admin, true, created.Id, Ct);
        await host.UpdateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { ManualEdits = new SmartSearchAutoCollectionManualEditsDto { Mode = "Overwrite" } }, current.UpdatedUtc);
        Assert.Empty(host.State.Find(created.Id)!.PendingEdits);
        Assert.DoesNotContain(c.Id, Members());
    }

    [Fact]
    public async Task ThePluginsOwnWrites_AreNotEdits_ButAnotherItemsEditMeanwhileIs()
    {
        Movie("A");
        var c = Movie("C", "other");
        var e = Movie("E", "other");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Q(Keep)));

        using (host.Suppression.Begin(Box().Id, [c.Id]))
        {
            host.Listener.OnCollectionEdited(Box(), [c.Id], added: true);
        }

        await host.Listener.WhenIdleAsync();
        Assert.Empty(host.State.Find(created.Id)!.PendingEdits);
        Assert.Equal(0, host.Listener.Recorded);

        // Only the items the write passes are the plugin's: a person adding another one meanwhile is recorded.
        using (host.Suppression.Begin(Box().Id, [c.Id]))
        {
            host.Listener.OnCollectionEdited(Box(), [c.Id, e.Id], added: true);
        }

        await host.Listener.WhenIdleAsync();
        Assert.Equal(1, host.Listener.Recorded);
        Assert.False(host.Suppression.IsWriting(Box().Id));

        // A collection without the mark isn't ours.
        host.Listener.OnCollectionEdited(new BoxSet { Id = Guid.NewGuid(), Name = "Mine" }, [c.Id], added: true);
        await host.Listener.WhenIdleAsync();
        Assert.Equal(1, host.Listener.Recorded);
    }

    [Fact]
    public async Task ARemovedItem_DetachesItsPin_AndARunDetachesOneThatIsGone()
    {
        Movie("A");
        var c = Movie("C", "other");
        var d = Movie("D", "other");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Q(Keep)));
        await EditAsync(host, created.Id, [c.Id, d.Id], added: true);

        _world.Core.Library.Raise(l => l.ItemRemoved += null, _world.Core.Library.Object, new ItemChangeEventArgs { Item = c.Item });
        await host.Listener.WhenIdleAsync();
        var pins = host.Store.Find(created.Id)!.ManualEdits.Pinned;
        Assert.NotNull(pins.Single(e => e.ItemId.Equals(c.Id)).DetachedUtc);
        Assert.Null(pins.Single(e => e.ItemId.Equals(d.Id)).DetachedUtc);

        // A pin whose item is gone without an event (a removal the server didn't announce) is detached by the next run.
        var ghost = Guid.NewGuid();
        await host.Store.UpdateDefinitionAsync(created.Id, x => x with { ManualEdits = x.ManualEdits with { Pinned = [.. x.ManualEdits.Pinned, new AutoCollectionItemEntry { ItemId = ghost, AddedUtc = DateTime.UtcNow }] } }, Ct);
        await host.RefreshAsync(_world.Admin, true, created.Id);
        pins = host.Store.Find(created.Id)!.ManualEdits.Pinned;
        Assert.NotNull(pins.Single(e => e.ItemId.Equals(ghost)).DetachedUtc);
        Assert.NotNull(pins.Single(e => e.ItemId.Equals(d.Id)).Identity);
    }

    [Fact]
    public async Task TheJournal_MovesPinsAndExcludes_ToTheNewIds_OnceAndForAll()
    {
        Movie("A");
        var c = Movie("C", "other");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Q(Keep)));
        var oldPin = Guid.NewGuid();
        var oldExclude = Guid.NewGuid();
        var newExclude = Guid.NewGuid();
        await host.Store.UpdateDefinitionAsync(created.Id, x => x with
        {
            ManualEdits = x.ManualEdits with
            {
                Pinned = [new AutoCollectionItemEntry { ItemId = oldPin, AddedUtc = DateTime.UtcNow, DetachedUtc = DateTime.UtcNow }],
                Excluded = [new AutoCollectionItemEntry { ItemId = oldExclude, AddedUtc = DateTime.UtcNow }]
            }
        }, Ct);

        // The pin moved twice (a chain), the exclude once; a line about an item nobody pinned changes nothing.
        var middle = Guid.NewGuid();
        WriteJournal(host, Line(1, oldPin, middle), Line(2, oldExclude, newExclude), Line(3, middle, c.Id), Line(4, Guid.NewGuid(), Guid.NewGuid()));
        var moved = await host.Keeper.CatchUpAsync(host.Store, Ct);

        Assert.Equal([created.Id], moved);
        var edits = host.Store.Find(created.Id)!.ManualEdits;
        Assert.Equal(c.Id, Assert.Single(edits.Pinned).ItemId);
        Assert.Null(edits.Pinned[0].DetachedUtc);
        Assert.Equal(newExclude, Assert.Single(edits.Excluded).ItemId);
        Assert.Equal((Guid.ParseExact(JournalId, "N"), 4L), (host.Store.Current.MoveJournalId!.Value, host.Store.Current.LastMoveSeq));

        // Caught up: nothing more; a replay from the start (a lower position) changes nothing either.
        Assert.Empty(await host.Keeper.CatchUpAsync(host.Store, Ct));
        await host.Store.UpdateAsync(x => x with { LastMoveSeq = 0 }, Ct);
        Assert.Empty(await host.Keeper.CatchUpAsync(host.Store, Ct));
        Assert.Equal(c.Id, Assert.Single(host.Store.Find(created.Id)!.ManualEdits.Pinned).ItemId);

        // The run then pins the moved item.
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Contains(c.Id, Members());
    }

    private static string Q(string rules) => QueryPlannerTests.Q(rules, "\"Movie\"");

    private static string Line(long seq, Guid oldId, Guid newId)
        => "{\"V\":1,\"JournalId\":\"" + JournalId + "\",\"Seq\":" + seq + ",\"Status\":\"Committed\",\"Utc\":\"" + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + "\",\"OldId\":\"" + oldId.ToString("N") + "\",\"NewId\":\"" + newId.ToString("N") + "\",\"Kind\":\"Movie\"}";

    private static void WriteJournal(AutoCollectionHost host, params string[] lines)
        => File.WriteAllLines(host.Moves.FilePath, ["{\"V\":1,\"Header\":true,\"JournalId\":\"" + JournalId + "\",\"CreatedUtc\":\"2026-09-01T00:00:00Z\"}", .. lines]);

    private static async Task EditAsync(AutoCollectionHost host, Guid definitionId, IReadOnlyCollection<Guid> ids, bool added)
    {
        // As the server does: the links change, then the event fires.
        var box = host.Locator.Locate(host.Store.Find(definitionId)!).Box!;
        box.LinkedChildren = added
            ? [.. box.LinkedChildren, .. ids.Select(id => new LinkedChild { ItemId = id, Type = MediaBrowser.Controller.Entities.LinkedChildType.Manual })]
            : [.. box.LinkedChildren.Where(l => l.ItemId is not { } id || !ids.Contains(id))];
        host.Listener.OnCollectionEdited(box, ids, added);
        await host.Listener.WhenIdleAsync();
        await host.Queue.WhenIdleAsync();
    }

    private List<Guid> Members() => Box().LinkedChildren.Select(l => l.ItemId!.Value).ToList();

    private BoxSet Box() => Assert.Single(_persistence.Boxes);

    private AutoCollectionHost Host()
    {
        var users = new Mock<IUserManager>();
        users.SetupUsers([_world.Admin, _world.Kid]);
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => new[] { _world.Admin, _world.Kid }.FirstOrDefault(u => u.Id.Equals(id)));
        return new AutoCollectionHost(_world, _harness, _folder.Combine("data"), _persistence, users.Object);
    }

    private FakeRow Movie(string name, string overview = "keep")
        => _world.Add(BaseItemKind.Movie, name, _world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = overview;
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });
}
