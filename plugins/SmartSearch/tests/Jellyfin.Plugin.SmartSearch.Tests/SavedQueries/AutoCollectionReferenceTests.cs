using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Api;
using Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.SavedQueries.SavedQueryTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>
/// The auto-collections' definitions as the saved queries' reference guard, on every write of a saved query: a delete,
/// an edit, sharing, an owner change, an import, a snapshot restore, a remap and a user's deletion. Refused while an
/// auto-collection follows the query (a dry run lists them), done when forced, and the auto-collections then run again
/// and show what is missing or changed. The lock order: the definitions' gate, then the saved queries' lock, and the
/// reference reads take neither.
/// </summary>
public sealed class AutoCollectionReferenceTests
{
    private const string Other = """{"Field":"Overview","Op":"Contains","Value":"other"}""";

    private static readonly string BoxSets = QueryPlannerTests.Q(Keep, "\"BoxSet\"");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADelete_IsRefusedWhileFollowed_AndDetachingGivesEachItsCopyFirst()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");
        var path = "/SmartSearch/Queries/" + query.Id;

        var problem = await ProblemAsync(host, "DELETE", path, manager, null, HttpStatusCode.Conflict, SavedQueryErrors.ReferencedCode);
        Assert.Equal(definition, Guid.Parse(Assert.Single(problem.GetProperty("autoCollections").EnumerateArray()).GetProperty("id").GetString()!));
        Assert.NotNull(host.Store.Find(query.Id));

