using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities.Movies;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// The auto-collection endpoints over HTTP: an administrator without the collection flag and a user with it manage
/// auto-collections, anyone else gets 403; an API key may only list and refresh; a create answers 201 with its collection,
/// and someone else's shared query is copied first.
/// </summary>
public sealed class AutoCollectionsApiTests : IAsyncLifetime
{
    private const string Keep = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    private readonly SearchApiHost _host = new();
    private readonly User _manager = PlannerWorld.NewUser("manager");
    private FakeRow? _kept;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        _manager.SetPermission(PermissionKind.EnableAllFolders, true);
        _host.Users.Add(_manager);
        _kept = _host.World.Add(BaseItemKind.Movie, "Kept", _host.World.Movies, s => { s.Overview = "keep"; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        _host.UseAutoCollections();
        await _host.StartAsync();
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task AnAdministratorWithoutTheFlag_AndAManager_MayManage_AUserMayNot()
    {
        Assert.False(_host.Admin.HasPermission(PermissionKind.EnableCollectionManagement));

        var created = await CreateAsync(_host.Admin, "By admin");
        var byManager = await CreateAsync(_manager, "By manager");
        var list = await _host.OkAsync("GET", "/SmartSearch/AutoCollections", _manager);

        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("Ok", created.GetProperty("State").GetProperty("Status").GetString());
        Assert.Single(_host.Collections.Boxes, b => b.Name == "By manager");
        Assert.Equal(_manager.Id, Guid.Parse(byManager.GetProperty("CreatedByUserId").GetString()!));

        foreach (var (method, path, body) in Calls(Guid.Parse(created.GetProperty("Id").GetString()!)))
        {
            var (status, problem, _) = await _host.SendAsync(method, path, _host.Kid, body);
            Assert.True(status == HttpStatusCode.Forbidden, method + " " + path + ": " + status);
            Assert.Equal("ManagerRequired", problem.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task AnApiKey_MayOnlyListAndRefresh()
    {
        var created = await CreateAsync(_host.Admin, "Keepers");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);

        using var list = await _host.SendRawAsync("GET", "/SmartSearch/AutoCollections", "apikey");
        using var refresh = await _host.SendRawAsync("POST", "/SmartSearch/AutoCollections/" + id + "/Refresh", "apikey");
        using var all = await _host.SendRawAsync("POST", "/SmartSearch/AutoCollections/Refresh", "apikey");
        using var get = await _host.SendRawAsync("GET", "/SmartSearch/AutoCollections/" + id, "apikey");
        using var delete = await _host.SendRawAsync("DELETE", "/SmartSearch/AutoCollections/" + id, "apikey");
        using var create = await _host.SendRawAsync("POST", "/SmartSearch/AutoCollections", "apikey", json: CreateBody("Other", "sha256:00"));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var rows = JsonDocument.Parse(await list.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.False(rows[0].GetProperty("Source").GetProperty("QueryVisibleToCaller").GetBoolean());
        Assert.Equal(HttpStatusCode.Accepted, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, all.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        await _host.AutoCollections!.Queue.WhenIdleAsync();
    }

    [Fact]
    public async Task Change_Detach_Recreate_AndDelete_AnswerAsDocumented()
    {
        var created = await CreateAsync(_host.Admin, "Keepers");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);
        var updatedUtc = created.GetProperty("UpdatedUtc").GetString();

        var (put, changed, _) = await _host.SendAsync("PUT", "/SmartSearch/AutoCollections/" + id, _host.Admin, "{\"Definition\":{\"Presentation\":{\"Lock\":false}},\"IfUpdatedAt\":\"" + updatedUtc + "\"}");
        var (stale, staleProblem, _) = await _host.SendAsync("PUT", "/SmartSearch/AutoCollections/" + id, _host.Admin, "{\"Definition\":{},\"IfUpdatedAt\":\"" + updatedUtc + "\"}");
        var (detach, detached, _) = await _host.SendAsync("POST", "/SmartSearch/AutoCollections/" + id + "/Detach", _host.Admin);
        var (recreate, recreateProblem, _) = await _host.SendAsync("POST", "/SmartSearch/AutoCollections/" + id + "/Recreate", _host.Admin);
        var (delete, _, _) = await _host.SendAsync("DELETE", "/SmartSearch/AutoCollections/" + id + "?deleteCollection=true", _host.Admin);

        Assert.Equal(HttpStatusCode.OK, put);
        Assert.False(changed.GetProperty("Presentation").GetProperty("Lock").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Equal("AutoCollectionChanged", staleProblem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, detach);
        Assert.Equal("AlreadySnapshot", detached.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Conflict, recreate);
        Assert.Equal("NotRecreatable", recreateProblem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, delete);
        Assert.Empty(_host.Collections.Boxes);
        Assert.Contains("Delete", _host.Collections.Calls);
    }

    [Fact]
    public async Task SomeoneElsesSharedQuery_IsCopiedFirst_WhenAskedFor()
    {
        var shared = Guid.NewGuid();
        using (var query = JsonDocument.Parse(QueryPlannerTests.Q(Keep, "\"Movie\"")))
        {
            var record = new SavedQueryRecord { Id = shared, OwnerUserId = _host.Kid.Id, Name = "Kid's", Shared = true, Query = query.RootElement.Clone(), CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow };
            await _host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, record]), Ct);
        }

        var definition = "{\"Name\":\"From Kid\",\"Source\":{\"SavedQueryId\":\"" + shared + "\"}}";
        var preview = await _host.OkAsync("POST", "/SmartSearch/AutoCollections/Preview", _manager, "{\"Definition\":" + definition + "}");
        var hash = preview.GetProperty("PreviewHash").GetString();
        var (refused, problem, _) = await _host.SendAsync("POST", "/SmartSearch/AutoCollections", _manager, "{\"Definition\":" + definition + ",\"AcceptedPreviewHash\":\"" + hash + "\"}");
        var (created, body, text) = await _host.SendAsync("POST", "/SmartSearch/AutoCollections", _manager, "{\"Definition\":" + definition + ",\"AcceptedPreviewHash\":\"" + hash + "\",\"DuplicateSourceQuery\":true}");

        Assert.Equal(HttpStatusCode.Forbidden, refused);
        Assert.Equal("NotQueryOwner", problem.GetProperty("code").GetString());
        Assert.True(created == HttpStatusCode.Created, text);
        var copy = Guid.Parse(body.GetProperty("Source").GetProperty("SavedQueryId").GetString()!);
        Assert.NotEqual(shared, copy);
        Assert.Equal(_manager.Id, _host.Store.Find(copy)!.OwnerUserId);
        Assert.Equal(_host.Kid.Id, _host.Store.Find(shared)!.OwnerUserId);
    }

    [Fact]
    public async Task TheHeldRemovals_AreAppliedOrKept_AsRead()
    {
        var created = await CreateAsync(_manager, "Keepers");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);
        _host.World.Core.Remove(_kept!);
        await _host.SendAsync("POST", "/SmartSearch/AutoCollections/" + id + "/Refresh", _manager);
        await _host.AutoCollections!.Queue.WhenIdleAsync();
        var held = await _host.OkAsync("GET", "/SmartSearch/AutoCollections/" + id, _manager);
        Assert.Equal("Guarded", held.GetProperty("State").GetProperty("Status").GetString());
        var detected = held.GetProperty("State").GetProperty("PendingGuard").GetProperty("DetectedUtc").GetString();
        var apply = "/SmartSearch/AutoCollections/" + id + "/Guard/Apply";

        var (missing, missingProblem, _) = await _host.SendAsync("POST", apply, _manager, "{}");
        var (unknown, _, _) = await _host.SendAsync("POST", apply, _manager, "{\"DetectedUtc\":\"" + detected + "\",\"Ids\":[]}");
        var (stale, staleProblem, _) = await _host.SendAsync("POST", apply, _manager, "{\"DetectedUtc\":\"2026-01-01T00:00:00Z\"}");
        var (kept, _, keptText) = await _host.SendAsync("POST", "/SmartSearch/AutoCollections/" + id + "/Guard/Keep", _manager, "{\"DetectedUtc\":\"" + detected + "\"}");
        await _host.AutoCollections!.Queue.WhenIdleAsync();

        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.Equal("InvalidRequest", missingProblem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, unknown);
        Assert.Equal(HttpStatusCode.Conflict, stale);
        Assert.Equal("PreviewStale", staleProblem.GetProperty("code").GetString());
        Assert.True(kept == HttpStatusCode.NoContent, keptText);

        // Kept: nothing could be pinned (the item is gone), so the next run holds the emptying again, as a new review.
        var again = await _host.OkAsync("GET", "/SmartSearch/AutoCollections/" + id, _manager);
        var detectedAgain = again.GetProperty("State").GetProperty("PendingGuard").GetProperty("DetectedUtc").GetString();
        var (applied, body, appliedText) = await _host.SendAsync("POST", apply, _manager, "{\"DetectedUtc\":\"" + detectedAgain + "\"}");
        Assert.True(applied == HttpStatusCode.Accepted, appliedText);
        Assert.True(body.GetProperty("State").GetProperty("PendingGuard").GetProperty("Approved").GetBoolean());
        await _host.AutoCollections!.Queue.WhenIdleAsync();
        var emptied = await _host.OkAsync("GET", "/SmartSearch/AutoCollections/" + id, _manager);
        Assert.Equal("Ok", emptied.GetProperty("State").GetProperty("Status").GetString());
        Assert.Empty(_host.Collections.Boxes.Single(b => b.Name == "Keepers").LinkedChildren);
    }

    [Fact]
    public async Task TheFieldsCatalogue_SaysWhoManagesAutoCollections()
    {
        var manager = await _host.OkAsync("GET", "/SmartSearch/Fields", _manager);
        var admin = await _host.OkAsync("GET", "/SmartSearch/Fields", _host.Admin);
        var kid = await _host.OkAsync("GET", "/SmartSearch/Fields", _host.Kid);

        Assert.Equal("""{"Enabled":true,"CanManage":true,"IsAdmin":false}""", manager.GetProperty("AutoCollections").GetRawText());
        Assert.Equal("""{"Enabled":true,"CanManage":true,"IsAdmin":true}""", admin.GetProperty("AutoCollections").GetRawText());
        Assert.Equal("""{"Enabled":true,"CanManage":false,"IsAdmin":false}""", kid.GetProperty("AutoCollections").GetRawText());
    }

    [Fact]
    public async Task TheListNamesItsCreator_AndTheHistory_ListsTheRuns()
    {
        var created = await CreateAsync(_manager, "Keepers");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);

        var row = (await _host.OkAsync("GET", "/SmartSearch/AutoCollections", _host.Admin))[0];
        var history = await _host.OkAsync("GET", "/SmartSearch/AutoCollections/" + id + "/History", _manager);
        var (missing, _, _) = await _host.SendAsync("GET", "/SmartSearch/AutoCollections/" + Guid.NewGuid() + "/History", _manager);

        Assert.Equal("manager", row.GetProperty("CreatedByName").GetString());
        Assert.False(row.TryGetProperty("EvaluationUserName", out var evaluation) && evaluation.ValueKind != JsonValueKind.Null);
        var run = Assert.Single(history.EnumerateArray());
        Assert.Equal(1, run.GetProperty("Added").GetInt32());
        Assert.Equal("Created", run.GetProperty("Outcome").GetString());
        Assert.Empty(run.GetProperty("RemovedIds").EnumerateArray());
        Assert.Equal(0, run.GetProperty("HiddenCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, missing);
    }

    [Fact]
    public async Task ACollectionsPage_LearnsWhatFillsIt_OnlyAboutCollectionsTheUserCanOpen_AndManagersGetItsId()
    {
        var created = await CreateAsync(_manager, "Keepers");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);
        var box = _host.Collections.Boxes.Single(b => b.Name == "Keepers");

        var managerIds = await _host.OkAsync("GET", "/SmartSearch/AutoCollections/Ids", _manager);
        var kidIds = await _host.OkAsync("GET", "/SmartSearch/AutoCollections/Ids", _host.Kid);
        var forManager = await _host.OkAsync("GET", "/SmartSearch/AutoCollections/ByCollection/" + box.Id, _manager);
        var forKid = await _host.OkAsync("GET", "/SmartSearch/AutoCollections/ByCollection/" + box.Id, _host.Kid);
        var (other, _, _) = await _host.SendAsync("GET", "/SmartSearch/AutoCollections/ByCollection/" + Guid.NewGuid(), _manager);
        using var apiKey = await _host.SendRawAsync("GET", "/SmartSearch/AutoCollections/Ids", "apikey");

        Assert.Equal([box.Id], managerIds.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)));
        Assert.Equal([box.Id], kidIds.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)));
        Assert.Equal("Keepers", forManager.GetProperty("Name").GetString());
        Assert.True(forManager.GetProperty("CanManage").GetBoolean());
        Assert.Equal(id, Guid.Parse(forManager.GetProperty("DefinitionId").GetString()!));
        Assert.Equal(JsonValueKind.String, forManager.GetProperty("LastSuccessUtc").ValueKind);
        Assert.Equal("Keepers", forKid.GetProperty("Name").GetString());
        Assert.False(forKid.GetProperty("CanManage").GetBoolean());
        Assert.False(forKid.TryGetProperty("DefinitionId", out var hidden) && hidden.ValueKind != JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.NotFound, other);
        Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);

        // A collection the user can't open (the lookup is the user's, never a user-less one): not listed, not found.
        _host.World.Core.Library.Setup(l => l.GetItemById<BoxSet>(box.Id, It.Is<User?>(u => u != null && u.Id.Equals(_host.Kid.Id)))).Returns((BoxSet?)null);
        Assert.Empty((await _host.OkAsync("GET", "/SmartSearch/AutoCollections/Ids", _host.Kid)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.SendAsync("GET", "/SmartSearch/AutoCollections/ByCollection/" + box.Id, _host.Kid)).Status);

        // A collection that lost its mark isn't the auto-collection's any more.
        box.ProviderIds.Remove(AutoCollectionDefinition.TetherKey);
        Assert.Empty((await _host.OkAsync("GET", "/SmartSearch/AutoCollections/Ids", _manager)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.SendAsync("GET", "/SmartSearch/AutoCollections/ByCollection/" + box.Id, _manager)).Status);
    }

    [Fact]
    public async Task WhileAutoCollectionsAreOff_NoCollectionsPageHearsOfThem()
    {
        await CreateAsync(_manager, "Keepers");
        var box = _host.Collections.Boxes.Single(b => b.Name == "Keepers");
        _host.AutoCollections!.Settings = _host.AutoCollections.Settings with { Enabled = false };

        Assert.Empty((await _host.OkAsync("GET", "/SmartSearch/AutoCollections/Ids", _manager)).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.SendAsync("GET", "/SmartSearch/AutoCollections/ByCollection/" + box.Id, _manager)).Status);
    }

    [Fact]
    public async Task AnExport_CarriesTheDefinitions_AndAnotherServer_ImportsThemWithoutCollections_UntilCreated()
    {
        var created = await CreateAsync(_manager, "Keepers");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);
        string export;
        using (var response = await _host.SendRawAsync("GET", "/SmartSearch/Admin/Export", "apikey"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            export = await response.Content.ReadAsStringAsync(Ct);
        }

        using (var envelope = JsonDocument.Parse(export))
        {
            var data = envelope.RootElement.GetProperty("Data");
            Assert.Equal(JsonValueKind.Array, data.GetProperty("Queries").ValueKind);
            var definition = Assert.Single(data.GetProperty("AutoCollections").GetProperty("Definitions").EnumerateArray());
            Assert.Equal(id, definition.GetProperty("Id").GetGuid());
            Assert.Equal(JsonValueKind.String, definition.GetProperty("CollectionId").ValueKind);
            Assert.Contains(envelope.RootElement.GetProperty("Users").EnumerateArray(), u => u.GetProperty("Name").GetString() == "manager");
        }

        // Another server, where the same people have other ids.
        await using var other = new SearchApiHost();
        var manager = PlannerWorld.NewUser("manager");
        manager.SetPermission(PermissionKind.EnableCollectionManagement, true);
        manager.SetPermission(PermissionKind.EnableAllFolders, true);
        other.Users.Add(manager);
        other.World.Add(BaseItemKind.Movie, "Kept", other.World.Movies, s => { s.Overview = "keep"; s.OfficialRating = "PG"; s.InheritedRating = 10; });
        other.UseAutoCollections();
        await other.StartAsync();

        var dry = await other.OkAsync("POST", "/SmartSearch/Admin/Import?mapUsersBy=Name", other.Admin, export);
        Assert.Equal(1, dry.GetProperty("AutoCollections").GetProperty("Records").GetProperty("Added").GetInt32());
        Assert.Empty(other.AutoCollections!.Store.Current.Definitions);

        var report = await other.OkAsync("POST", "/SmartSearch/Admin/Import?mapUsersBy=Name&dryRun=false", other.Admin, export);
        var part = report.GetProperty("AutoCollections");
        Assert.Equal(1, part.GetProperty("NotCreated").GetInt32());
        Assert.Contains(part.GetProperty("Notes").EnumerateArray(), n => n.GetString()!.Contains("Create on the Auto-collections tab", StringComparison.Ordinal));
        var imported = Assert.Single(other.AutoCollections.Store.Current.Definitions);
        Assert.Equal(id, imported.Id);
        Assert.Equal(manager.Id, imported.CreatedByUserId);
        Assert.Null(imported.CollectionId);
        var row = Assert.Single((await other.OkAsync("GET", "/SmartSearch/AutoCollections", manager)).EnumerateArray());
        Assert.Equal("NotCreated", row.GetProperty("State").GetProperty("Status").GetString());
        await other.AutoCollections.Queue.WhenIdleAsync();
        Assert.Empty(other.Collections.Boxes);

        // Nothing is made until a manager asks; then it is.
        var (status, _, text) = await other.SendAsync("POST", "/SmartSearch/AutoCollections/" + id + "/Recreate", manager);
        Assert.True(status is HttpStatusCode.Created or HttpStatusCode.Accepted, text);
        await other.AutoCollections.Queue.WhenIdleAsync();
        Assert.Single(other.Collections.Boxes, b => b.Name == "Keepers");

        // Again: the definition here keeps its collection; the 1.0 shape (no definitions) still imports.
        var again = await other.OkAsync("POST", "/SmartSearch/Admin/Import?mapUsersBy=Name&dryRun=false", other.Admin, export);
        Assert.Equal(1, again.GetProperty("AutoCollections").GetProperty("Records").GetProperty("Unchanged").GetInt32());
        Assert.NotNull(other.AutoCollections.Store.Find(id)!.CollectionId);
        var old = System.Text.Json.Nodes.JsonNode.Parse(export)!.AsObject();
        old["Data"]!.AsObject().Remove("AutoCollections");
        var plain = await other.OkAsync("POST", "/SmartSearch/Admin/Import?mapUsersBy=Name", other.Admin, old.ToJsonString());
        Assert.False(plain.TryGetProperty("AutoCollections", out _));
    }

    [Fact]
    public async Task TheDiagnostics_ListHeldRemovals_CollectionsMarkedTwice_AndMarkedWithoutDefinition()
    {
        var created = await CreateAsync(_manager, "Keepers");
        var id = Guid.Parse(created.GetProperty("Id").GetString()!);
        var box = _host.Collections.Boxes.Single(b => b.Name == "Keepers");
        var copy = new MediaBrowser.Controller.Entities.Movies.BoxSet { Id = Guid.NewGuid(), Name = "Keepers copy", DateCreated = box.DateCreated.AddDays(1) };
        copy.ProviderIds[AutoCollectionDefinition.TetherKey] = AutoCollectionDefinition.FormatTether(id);
        _host.Collections.Boxes.Add(copy);
        var left = new MediaBrowser.Controller.Entities.Movies.BoxSet { Id = Guid.NewGuid(), Name = "From a newer definitions file" };
        left.ProviderIds[AutoCollectionDefinition.TetherKey] = AutoCollectionDefinition.FormatTether(Guid.NewGuid());
        _host.Collections.Boxes.Add(left);
        await _host.AutoCollections!.State.UpdateAsync(id, s => s! with { PendingGuard = new AutoCollectionPendingGuard { DetectedUtc = DateTime.UtcNow, WouldRemoveCount = 3, Reasons = ["FolderMissing"] } }, Ct);

        var runs = _host.AutoCollections.Diagnostics.Describe();

        Assert.Equal(1, runs["Count"]);
        var held = (System.Collections.Generic.IDictionary<string, object?>)Assert.Single((System.Collections.IEnumerable)runs["PendingGuards"]!)!;
        Assert.Equal("Keepers", held["Name"]);
        Assert.Equal(3, held["WouldRemove"]);
        var twice = (System.Collections.Generic.IDictionary<string, object?>)Assert.Single((System.Collections.IEnumerable)runs["DuplicateMarkedCollections"]!)!;
        Assert.Equal(box.Id, twice["CollectionId"]);
        var others = (System.Collections.IEnumerable)twice["Others"]!;
        Assert.Equal(copy.Id, ((System.Collections.Generic.IDictionary<string, object?>)Assert.Single(others)!)["Id"]);
        Assert.Equal(1, runs["MarkedWithoutDefinition"]);
        var unowned = (System.Collections.Generic.IDictionary<string, object?>)Assert.Single((System.Collections.IEnumerable)runs["CollectionsWithoutDefinition"]!)!;
        Assert.Equal((left.Id, "From a newer definitions file"), ((Guid)unowned["Id"]!, (string)unowned["Name"]!));
    }

    private static string CreateBody(string name, string? hash)
        => "{\"Definition\":{\"Name\":\"" + name + "\",\"Source\":{\"Query\":" + QueryPlannerTests.Q(Keep, "\"Movie\"") + "}},\"AcceptedPreviewHash\":\"" + hash + "\"}";

    private static (string Method, string Path, string? Body)[] Calls(Guid id) =>
    [
        ("GET", "/SmartSearch/AutoCollections", null),
        ("GET", "/SmartSearch/AutoCollections/" + id, null),
        ("POST", "/SmartSearch/AutoCollections/CheckName", "{\"Name\":\"X\"}"),
        ("POST", "/SmartSearch/AutoCollections/Preview", "{\"DefinitionId\":\"" + id + "\"}"),
        ("POST", "/SmartSearch/AutoCollections", CreateBody("X", "h")),
        ("PUT", "/SmartSearch/AutoCollections/" + id, "{\"Definition\":{},\"IfUpdatedAt\":\"2026-11-01T00:00:00Z\"}"),
        ("POST", "/SmartSearch/AutoCollections/" + id + "/Refresh", null),
        ("POST", "/SmartSearch/AutoCollections/Refresh", null),
        ("POST", "/SmartSearch/AutoCollections/" + id + "/Recreate", null),
        ("POST", "/SmartSearch/AutoCollections/" + id + "/Detach", null),
        ("POST", "/SmartSearch/AutoCollections/" + id + "/Guard/Apply", "{\"DetectedUtc\":\"2026-11-01T00:00:00Z\"}"),
        ("POST", "/SmartSearch/AutoCollections/" + id + "/Guard/Keep", "{\"DetectedUtc\":\"2026-11-01T00:00:00Z\"}"),
        ("GET", "/SmartSearch/AutoCollections/" + id + "/History", null),
        ("DELETE", "/SmartSearch/AutoCollections/" + id, null)
    ];

    // A preview, then a create from it: 201 with the collection made.
    private async Task<JsonElement> CreateAsync(User user, string name)
    {
        var preview = await _host.OkAsync("POST", "/SmartSearch/AutoCollections/Preview", user, "{\"Definition\":{\"Source\":{\"Query\":" + QueryPlannerTests.Q(Keep, "\"Movie\"") + "}}}");
        var (status, body, text) = await _host.SendAsync("POST", "/SmartSearch/AutoCollections", user, CreateBody(name, preview.GetProperty("PreviewHash").GetString()));
        Assert.True(status == HttpStatusCode.Created, text);
        await _host.AutoCollections!.Queue.WhenIdleAsync();
        return body;
    }
}
