using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
using Moq;
using Xunit;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// Whole runs through the endpoints' service over the test world, with the server's collection calls recorded: create,
/// update (add, remove, reorder), rename and detach each write once; a run with nothing to change writes nothing; a
/// collection that lost its mark, a definition a newer version wrote and a store a newer version wrote are never written.
/// Also who may change what, the preview a create was saved from, deleting the collection too, and emptying.
/// </summary>
public sealed class AutoCollectionRunTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();
    private readonly User _manager = PlannerWorld.NewUser("manager");
    private readonly User _manager2 = PlannerWorld.NewUser("manager2");

    public AutoCollectionRunTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
        _manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        _manager2.SetPermission(PermissionKind.EnableCollectionManagement, true);
        _manager.SetPermission(PermissionKind.EnableAllFolders, true);
        _manager2.SetPermission(PermissionKind.EnableAllFolders, true);
    }

    private static CancellationToken Ct => CancellationToken.None;

    public void Dispose()
    {
        _harness.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task Create_Update_Rename_Reorder_AndDetach_EachWriteOnce_AndANoOpWritesNothing()
    {
        var a = Movie("A");
        var b = Movie("B");
        var c = Movie("C");
        using var host = Host();
        var admin = _world.Admin;

        // Create: the collection is made with its members in the query's order, marked, then saved once.
        var created = await host.CreateAsync(admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep), "QueryOrder"));
        var box = Assert.Single(_persistence.Boxes);
        Assert.Equal(box.Id, created.CollectionId);
        Assert.Equal("Ok", created.State.Status);
        Assert.Equal([a.Id, b.Id, c.Id], Members(box));
        Assert.Equal("Default", box.DisplayOrder);
        Assert.True(CollectionLocator.HasTether(box, created.Id));
        Assert.Equal(["Create", "Save"], Take());

        // Nothing changed: nothing is written.
        var same = await host.RefreshAsync(admin, true, created.Id);
        Assert.Equal("Ok", same.State.Status);
        Assert.Equal("Manual", same.State.LastReason);
        Assert.Empty(Take());

        // An item added and one gone: one write, the dead link pruned.
        var d = Movie("D");
        _world.Core.Remove(b);
        var updated = await host.RefreshAsync(admin, true, created.Id);
        Assert.Equal([a.Id, c.Id, d.Id], Members(box));
        Assert.Equal(1, updated.State.LastAdded);
        Assert.Equal(1, updated.State.LastDeadLinks);
        Assert.Equal(["Rating", "Save", "Refresh"], Take());
        await host.RefreshAsync(admin, true, created.Id);
        Assert.Empty(Take());

        // Renamed, with an item excluded: one write.
        var changes = new SmartSearchAutoCollectionDefinitionDto { Name = "Kept", ManualEdits = new SmartSearchAutoCollectionManualEditsDto { ExcludedItemIds = [a.Id] } };
        var renamed = await host.UpdateAsync(admin, true, created.Id, changes, updated.UpdatedUtc);
        Assert.Equal("Kept", box.Name);
        Assert.Equal([c.Id, d.Id], Members(box));
        Assert.Equal(1, renamed.State.LastRemoved);
        Assert.Equal(a.Id, Assert.Single(renamed.ManualEdits!.Excluded!).ItemId);
        Assert.Equal(["Rating", "Save", "Refresh"], Take());

        // Reordered: the same members in another order are a change for the query's order.
        var reordered = await host.UpdateAsync(admin, true, created.Id, AutoCollectionHost.Def(null, Sorted(Keep, "Descending")), renamed.UpdatedUtc);
        Assert.Equal([d.Id, c.Id], Members(box));
        Assert.Equal("Ok", reordered.State.Status);
        Assert.Equal(["Rating", "Save", "Refresh"], Take());

        // A change against what was read before is refused.
        var stale = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { Name = "Late" }, renamed.UpdatedUtc));
        Assert.Equal(AutoCollectionService.ChangedCode, stale.Code);

        // Detached: the mark goes, the collection and its members stay, the definition is gone.
        await host.Service.DeleteAsync(admin, true, created.Id, false, Ct);
        Assert.False(box.ProviderIds.ContainsKey(AutoCollectionDefinition.TetherKey));
        Assert.Equal([d.Id, c.Id], Members(box));
        Assert.Equal(["Save"], Take());
        await Assert.ThrowsAsync<PluginException>(() => host.Service.GetAsync(admin, true, created.Id, Ct));
    }

    [Fact]
    public async Task ATakenName_IsRefusedWithSuggestions_AndNothingIsSaved()
    {
        Movie("A");
        _persistence.Boxes.Add(new BoxSet { Id = Guid.NewGuid(), Name = "Keepers", Path = Path.Combine(_folder.Path, "data", "collections", "Keepers [boxset]") });
        using var host = Host();

        var error = await Assert.ThrowsAsync<PluginException>(() => host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("keepers", Sorted(Keep))));

        Assert.Equal(AutoCollectionReasons.NameTaken, error.Code);
        Assert.Equal(PluginErrorKind.Conflict, error.Kind);
        Assert.Contains("keepers (2)", (IReadOnlyList<string>)error.Extensions!["suggestions"]!);
        Assert.Empty(host.Store.Current.Definitions);
        Assert.Empty(Take());
    }

    [Fact]
    public async Task ACreate_MustMatchItsPreview_WithinAFewItems()
    {
        Movie("A");
        using var host = Host();
        var definition = AutoCollectionHost.Def("Keepers", Sorted(Keep));
        var preview = await host.Service.PreviewAsync(_world.Admin, true, new SmartSearchAutoCollectionPreviewRequestDto { Definition = definition }, Ct);

        // A hash no preview gave, or members that moved by more than the margin since: refused, nothing saved.
        var unknown = await Assert.ThrowsAsync<PluginException>(() => host.Service.CreateAsync(_world.Admin, true, new SmartSearchAutoCollectionRequestDto { Definition = definition, AcceptedPreviewHash = "sha256:00" }, Ct));
        Movie("B");
        Movie("C");
        var moved = await Assert.ThrowsAsync<PluginException>(() => host.Service.CreateAsync(_world.Admin, true, new SmartSearchAutoCollectionRequestDto { Definition = definition, AcceptedPreviewHash = preview.PreviewHash }, Ct));
        Assert.Equal("PreviewStale", unknown.Code);
        Assert.Equal("PreviewStale", moved.Code);
        Assert.Equal(PluginErrorKind.Conflict, moved.Kind);
        Assert.Empty(host.Store.Current.Definitions);

        // One item more than the preview is within the margin.
        var again = await host.Service.PreviewAsync(_world.Admin, true, new SmartSearchAutoCollectionPreviewRequestDto { Definition = definition }, Ct);
        Movie("D");
        var created = await host.Service.CreateAsync(_world.Admin, true, new SmartSearchAutoCollectionRequestDto { Definition = definition, AcceptedPreviewHash = again.PreviewHash }, Ct);
        await host.Queue.WhenIdleAsync();
        Assert.Equal(4, Assert.Single(_persistence.Boxes).LinkedChildren.Length);
        Assert.Equal(AutoCollectionRequestKind.CreateNew, Assert.Single(host.Queue.Requests).Kind);
        Assert.NotNull(host.Store.Find(created.Id));
    }

    [Fact]
    public async Task OtherManagers_ChangeOnlyTheLook_TheSafeguardsAndTheManualEdits()
    {
        var a = Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_manager, false, AutoCollectionHost.Def("Keepers", Sorted(Keep)));

        // Another manager: the look, the safeguards and the manual edits; not the name, the source or whether it is on.
        var look = new SmartSearchAutoCollectionDefinitionDto
        {
            Presentation = new SmartSearchAutoCollectionPresentationDto { Overview = "Managed text", OverviewMode = "Managed" },
            Safety = new SmartSearchAutoCollectionSafetyDto { AllowEmpty = true },
            ManualEdits = new SmartSearchAutoCollectionManualEditsDto { PinnedItemIds = [a.Id] }
        };
        var changed = await host.UpdateAsync(_manager2, false, created.Id, look, created.UpdatedUtc);
        Assert.Equal("Managed text", changed.Presentation.Overview);
        Assert.True(changed.Safety.AllowEmpty);
        Assert.Equal(_manager2.Id, changed.UpdatedByUserId);
        Assert.Equal(created.CreatedByUserId, changed.CreatedByUserId);

        var rename = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(_manager2, false, created.Id, new SmartSearchAutoCollectionDefinitionDto { Name = "Mine" }, changed.UpdatedUtc));
        var off = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(_manager2, false, created.Id, new SmartSearchAutoCollectionDefinitionDto { Enabled = false }, changed.UpdatedUtc));
        var source = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(_manager2, false, created.Id, AutoCollectionHost.Def(null, Sorted(Keep, "Descending")), changed.UpdatedUtc));
        var detach = await Assert.ThrowsAsync<PluginException>(() => host.Service.DetachAsync(_manager2, false, created.Id, Ct));
        Assert.All(new[] { rename, off, source, detach }, e => Assert.Equal(PluginErrorKind.Forbidden, e.Kind));
        Assert.All(new[] { rename, off, source, detach }, e => Assert.Equal(AutoCollectionErrors.NotCreatorCode, e.Code));

        // The same name sent back unchanged is no rename.
        await host.UpdateAsync(_manager2, false, created.Id, new SmartSearchAutoCollectionDefinitionDto { Name = "Keepers", Presentation = new SmartSearchAutoCollectionPresentationDto { Overview = "Again" } }, changed.UpdatedUtc);

        // An administrator may switch someone else's source to a copy, never to a saved query.
        var current = await host.Service.GetAsync(_world.Admin, true, created.Id, Ct);
        var reference = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(_world.Admin, true, created.Id, AutoCollectionHost.Def(null, savedQueryId: Guid.NewGuid()), current.UpdatedUtc));
        Assert.Equal("SnapshotOnly", reference.Code);
        var copy = await host.UpdateAsync(_world.Admin, true, created.Id, AutoCollectionHost.Def("Renamed by admin", Sorted(Keep, "Descending")), current.UpdatedUtc);
        Assert.Equal("Snapshot", copy.Source.Mode);
        Assert.Equal("Renamed by admin", copy.Name);
    }

    [Fact]
    public async Task ACopySentBackTheSame_IsNoSourceChange_WhileADifferentQueryIs()
    {
        const string Year = """{"Field":"Year","Op":"Eq","Value":2000}""";
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_manager, false, AutoCollectionHost.Def("Keepers", Sorted(Keep + "," + Year)));
        var taken = host.Store.Find(created.Id)!.Source.SnapshotTakenUtc;
        Assert.NotNull(taken);
        await Task.Delay(20, Ct);

        // Another manager sends the copy back (its rules in another order) with a new look: no source change, so no
        // refusal and no new copy.
        var same = AutoCollectionHost.Def(null, Sorted(Year + "," + Keep));
        same.Presentation = new SmartSearchAutoCollectionPresentationDto { Overview = "Look", OverviewMode = "Managed" };
        var looked = await host.UpdateAsync(_manager2, false, created.Id, same, created.UpdatedUtc);
        Assert.Equal("Look", looked.Presentation.Overview);
        Assert.Equal(taken, host.Store.Find(created.Id)!.Source.SnapshotTakenUtc);

        // A different query is a source change: theirs to refuse, the creator's to make, with a new copy.
        var other = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(_manager2, false, created.Id, AutoCollectionHost.Def(null, Sorted(Keep)), looked.UpdatedUtc));
        Assert.Equal(AutoCollectionErrors.NotCreatorCode, other.Code);
        Assert.Equal(PluginErrorKind.Forbidden, other.Kind);
        var unchanged = await host.UpdateAsync(_manager, false, created.Id, AutoCollectionHost.Def(null, Sorted(Year + "," + Keep)), looked.UpdatedUtc);
        Assert.Equal(taken, host.Store.Find(created.Id)!.Source.SnapshotTakenUtc);
        await host.UpdateAsync(_manager, false, created.Id, AutoCollectionHost.Def(null, Sorted(Keep)), unchanged.UpdatedUtc);
        Assert.True(host.Store.Find(created.Id)!.Source.SnapshotTakenUtc > taken);
    }

    [Fact]
    public async Task TheSameSavedQuerySentBack_IsNoSourceChange_WhileAnotherIs()
    {
        Movie("A");
        using var host = Host();
        var mine = await AddQueryAsync(host, _manager.Id, shared: false);
        var another = await AddQueryAsync(host, _manager.Id, shared: false);
        var created = await host.CreateAsync(_manager, false, AutoCollectionHost.Def("Mine", savedQueryId: mine));

        // Another manager sends the reference back with a new look: no refusal.
        var same = AutoCollectionHost.Def(null, savedQueryId: mine);
        same.Presentation = new SmartSearchAutoCollectionPresentationDto { Overview = "Look", OverviewMode = "Managed" };
        var looked = await host.UpdateAsync(_manager2, false, created.Id, same, created.UpdatedUtc);
        Assert.Equal("Look", looked.Presentation.Overview);
        var other = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(_manager2, false, created.Id, AutoCollectionHost.Def(null, savedQueryId: another), looked.UpdatedUtc));
        Assert.Equal(AutoCollectionErrors.NotCreatorCode, other.Code);

        // The creator sending it back keeps it; another query is a change.
        var kept = await host.UpdateAsync(_manager, false, created.Id, AutoCollectionHost.Def(null, savedQueryId: mine), looked.UpdatedUtc);
        Assert.Equal(mine, kept.Source.SavedQueryId);
        var changed = await host.UpdateAsync(_manager, false, created.Id, AutoCollectionHost.Def(null, savedQueryId: another), kept.UpdatedUtc);
        Assert.Equal(another, changed.Source.SavedQueryId);
    }

    [Fact]
    public async Task AReferenceToSomeoneElsesQuery_IsRefused_WithoutACopy()
    {
        Movie("A");
        using var host = Host();
        var theirs = await AddQueryAsync(host, _manager2.Id, shared: true);
        var mine = await AddQueryAsync(host, _manager.Id, shared: false);

        // The preview reads someone else's shared query as the copy a save would make.
        var preview = await host.Service.PreviewAsync(_manager, false, new SmartSearchAutoCollectionPreviewRequestDto { Definition = AutoCollectionHost.Def("X", savedQueryId: theirs) }, Ct);
        Assert.Equal(1, preview.Total);

        var refused = await Assert.ThrowsAsync<PluginException>(() => host.Service.CreateAsync(_manager, false, new SmartSearchAutoCollectionRequestDto { Definition = AutoCollectionHost.Def("X", savedQueryId: theirs), AcceptedPreviewHash = preview.PreviewHash }, Ct));
        Assert.Equal("NotQueryOwner", refused.Code);
        Assert.Equal(PluginErrorKind.Forbidden, refused.Kind);

        // Their own query: referenced; then detached into a copy, once.
        var created = await host.CreateAsync(_manager, false, AutoCollectionHost.Def("Mine", savedQueryId: mine));
        Assert.Equal("Reference", created.Source.Mode);
        Assert.True(created.Source.QueryVisibleToCaller);
        Assert.Equal("Query", created.Source.QueryName);
        var other = await host.Service.GetAsync(_manager2, false, created.Id, Ct);
        Assert.False(other.Source.QueryVisibleToCaller);
        Assert.Null(other.Source.Query);
        Assert.Null(other.Source.QueryName);

        var detached = await host.Service.DetachAsync(_manager, false, created.Id, Ct);
        Assert.Equal("Snapshot", detached.Source.Mode);
        Assert.NotNull(detached.Source.Query);
        var twice = await Assert.ThrowsAsync<PluginException>(() => host.Service.DetachAsync(_manager, false, created.Id, Ct));
        Assert.Equal("AlreadySnapshot", twice.Code);
    }

    [Fact]
    public async Task DeletingTheCollectionToo_NeedsItsMark_AndACallerWhoMayDeleteIt()
    {
        Movie("A");
        using var host = Host();
        var first = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("One", Sorted(Keep)));
        var second = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Two", Sorted(Keep)));
        Take();

        // A caller the server wouldn't let delete it: refused, nothing deleted.
        _persistence.Deletable = (_, user) => user.Id.Equals(_world.Admin.Id);
        var refused = await Assert.ThrowsAsync<PluginException>(() => host.Service.DeleteAsync(_manager, false, first.Id, true, Ct));
        Assert.Equal("CannotDeleteCollection", refused.Code);
        Assert.Equal(2, _persistence.Boxes.Count);
        Assert.NotNull(host.Store.Find(first.Id));

        // A collection that lost its mark is the user's: refused too.
        var box = _persistence.Boxes.Single(b => b.Id.Equals(second.CollectionId));
        box.ProviderIds = new Dictionary<string, string>();
        var notOurs = await Assert.ThrowsAsync<PluginException>(() => host.Service.DeleteAsync(_world.Admin, true, second.Id, true, Ct));
        Assert.Equal("CannotDeleteCollection", notOurs.Code);
        Assert.Contains(box, _persistence.Boxes);

        // Ours, and the caller may: both go.
        Assert.True(await host.Service.DeleteAsync(_world.Admin, true, first.Id, true, Ct));
        Assert.Equal(["Delete"], Take());
        Assert.DoesNotContain(_persistence.Boxes, b => b.Id.Equals(first.CollectionId));
        Assert.Null(host.Store.Find(first.Id));
    }

    [Fact]
    public async Task Emptying_ACollection_IsHeldForAReview_UnlessItIsAllowed()
    {
        var a = Movie("A");
        var b = Movie("B");
        using var host = Host();
        var held = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Held", Sorted(Keep)));
        var allowed = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Allowed", Sorted(Keep)));
        await host.UpdateAsync(_world.Admin, true, allowed.Id, new SmartSearchAutoCollectionDefinitionDto { Safety = new SmartSearchAutoCollectionSafetyDto { AllowEmpty = true } }, allowed.UpdatedUtc);
        Take();
        _world.Core.Remove(a);
        _world.Core.Remove(b);

        var run = await host.RefreshAsync(_world.Admin, true, held.Id);

        Assert.Equal(nameof(SmartSearchAutoCollectionStatus.Guarded), run.State.Status);
        Assert.Equal(GuardReasons.Empty, run.State.Reason);
        Assert.Equal(2, run.State.PendingGuard!.WouldRemoveCount);
        Assert.Equal([GuardReasons.Empty], run.State.PendingGuard.Reasons);
        Assert.Equal(2, _persistence.Boxes.Single(x => x.Id.Equals(held.CollectionId)).LinkedChildren.Length);
        Assert.Empty(Take());

        // Allowed to be empty: the dead links go.
        var emptied = await host.RefreshAsync(_world.Admin, true, allowed.Id);
        Assert.Equal("Ok", emptied.State.Status);
        Assert.Empty(_persistence.Boxes.Single(x => x.Id.Equals(allowed.CollectionId)).LinkedChildren);
    }

    [Fact]
    public async Task TheListAndARefresh_TakeAnApiKey_AndShowNoQueryToIt()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));

        var list = await host.Service.ListAsync(null, true, null, Ct);
        var okOnly = await host.Service.ListAsync(_world.Admin, true, "ok", Ct);
        var none = await host.Service.ListAsync(_world.Admin, true, "Guarded", Ct);
        var bad = await Assert.ThrowsAsync<PluginException>(() => host.Service.ListAsync(_world.Admin, true, "Sleeping", Ct));
        var queued = await host.Service.RefreshAsync(null, true, created.Id, Ct);
        await host.Queue.WhenIdleAsync();

        var row = Assert.Single(list);
        Assert.Null(row.Source.Query);
        Assert.Null(row.ManualEdits);
        Assert.Single(okOnly);
        Assert.Empty(none);
        Assert.Equal(PluginErrorKind.Invalid, bad.Kind);
        Assert.Equal(created.Id, queued.Id);
        Assert.Equal(SmartSearchAutoCollectionRunReason.Manual, host.Queue.Requests[^1].Reason);
    }

    [Fact]
    public async Task ACollectionThatLostItsMark_IsNeverWritten_UntilRecreatedUnderItsOwnId()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        Take();
        var box = Assert.Single(_persistence.Boxes);
        box.ProviderIds = new Dictionary<string, string>();
        box.Name = "Users own";
        Movie("B");

        var run = await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Equal(nameof(SmartSearchAutoCollectionStatus.TetherMissing), run.State.Status);
        Assert.Empty(Take());

        // Recreated: a new collection, the user's left alone.
        var recreated = await host.Service.RecreateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionRecreateRequestDto(), Ct);
        await host.Queue.WhenIdleAsync();
        Assert.Equal(2, _persistence.Boxes.Count);
        Assert.NotEqual(box.Id, host.Store.Find(created.Id)!.CollectionId);
        Assert.Equal(2, Members(_persistence.Boxes[1]).Count);
        Assert.Single(box.LinkedChildren);
        Assert.Equal(created.Id, recreated.Id);
    }

    [Fact]
    public async Task ADeletedCollection_IsOrphaned_UntilRecreated()
    {
        var a = Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        _persistence.Boxes.Clear();
        Take();

        var run = await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Equal(nameof(SmartSearchAutoCollectionStatus.Orphaned), run.State.Status);
        Assert.Empty(Take());

        var recreated = await host.Service.RecreateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionRecreateRequestDto { Name = "Keepers again" }, Ct);
        Assert.Equal("Ok", recreated.State.Status);
        Assert.Equal("Keepers again", recreated.Name);
        Assert.Equal(Assert.Single(_persistence.Boxes).Id, recreated.CollectionId);
        Assert.Equal([a.Id], Members(_persistence.Boxes[0]));
        var again = await Assert.ThrowsAsync<PluginException>(() => host.Service.RecreateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionRecreateRequestDto(), Ct));
        Assert.Equal("NotRecreatable", again.Code);
    }

    [Fact]
    public async Task ACreateThatFailedAfterTheServerMadeTheCollection_BindsIt()
    {
        var a = Movie("A");
        using var host = Host();
        _persistence.CreateThrowsAfterMaking = true;

        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));

        var box = Assert.Single(_persistence.Boxes);
        Assert.Equal(box.Id, created.CollectionId);
        Assert.Equal(1, _persistence.Creates);

        // The next run fills the members the failed create didn't add.
        _persistence.Calls.Clear();
        await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Equal([a.Id], Members(box));
    }

    [Fact]
    public async Task ADefinitionANewerVersionWrote_IsKeptAsItIs_AndNeverWritten()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        Take();
        using var extra = JsonDocument.Parse("{\"Weight\":3}");
        await host.Store.UpdateDefinitionAsync(
            created.Id,
            d => d with
            {
                Source = d.Source with { Mode = new StoredEnum<SmartSearchAutoCollectionSourceMode>(SmartSearchAutoCollectionSourceMode.Unknown, "Federated") },
                Extra = new Dictionary<string, JsonElement> { ["Future"] = extra.RootElement.Clone() }
            },
            Ct);
        Movie("B");

        var refresh = await Assert.ThrowsAsync<PluginException>(() => host.Service.RefreshAsync(_world.Admin, true, created.Id, Ct));
        var run = await host.Runner.RunAsync(created.Id, SmartSearchAutoCollectionRunReason.Daily, Ct);

        Assert.Equal(AutoCollectionReasons.NewerDefinition, refresh.Code);
        Assert.Equal(SmartSearchAutoCollectionStatus.NeedsNewerVersion, run.Status.Value);
        Assert.Empty(Take());
        var kept = host.Store.Find(created.Id)!;
        Assert.Equal("Federated", kept.Source.Mode.ToString());
        Assert.True(kept.Extra!.ContainsKey("Future"));
    }

    [Fact]
    public async Task AStoreANewerVersionWrote_IsReadOnly_AndItsCollectionsStayIdle()
    {
        Movie("A");
        Guid id;
        var data = _folder.Combine("data");
        using (var host = Host())
        {
            id = (await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)))).Id;
        }

        var file = Path.Combine(data, AutoCollectionStore.FileName);
        var text = await File.ReadAllTextAsync(file, Ct);
        await File.WriteAllTextAsync(file, text.Replace("\"Version\": 1", "\"Version\": 99", StringComparison.Ordinal).Replace("\"Version\":1", "\"Version\":99", StringComparison.Ordinal), Ct);
        Take();
        Movie("B");

        using var newer = Host();
        Assert.True(newer.Store.IsReadOnly);
        var refresh = await Assert.ThrowsAsync<PluginException>(() => newer.Service.RefreshAsync(_world.Admin, true, id, Ct));
        var run = await newer.Runner.RunAsync(id, SmartSearchAutoCollectionRunReason.Daily, Ct);
        var create = await Assert.ThrowsAsync<PluginException>(() => newer.Service.CreateAsync(_world.Admin, true, new SmartSearchAutoCollectionRequestDto { Definition = AutoCollectionHost.Def("Other", Sorted(Keep)), AcceptedPreviewHash = "x" }, Ct));

        Assert.Equal(PluginErrorKind.Conflict, refresh.Kind);
        Assert.NotEqual(SmartSearchAutoCollectionStatus.Ok, run.Status.Value);
        Assert.Equal(AutoCollectionReasons.StoreReadOnly, create.Code);
        Assert.Empty(Take());
    }

    [Fact]
    public async Task WithAutoCollectionsOff_NothingIsCreatedOrRun()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        host.Settings = new AutoCollectionSettings { Enabled = false };

        var create = await Assert.ThrowsAsync<PluginException>(() => host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Other", Sorted(Keep))));
        var refresh = await Assert.ThrowsAsync<PluginException>(() => host.Service.RefreshAsync(_world.Admin, true, created.Id, Ct));
        var all = await Assert.ThrowsAsync<PluginException>(() => host.Service.RefreshAllAsync(_world.Admin, true, Ct));

        Assert.All(new[] { create, refresh, all }, e => Assert.Equal(PluginErrorKind.FeatureDisabled, e.Kind));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\"},\"AcceptedPreviewHash\":\"h\"}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\",\"Source\":{\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\"}}}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\",\"Source\":{\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\",\"Query\":{}}},\"AcceptedPreviewHash\":\"h\"}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\",\"Source\":{\"Mode\":\"Snapshot\",\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\"}},\"AcceptedPreviewHash\":\"h\"}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\",\"Source\":{\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\"},\"Presentation\":{\"SortMode\":\"Random\"}},\"AcceptedPreviewHash\":\"h\"}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\",\"Source\":{\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\"}},\"AcceptedPreviewHash\":\"h\",\"IfUpdatedAt\":\"2026-11-01T00:00:00Z\"}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\",\"Name\":\"Y\",\"Source\":{\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\"}},\"AcceptedPreviewHash\":\"h\"}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\",\"Colour\":1,\"Source\":{\"SavedQueryId\":\"11111111-1111-1111-1111-111111111111\"}},\"AcceptedPreviewHash\":\"h\"}", false)]
    [InlineData("{\"Definition\":{\"Name\":\"X\"}}", true)]
    [InlineData("{\"Definition\":{\"Name\":\"X\"},\"IfUpdatedAt\":\"yesterday\"}", true)]
    [InlineData("{\"Definition\":{\"ManualEdits\":{\"PinnedItemIds\":[\"x\"]}},\"IfUpdatedAt\":\"2026-11-01T00:00:00Z\"}", true)]
    [InlineData("{\"Definition\":{\"Safety\":{\"MaxRemovalPercent\":0}},\"IfUpdatedAt\":\"2026-11-01T00:00:00Z\"}", true)]
    [InlineData("{\"Definition\":[],\"IfUpdatedAt\":\"2026-11-01T00:00:00Z\"}", true)]
    public void TheBody_IsReadStrictly(string body, bool isChange)
    {
        var error = Assert.Throws<PluginException>(() => Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.ReadAutoCollection(Encoding.UTF8.GetBytes(body), isChange));

        Assert.Equal(Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.InvalidRequestCode, error.Code);
    }

    [Fact]
    public void AGoodBody_IsRead()
    {
        var create = Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.ReadAutoCollection(
            Encoding.UTF8.GetBytes("{\"definition\":{\"name\":\"X\",\"source\":{\"savedQueryId\":\"11111111-1111-1111-1111-111111111111\"},\"presentation\":{\"sortMode\":\"sortname\",\"lock\":false}},\"acceptedPreviewHash\":\"h\",\"duplicateSourceQuery\":true}"),
            false);
        var change = Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.ReadAutoCollection(
            Encoding.UTF8.GetBytes("{\"Definition\":{\"EvaluationUserId\":null,\"ManualEdits\":{\"ExcludedItemIds\":[\"11111111-1111-1111-1111-111111111111\"]},\"Safety\":{\"MaxRemovalPercent\":null}},\"IfUpdatedAt\":\"2026-11-01T10:00:00.1234567Z\"}"),
            true);
        var recreate = Jellyfin.Plugin.SmartSearch.Api.SmartSearchRequestBinder.ReadRecreate([]);

        Assert.Equal("SortName", create.Definition!.Presentation!.SortMode);
        Assert.Equal("Reference", create.Definition.Source!.Mode);
        Assert.False(create.Definition.Presentation.Lock);
        Assert.True(create.DuplicateSourceQuery);
        Assert.Equal(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc).AddTicks(1234567), change.IfUpdatedAt);
        Assert.Equal(DateTimeKind.Utc, change.IfUpdatedAt!.Value.Kind);
        Assert.Single(change.Definition!.ManualEdits!.ExcludedItemIds!);
        Assert.True(change.Definition.HasEvaluationUserId);
        Assert.True(change.Definition.Safety!.HasMaxRemovalPercent);
        Assert.Null(recreate.Name);
    }

    private static string Sorted(string rules, string order = "Ascending")
        => QueryPlannerTests.Q(rules, "\"Movie\"", extra: ",\"Sort\":[{\"Key\":\"SortName\",\"Order\":\"" + order + "\"}]");

    private static List<Guid> Members(BoxSet box) => box.LinkedChildren.Select(l => l.ItemId!.Value).ToList();

    private static async Task<Guid> AddQueryAsync(AutoCollectionHost host, Guid owner, bool shared)
    {
        var id = Guid.NewGuid();
        using var query = JsonDocument.Parse(Sorted(Keep));
        var record = new Storage.SavedQueryRecord { Id = id, OwnerUserId = owner, Name = "Query", Shared = shared, Query = query.RootElement.Clone(), CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
        await host.Queries.UpdateAsync(d => Storage.SavedQueryStore.WithQueries(d, [.. d.Queries, record]), Ct);
        return id;
    }

    private AutoCollectionHost Host()
    {
        var users = new Mock<IUserManager>();
        var all = new[] { _world.Admin, _world.Kid, _manager, _manager2 };
        users.SetupGet(m => m.Users).Returns(all);
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => all.FirstOrDefault(u => u.Id.Equals(id)));
        return new AutoCollectionHost(_world, _harness, _folder.Combine("data"), _persistence, users.Object);
    }

    private List<string> Take()
    {
        var calls = _persistence.Calls.ToList();
        _persistence.Calls.Clear();
        return calls;
    }

    private FakeRow Movie(string name)
        => _world.Add(BaseItemKind.Movie, name, _world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });
}