        var stored = host.Store.Find(query.Id)!.Query.GetRawText();
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("DELETE", path + "?detachAutoCollections=true", manager)).Status);
        Assert.Null(host.Store.Find(query.Id));
        var detached = host.AutoCollections!.Store.Find(definition)!;
        Assert.Equal(SmartSearchAutoCollectionSourceMode.Snapshot, detached.Source.Mode.Value);
        Assert.Equal(stored, detached.Source.Snapshot!.Value.GetRawText());
        Assert.Equal(0, host.Guard.CountReferences(query.Id));
        Assert.Equal("Ok", (await host.AutoCollections.RefreshAsync(manager, false, definition)).State.Status);
    }

    [Fact]
    public async Task AnEdit_ThatBreaksOrNeedsAttention_IsRefused_ANameOrSharingGoesThrough_AndTheCollectionRunsAgain()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");
        var path = "/SmartSearch/Queries/" + query.Id;

        // The same query: a name, a description or sharing changes no collection.
        var renamed = await host.OkAsync("PUT", path, manager, Edit(host, query.Id, "Renamed", shared: true));
        Assert.Empty(renamed.GetProperty("AffectedAutoCollections").EnumerateArray());
        Assert.True(host.Store.Find(query.Id)!.Shared);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("PUT", path, manager, Edit(host, query.Id, "Renamed"))).Status);

        // A collection can't hold collections: refused, nothing written.
        var problem = await ProblemAsync(host, "PUT", path, manager, Edit(host, query.Id, "Renamed", BoxSets), HttpStatusCode.Conflict, SavedQueryErrors.BreaksAutoCollectionsCode);
        Assert.Contains("can't hold collections", Assert.Single(problem.GetProperty("autoCollections").EnumerateArray()).GetProperty("messages")[0].GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("BoxSet", host.Store.Find(query.Id)!.Query.GetRawText(), StringComparison.Ordinal);

        // A watch-state rule: the collection would pause until someone picks whose, so it asks first.
        await ProblemAsync(host, "PUT", path, manager, Edit(host, query.Id, "Renamed", Q(PerUser)), HttpStatusCode.Conflict, SavedQueryErrors.NeedsAttentionCode);
        var acknowledged = await host.OkAsync("PUT", path, manager, Edit(host, query.Id, "Renamed", Q(PerUser), extra: ",\"AcknowledgeAutoCollectionWarnings\":true"));
        Assert.Single(acknowledged.GetProperty("AffectedAutoCollections").EnumerateArray());
        await auto.Queue.WhenIdleAsync();
        Assert.Equal(SmartSearchAutoCollectionStatus.EvaluationUserMissing, auto.State.Find(definition)!.Status.Value);

        // Another query: saved, and the collection runs again with it.
        auto.Queue.Requests.Clear();
        var changed = await host.OkAsync("PUT", path, manager, Edit(host, query.Id, "Renamed", Q(Other)));
        Assert.Single(changed.GetProperty("AffectedAutoCollections").EnumerateArray());
        await auto.Queue.WhenIdleAsync();
        Assert.Contains(auto.Queue.Requests, r => r.DefinitionId.Equals(definition) && r.Reason == SmartSearchAutoCollectionRunReason.Saved);
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, auto.State.Find(definition)!.Status.Value);
    }

    [Fact]
    public async Task ACreatorWhoLostCollectionManagement_CantChangeTheCollectionThroughTheQuery_ButMayRenameOrDetachThenSave()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");
        var path = "/SmartSearch/Queries/" + query.Id;
        var before = host.Store.Find(query.Id)!.Query.GetRawText();
        manager.SetPermission(PermissionKind.EnableCollectionManagement, false);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("PUT", path, manager, Edit(host, query.Id, "Renamed", shared: true))).Status);
        var problem = await ProblemAsync(host, "PUT", path, manager, Edit(host, query.Id, "Renamed", Q(Other)), HttpStatusCode.Forbidden, SavedQueryErrors.ManagerRequiredCode);
        Assert.Single(problem.GetProperty("autoCollections").EnumerateArray());
        Assert.Equal(before, host.Store.Find(query.Id)!.Query.GetRawText());

        // Detach these collections, then save: each keeps the query as it was, the query takes the change.
        var saved = await host.OkAsync("PUT", path, manager, Edit(host, query.Id, "Renamed", Q(Other), extra: ",\"DetachAutoCollections\":true"));
        Assert.Empty(saved.GetProperty("AffectedAutoCollections").EnumerateArray());
        Assert.Contains("\"other\"", host.Store.Find(query.Id)!.Query.GetRawText(), StringComparison.Ordinal);
        var detached = auto.Store.Find(definition)!;
        Assert.Equal(SmartSearchAutoCollectionSourceMode.Snapshot, detached.Source.Mode.Value);
        Assert.Equal(before, detached.Source.Snapshot!.Value.GetRawText());
        Assert.Equal(manager.Id, detached.UpdatedByUserId);
        Assert.Equal(0, host.Guard.CountReferences(query.Id));

        // An administrator may change a followed query without the collection flag.
        var (adminQuery, adminDefinition) = await FollowedAsync(host, host.Admin, "Admin's");
        var changed = await host.OkAsync("PUT", "/SmartSearch/Queries/" + adminQuery.Id, host.Admin, Edit(host, adminQuery.Id, "Admin's", Q(Other)));
        Assert.Equal(adminDefinition, Guid.Parse(Assert.Single(changed.GetProperty("AffectedAutoCollections").EnumerateArray()).GetProperty("Id").GetString()!));
    }

    [Fact]
    public async Task AnOwnerChange_IsRefusedWhileFollowed_AndForcedTheCollectionStopsAsTheQueryIsNoLongerItsCreators()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");
        var path = "/SmartSearch/Admin/Queries/" + query.Id + "/Owner";
        var body = "{\"UserId\":\"" + host.Twin.Id + "\"}";

        var problem = await ProblemAsync(host, "POST", path, host.Admin, body, HttpStatusCode.Conflict, SavedQueryErrors.BreaksAutoCollectionsCode);
        Assert.Contains("reassign the auto-collection", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Single(problem.GetProperty("autoCollections").EnumerateArray());
        Assert.Equal(manager.Id, host.Store.Find(query.Id)!.OwnerUserId);

        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("POST", path + "?force=true", host.Admin, body)).Status);
        Assert.Equal(host.Twin.Id, host.Store.Find(query.Id)!.OwnerUserId);
        await auto.Queue.WhenIdleAsync();
        var state = auto.State.Find(definition)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.QueryInvalid, state.Status.Value);
        Assert.Equal(AutoCollectionReasons.SourceOwnerChanged, state.Reason);
    }

    [Fact]
    public async Task AReplacingImport_ListsWhatItBreaks_IsRefusedWithoutForce_AndForcedTheCollectionStopsAsQueryMissing()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var export = await ExportAsync(host); // before the query
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");

        var dry = await host.OkAsync("POST", "/SmartSearch/Admin/Import?mode=Replace", host.Admin, export);
        Assert.Equal([definition], Broken(dry));

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Import?mode=Replace&dryRun=false", host.Admin, export);
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.Equal(SavedQueryErrors.BreaksAutoCollectionsCode, problem.GetProperty("code").GetString());
        Assert.NotNull(host.Store.Find(query.Id));

        var withQuery = await ExportAsync(host);
        var forced = await host.OkAsync("POST", "/SmartSearch/Admin/Import?mode=Replace&dryRun=false&force=true", host.Admin, export);
        Assert.Equal([definition], Broken(forced));
        Assert.Null(host.Store.Find(query.Id));
        await auto.Queue.WhenIdleAsync();
        Assert.Equal(SmartSearchAutoCollectionStatus.QueryMissing, auto.State.Find(definition)!.Status.Value);

        // The query imported back: the collection that stopped runs again with it.
        await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, withQuery);
        Assert.NotNull(host.Store.Find(query.Id));
        await auto.Queue.WhenIdleAsync();
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, auto.State.Find(definition)!.Status.Value);
    }

    [Fact]
    public async Task AMergingImport_ThatChangesAFollowedQuery_BreaksOnlyWhatStops_AndTheChangedOneRunsAgain()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");
        var export = await ExportAsync(host);

        // Collections in the query: it would stop, so it's listed and refused.
        var boxSets = WithQuery(export, query.Id, BoxSets, "2030-01-01T00:00:00Z");
        Assert.Equal([definition], Broken(await host.OkAsync("POST", "/SmartSearch/Admin/Import", host.Admin, boxSets)));
        var (status, _, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, boxSets);
        Assert.True(status == HttpStatusCode.Conflict, text);

        // Another query that runs: nothing breaks; the collection runs again with it.
        var other = WithQuery(export, query.Id, Q(Other), "2030-01-01T00:00:00Z");
        Assert.Empty(Broken(await host.OkAsync("POST", "/SmartSearch/Admin/Import", host.Admin, other)));
        await auto.Queue.WhenIdleAsync();
        auto.Queue.Requests.Clear();
        await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, other);
        Assert.Contains("\"other\"", host.Store.Find(query.Id)!.Query.GetRawText(), StringComparison.Ordinal);
        await auto.Queue.WhenIdleAsync();
        Assert.Contains(auto.Queue.Requests, r => r.DefinitionId.Equals(definition) && r.Reason == SmartSearchAutoCollectionRunReason.Saved);

        // Forced over the break: done, and the run shows it.
        await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false&force=true", host.Admin, WithQuery(export, query.Id, BoxSets, "2031-01-01T00:00:00Z"));
        await auto.Queue.WhenIdleAsync();
        var state = auto.State.Find(definition)!;
        Assert.Equal(SmartSearchAutoCollectionStatus.QueryInvalid, state.Status.Value);
        Assert.Equal(AutoCollectionReasons.BoxSetKind, state.Reason);
    }

    [Fact]
    public async Task ARestore_ListsWhatItBreaks_IsRefusedWithoutForce_AndForcedTheCollectionStopsAsQueryMissing()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        await host.SavedQueries.CreateAsync(manager, false, Request("Before"), Ct);
        var name = System.IO.Path.GetFileName(await host.Store.Documents.SnapshotAsync(host.Store.SnapshotDirectory, 7, Ct));
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");
        var path = "/SmartSearch/Admin/Snapshots/Restore?name=" + Uri.EscapeDataString(name!);

        Assert.Equal([definition], Broken(await host.OkAsync("POST", path + "&dryRun=true", host.Admin)));
        await ProblemAsync(host, "POST", path, host.Admin, null, HttpStatusCode.Conflict, SavedQueryErrors.BreaksAutoCollectionsCode);
        Assert.NotNull(host.Store.Find(query.Id));

        Assert.Equal([definition], Broken(await host.OkAsync("POST", path + "&force=true", host.Admin)));
        Assert.Null(host.Store.Find(query.Id));
        await auto.Queue.WhenIdleAsync();
        Assert.Equal(SmartSearchAutoCollectionStatus.QueryMissing, auto.State.Find(definition)!.Status.Value);

        // The restore's own safety snapshot has the query: restored, the collection runs again with it.
        var safety = host.Store.Documents.ListSnapshots(host.Store.SnapshotDirectory).OrderBy(x => x.Utc).Last().Name;
        await host.OkAsync("POST", "/SmartSearch/Admin/Snapshots/Restore?name=" + Uri.EscapeDataString(safety), host.Admin);
        Assert.NotNull(host.Store.Find(query.Id));
        await auto.Queue.WhenIdleAsync();
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, auto.State.Find(definition)!.Status.Value);
    }

    [Fact]
    public async Task ARemap_ReadsTheDefinitionsAsTheyWillBe_SoOneThatMovesWithItsQueryBreaksNothing_AndAnAdministratorOnlyOneIsListed()
    {
        var (host, _) = await StartAsync();
        await using var __ = host;
        var auto = host.AutoCollections!;
        var foreign = Guid.NewGuid();
        var plain = Record(foreign, "From elsewhere", Q());
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, plain]) with { KnownUsers = d.KnownUsers.With(foreign, host.Twin.Username) }, Ct);
        var moving = await AddDefinitionAsync(auto, foreign, plain.Id, "Moves along");

        // The definition and its query both go to the twin: before, its creator was missing; after, it runs.
        var dry = await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name", host.Admin);
        Assert.Empty(Broken(dry));

        // An administrator-only query the twin would get: the definition would stop, listed, refused without force.
        var paths = Record(foreign, "Paths from elsewhere", Q(AdminOnly));
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, paths]), Ct);
        var stopping = await AddDefinitionAsync(auto, foreign, paths.Id, "Would stop");
        Assert.Equal([stopping], Broken(await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name", host.Admin)));
        await ProblemAsync(host, "POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false", host.Admin, null, HttpStatusCode.Conflict, SavedQueryErrors.BreaksAutoCollectionsCode);
        Assert.Equal(foreign, host.Store.Find(plain.Id)!.OwnerUserId);

        // Without it, the remap moves the query and the definition.
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, d.Queries.Where(q => q.Id != paths.Id)), Ct);
        await auto.Store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, d.Definitions.Where(x => !x.Id.Equals(stopping))), Ct);
        await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false", host.Admin);
        Assert.Equal(host.Twin.Id, host.Store.Find(plain.Id)!.OwnerUserId);
        Assert.Equal(host.Twin.Id, auto.Store.Find(moving)!.CreatedByUserId);
    }

    [Fact]
    public async Task ADeletedCreatorsQuery_StaysWithTheCollection_AndAnAdministratorGivesBothToAManager()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");
        var second = (await auto.CreateAsync(manager, false, AutoCollectionHost.Def("Same query", savedQueryId: query.Id))).Id;
        var otherManager = Manager("other manager");
        host.Users.Add(otherManager);

        host.Users.Remove(manager);
        await host.Reconciler.HandleUserDeletedAsync(manager.Id, Ct);
        Assert.Equal(manager.Id, host.Store.Find(query.Id)!.OwnerUserId);
        Assert.Equal("CreatorMissing", (await auto.RefreshAsync(host.Admin, true, definition)).State.Status);

        var path = "/SmartSearch/AutoCollections/" + definition + "/Reassign";
        await ProblemAsync(host, "POST", path, otherManager, "{\"UserId\":\"" + otherManager.Id + "\"}", HttpStatusCode.Forbidden, "AdministratorRequired");
        var (status, problem, text) = await host.SendAsync("POST", path, host.Admin, "{\"UserId\":\"" + host.Kid.Id + "\"}");
        Assert.True(status == HttpStatusCode.BadRequest, text);
        Assert.Equal(AutoCollectionErrors.ManagerRequiredCode, problem.GetProperty("errors")[0].GetProperty("code").GetString());

        var given = await host.OkAsync("POST", path, host.Admin, "{\"UserId\":\"" + otherManager.Id + "\"}");
        Assert.Equal(otherManager.Id, Guid.Parse(given.GetProperty("CreatedByUserId").GetString()!));
        Assert.Equal(otherManager.Id, host.Store.Find(query.Id)!.OwnerUserId);
        Assert.Equal(otherManager.Id, auto.Store.Find(second)!.CreatedByUserId);
        await auto.Queue.WhenIdleAsync();
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, auto.State.Find(definition)!.Status.Value);
        Assert.Equal(SmartSearchAutoCollectionStatus.Ok, auto.State.Find(second)!.Status.Value);

        await ProblemAsync(host, "POST", path, host.Admin, "{\"UserId\":\"" + host.Admin.Id + "\"}", HttpStatusCode.Conflict, "CreatorNotMissing");
    }

    [Fact]
    public async Task ACreate_ChecksUnderTheGate_ThatItsQueryIsStillThere()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var query = await host.SavedQueries.CreateAsync(manager, false, Request("Going"), Ct);
        var definition = AutoCollectionHost.Def("Too late", savedQueryId: query.Id);
        var preview = await auto.Service.PreviewAsync(manager, false, new SmartSearchAutoCollectionPreviewRequestDto { Definition = definition }, Ct);
        await host.SavedQueries.DeleteAsync(manager, false, query.Id, false, Ct);

        var refused = await Assert.ThrowsAsync<PluginException>(() => auto.Service.CreateAsync(manager, false, new SmartSearchAutoCollectionRequestDto { Definition = definition, AcceptedPreviewHash = preview.PreviewHash }, Ct));
        Assert.Equal(AutoCollectionReasons.QueryMissing, refused.Code);
        Assert.Empty(auto.Store.Current.Definitions);
    }

    [Fact]
    public async Task TheReferenceReads_TakeNoLock_SoTheSavedQueriesLockHolderReadsThemWhileAGuardedSaveWaitsUnderTheGate()
    {
        // The saved queries' own writes read the references under their lock (a deleted user's queries, a bulk change's
        // check). A guarded save holds the definitions' gate and waits for that lock: were the reads to take the gate,
        // the two would wait for each other.
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var (query, _) = await FollowedAsync(host, manager, "Kept films");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var go = new ManualResetEventSlim();
        var counted = -1;
        IReadOnlyList<SmartSearchAutoCollectionRefDto>? broken = null;
        var holder = Task.Run(
            () => host.Store.UpdateAsync(
                d =>
                {
                    entered.TrySetResult();
                    go.Wait(TimeSpan.FromSeconds(30));
                    counted = host.Guard.CountReferences(query.Id);
                    broken = host.Guard.FindBrokenReferences(new SavedQueryBulkChange([query.Id], []));
                    return d;
                },
                Ct),
            Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);

        var save = host.SavedQueries.UpdateAsync(manager, false, query.Id, Request("Kept films", Q(Other), ifUpdatedAt: host.Store.Find(query.Id)!.UpdatedUtc), Ct);
        await WaitUntilHeldAsync(auto.Store, save);
        go.Set();

        await holder.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var saved = await save.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        Assert.Equal(1, counted);
        Assert.Single(broken!);
        Assert.Single(saved.AffectedAutoCollections);
        Assert.Contains("\"other\"", host.Store.Find(query.Id)!.Query.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuardedSaves_Reassignments_Remaps_ReconcilesAndRuns_AtOnce_AllFinish()
    {
        // Every path takes the locks in one order (the reconciler with none held; the definitions' gate before the saved
        // queries' lock; a run's gate never under the definitions' gate), so together they finish.
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var (query, definition) = await FollowedAsync(host, manager, "Kept films");
        var (adminQuery, adminDefinition) = await FollowedAsync(host, host.Admin, "Admin's");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;

        async Task Edits(User user, Guid id, string name)
        {
            for (var i = 0; i < 10; i++)
            {
                await host.SavedQueries.UpdateAsync(user, user == host.Admin, id, Request(name, Q(i % 2 == 0 ? Other : Keep), ifUpdatedAt: host.Store.Find(id)!.UpdatedUtc), ct);
            }
        }

        async Task Repeat(Func<Task> action)
        {
            for (var i = 0; i < 10; i++)
            {
                await action();
            }
        }

        var all = Task.WhenAll(
            Edits(manager, query.Id, "Kept films"),
            Edits(host.Admin, adminQuery.Id, "Admin's"),
            Repeat(() => host.Reconciler.ReconcileAsync(ct)),
            Repeat(() => host.SavedQueriesAdmin.RemapUsersAsync("Name", false, false, ct)),
            Repeat(() => auto.Service.RefreshAsync(host.Admin, true, definition, ct)),
            Repeat(async () => await auto.Service.ReassignAsync(host.Admin, true, adminDefinition, new SmartSearchSavedQueryOwnerDto { UserId = host.Admin.Id }, ct).ContinueWith(t => t.Exception, TaskScheduler.Default)),
            Repeat(() => Task.FromResult(host.Guard.FindBrokenReferences(new SavedQueryBulkChange([], [query.Id, adminQuery.Id])))));
        await all.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await auto.Queue.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(2, auto.Store.Current.Definitions.Count);
    }

    private static User Manager(string name)
    {
        var user = PlannerWorld.NewUser(name);
        user.SetPermission(PermissionKind.EnableCollectionManagement, true);
        user.SetPermission(PermissionKind.EnableAllFolders, true);
        return user;
    }

    private static async Task<(SearchApiHost Host, User Manager)> StartAsync()
    {
        var host = new SearchApiHost();
        var manager = Manager("manager");
        host.Users.Add(manager);
        host.World.Add(BaseItemKind.Movie, "Kept", host.World.Movies, s =>
        {
            s.Overview = "keep other";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });
        host.UseReferenceGuard();
        return (await host.StartAsync(), manager);
    }

    // A saved query of the creator's, and an auto-collection of theirs that follows it, with its collection.
    private static async Task<(SmartSearchSavedQueryDto Query, Guid Definition)> FollowedAsync(SearchApiHost host, User creator, string name)
    {
        var admin = creator == host.Admin;
        var query = await host.SavedQueries.CreateAsync(creator, admin, Request(name), Ct);
        var created = await host.AutoCollections!.CreateAsync(creator, admin, AutoCollectionHost.Def(name + " collection", savedQueryId: query.Id));
        Assert.Equal("Ok", created.State.Status);
        Assert.Equal(1, host.Guard.CountReferences(query.Id));
        return (query, created.Id);
    }

    // A definition written straight to the store (an import from another server, its creator unknown here).
    private static async Task<Guid> AddDefinitionAsync(AutoCollectionHost auto, Guid creator, Guid savedQueryId, string name)
    {
        var at = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var definition = new AutoCollectionDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            Enabled = true,
            Source = new AutoCollectionSource { Mode = SmartSearchAutoCollectionSourceMode.Reference, SavedQueryId = savedQueryId },
            CreatedByUserId = creator,
            UpdatedByUserId = creator,
            CreatedUtc = at,
            UpdatedUtc = at
        };
        await auto.Store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [.. d.Definitions, definition]), Ct);
        return definition.Id;
    }

    // Waits until the save holds the definitions' gate (a short wait for it times out), or fails when it ended first.
    private static async Task WaitUntilHeldAsync(AutoCollectionStore store, Task save)
    {
        for (var i = 0; i < 500 && !save.IsCompleted; i++)
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            try
            {
                using var held = await store.AcquireAsync(probe.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        Assert.Fail("The save never waited under the definitions' gate.");
    }

    // A save request's JSON with the query's UpdatedUtc as stored now.
    private static string Edit(SearchApiHost host, Guid id, string name, string? query = null, bool shared = false, string extra = "")
        => Body(name, query, shared, host.Store.Find(id)!.UpdatedUtc, extra);

    private static async Task<JsonElement> ProblemAsync(SearchApiHost host, string method, string path, User user, string? body, HttpStatusCode expected, string code)
    {
        var (status, problem, text) = await host.SendAsync(method, path, user, body);
        Assert.True(status == expected, method + " " + path + ": " + status + " " + text);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    private static async Task<string> ExportAsync(SearchApiHost host)
    {
        using var response = await host.SendRawAsync("GET", "/SmartSearch/Admin/Export", "apikey");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static string WithQuery(string export, Guid id, string query, string updatedUtc)
    {
        var root = JsonNode.Parse(export)!;
        var record = root["Data"]!["Queries"]!.AsArray().First(r => Guid.Parse(r!["Id"]!.GetValue<string>()) == id)!;
        record["Query"] = JsonNode.Parse(query);
        record["UpdatedUtc"] = updatedUtc;
        return root.ToJsonString();
    }

    private static List<Guid> Broken(JsonElement report)
        => report.GetProperty("BrokenAutoCollections").EnumerateArray().Select(r => Guid.Parse(r.GetProperty("Id").GetString()!)).ToList();
}
