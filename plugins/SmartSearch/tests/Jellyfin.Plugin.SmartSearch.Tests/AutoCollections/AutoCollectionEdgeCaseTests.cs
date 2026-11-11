using System;
using System.Collections.Generic;
using System.IO;
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
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Execution;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;
using User = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// Whole runs at the edges: a save that fails leaves the collection as it was and the next run writes again; a query edit
/// that matches nothing is held; a create the engine refused is made by the next run; a rename in the server's editor is kept unless the definition changed since; a pin of a
/// kind a collection doesn't hold is left out; a document field from a newer version stops everything; null entries in
/// the file are dropped; pins and excludes are capped; a taken name names the collection in the way only to a caller who
/// can see it.
/// </summary>
public sealed class AutoCollectionEdgeCaseTests : IDisposable
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";
    private const string Nothing = """{"Field":"Overview","Op":"Contains","Value":"zzz-nothing"}""";

    private readonly PlannerWorld _world = new();
    private readonly EngineHarness _harness;
    private readonly TempDirectory _folder = new();
    private readonly RecordingPersistence _persistence = new();
    private readonly User _manager = PlannerWorld.NewUser("manager");

    public AutoCollectionEdgeCaseTests()
    {
        _harness = new EngineHarness(_world);
        AutoCollectionHost.SetUpLibrary(_world, _persistence);
        _manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        _manager.SetPermission(PermissionKind.EnableAllFolders, true);
    }

    private static CancellationToken Ct => CancellationToken.None;

    public void Dispose()
    {
        _harness.Dispose();
        _folder.Dispose();
    }

    [Fact]
    public async Task ASaveThatFails_LeavesTheCollectionAsItWas_AndTheNextRunWritesAgain()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        var box = Assert.Single(_persistence.Boxes);
        var b = Movie("B");
        _persistence.SaveFails = _ => true;

        var failed = await host.RefreshAsync(_world.Admin, true, created.Id);

        Assert.Equal(nameof(SmartSearchAutoCollectionStatus.Error), failed.State.Status);
        Assert.DoesNotContain(b.Id, Members(box));

        _persistence.SaveFails = _ => false;
        _persistence.Calls.Clear();
        var again = await host.RefreshAsync(_world.Admin, true, created.Id);

        Assert.Equal("Ok", again.State.Status);
        Assert.Contains("Save", _persistence.Calls);
        Assert.Contains(b.Id, Members(box));
    }

    [Fact]
    public async Task ACreateTheEngineRefused_IsMadeByTheNextRun_WhileOneThatNeverAskedWaits()
    {
        Movie("A");
        using var host = Host();

        // The engine refuses the create's run (too broad by then), after the preview was accepted and the definition saved.
        host.Queue.OnRequest = r => _harness.Settings = r.Kind == AutoCollectionRequestKind.CreateNew ? new EngineSettings(MaxCandidates: 0) : new EngineSettings();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));

        Assert.Equal(nameof(SmartSearchAutoCollectionStatus.Error), created.State.Status);
        Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, host.State.Find(created.Id)!.Reason);
        Assert.True(host.State.Find(created.Id)!.CreatePending);
        Assert.Empty(_persistence.Boxes);

        // An imported definition (no create asked for) stays NotCreated through the same runs.
        var imported = host.Store.Find(created.Id)! with { Id = Guid.NewGuid(), Name = "Imported", CollectionId = null };
        await host.Store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [.. d.Definitions, imported]), Ct);

        var run = await host.RefreshAsync(_world.Admin, true, created.Id);
        var waiting = await host.RefreshAsync(_world.Admin, true, imported.Id);

        Assert.Equal("Ok", run.State.Status);
        var box = Assert.Single(_persistence.Boxes);
        Assert.Equal("Keepers", box.Name);
        Assert.Equal(box.Id, host.Store.Find(created.Id)!.CollectionId);
        Assert.False(host.State.Find(created.Id)!.CreatePending);
        Assert.Equal(RunOutcome.Created, host.State.Find(created.Id)!.History[0].Outcome);
        Assert.Equal(nameof(SmartSearchAutoCollectionStatus.NotCreated), waiting.State.Status);
    }

    [Fact]
    public async Task AQueryEditThatMatchesNothing_IsHeld_AndTheCollectionKeepsItsMembers()
    {
        Movie("A");
        Movie("B");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        var box = Assert.Single(_persistence.Boxes);
        _persistence.Calls.Clear();

        var updated = await host.UpdateAsync(_world.Admin, true, created.Id, AutoCollectionHost.Def(null, Sorted(Nothing)), created.UpdatedUtc);

        Assert.Equal(2, box.LinkedChildren.Length);
        Assert.Equal(nameof(SmartSearchAutoCollectionStatus.Guarded), updated.State.Status);
        Assert.Equal(GuardReasons.Empty, updated.State.Reason);
        Assert.Equal(2, updated.State.PendingGuard!.WouldRemoveCount);
        Assert.DoesNotContain("Save", _persistence.Calls);
    }

    [Fact]
    public async Task ARenameInTheServersEditor_IsKept_UnlessTheDefinitionChangedSince()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        var box = Assert.Single(_persistence.Boxes);

        // Renamed natively, the definition unchanged since its last run: the definition takes the name, nothing is written.
        box.Name = "Renamed by a user";
        _persistence.Calls.Clear();
        var adopted = await host.RefreshAsync(_world.Admin, true, created.Id);

        Assert.Equal("Renamed by a user", adopted.Name);
        Assert.Equal("Renamed by a user", box.Name);
        Assert.True(adopted.UpdatedUtc > created.UpdatedUtc);
        Assert.DoesNotContain("Save", _persistence.Calls);

        // Renamed natively again, but the definition changed after its last run: its name is written back.
        box.Name = "Second rename";
        var changed = await host.UpdateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { Presentation = new SmartSearchAutoCollectionPresentationDto { OverviewMode = "Managed", Overview = "Text" } }, adopted.UpdatedUtc);

        Assert.Equal("Renamed by a user", changed.Name);
        Assert.Equal("Renamed by a user", box.Name);

        // A name that isn't one is never taken.
        box.Name = "   ";
        var blank = await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Equal("Renamed by a user", blank.Name);
        Assert.Equal("Renamed by a user", box.Name);

        // Nor is one another auto-collection has (in any case): names stay unique among them, and the run writes its own back.
        await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Others", Sorted(Keep)));
        box.Name = "OTHERS";
        var clash = await host.RefreshAsync(_world.Admin, true, created.Id);
        Assert.Equal("Renamed by a user", clash.Name);
        Assert.Equal("Renamed by a user", box.Name);
        Assert.Equal(["Others", "Renamed by a user"], host.Store.Current.Definitions.Select(d => d.Name).Order());
    }

    [Fact]
    public async Task APinOfAKindACollectionDoesntHold_IsNeverLinked()
    {
        Movie("A");
        var legacy = _world.Add(BaseItemKind.BoxSet, "Legacy box set", _world.Movies, s => s.Overview = "x");
        var pinned = Movie("Pinned", overview: "other");
        using var host = Host();
        var definition = AutoCollectionHost.Def("Keepers", Sorted(Keep));
        definition.ManualEdits = new SmartSearchAutoCollectionManualEditsDto { PinnedItemIds = [legacy.Id, pinned.Id] };

        var created = await host.CreateAsync(_world.Admin, true, definition);
        var box = Assert.Single(_persistence.Boxes);

        Assert.DoesNotContain(legacy.Id, Members(box));
        Assert.Contains(pinned.Id, Members(box));
        Assert.Equal("Ok", created.State.Status);
    }

    [Fact]
    public async Task ADocumentFieldFromANewerVersion_MakesTheStoreReadOnly_AndNothingRunsOrIsWritten()
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
        await File.WriteAllTextAsync(file, text.Insert(text.IndexOf('{', StringComparison.Ordinal) + 1, "\"PausedAll\": true,"), Ct);
        Movie("B");
        _persistence.Calls.Clear();

        using var reopened = Host();
        Assert.True(reopened.Store.IsReadOnly);
        var refresh = await Assert.ThrowsAsync<PluginException>(() => reopened.Service.RefreshAsync(_world.Admin, true, id, Ct));
        var run = await reopened.Runner.RunAsync(id, SmartSearchAutoCollectionRunReason.Daily, Ct);
        var create = await Assert.ThrowsAsync<PluginException>(() => reopened.Service.CreateAsync(_world.Admin, true, new SmartSearchAutoCollectionRequestDto { Definition = AutoCollectionHost.Def("Other", Sorted(Keep)), AcceptedPreviewHash = "x" }, Ct));
        var write = await Assert.ThrowsAsync<PluginException>(() => reopened.Store.UpdateDefinitionAsync(id, d => d with { Name = "Changed" }, Ct));

        Assert.Equal(PluginErrorKind.Conflict, refresh.Kind);
        Assert.Equal(SmartSearchAutoCollectionStatus.NeedsNewerVersion, run.Status.Value);
        Assert.Equal(AutoCollectionReasons.StoreReadOnly, create.Code);
        Assert.Equal(AutoCollectionReasons.StoreReadOnly, write.Code);
        Assert.Empty(_persistence.Calls);
        Assert.Contains("PausedAll", await File.ReadAllTextAsync(file, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NullEntriesInTheFile_AreDropped_AndTheStoreKeepsWorking()
    {
        Movie("A");
        var data = _folder.Combine("data");
        Directory.CreateDirectory(data);
        var old = Guid.NewGuid();
        var json = "{\"Version\":1,\"KnownUsers\":null,\"Definitions\":[null,{\"Id\":\"" + old + "\",\"Name\":\"Old\",\"Source\":null,\"Presentation\":null,\"Safety\":null,"
            + "\"ManualEdits\":{\"Pinned\":null,\"Excluded\":[null]},\"CreatedByUserId\":\"" + _world.Admin.Id + "\",\"CreatedUtc\":\"2026-11-01T00:00:00Z\",\"UpdatedUtc\":\"2026-11-01T00:00:00Z\"}]}";
        await File.WriteAllTextAsync(Path.Combine(data, AutoCollectionStore.FileName), json, Ct);
        using var host = Host();

        Assert.False(host.Store.IsReadOnly);
        var kept = Assert.Single(host.Store.Current.Definitions);
        Assert.Empty(kept.ManualEdits.Pinned);
        Assert.Empty(kept.ManualEdits.Excluded);
        Assert.NotNull(kept.Presentation);
        Assert.Single(await host.Service.ListAsync(_world.Admin, true, null, Ct));
        var run = await host.Runner.RunAsync(old, SmartSearchAutoCollectionRunReason.Manual, Ct);
        Assert.NotEqual(SmartSearchAutoCollectionStatus.Ok, run.Status.Value);

        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));

        Assert.Equal("Ok", created.State.Status);
        Assert.Equal(2, host.Store.Current.Definitions.Count);
        Assert.DoesNotContain("[null", await File.ReadAllTextAsync(Path.Combine(data, AutoCollectionStore.FileName), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoreThanAThousandPinsOrExcludes_AreRefused()
    {
        Movie("A");
        using var host = Host();
        var created = await host.CreateAsync(_world.Admin, true, AutoCollectionHost.Def("Keepers", Sorted(Keep)));
        var tooMany = Enumerable.Range(0, AutoCollectionManualEdits.MaxEntries + 1).Select(_ => Guid.NewGuid()).ToList();

        var pins = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { ManualEdits = new SmartSearchAutoCollectionManualEditsDto { PinnedItemIds = tooMany } }, created.UpdatedUtc));
        var excludes = await Assert.ThrowsAsync<PluginException>(() => host.UpdateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { ManualEdits = new SmartSearchAutoCollectionManualEditsDto { ExcludedItemIds = tooMany } }, created.UpdatedUtc));
        var newOne = AutoCollectionHost.Def("Other", Sorted(Keep));
        newOne.ManualEdits = new SmartSearchAutoCollectionManualEditsDto { PinnedItemIds = tooMany };
        var create = await Assert.ThrowsAsync<PluginException>(() => host.Service.CreateAsync(_world.Admin, true, new SmartSearchAutoCollectionRequestDto { Definition = newOne, AcceptedPreviewHash = "x" }, Ct));

        Assert.All(new[] { pins, excludes, create }, e => Assert.Equal(AutoCollectionService.TooManyEntriesCode, e.Code));
        Assert.All(new[] { pins, excludes, create }, e => Assert.Equal(PluginErrorKind.Invalid, e.Kind));
        var exactly = await host.UpdateAsync(_world.Admin, true, created.Id, new SmartSearchAutoCollectionDefinitionDto { ManualEdits = new SmartSearchAutoCollectionManualEditsDto { ExcludedItemIds = tooMany.Skip(1).ToList() } }, created.UpdatedUtc);
        Assert.Equal(AutoCollectionManualEdits.MaxEntries, exactly.ManualEdits!.Excluded!.Count);
    }

    [Fact]
    public async Task ATakenName_NamesTheCollectionInTheWay_OnlyToACallerWhoCanSeeIt()
    {
        Movie("A");
        var hidden = new BoxSet { Id = Guid.NewGuid(), Name = "Keepers", Path = Path.Combine(_folder.Path, "data", "collections", "Keepers [boxset]") };
        _persistence.Boxes.Add(hidden);
        _world.Core.Library.Setup(l => l.GetItemById<BaseItem>(hidden.Id, It.Is<User?>(u => u != null && u.Id.Equals(_manager.Id)))).Returns((BaseItem?)null);
        using var host = Host();
        var request = new SmartSearchAutoCollectionCheckNameRequestDto { Name = "keepers" };

        var asManager = await host.Service.CheckNameAsync(_manager, false, request, Ct);
        var asAdmin = await host.Service.CheckNameAsync(_world.Admin, true, request, Ct);
        var create = await Assert.ThrowsAsync<PluginException>(() => host.CreateAsync(_manager, false, AutoCollectionHost.Def("keepers", Sorted(Keep))));

        Assert.False(asManager.Available);
        var conflict = Assert.Single(asManager.Conflicts);
        Assert.Equal("ExistingCollection", conflict.Kind);
        Assert.Null(conflict.CollectionId);
        Assert.Null(conflict.Name);
        Assert.Contains("keepers (2)", asManager.Suggestions);

        var seen = Assert.Single(asAdmin.Conflicts);
        Assert.Equal(hidden.Id, seen.CollectionId);
        Assert.Equal("Keepers", seen.Name);

        Assert.Equal(AutoCollectionReasons.NameTaken, create.Code);
        var problem = Assert.Single((IEnumerable<AutoCollectionErrors.NameConflictProblem>)create.Extensions!["conflicts"]!);
        Assert.Null(problem.Id);
        Assert.Null(problem.Name);
        Assert.Contains("keepers (2)", (IReadOnlyList<string>)create.Extensions["suggestions"]!);
    }

    private static string Sorted(string rules)
        => QueryPlannerTests.Q(rules, "\"Movie\"", extra: ",\"Sort\":[{\"Key\":\"SortName\",\"Order\":\"Ascending\"}]");

    private static List<Guid> Members(BoxSet box) => box.LinkedChildren.Select(l => l.ItemId!.Value).ToList();

    private AutoCollectionHost Host()
    {
        var users = new Mock<IUserManager>();
        var all = new[] { _world.Admin, _world.Kid, _manager };
        users.SetupGet(m => m.Users).Returns(all);
        users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => all.FirstOrDefault(u => u.Id.Equals(id)));
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
