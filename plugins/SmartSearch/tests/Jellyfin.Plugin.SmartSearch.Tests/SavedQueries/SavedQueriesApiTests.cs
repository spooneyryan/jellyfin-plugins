using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Search;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Api;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.SavedQueries.SavedQueryTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>
/// <c>/SmartSearch/Queries</c> on the wire, for each caller: the owner (Kid, the restricted user), another user (Twin), an
/// administrator, an API key and nobody. Who sees, changes and runs what; a shared query run as the caller; the sharing
/// setting and administrator-only fields; the strict bodies and their limits; queries of a newer version; the reference
/// guard's refusals; the page switch before anything is read.
/// </summary>
public sealed class SavedQueriesApiTests
{
    private static readonly Guid Missing = new("0d0d0d0d-0000-4000-8000-000000000001");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", "/SmartSearch/Queries" },
        { "GET", "/SmartSearch/Queries/{id}" },
        { "POST", "/SmartSearch/Queries" },
        { "PUT", "/SmartSearch/Queries/{id}" },
        { "POST", "/SmartSearch/Queries/{id}/Duplicate" },
        { "DELETE", "/SmartSearch/Queries/{id}" },
        { "POST", "/SmartSearch/Queries/{id}/Search" }
    };

    private static string? BodyOf(string method, string path, DateTime updated)
        => method is "GET" or "DELETE" ? null
            : path.EndsWith("/Duplicate", StringComparison.Ordinal) || path.EndsWith("/Search", StringComparison.Ordinal) ? "{}"
            : Body("Body", ifUpdatedAt: method == "PUT" ? updated : null);

    private static string Stamp(JsonElement query) => query.GetProperty("UpdatedUtc").GetString()!;

    private static DateTime Updated(JsonElement query) => DateTime.Parse(Stamp(query), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static Guid Id(JsonElement query) => Guid.Parse(query.GetProperty("Id").GetString()!);

    private static FakeRow Movie(PlannerWorld world, string name, CollectionFolder? library = null, Action<RowSpec>? set = null)
        => world.Add(BaseItemKind.Movie, name, library ?? world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
            set?.Invoke(s);
        });

    private static async Task<JsonElement> CreatedAsync(SearchApiHost host, User user, string body)
    {
        var (status, created, text) = await host.SendAsync("POST", "/SmartSearch/Queries", user, body);
        Assert.True(status == HttpStatusCode.Created, text);
        return created;
    }

    private static async Task<JsonElement> ProblemAsync(SearchApiHost host, string method, string path, User user, string? body, HttpStatusCode expected, string code)
    {
        var (status, problem, text) = await host.SendAsync(method, path, user, body);
        Assert.True(status == expected, method + " " + path + ": " + status + " " + text);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("Code", out _), "the problem is camelCase");
        return problem;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_Is401Anonymous_403ForAnApiKey_And403WhileThePageIsOff_BeforeTheBodyIsRead(string method, string route)
    {
        await using var host = await new SearchApiHost().StartAsync();
        var mine = await CreatedAsync(host, host.Kid, Body("Mine"));
        var path = route.Replace("{id}", Id(mine).ToString(), StringComparison.Ordinal);
        var body = BodyOf(method, path, Updated(mine));

        using (var anonymous = await host.SendRawAsync(method, path, null, json: body))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var apiKey = await host.SendRawAsync(method, path, "apikey", json: body))
        {
            Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);
        }

        // Off: 403 for everyone, with a body that isn't even JSON (the switch comes first) and an id that isn't one.
        host.Enabled = false;
        foreach (var user in new[] { host.Admin, host.Kid })
        {
            var (status, off, text) = await host.SendAsync(method, route.Replace("{id}", "not-an-id", StringComparison.Ordinal), user, method is "GET" or "DELETE" ? null : "{not json");
            Assert.True(status == HttpStatusCode.Forbidden, text);
            Assert.Equal(SearchPageGate.DisabledMessage, off.GetProperty("detail").GetString());
        }

        Assert.Single(host.Store.Current.Queries);
    }

    [Fact]
    public async Task TheOwner_SavesOpensListsChangesDuplicatesAndDeletes()
    {
        await using var host = await new SearchApiHost().StartAsync();

        using (var response = await host.SendRawAsync("POST", "/SmartSearch/Queries", "user", host.Kid, Body("Films")))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.EndsWith("/SmartSearch/Queries/" + host.Store.Current.Queries.Single().Id, response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        var id = host.Store.Current.Queries.Single().Id;
        var opened = await host.OkAsync("GET", "/SmartSearch/Queries/" + id, host.Kid);
        Assert.Equal(
            ["Id", "Name", "Description", "OwnerName", "OwnerDeleted", "IsOwner", "CanEdit", "Shared", "Query", "QueryHash", "Supported", "Valid", "Errors", "UsesPerUserFields", "UsesAdminOnlyFields", "CreatedUtc", "UpdatedUtc", "AutoCollectionCount", "AffectedAutoCollections"],
            opened.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Films", opened.GetProperty("Name").GetString());
        Assert.False(opened.TryGetProperty("OwnerUserId", out _), "the owner's id is for administrators only");
        Assert.Equal(host.Kid.Username, opened.GetProperty("OwnerName").GetString());
        Assert.True(opened.GetProperty("IsOwner").GetBoolean());
        Assert.True(opened.GetProperty("CanEdit").GetBoolean());
        Assert.True(opened.GetProperty("Valid").GetBoolean());
        Assert.Equal("Movie", opened.GetProperty("Query").GetProperty("Scope").GetProperty("ItemKinds")[0].GetString());

        var list = await host.OkAsync("GET", "/SmartSearch/Queries?scope=Mine", host.Kid);
        Assert.Equal(["Items", "TotalRecordCount", "StartIndex"], list.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, list.GetProperty("TotalRecordCount").GetInt32());
        Assert.False(list.GetProperty("Items")[0].TryGetProperty("Query", out _), "a list has no definitions");

        var changed = await host.OkAsync("PUT", "/SmartSearch/Queries/" + id, host.Kid, "{\"Name\":\"Films 2000\",\"Description\":\"Old ones\",\"Shared\":true,\"Query\":" + Q() + ",\"IfUpdatedAt\":\"" + Stamp(opened) + "\"}");
        Assert.Equal("Films 2000", changed.GetProperty("Name").GetString());
        Assert.Equal("Old ones", changed.GetProperty("Description").GetString());
        Assert.True(changed.GetProperty("Shared").GetBoolean());
        Assert.Equal(0, changed.GetProperty("AffectedAutoCollections").GetArrayLength());

        // The same IfUpdatedAt again: changed since.
        await ProblemAsync(host, "PUT", "/SmartSearch/Queries/" + id, host.Kid, Body("Again", ifUpdatedAt: Updated(opened)), HttpStatusCode.Conflict, SavedQueryErrors.ChangedCode);

        var (status, copy, text) = await host.SendAsync("POST", "/SmartSearch/Queries/" + id + "/Duplicate", host.Kid, null);
        Assert.True(status == HttpStatusCode.Created, text);
        Assert.Equal("Films 2000 (2)", copy.GetProperty("Name").GetString());
        Assert.False(copy.GetProperty("Shared").GetBoolean());
        (status, _, text) = await host.SendAsync("POST", "/SmartSearch/Queries/" + id + "/Duplicate", host.Kid, "{\"Name\":\"Films 2000\"}");
        Assert.True(status == HttpStatusCode.Conflict, text);

        using (var deleted = await host.SendRawAsync("DELETE", "/SmartSearch/Queries/" + id, "user", host.Kid))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await ProblemAsync(host, "GET", "/SmartSearch/Queries/" + id, host.Kid, null, HttpStatusCode.NotFound, SavedQueryErrors.NotFoundCode);
        Assert.Equal(Id(copy), Assert.Single(host.Store.Current.Queries).Id);
    }

    [Fact]
    public async Task APrivateQuery_IsA404ForEveryoneButItsOwnerAndAnAdministrator()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var mine = await CreatedAsync(host, host.Kid, Body("Private"));
        var path = "/SmartSearch/Queries/" + Id(mine);
        var stamp = Updated(mine);

        foreach (var (method, suffix, body) in new[] { ("GET", "", null), ("PUT", "", Body("X", ifUpdatedAt: stamp)), ("DELETE", "", null), ("POST", "/Duplicate", "{}"), ("POST", "/Search", "{}") })
        {
            var notFound = await ProblemAsync(host, method, path + suffix, host.Twin, body, HttpStatusCode.NotFound, SavedQueryErrors.NotFoundCode);
            Assert.DoesNotContain("Private", notFound.GetRawText(), StringComparison.Ordinal);

            // As an id nobody has.
            await ProblemAsync(host, method, "/SmartSearch/Queries/" + Missing + suffix, host.Twin, body, HttpStatusCode.NotFound, SavedQueryErrors.NotFoundCode);
        }

        Assert.Equal(0, (await host.OkAsync("GET", "/SmartSearch/Queries", host.Twin)).GetProperty("TotalRecordCount").GetInt32());

        // An administrator opens and changes it; it stays Kid's, and Kid sees who changed nothing else.
        var opened = await host.OkAsync("GET", path, host.Admin);
        Assert.False(opened.GetProperty("IsOwner").GetBoolean());
        Assert.True(opened.GetProperty("CanEdit").GetBoolean());
        var changed = await host.OkAsync("PUT", path, host.Admin, Body("Renamed by admin", ifUpdatedAt: stamp));
        Assert.Equal(host.Kid.Id, Guid.Parse(changed.GetProperty("OwnerUserId").GetString()!));
        Assert.Equal(host.Admin.Id, host.Store.Find(Id(mine))!.UpdatedByUserId);
        Assert.Equal(0, (await host.OkAsync("GET", "/SmartSearch/Queries", host.Admin)).GetProperty("TotalRecordCount").GetInt32());
    }

    [Fact]
    public async Task ASharedQuery_OthersOpenRunAndDuplicate_ButNeverChangeOrDelete()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var shared = await CreatedAsync(host, host.Kid, Body("Shared", shared: true));
        var path = "/SmartSearch/Queries/" + Id(shared);

        var opened = await host.OkAsync("GET", path, host.Twin);
        Assert.False(opened.GetProperty("IsOwner").GetBoolean());
        Assert.False(opened.GetProperty("CanEdit").GetBoolean());
        Assert.Equal(host.Kid.Username, opened.GetProperty("OwnerName").GetString());
        Assert.False(opened.TryGetProperty("LastRunUtc", out _));
        var listed = await host.OkAsync("GET", "/SmartSearch/Queries?scope=Shared", host.Twin);
        Assert.Equal(Id(shared), Id(listed.GetProperty("Items")[0]));
        Assert.Equal(0, (await host.OkAsync("GET", "/SmartSearch/Queries?scope=Shared", host.Kid)).GetProperty("TotalRecordCount").GetInt32());

        await ProblemAsync(host, "PUT", path, host.Twin, Body("Mine now", ifUpdatedAt: Updated(shared)), HttpStatusCode.Forbidden, SavedQueryErrors.NotOwnerCode);
        await ProblemAsync(host, "DELETE", path, host.Twin, null, HttpStatusCode.Forbidden, SavedQueryErrors.NotOwnerCode);

        var (status, copy, text) = await host.SendAsync("POST", path + "/Duplicate", host.Twin, "{}");
        Assert.True(status == HttpStatusCode.Created, text);
        Assert.Equal("Shared", copy.GetProperty("Name").GetString());
        Assert.True(copy.GetProperty("IsOwner").GetBoolean());
        Assert.False(copy.GetProperty("Shared").GetBoolean());
        Assert.Equal(host.Twin.Id, host.Store.Find(Id(copy))!.OwnerUserId);

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("POST", path + "/Search", host.Twin, "{}")).Status);
    }

    [Fact]
    public async Task ASharedQuery_RunsAsTheCaller_NeverAsItsOwner()
    {
        var world = new PlannerWorld();
        var open = Movie(world, "Open");
        var hidden = Movie(world, "Private", world.Private);
        var rated = Movie(world, "Rated R", set: s =>
        {
            s.OfficialRating = "R";
            s.InheritedRating = 17;
        });
        var gore = Movie(world, "Gory", set: s => s.Tags = ["Gore"]);
        await using var host = await new SearchApiHost(world).StartAsync();

        // The administrator's shared query: every item for them, only what each user may see for the others.
        var shared = await CreatedAsync(host, host.Admin, Body("Everything kept", shared: true));
        var path = "/SmartSearch/Queries/" + Id(shared) + "/Search";
        static Guid[] Ids(JsonElement page) => page.GetProperty("Items").EnumerateArray().Select(i => Guid.Parse(i.GetProperty("Id").GetString()!)).Order().ToArray();

        Assert.Equal(new[] { open.Id, hidden.Id, rated.Id, gore.Id }.Order(), Ids(await host.OkAsync("POST", path, host.Admin, "{}")));
        var kid = await host.OkAsync("POST", path, host.Kid, "{\"Limit\":10}");
        Assert.Equal([open.Id], Ids(kid));
        Assert.Equal(1, kid.GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain("Private", kid.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(new[] { open.Id, hidden.Id }.Order(), Ids(await host.OkAsync("POST", path, host.Twin, string.Empty)));

        // Kid's own query reaches no further than Kid either, and an administrator running it sees everything.
        var kids = await CreatedAsync(host, host.Kid, Body("Kid's", shared: true));
        Assert.Equal([open.Id], Ids(await host.OkAsync("POST", "/SmartSearch/Queries/" + Id(kids) + "/Search", host.Kid, "{}")));
        Assert.Equal(4, (await host.OkAsync("POST", "/SmartSearch/Queries/" + Id(kids) + "/Search", host.Admin, "{}")).GetProperty("TotalRecordCount").GetInt32());

        // Pages: the token of the first page.
        var first = await host.OkAsync("POST", path, host.Admin, "{\"Limit\":2}");
        var token = first.GetProperty("ResultToken").GetString();
        var second = await host.OkAsync("POST", path, host.Admin, "{\"StartIndex\":2,\"Limit\":2,\"ResultToken\":\"" + token + "\"}");
        Assert.Equal(2, second.GetProperty("Items").GetArrayLength());
    }

    [Fact]
    public async Task TheOwnersRun_IsNotedHourly_OthersRunsAreNot()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var shared = await CreatedAsync(host, host.Kid, Body("Shared", shared: true));
        var id = Id(shared);

        await host.OkAsync("POST", "/SmartSearch/Queries/" + id + "/Search", host.Twin, "{}");
        Assert.Null(host.Store.Find(id)!.LastRunUtc);

        await host.OkAsync("POST", "/SmartSearch/Queries/" + id + "/Search", host.Kid, "{}");
        var noted = host.Store.Find(id)!.LastRunUtc;
        Assert.NotNull(noted);
        var updated = host.Store.Find(id)!.UpdatedUtc;

        host.Clock.Advance(TimeSpan.FromMinutes(30));
        await host.OkAsync("POST", "/SmartSearch/Queries/" + id + "/Search", host.Kid, "{}");
        Assert.Equal(noted, host.Store.Find(id)!.LastRunUtc);
        host.Clock.Advance(TimeSpan.FromMinutes(31));
        await host.OkAsync("POST", "/SmartSearch/Queries/" + id + "/Search", host.Kid, "{}");
        Assert.True(host.Store.Find(id)!.LastRunUtc > noted);
        Assert.Equal(updated, host.Store.Find(id)!.UpdatedUtc);

        Assert.True((await host.OkAsync("GET", "/SmartSearch/Queries/" + id, host.Kid)).TryGetProperty("LastRunUtc", out _));
        Assert.False((await host.OkAsync("GET", "/SmartSearch/Queries/" + id, host.Twin)).TryGetProperty("LastRunUtc", out _));
    }

    [Fact]
    public async Task WithSharingOff_SharedQueriesAreTheirOwnersAlone_AndNothingNewIsShared()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var shared = await CreatedAsync(host, host.Kid, Body("Shared", shared: true));
        host.AllowSharing = false;

        await ProblemAsync(host, "GET", "/SmartSearch/Queries/" + Id(shared), host.Twin, null, HttpStatusCode.NotFound, SavedQueryErrors.NotFoundCode);
        Assert.Equal(0, (await host.OkAsync("GET", "/SmartSearch/Queries", host.Twin)).GetProperty("TotalRecordCount").GetInt32());
        await ProblemAsync(host, "POST", "/SmartSearch/Queries", host.Kid, Body("New shared", shared: true), HttpStatusCode.Forbidden, SavedQueryErrors.SharingDisabledCode);

        // Kept shared by its owner, it comes back when sharing is on again; a private one can't be shared meanwhile.
        var kept = await host.OkAsync("PUT", "/SmartSearch/Queries/" + Id(shared), host.Kid, Body("Shared", shared: true, ifUpdatedAt: Updated(shared)));
        var other = await CreatedAsync(host, host.Kid, Body("Private"));
        await ProblemAsync(host, "PUT", "/SmartSearch/Queries/" + Id(other), host.Kid, Body("Private", shared: true, ifUpdatedAt: Updated(other)), HttpStatusCode.Forbidden, SavedQueryErrors.SharingDisabledCode);
        host.AllowSharing = true;
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("GET", "/SmartSearch/Queries/" + Id(kept), host.Twin)).Status);
    }

    [Fact]
    public async Task AnAdministratorOnlyField_IsSavedOnlyInAnAdministratorsOwnPrivateQueries()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var adminOnly = Q(AdminOnly);

        await ProblemAsync(host, "POST", "/SmartSearch/Queries", host.Kid, Body("Paths", adminOnly), HttpStatusCode.Forbidden, SmartSearchService.AdminOnlyFieldCode);
        await ProblemAsync(host, "POST", "/SmartSearch/Queries", host.Admin, Body("Paths", adminOnly, shared: true), HttpStatusCode.Forbidden, SavedQueryErrors.SharedAdminOnlyFieldCode);
        var own = await CreatedAsync(host, host.Admin, Body("Paths", adminOnly));
        Assert.True(own.GetProperty("UsesAdminOnlyFields").GetBoolean());

        // In Kid's query an administrator can't put one: Kid couldn't run it.
        var kids = await CreatedAsync(host, host.Kid, Body("Kid's"));
        await ProblemAsync(host, "PUT", "/SmartSearch/Queries/" + Id(kids), host.Admin, Body("Kid's", adminOnly, ifUpdatedAt: Updated(kids)), HttpStatusCode.Forbidden, SmartSearchService.AdminOnlyFieldCode);

        // One stored shared anyway (an older file, an edit by hand) is never another user's to open, run or copy.
        var stored = Record(host.Admin.Id, "Stored shared", adminOnly, shared: true);
        await AddAsync(host.Store, stored);
        foreach (var (method, suffix, body) in new[] { ("GET", "", (string?)null), ("POST", "/Search", "{}"), ("POST", "/Duplicate", "{}") })
        {
            await ProblemAsync(host, method, "/SmartSearch/Queries/" + stored.Id + suffix, host.Kid, body, HttpStatusCode.NotFound, SavedQueryErrors.NotFoundCode);
        }

        Assert.DoesNotContain(stored.Id.ToString(), (await host.OkAsync("GET", "/SmartSearch/Queries", host.Kid)).GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("POST", "/SmartSearch/Queries/" + stored.Id + "/Search", host.Admin, "{}")).Status);

        // Given to Kid by an administrator, Kid's run is refused as Kid's search would be.
        var mine = Record(host.Kid.Id, "Kid's paths", adminOnly);
        await AddAsync(host.Store, mine);
        await ProblemAsync(host, "POST", "/SmartSearch/Queries/" + mine.Id + "/Search", host.Kid, "{}", HttpStatusCode.Forbidden, SmartSearchService.AdminOnlyFieldCode);
        await ProblemAsync(host, "POST", "/SmartSearch/Queries/" + mine.Id + "/Duplicate", host.Kid, "{}", HttpStatusCode.Forbidden, SmartSearchService.AdminOnlyFieldCode);
    }

    [Theory]
    [InlineData("{\"Name\":\"A\",\"Query\":{},\"Bogus\":1}", "Bogus", "UnknownProperty")]
    [InlineData("{\"Query\":{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[]}}}", "Name", "Required")]
    [InlineData("{\"Name\":\"   \",\"Query\":{}}", "Name", "Required")]
    [InlineData("{\"Name\":\"A\",\"Shared\":\"yes\",\"Query\":{}}", "Shared", "InvalidValue")]
    [InlineData("{\"Name\":\"A\",\"IfUpdatedAt\":\"2026-01-01T00:00:00Z\",\"Query\":{}}", "IfUpdatedAt", "UnknownProperty")]
    public async Task ASaveBodyThatIsntOne_Is400InvalidRequest(string body, string path, string code)
    {
        await using var host = await new SearchApiHost().StartAsync();

        var problem = await ProblemAsync(host, "POST", "/SmartSearch/Queries", host.Kid, body, HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);

        Assert.Equal(["title", "status", "detail", "code", "errors"], problem.EnumerateObject().Select(p => p.Name));
        var error = problem.GetProperty("errors").EnumerateArray().First(e => e.GetProperty("path").GetString() == path);
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Empty(host.Store.Current.Queries);
    }

    [Fact]
    public async Task TheOtherBodies_AreReadStrictly_AndHaveTheirLimits()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var mine = await CreatedAsync(host, host.Kid, Body("Mine"));
        var path = "/SmartSearch/Queries/" + Id(mine);

        // A change needs IfUpdatedAt, as read.
        var problem = await ProblemAsync(host, "PUT", path, host.Kid, Body("Mine"), HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);
        Assert.Equal("IfUpdatedAt", problem.GetProperty("errors")[0].GetProperty("path").GetString());
        await ProblemAsync(host, "PUT", path, host.Kid, Body("Mine", extra: ",\"IfUpdatedAt\":\"yesterday\""), HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);

        await ProblemAsync(host, "POST", path + "/Search", host.Kid, "{\"Limit\":10,\"Query\":{}}", HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);
        await ProblemAsync(host, "POST", path + "/Duplicate", host.Kid, "{\"Name\":1}", HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);
        await ProblemAsync(host, "POST", path + "/Duplicate", host.Kid, "[]", HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);
        await ProblemAsync(host, "GET", "/SmartSearch/Queries/not-an-id", host.Kid, null, HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);
        await ProblemAsync(host, "GET", "/SmartSearch/Queries?scope=Others", host.Kid, null, HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);
        await ProblemAsync(host, "GET", "/SmartSearch/Queries?limit=501", host.Kid, null, HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);

        // Names and descriptions have their lengths; a query its errors, with their paths.
        await ProblemAsync(host, "POST", "/SmartSearch/Queries", host.Kid, Body(new string('n', 101)), HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);
        await ProblemAsync(host, "POST", "/SmartSearch/Queries", host.Kid, Body("Long", extra: ",\"Description\":\"" + new string('d', 501) + "\""), HttpStatusCode.BadRequest, SmartSearchRequestBinder.InvalidRequestCode);
        var invalid = await ProblemAsync(host, "POST", "/SmartSearch/Queries", host.Kid, Body("Bad", Q("""{"Field":"Year","Op":"Contains","Value":2000}""")), HttpStatusCode.BadRequest, SmartSearchService.InvalidQueryCode);
        Assert.Equal("Where.Children[0].Op", invalid.GetProperty("errors")[0].GetProperty("path").GetString());

        // Over the save limit: 413 before anything is read.
        var (status, _, text) = await host.SendAsync("POST", "/SmartSearch/Queries", host.Kid, Body("Huge", extra: ",\"Description\":\"" + new string('d', SmartSearchRequestLimits.MaxSaveBodyBytes) + "\""));
        Assert.True(status == HttpStatusCode.RequestEntityTooLarge, text);
        Assert.Single(host.Store.Current.Queries);
    }

    [Fact]
    public async Task AQueryOfANewerVersion_IsListedAndOpened_ButNeitherChangedNorRun_AndCanBeCopiedOrDeleted()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var newer = Record(host.Kid.Id, "From the future", "{\"Version\":99,\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[{\"Field\":\"Mood\",\"Op\":\"Eq\",\"Value\":\"calm\"}]}}");
        await AddAsync(host.Store, newer);
        var path = "/SmartSearch/Queries/" + newer.Id;

        var opened = await host.OkAsync("GET", path, host.Kid);
        Assert.False(opened.GetProperty("Supported").GetBoolean());
        Assert.False(opened.GetProperty("Valid").GetBoolean());
        Assert.Equal(99, opened.GetProperty("Query").GetProperty("Version").GetInt32());
        Assert.False((await host.OkAsync("GET", "/SmartSearch/Queries", host.Kid)).GetProperty("Items")[0].GetProperty("Supported").GetBoolean());

        await ProblemAsync(host, "PUT", path, host.Kid, Body("Changed", ifUpdatedAt: newer.UpdatedUtc), HttpStatusCode.Conflict, SavedQueryErrors.NeedsNewerVersionCode);
        await ProblemAsync(host, "POST", path + "/Search", host.Kid, "{}", HttpStatusCode.Conflict, SavedQueryErrors.NeedsNewerVersionCode);

        var (status, copy, text) = await host.SendAsync("POST", path + "/Duplicate", host.Kid, "{}");
        Assert.True(status == HttpStatusCode.Created, text);
        Assert.Equal(newer.Query.GetRawText(), host.Store.Find(Id(copy))!.Query.GetRawText());
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("DELETE", path, host.Kid)).Status);
    }

    [Fact]
    public async Task TheReferenceGuard_RefusesInTheKitsShape_AndANotedReferenceIsCounted()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard }.StartAsync();
        var mine = await CreatedAsync(host, host.Kid, Body("Used"));
        var id = Id(mine);
        var path = "/SmartSearch/Queries/" + id;
        guard.Referenced.Add(id);
        Assert.Equal(1, (await host.OkAsync("GET", "/SmartSearch/Queries", host.Kid)).GetProperty("Items")[0].GetProperty("AutoCollectionCount").GetInt32());

        foreach (var (next, expected, code) in new[]
        {
            (SavedQueryGuardStatus.ManagerRequired, HttpStatusCode.Forbidden, SavedQueryErrors.ManagerRequiredCode),
            (SavedQueryGuardStatus.BreaksReferences, HttpStatusCode.Conflict, SavedQueryErrors.BreaksAutoCollectionsCode),
            (SavedQueryGuardStatus.NeedsAttention, HttpStatusCode.Conflict, SavedQueryErrors.NeedsAttentionCode)
        })
        {
            guard.Next = next;
            var problem = await ProblemAsync(host, "PUT", path, host.Kid, Body("Changed", ifUpdatedAt: Updated(mine)), expected, code);
            var reference = Assert.Single(problem.GetProperty("autoCollections").EnumerateArray());
            Assert.Equal(["id", "name", "messages"], reference.EnumerateObject().Select(p => p.Name));
            Assert.Equal("Used", host.Store.Find(id)!.Name);
        }

        guard.Next = SavedQueryGuardStatus.Done;
        var changed = await host.OkAsync("PUT", path, host.Kid, Body("Changed", ifUpdatedAt: Updated(mine), extra: ",\"AcknowledgeAutoCollectionWarnings\":true"));
        Assert.True(guard.Edits[^1].AcknowledgeWarnings);
        Assert.Equal("Changed", Assert.Single(changed.GetProperty("AffectedAutoCollections").EnumerateArray()).GetProperty("Messages")[0].GetString());

        await ProblemAsync(host, "DELETE", path, host.Kid, null, HttpStatusCode.Conflict, SavedQueryErrors.ReferencedCode);
        Assert.NotNull(host.Store.Find(id));
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("DELETE", path + "?detachAutoCollections=true", host.Kid)).Status);
        Assert.True(guard.Deletions[^1].DetachReferences);
        Assert.Null(host.Store.Find(id));
    }
}
