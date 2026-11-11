using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Api;
using Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.SavedQueries.SavedQueryTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>
/// The reference guard's edges: an auto-collection whose creator doesn't own the saved query it follows (after a forced
/// owner change, a remap refused part-way, or an import) is no reference to it, gets no copy of it, and a copy's query is
/// shown only to its creator and administrators; while the definitions couldn't be loaded the guard refuses whatever
/// could break a reference; a reassignment keeps to the saved-query quota; a save that would be refused detaches nothing;
/// and an import reports the definitions' part on its own.
/// </summary>
public sealed class AutoCollectionReferenceEdgeCaseTests
{
    private const string Secret = """{"Field":"Overview","Op":"Contains","Value":"secret"}""";
    private const string Other = """{"Field":"Overview","Op":"Contains","Value":"other"}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> UnloadedStates => new() { "Unreadable", "Missing", "MigrationFailed", "NewerUnreadable" };

    [Fact]
    public async Task AfterAForcedOwnerChange_TheOldCreatorsFollower_DoesntHoldTheNewOwnerBack_AndADetachCopiesNothingIntoIt()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var query = await host.SavedQueries.CreateAsync(manager, false, Request("Kept films"), Ct);
        var definition = (await auto.CreateAsync(manager, false, AutoCollectionHost.Def("Kept films collection", savedQueryId: query.Id))).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("POST", "/SmartSearch/Admin/Queries/" + query.Id + "/Owner?force=true", host.Admin, "{\"UserId\":\"" + host.Twin.Id + "\"}")).Status);
        var path = "/SmartSearch/Queries/" + query.Id;
        Assert.Equal(0, host.Guard.CountReferences(query.Id));
        Assert.Empty(host.Guard.FindBrokenReferences(new SavedQueryBulkChange([query.Id], [])));

        // The new owner (no collection management) changes their own query, and isn't told of the other user's collection.
        var saved = await host.OkAsync("PUT", path, host.Twin, Body("Kept films", Q(Secret), ifUpdatedAt: host.Store.Find(query.Id)!.UpdatedUtc));
        Assert.Empty(saved.GetProperty("AffectedAutoCollections").EnumerateArray());
        Assert.Contains("secret", host.Store.Find(query.Id)!.Query.GetRawText(), StringComparison.Ordinal);

        // The creator's own detach copies nothing of someone else's.
        await ProblemAsync(host, "POST", "/SmartSearch/AutoCollections/" + definition + "/Detach", manager, null, HttpStatusCode.Conflict, AutoCollectionReasons.SourceOwnerChanged);

        // The new owner deletes it with detach: the other user's definition gets no copy and finds its query missing.
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("DELETE", path + "?detachAutoCollections=true", host.Twin)).Status);
        var kept = auto.Store.Find(definition)!;
        Assert.Equal(manager.Id, kept.CreatedByUserId);
        Assert.Equal(SmartSearchAutoCollectionSourceMode.Reference, kept.Source.Mode.Value);
        Assert.Null(kept.Source.Snapshot);
        Assert.DoesNotContain("secret", await File.ReadAllTextAsync(auto.Store.Documents.FilePath, Ct), StringComparison.Ordinal);
        await auto.Queue.WhenIdleAsync();
        Assert.Equal(nameof(SmartSearchAutoCollectionStatus.QueryMissing), (await auto.RefreshAsync(manager, false, definition)).State.Status);
    }

    [Fact]
    public async Task AfterARemapRefusedPartWay_TheMovedFollower_IsNoReferenceToTheQueryLeftBehind_UntilTheRemapEnds()
    {
        var (host, _) = await StartAsync();
        await using var __ = host;
        var auto = host.AutoCollections!;
        var foreign = Guid.NewGuid();
        var plain = Record(foreign, "From elsewhere", Q());
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, plain]) with { KnownUsers = d.KnownUsers.With(foreign, host.Twin.Username) }, Ct);
        var moving = await AddDefinitionAsync(auto, foreign, plain.Id, "Moves along");
        Assert.Equal(1, host.Guard.CountReferences(plain.Id));
        Assert.False(host.Store.WouldChangeRecords(foreign, DeletedUserPolicy.Delete));

        // The reconciler remaps the definitions first; the saved queries' part refused, the query keeps its old owner.
        await auto.Store.RemapUsersAsync(new Dictionary<Guid, Guid> { [foreign] = host.Twin.Id }, Ct);
        Assert.Equal(host.Twin.Id, auto.Store.Find(moving)!.CreatedByUserId);
        Assert.Equal(0, host.Guard.CountReferences(plain.Id));
        Assert.Empty(host.Guard.FindBrokenReferences(new SavedQueryBulkChange([plain.Id], [])));
        Assert.True(host.Store.WouldChangeRecords(foreign, DeletedUserPolicy.Delete)); // a private query nothing of its owner's follows

        // The rest of the remap moves the query too: the follower counts again.
        await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false", host.Admin);
        Assert.Equal(host.Twin.Id, host.Store.Find(plain.Id)!.OwnerUserId);
        Assert.Equal(1, host.Guard.CountReferences(plain.Id));
    }

    [Fact]
    public async Task AnImportedFollowerOfAnotherUsersQuery_IsNoReference_AndTheImportSaysSo()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var query = await host.SavedQueries.CreateAsync(manager, false, Request("Kept films"), Ct);
        var definition = (await auto.CreateAsync(manager, false, AutoCollectionHost.Def("Kept films collection", savedQueryId: query.Id))).Id;
        var export = await ExportAsync(host);

        // Here the query went to the twin and the auto-collection was deleted; the import brings the auto-collection back,
        // following a query that is no longer its creator's (the twin's newer query stays).
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("POST", "/SmartSearch/Admin/Queries/" + query.Id + "/Owner?force=true", host.Admin, "{\"UserId\":\"" + host.Twin.Id + "\"}")).Status);
        await auto.Service.DeleteAsync(manager, false, definition, false, Ct);
        var report = await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false&mode=Merge", host.Admin, export);
        var notes = report.GetProperty("AutoCollections").GetProperty("Notes").EnumerateArray().Select(n => n.GetString()!).ToList();
        Assert.Contains(notes, n => n.Contains("isn't their creator's", StringComparison.Ordinal) && n.Contains("Kept films collection", StringComparison.Ordinal));
        Assert.Equal(manager.Id, auto.Store.Find(definition)!.CreatedByUserId);
        Assert.Equal(host.Twin.Id, host.Store.Find(query.Id)!.OwnerUserId);
        Assert.Equal(0, host.Guard.CountReferences(query.Id));

        // The twin changes their query; the owner changes without force; the query is deleted without a detach.
        var path = "/SmartSearch/Queries/" + query.Id;
        var saved = await host.OkAsync("PUT", path, host.Twin, Body("Kept films", Q(Other), ifUpdatedAt: host.Store.Find(query.Id)!.UpdatedUtc));
        Assert.Empty(saved.GetProperty("AffectedAutoCollections").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("POST", "/SmartSearch/Admin/Queries/" + query.Id + "/Owner", host.Admin, "{\"UserId\":\"" + host.Admin.Id + "\"}")).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("DELETE", path, host.Admin)).Status);
        Assert.Equal(SmartSearchAutoCollectionSourceMode.Reference, auto.Store.Find(definition)!.Source.Mode.Value);
    }

    [Fact]
    public async Task ACopysQuery_IsShownToItsCreatorAndAdministrators_OtherManagersSeeOnlyThatItIsACopy()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var query = await host.SavedQueries.CreateAsync(manager, false, Request("Private films", Q(Secret)), Ct);
        var definition = (await auto.CreateAsync(manager, false, AutoCollectionHost.Def("Private films collection", savedQueryId: query.Id))).Id;
        await host.OkAsync("POST", "/SmartSearch/AutoCollections/" + definition + "/Detach", manager);
        var other = Manager("other manager");
        host.Users.Add(other);
        var path = "/SmartSearch/AutoCollections/" + definition;

        var seen = await host.OkAsync("GET", path, other);
        Assert.Equal("Private films collection", seen.GetProperty("Name").GetString());
        Assert.Equal("Ok", seen.GetProperty("State").GetProperty("Status").GetString());
        var source = seen.GetProperty("Source");
        Assert.Equal("Snapshot", source.GetProperty("Mode").GetString());
        Assert.False(source.GetProperty("QueryVisibleToCaller").GetBoolean());
        Assert.True(!source.TryGetProperty("Query", out var hidden) || hidden.ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain("secret", seen.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", (await host.OkAsync("GET", "/SmartSearch/AutoCollections", other)).GetRawText(), StringComparison.Ordinal);

        foreach (var reader in new[] { manager, host.Admin })
        {
            var full = (await host.OkAsync("GET", path, reader)).GetProperty("Source");
            Assert.True(full.GetProperty("QueryVisibleToCaller").GetBoolean());
            Assert.Contains("secret", full.GetProperty("Query").GetRawText(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(UnloadedStates))]
    public async Task WhileTheDefinitionsCouldntBeLoaded_WhatCouldBreakAReferenceIsRefused_ANameOrSharingGoesThrough_AndNoQueryGoesWithItsOwner(string state)
    {
        await using var host = await new SearchApiHost().StartAsync();
        var followed = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Followed"), Ct);
        var snapshot = Path.GetFileName(await host.Store.Documents.SnapshotAsync(host.Store.SnapshotDirectory, 7, Ct))!;
        var export = await ExportAsync(host);
        var later = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Later"), Ct);
        var foreign = Record(Guid.NewGuid(), "From elsewhere", Q());
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, foreign]) with { KnownUsers = d.KnownUsers.With(foreign.OwnerUserId, host.Twin.Username) }, Ct);
        using var folder = new TempDirectory();
        using var store = await UnloadedStoreAsync(state, folder.Path, AutoCollectionTestData.Definition(host.Kid.Id, followed.Id));
        host.Guard = Guard(host, store);

        Assert.True(store.IsReadOnly);
        Assert.True(store.DefinitionsUnknown);
        Assert.True(host.Guard.ReferencesUnknown);
        var path = "/SmartSearch/Queries/" + followed.Id;
        var query = host.Store.Find(followed.Id)!.Query.GetRawText();

        // Refused, forced or not: a delete (with a detach too), a change of the query, an owner change, an import, a
        // restore and a remap that remove or change queries.
        await RefusedAsync(host, "DELETE", path, host.Kid, null);
        await RefusedAsync(host, "DELETE", path + "?detachAutoCollections=true", host.Kid, null);
        await RefusedAsync(host, "PUT", path, host.Kid, Edit(host, followed.Id, "Followed", Q(Other)));
        await RefusedAsync(host, "PUT", path, host.Kid, Edit(host, followed.Id, "Followed", Q(Other), extra: ",\"DetachAutoCollections\":true"));
        await RefusedAsync(host, "POST", "/SmartSearch/Admin/Queries/" + followed.Id + "/Owner?force=true", host.Admin, "{\"UserId\":\"" + host.Twin.Id + "\"}");
        await RefusedAsync(host, "POST", "/SmartSearch/Admin/Import?dryRun=false&mode=Replace&force=true", host.Admin, export);
        await RefusedAsync(host, "POST", "/SmartSearch/Admin/Snapshots/Restore?force=true&name=" + Uri.EscapeDataString(snapshot), host.Admin, null);
        await RefusedAsync(host, "POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false&force=true", host.Admin, null);
        Assert.Equal(3, host.Store.Current.Queries.Count);
        Assert.Equal(query, host.Store.Find(followed.Id)!.Query.GetRawText());
        Assert.Equal(host.Kid.Id, host.Store.Find(followed.Id)!.OwnerUserId);
        Assert.Equal(foreign.OwnerUserId, host.Store.Find(foreign.Id)!.OwnerUserId);

        // Passes: a name, a description and sharing; the dry runs, which say what a real run would meet.
        await host.OkAsync("PUT", path, host.Kid, Edit(host, followed.Id, "Renamed", shared: true));
        Assert.Equal("Renamed", host.Store.Find(followed.Id)!.Name);
        Assert.True(host.Store.Find(followed.Id)!.Shared);
        Assert.Contains("couldn't be loaded", (await host.OkAsync("POST", "/SmartSearch/Admin/Import?mode=Replace", host.Admin, export)).GetRawText(), StringComparison.Ordinal);
        Assert.Contains("couldn't be loaded", (await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name", host.Admin)).GetProperty("Refusal").GetString(), StringComparison.Ordinal);

        // No query goes with its deleted owner, even a private one nothing seemed to follow.
        Assert.False(host.Store.WouldChangeRecords(host.Kid.Id, DeletedUserPolicy.Delete));
        host.Users.Remove(host.Kid);
        await host.Reconciler.HandleUserDeletedAsync(host.Kid.Id, Ct);
        Assert.NotNull(host.Store.Find(later.Id));
        Assert.NotNull(host.Store.Find(followed.Id));
    }

    [Fact]
    public async Task ANewerVersionsDefinitions_ThatCouldBeRead_StillGuardTheirQueries_AndAQueryChangeLeavesThemWaitingForThatVersion()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var followed = await host.SavedQueries.CreateAsync(host.Admin, true, Request("Followed"), Ct);
        using var folder = new TempDirectory();
        var follower = AutoCollectionTestData.Definition(host.Admin.Id, followed.Id);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, AutoCollectionStore.FileName), JsonSerializer.Serialize(new AutoCollectionsDocument { Version = 99, Definitions = [follower] }, JsonStoreFormat.Options), Ct);
        using var store = AutoCollectionTestData.Store(folder.Path);
        host.Guard = Guard(host, store);

        Assert.Equal(StoreStatus.ReadOnlyNewerVersion, store.Documents.Status);
        Assert.False(store.DefinitionsUnknown);
        Assert.Equal(1, host.Guard.CountReferences(followed.Id));
        var path = "/SmartSearch/Queries/" + followed.Id;
        await ProblemAsync(host, "DELETE", path, host.Admin, null, HttpStatusCode.Conflict, SavedQueryErrors.ReferencedCode);
        var changed = await host.OkAsync("PUT", path, host.Admin, Edit(host, followed.Id, "Followed", Q(Other)));
        Assert.Single(changed.GetProperty("AffectedAutoCollections").EnumerateArray());
    }

    [Fact]
    public async Task AReassignment_RefusesANewCreatorAtTheirSavedQueryQuota_AndWritesNothing()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var query = await host.SavedQueries.CreateAsync(manager, false, Request("Kept films"), Ct);
        var definition = (await auto.CreateAsync(manager, false, AutoCollectionHost.Def("Kept films collection", savedQueryId: query.Id))).Id;
        var other = Manager("other manager");
        host.Users.Add(other);
        await host.SavedQueries.CreateAsync(other, false, Request("Theirs"), Ct);
        host.MaxPerUser = 1;
        host.Users.Remove(manager);
        await host.Reconciler.HandleUserDeletedAsync(manager.Id, Ct);
        var path = "/SmartSearch/AutoCollections/" + definition + "/Reassign";
        var body = "{\"UserId\":\"" + other.Id + "\"}";

        await ProblemAsync(host, "POST", path, host.Admin, body, HttpStatusCode.Conflict, SavedQueryErrors.QuotaReachedCode);
        Assert.Equal(manager.Id, host.Store.Find(query.Id)!.OwnerUserId);
        Assert.Equal(manager.Id, auto.Store.Find(definition)!.CreatedByUserId);
        Assert.Equal(1, host.Store.CountRecordsByUser()[other.Id]);

        host.MaxPerUser = 2;
        await host.OkAsync("POST", path, host.Admin, body);
        Assert.Equal(other.Id, host.Store.Find(query.Id)!.OwnerUserId);
        Assert.Equal(other.Id, auto.Store.Find(definition)!.CreatedByUserId);
    }

    [Fact]
    public async Task ADetachThenSave_ThatTheSaveWouldRefuse_DetachesNothing()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var query = await host.SavedQueries.CreateAsync(manager, false, Request("Kept films"), Ct);
        await host.SavedQueries.CreateAsync(manager, false, Request("Taken"), Ct);
        var definition = (await auto.CreateAsync(manager, false, AutoCollectionHost.Def("Kept films collection", savedQueryId: query.Id))).Id;
        var path = "/SmartSearch/Queries/" + query.Id;

        await ProblemAsync(host, "PUT", path, manager, Edit(host, query.Id, "taken", Q(Other), extra: ",\"DetachAutoCollections\":true"), HttpStatusCode.Conflict, SavedQueryErrors.DuplicateNameCode);
        Assert.Equal(SmartSearchAutoCollectionSourceMode.Reference, auto.Store.Find(definition)!.Source.Mode.Value);
        Assert.Equal(1, host.Guard.CountReferences(query.Id));

        var stale = Body("Kept films", Q(Other), ifUpdatedAt: host.Store.Find(query.Id)!.UpdatedUtc.AddSeconds(-1), extra: ",\"DetachAutoCollections\":true");
        await ProblemAsync(host, "PUT", path, manager, stale, HttpStatusCode.Conflict, SavedQueryErrors.ChangedCode);
        Assert.Equal(SmartSearchAutoCollectionSourceMode.Reference, auto.Store.Find(definition)!.Source.Mode.Value);
    }

    [Fact]
    public async Task AnImport_WhoseDefinitionsCantBeWritten_StillReportsItsQueries_AndTheDefinitionsPartAsFailed()
    {
        var (host, manager) = await StartAsync();
        await using var _ = host;
        var auto = host.AutoCollections!;
        var query = await host.SavedQueries.CreateAsync(manager, false, Request("Kept films"), Ct);
        var definition = (await auto.CreateAsync(manager, false, AutoCollectionHost.Def("Kept films collection", savedQueryId: query.Id))).Id;
        var export = await ExportAsync(host);
        await auto.Service.DeleteAsync(manager, false, definition, false, Ct);
        await host.SavedQueries.DeleteAsync(manager, false, query.Id, false, Ct);

        // The definitions' write fails (their temporary file can't be made).
        Directory.CreateDirectory(auto.Store.Documents.FilePath + ".tmp");
        var report = await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false&mode=Merge", host.Admin, export);

        Assert.NotNull(host.Store.Find(query.Id));
        Assert.Equal(1, report.GetProperty("Records").GetProperty("Added").GetInt32());
        var part = report.GetProperty("AutoCollections");
        Assert.True(part.GetProperty("Failed").GetBoolean());
        Assert.False(string.IsNullOrEmpty(part.GetProperty("Error").GetString()));
        Assert.Null(auto.Store.Find(definition));
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
        host.World.Add(Jellyfin.Data.Enums.BaseItemKind.Movie, "Kept", host.World.Movies, s =>
        {
            s.Overview = "keep other secret";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
        });
        host.UseReferenceGuard();
        return (await host.StartAsync(), manager);
    }

    // A definition written straight to the store (an import from another server, its creator unknown here).
    private static async Task<Guid> AddDefinitionAsync(AutoCollectionHost auto, Guid creator, Guid savedQueryId, string name)
    {
        var definition = AutoCollectionTestData.Definition(creator, savedQueryId, name) with { Enabled = true };
        await auto.Store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [.. d.Definitions, definition]), Ct);
        return definition.Id;
    }

    // The definitions' store in a read-only state that leaves it without its data; the follower is in what couldn't load.
    private static async Task<AutoCollectionStore> UnloadedStoreAsync(string state, string folder, AutoCollectionDefinition follower)
    {
        var file = Path.Combine(folder, AutoCollectionStore.FileName);
        var snapshots = Path.Combine(folder, "snapshots");
        var json = JsonSerializer.Serialize(new AutoCollectionsDocument { Definitions = [follower] }, JsonStoreFormat.Options);
        AutoCollectionStore store;
        StoreStatus expected;
        switch (state)
        {
            case "Unreadable":
                await File.WriteAllTextAsync(file, json[..(json.Length / 2)], Ct);
                store = AutoCollectionTestData.Store(folder);
                expected = StoreStatus.ReadOnlyDataUnreadable;
                break;
            case "Missing":
                using (var written = AutoCollectionTestData.Store(folder))
                {
                    await written.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, [follower]), Ct);
                    var taken = await written.Documents.SnapshotAsync(snapshots, 7, Ct);
                    await File.WriteAllTextAsync(taken!, "{ not json", Ct);
                }

                File.Delete(file);
                File.Delete(file + ".bak");
                store = AutoCollectionTestData.Store(folder);
                expected = StoreStatus.ReadOnlyDataMissing;
                break;
            case "MigrationFailed":
                await File.WriteAllTextAsync(file, json, Ct);
                store = new AutoCollectionStore(
                    new JsonDocumentStore<AutoCollectionsDocument>(file, 2, () => new AutoCollectionsDocument { Version = 2 }, [new FailingUpgrade()], NullLogger.Instance, JsonStoreFormat.Options, null, snapshots),
                    snapshots,
                    NullLogger.Instance);
                expected = StoreStatus.ReadOnlyMigrationFailed;
                break;
            default:
                var newer = JsonNode.Parse(json)!.AsObject();
                newer["Version"] = 99;
                newer["Definitions"] = "not a list";
                await File.WriteAllTextAsync(file, newer.ToJsonString(), Ct);
                store = AutoCollectionTestData.Store(folder);
                expected = StoreStatus.ReadOnlyNewerVersion;
                break;
        }

        Assert.Equal(expected, store.Documents.Status);
        Assert.Empty(store.Current.Definitions);
        return store;
    }

    // The definitions as the saved queries' reference guard, over the host's saved queries.
    private static DefinitionReferenceGuard Guard(SearchApiHost host, AutoCollectionStore store)
    {
        var registry = host.Engine.Registry;
        var checks = new AutoCollectionChecks(new QueryValidator(registry), new QueryCanonicalizer(registry), registry);
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => host.Users.Find(u => u.Id == id));
        var context = new AutoCollectionCheckContextSource(store, host.Store, users.Object, () => QueryLimits.ItemKinds, () => new AutoCollectionSettings());
        return new DefinitionReferenceGuard(store, host.Store, checks, context, host.Clock, NullLogger.Instance);
    }

    private static string Edit(SearchApiHost host, Guid id, string name, string? query = null, bool shared = false, string extra = "")
        => Body(name, query, shared, host.Store.Find(id)!.UpdatedUtc, extra);

    private static Task RefusedAsync(SearchApiHost host, string method, string path, User user, string? body)
        => ProblemAsync(host, method, path, user, body, HttpStatusCode.Conflict, SavedQueryErrors.StoreReadOnlyCode);

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

    private sealed class FailingUpgrade : IJsonMigration
    {
        public int FromVersion => 1;

        public void Apply(JsonObject root) => throw new InvalidOperationException("The upgrade step failed.");
    }
}
