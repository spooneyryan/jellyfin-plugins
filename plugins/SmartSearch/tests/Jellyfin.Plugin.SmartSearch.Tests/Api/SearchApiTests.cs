using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Tests.Planning;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.Api;

/// <summary>
/// <c>POST /SmartSearch/Search</c> and <c>/Validate</c> on the wire: every route signed-in only, the body read strictly,
/// the query's errors with their paths, the engine's refusals as problems with their codes, each result scoped to the
/// caller (libraries, parental limit, unrated kinds, blocked tags), merged versions as one result, pages through the
/// result token, the settings that turn the endpoints off or narrow the item kinds, and what the API explorer documents.
/// </summary>
public sealed class SearchApiTests
{
    private const string InMemory = """{"Field":"Overview","Op":"Contains","Value":"keep"}""";

    private const string Exact = """{"Field":"Year","Op":"Eq","Value":2000}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Routes => new()
    {
        { "POST", "/SmartSearch/Search" },
        { "POST", "/SmartSearch/Validate" },
        { "GET", "/SmartSearch/Fields" },
        { "GET", "/SmartSearch/Libraries" },
        { "GET", "/SmartSearch/Values/Genres" },
        { "GET", "/SmartSearch/Series?prefix=he" }
    };

    // A valid body for a route: the search request, or the validate request (the query alone).
    private static string? Body(string method, string path)
        => method != "POST" ? null : path.EndsWith("/Validate", StringComparison.Ordinal) ? "{\"Query\":" + Q(InMemory) + "}" : SearchApiHost.Body(Q(InMemory));

    private static string Q(string rules, string kinds = "\"Movie\"", IReadOnlyList<Guid>? libraries = null) => QueryPlannerTests.Q(rules, kinds, libraries);

    // A movie of 2000 with a "keep" overview that Kid may see unless the test changes it.
    private static FakeRow Movie(PlannerWorld world, string name, CollectionFolder? library = null, Action<RowSpec>? set = null)
        => world.Add(BaseItemKind.Movie, name, library ?? world.Movies, s =>
        {
            s.Year = 2000;
            s.Overview = "keep";
            s.OfficialRating = "PG";
            s.InheritedRating = 10;
            set?.Invoke(s);
        });

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_Is401Anonymous_And403ForAnApiKey(string method, string path)
    {
        await using var host = await new SearchApiHost().StartAsync();
        var body = Body(method, path);

        using (var anonymous = await host.SendRawAsync(method, path, null, json: body))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using var apiKey = await host.SendRawAsync(method, path, "apikey", json: body);
        Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);
    }

    [Theory]
    [InlineData(InMemory, "Residual")]
    [InlineData(Exact, "Fast")]
    public async Task ASearch_ShowsEachCallerOnlyWhatTheyMaySee_OnEitherPath(string rule, string path)
    {
        var world = new PlannerWorld();
        var open = Movie(world, "Open");
        var other = Movie(world, "Other folder", set: s => s.TopParentId = world.FolderB);
        var home = world.Add(BaseItemKind.Movie, "Home movie", world.Home, s =>
        {
            s.Year = 2000;
            s.Overview = "keep";
            s.OfficialRating = "G";
            s.InheritedRating = 0;
        });
        var hidden = Movie(world, "Private", world.Private);
        var rated = Movie(world, "Rated R", set: s =>
        {
            s.OfficialRating = "R";
            s.InheritedRating = 17;
        });
        var unrated = Movie(world, "Unrated", set: s =>
        {
            s.OfficialRating = null;
            s.InheritedRating = null;
        });
        var gore = Movie(world, "Gory", set: s => s.Tags = ["Gore"]);
        await using var host = await new SearchApiHost(world).StartAsync();
        var query = Q(rule);
        Assert.Equal(path, (await host.Engine.Engine.ExplainAsync(host.Engine.Read(query), world.Kid, PlanPurpose.Search, Ct)).Path.ToString());

        var admin = await host.SearchAllAsync(query, host.Admin);
        var kid = await host.SearchAllAsync(query, host.Kid, limit: 2);

        Assert.Equal(new[] { open.Id, other.Id, home.Id, hidden.Id, rated.Id, unrated.Id, gore.Id }.Order(), admin.Order());
        Assert.Equal(new[] { open.Id, other.Id, home.Id }.Order(), kid.Order());
        var first = await host.OkAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(query, 0, 2));
        Assert.Equal(3, first.GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain("Private", first.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pages_FollowTheFirstPagesToken_AndTheBodyCarriesThePageFields()
    {
        var world = new PlannerWorld();
        for (var i = 0; i < 5; i++)
        {
            Movie(world, "Film " + i);
        }

        await using var host = await new SearchApiHost(world).StartAsync();
        var query = Q(InMemory);

        var first = await host.OkAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(query, 0, 2));
        Assert.Equal(["Items", "TotalRecordCount", "StartIndex", "ResultToken", "HasMore", "ScanInProgress", "Warnings", "ElapsedMs"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal(5, first.GetProperty("TotalRecordCount").GetInt32());
        Assert.True(first.GetProperty("HasMore").GetBoolean());
        Assert.False(first.GetProperty("ScanInProgress").GetBoolean());
        var token = first.GetProperty("ResultToken").GetString();
        Assert.False(string.IsNullOrEmpty(token));

        var second = await host.OkAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(query, 2, 2, token));
        Assert.Equal(2, second.GetProperty("StartIndex").GetInt32());
        Assert.Equal(2, second.GetProperty("Items").GetArrayLength());
        var ids = await host.SearchAllAsync(query, host.Admin, limit: 2);
        Assert.Equal(5, ids.Distinct().Count());

        // A token is the caller's own: another user's first page never reuses it.
        var kidsPage = await host.OkAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(query, 0, 2, token));
        Assert.Equal(5, kidsPage.GetProperty("TotalRecordCount").GetInt32());
        Assert.NotEqual(token, kidsPage.GetProperty("ResultToken").GetString());

        host.ScanRunning = true;
        Assert.True((await host.OkAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(query))).GetProperty("ScanInProgress").GetBoolean());
    }

    [Fact]
    public async Task MergedVersions_AreOneResult_ForEachCaller()
    {
        var world = new PlannerWorld();
        var ronin = Movie(world, "Ronin", world.Private);
        var roninCut = Movie(world, "Ronin", set: s => s.TopParentId = world.FolderB);
        world.Core.Merge(ronin, roninCut);
        var heat = Movie(world, "Heat");
        var heatCut = Movie(world, "Heat", set: s => s.TopParentId = world.FolderB);
        world.Core.Merge(heat, heatCut);
        await using var host = await new SearchApiHost(world).StartAsync();

        foreach (var rule in new[] { InMemory, Exact })
        {
            Assert.Equal(new[] { heat.Id, ronin.Id }.Order(), (await host.SearchAllAsync(Q(rule), host.Admin)).Order());
            Assert.Equal(new[] { heat.Id, roninCut.Id }.Order(), (await host.SearchAllAsync(Q(rule), host.Kid)).Order());
        }
    }

    [Fact]
    public async Task ALibraryTheCallerCantOpen_IsAWarning_TheSameAsOneThatDoesntExist()
    {
        var world = new PlannerWorld();
        Movie(world, "Open");
        Movie(world, "Private", world.Private);
        await using var host = await new SearchApiHost(world).StartAsync();

        var hidden = await host.OkAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(Q(InMemory, libraries: [world.Private.Id])));
        var missing = await host.OkAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(Q(InMemory, libraries: [Guid.NewGuid()])));
        var mixed = await host.OkAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(Q(InMemory, libraries: [world.Private.Id, world.Movies.Id])));
        var admin = await host.OkAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(Q(InMemory, libraries: [world.Private.Id])));

        Assert.Equal(hidden.GetProperty("Warnings").GetRawText(), missing.GetProperty("Warnings").GetRawText());
        Assert.Contains(SmartSearchService.LibrariesUnavailableWarning, hidden.GetProperty("Warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal(0, hidden.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(1, mixed.GetProperty("TotalRecordCount").GetInt32());
        Assert.Contains(SmartSearchService.LibrariesUnavailableWarning, mixed.GetProperty("Warnings").EnumerateArray().Select(w => w.GetString()));
        Assert.Equal(1, admin.GetProperty("TotalRecordCount").GetInt32());
        Assert.DoesNotContain(SmartSearchService.LibrariesUnavailableWarning, admin.GetProperty("Warnings").EnumerateArray().Select(w => w.GetString()));
    }

    [Theory]
    [InlineData("""{"Query":{},"Bogus":1}""", "Bogus", "UnknownProperty")]
    [InlineData("""{"Query":{},"Limit":5,"limit":6}""", "limit", "InvalidJson")]
    [InlineData("""{"Query":{},"Limit":201}""", "Limit", "OutOfRange")]
    [InlineData("""{"Query":{},"Limit":"5"}""", "Limit", "InvalidValue")]
    [InlineData("""{"Query":{},"StartIndex":-1}""", "StartIndex", "OutOfRange")]
    [InlineData("""{"Query":{},"IncludeTotal":1}""", "IncludeTotal", "InvalidValue")]
    [InlineData("""{"Query":{}""", "", "InvalidJson")]
    [InlineData("""[]""", "", "NotAnObject")]
    public async Task ABodyThatIsntASearchRequest_Is400InvalidRequest_WithItsErrors(string body, string path, string code)
    {
        await using var host = await new SearchApiHost().StartAsync();

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Search", host.Kid, body);

        Assert.True(status == HttpStatusCode.BadRequest, text);
        Assert.Equal(SmartSearchRequestBinder.InvalidRequestCode, problem.GetProperty("code").GetString());
        var error = Assert.Single(problem.GetProperty("errors").EnumerateArray());
        Assert.Equal((path, code), (error.GetProperty("path").GetString(), error.GetProperty("code").GetString()));

        // Its items are camelCase, like the problem's own members.
        Assert.Equal(["path", "code", "message"], error.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("POST", "/SmartSearch/Search", "{\"Query\":")]
    [InlineData("POST", "/SmartSearch/Validate", "[]")]
    [InlineData("GET", "/SmartSearch/Values/Genres?limit=abc", null)]
    [InlineData("GET", "/SmartSearch/Series?prefix=he&limit=x", null)]
    public async Task WhoMayCall_IsCheckedBeforeTheBodyOrTheParameters(string method, string path, string? body)
    {
        await using var host = await new SearchApiHost().StartAsync();

        // Anonymous: 401; an API key: 403; a user while the page is off: 403 turned off; never the 400 of what was sent.
        using (var anonymous = await host.SendRawAsync(method, path, null, json: body))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using (var apiKey = await host.SendRawAsync(method, path, "apikey", json: body))
        {
            Assert.Equal(HttpStatusCode.Forbidden, apiKey.StatusCode);
            using var problem = JsonDocument.Parse(await apiKey.Content.ReadAsStringAsync(Ct));
            Assert.Equal(403, problem.RootElement.GetProperty("status").GetInt32());
        }

        host.Enabled = false;
        var (status, off, text) = await host.SendAsync(method, path, host.Kid, body);
        Assert.True(status == HttpStatusCode.Forbidden, text);
        Assert.Equal(SearchPageGate.DisabledMessage, off.GetProperty("detail").GetString());

        // On, the same request is a 400 in the kit's shape, with camelCase errors.
        host.Enabled = true;
        (status, var problem400, text) = await host.SendAsync(method, path, host.Kid, body);
        Assert.True(status == HttpStatusCode.BadRequest, text);
        Assert.Equal(SmartSearchRequestBinder.InvalidRequestCode, problem400.GetProperty("code").GetString());
        Assert.Equal(["title", "status", "detail", "code", "errors"], problem400.EnumerateObject().Select(p => p.Name));
        Assert.All(problem400.GetProperty("errors").EnumerateArray(), e => Assert.Equal(["path", "code", "message"], e.EnumerateObject().Select(p => p.Name)));
    }

    [Fact]
    public async Task AQuerysErrors_Are400InvalidQuery_EachWithItsPathInsideTheQuery()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var query = """{"Scope":{"ItemKinds":["Movie"]},"Where":{"Children":[{"Field":"Year","Op":"Eq","Value":"soon"},{"Field":"Title","Op":"Contains","Value":"x","Negat":true}]}}""";

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(query));

        Assert.True(status == HttpStatusCode.BadRequest, text);
        Assert.Equal(SmartSearchService.InvalidQueryCode, problem.GetProperty("code").GetString());
        var errors = problem.GetProperty("errors").EnumerateArray().Select(e => (e.GetProperty("path").GetString(), e.GetProperty("code").GetString())).ToList();
        Assert.Contains(("Where.Children[1].Negat", "UnknownProperty"), errors);
        Assert.Contains(errors, e => e.Item1 == "Where.Children[0].Value");
        Assert.Equal(2, errors.Count);

        // A query given twice a name, or with no query at all.
        (status, problem, _) = await host.SendAsync("POST", "/SmartSearch/Search", host.Kid, """{"Query":{"Version":1,"Version":1}}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(SmartSearchService.InvalidQueryCode, problem.GetProperty("code").GetString());
        (status, problem, _) = await host.SendAsync("POST", "/SmartSearch/Search", host.Kid, """{"Limit":5}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Required", Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ARequestOrAQueryTooLarge_Is413()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var bigQuery = Q("{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"" + new string('a', 65_600) + "\"}");
        var hugeBody = SearchApiHost.Body(Q("{\"Field\":\"Title\",\"Op\":\"Contains\",\"Value\":\"" + new string('a', 70_000) + "\"}"));

        foreach (var route in new[] { "/SmartSearch/Search", "/SmartSearch/Validate" })
        {
            var (status, problem, text) = await host.SendAsync("POST", route, host.Kid, "{\"Query\":" + bigQuery + "}");
            Assert.True(status == HttpStatusCode.RequestEntityTooLarge, route + " " + text);
            Assert.Equal(SmartSearchService.TooLargeCode, problem.GetProperty("code").GetString());

            (status, _, text) = await host.SendAsync("POST", route, host.Kid, hugeBody);
            Assert.True(status == HttpStatusCode.RequestEntityTooLarge, route + " " + text);
        }
    }

    [Fact]
    public async Task AnAdministratorOnlyField_Is403ForOthers_AndRunsForAnAdministrator()
    {
        var world = new PlannerWorld();
        Movie(world, "Film");
        await using var host = await new SearchApiHost(world).StartAsync();
        var query = Q("""{"Field":"Path","Op":"Contains","Value":"film"}""");

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(query));
        Assert.True(status == HttpStatusCode.Forbidden, text);
        Assert.Equal(SmartSearchService.AdminOnlyFieldCode, problem.GetProperty("code").GetString());
        Assert.Equal("Where.Children[0].Field", Assert.Single(problem.GetProperty("errors").EnumerateArray()).GetProperty("path").GetString());

        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(query))).Status);

        // Validate answers 200 and says so.
        var check = await host.OkAsync("POST", "/SmartSearch/Validate", host.Kid, "{\"Query\":" + query + "}");
        Assert.False(check.GetProperty("Valid").GetBoolean());
        Assert.True(check.GetProperty("UsesAdminOnlyFields").GetBoolean());
        Assert.Equal("AdminOnlyField", Assert.Single(check.GetProperty("Errors").EnumerateArray()).GetProperty("Code").GetString());
    }

    [Fact]
    public async Task ForceResidual_IsForAdministrators_FindsWhatTheQueryFinds_AndIsCachedApart()
    {
        var world = new PlannerWorld();
        Movie(world, "Film A");
        Movie(world, "Film B");
        Movie(world, "Film C", set: s => s.Year = 2001);
        await using var host = await new SearchApiHost(world).StartAsync();
        var query = Q(Exact);
        string Forced(int start = 0, string? token = null)
            => "{\"Query\":" + query + ",\"StartIndex\":" + start + ",\"Limit\":1,\"ForceResidual\":true" + (token is null ? string.Empty : ",\"ResultToken\":\"" + token + "\"") + "}";

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Search", host.Kid, Forced());
        Assert.True(status == HttpStatusCode.Forbidden, text);
        Assert.Equal(SmartSearchService.AdminOnlyOptionCode, problem.GetProperty("code").GetString());

        var (badStatus, badProblem, _) = await host.SendAsync("POST", "/SmartSearch/Search", host.Admin, "{\"Query\":" + query + ",\"ForceResidual\":1}");
        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
        Assert.Equal(SmartSearchRequestBinder.InvalidRequestCode, badProblem.GetProperty("code").GetString());

        // The same two movies through both plans, each result under its own token.
        var plain = await host.OkAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(query, 0, 1));
        var forced = await host.OkAsync("POST", "/SmartSearch/Search", host.Admin, Forced());
        Assert.Equal(2, plain.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(2, forced.GetProperty("TotalRecordCount").GetInt32());
        Assert.NotEqual(plain.GetProperty("ResultToken").GetString(), forced.GetProperty("ResultToken").GetString());
        var second = await host.OkAsync("POST", "/SmartSearch/Search", host.Admin, Forced(1, forced.GetProperty("ResultToken").GetString()));
        Assert.Empty(second.GetProperty("Warnings").EnumerateArray());
        var ids = new[] { forced, second }.SelectMany(p => p.GetProperty("Items").EnumerateArray()).Select(i => Guid.Parse(i.GetProperty("Id").GetString()!));
        Assert.Equal((await host.SearchAllAsync(query, host.Admin)).Order(), ids.Order());
    }

    [Fact]
    public async Task ATooBroadQuery_Is400QueryTooBroad_WithTheCandidateCountAndKinds()
    {
        var world = new PlannerWorld();
        for (var i = 0; i < 6; i++)
        {
            Movie(world, "Film " + i);
        }

        await using var host = await new SearchApiHost(world).StartAsync();
        host.Engine.Settings = new EngineSettings(MaxCandidates: 5);

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(Q(InMemory)));

        Assert.True(status == HttpStatusCode.BadRequest, text);
        Assert.Equal(SmartSearchRefusedException.QueryTooBroadCode, problem.GetProperty("code").GetString());
        Assert.Equal(6, problem.GetProperty("candidateCount").GetInt32());
        Assert.Equal(["Movie"], problem.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task PastTheTimeout_ASearchIs503Timeout()
    {
        var world = new PlannerWorld();
        for (var i = 0; i < 2500; i++)
        {
            Movie(world, "Film " + i);
        }

        await using var host = await new SearchApiHost(world).StartAsync();
        host.Engine.Settings = new EngineSettings(SearchTimeoutSeconds: 5);
        var batches = 0;
        world.Core.OnQuery = q =>
        {
            if (q.ItemIds.Length > 0 && ++batches == 2)
            {
                host.Engine.Clock.Advance(TimeSpan.FromSeconds(6));
            }
        };

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(Q(InMemory)));

        Assert.True(status == HttpStatusCode.ServiceUnavailable, text);
        Assert.Equal(SmartSearchRefusedException.TimeoutCode, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WhenEverySlotIsTaken_ASearchIs503Busy()
    {
        var world = new PlannerWorld();
        Movie(world, "Film");
        await using var host = await new SearchApiHost(world, slots: 1, slotWait: TimeSpan.FromMilliseconds(100)).StartAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var block = 1;
        world.Core.OnQuery = q =>
        {
            if (Interlocked.Exchange(ref block, 0) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        };

        var first = Task.Run(() => host.SendAsync("POST", "/SmartSearch/Search", host.Admin, SearchApiHost.Body(Q(InMemory))), Ct);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), Ct));

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(Q(InMemory)));
        Assert.True(status == HttpStatusCode.ServiceUnavailable, text);
        Assert.Equal(SmartSearchRefusedException.BusyCode, problem.GetProperty("code").GetString());

        release.Set();
        Assert.Equal(HttpStatusCode.OK, (await first).Status);
    }

    [Fact]
    public async Task WhileTheSearchPageIsOff_EveryEndpointIs403()
    {
        await using var host = await new SearchApiHost().StartAsync();
        host.Enabled = false;

        foreach (var (method, path) in new[] { ("POST", "/SmartSearch/Search"), ("POST", "/SmartSearch/Validate"), ("GET", "/SmartSearch/Fields"), ("GET", "/SmartSearch/Libraries"), ("GET", "/SmartSearch/Values/Genres"), ("GET", "/SmartSearch/Series?prefix=he") })
        {
            var body = Body(method, path);
            foreach (var user in new[] { host.Admin, host.Kid })
            {
                var (status, problem, text) = await host.SendAsync(method, path, user, body);
                Assert.True(status == HttpStatusCode.Forbidden, method + " " + path + " " + text);
                Assert.Equal(SearchPageGate.DisabledMessage, problem.GetProperty("detail").GetString());
            }
        }
    }

    [Fact]
    public async Task AnItemKindTheSettingLeavesOut_IsAQueryError_AndTheFieldsListTheAllowedKinds()
    {
        await using var host = await new SearchApiHost().StartAsync();
        host.AllowedKinds = [BaseItemKind.Movie, BaseItemKind.Series];

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Search", host.Kid, SearchApiHost.Body(Q(InMemory, "\"Movie\",\"Episode\"")));
        Assert.True(status == HttpStatusCode.BadRequest, text);
        Assert.Equal(("Scope.ItemKinds[1]", "ItemKindNotAllowed"), Assert.Single(problem.GetProperty("errors").EnumerateArray()) is var e ? (e.GetProperty("path").GetString(), e.GetProperty("code").GetString()) : default);

        var fields = await host.OkAsync("GET", "/SmartSearch/Fields", host.Kid);
        Assert.Equal(["Movie", "Series"], fields.GetProperty("ItemKinds").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public async Task Validate_ReportsTheErrors_AsA200_AndThePathToAnAdministratorOnly()
    {
        var world = new PlannerWorld();
        Movie(world, "Film");
        await using var host = await new SearchApiHost(world).StartAsync();

        var residual = await host.OkAsync("POST", "/SmartSearch/Validate", host.Admin, "{\"Query\":" + Q(InMemory) + "}");
        Assert.True(residual.GetProperty("Valid").GetBoolean());
        Assert.Equal(["Residual"], residual.GetProperty("Paths").EnumerateArray().Select(p => p.GetString()));
        Assert.Empty(residual.GetProperty("Errors").EnumerateArray());

        var fast = await host.OkAsync("POST", "/SmartSearch/Validate", host.Admin, "{\"Query\":" + Q(Exact) + "}");
        Assert.Equal(["Fast"], fast.GetProperty("Paths").EnumerateArray().Select(p => p.GetString()));

        // Anyone else gets what the query is and uses, never how the server would run it.
        var kid = await host.OkAsync("POST", "/SmartSearch/Validate", host.Kid, "{\"Query\":" + Q(InMemory) + "}");
        Assert.True(kid.GetProperty("Valid").GetBoolean());
        Assert.False(kid.TryGetProperty("Paths", out _));
        Assert.Equal(["Valid", "Supported", "Errors", "UsesPerUserFields", "UsesAdminOnlyFields", "UnavailableFields"], kid.EnumerateObject().Select(p => p.Name));

        var played = await host.OkAsync("POST", "/SmartSearch/Validate", host.Kid, "{\"Query\":" + Q("""{"Field":"Played","Op":"Is","Value":true}""") + "}");
        Assert.True(played.GetProperty("UsesPerUserFields").GetBoolean());

        var bad = await host.OkAsync("POST", "/SmartSearch/Validate", host.Kid, """{"Query":{"Scope":{"ItemKinds":["Movie"]},"Where":{"Children":[{"Field":"Year","Op":"Eq","Value":"soon"}]}}}""");
        Assert.False(bad.GetProperty("Valid").GetBoolean());
        Assert.Equal("Where.Children[0].Value", Assert.Single(bad.GetProperty("Errors").EnumerateArray()).GetProperty("Path").GetString());
        Assert.False(bad.TryGetProperty("Paths", out _));
        Assert.Empty((await host.OkAsync("POST", "/SmartSearch/Validate", host.Admin, """{"Query":{"Scope":{"ItemKinds":["Movie"]},"Where":{"Children":[{"Field":"Year","Op":"Eq","Value":"soon"}]}}}""")).GetProperty("Paths").EnumerateArray());

        var unread = await host.OkAsync("POST", "/SmartSearch/Validate", host.Kid, """{"Query":{"Version":1,"Version":2}}""");
        Assert.False(unread.GetProperty("Valid").GetBoolean());

        var (status, problem, _) = await host.SendAsync("POST", "/SmartSearch/Validate", host.Kid, """{"Query":{},"Limit":5}""");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(SmartSearchRequestBinder.InvalidRequestCode, problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheApiExplorer_DescribesTheBodiesAndTheLookupsParameters()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var descriptions = host.Web!.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .ToList();
        ApiDescription Find(string method, string route) => Assert.Single(descriptions, d => d.HttpMethod == method && d.RelativePath == route);

        Assert.Equal(typeof(SmartSearchRequestDto), Assert.Single(Find("POST", "SmartSearch/Search").ParameterDescriptions, p => p.Source == BindingSource.Body).Type);
        Assert.Equal(typeof(SmartSearchValidateRequestDto), Assert.Single(Find("POST", "SmartSearch/Validate").ParameterDescriptions, p => p.Source == BindingSource.Body).Type);
        Assert.Contains(Find("POST", "SmartSearch/Search").SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(SmartSearchResultDto));
        Assert.Contains(Find("POST", "SmartSearch/Validate").SupportedResponseTypes, r => r.StatusCode == 200 && r.Type == typeof(SmartSearchValidationDto));

        var values = Find("GET", "SmartSearch/Values/{fieldId}");
        Assert.Equal(["fieldId", "prefix", "libraryIds", "limit"], values.ParameterDescriptions.Select(p => p.Name));
        Assert.Equal(typeof(Guid[]), values.ParameterDescriptions.Single(p => p.Name == "libraryIds").Type);
        Assert.Equal(["prefix", "limit"], Find("GET", "SmartSearch/Series").ParameterDescriptions.Select(p => p.Name));
        Assert.Empty(Find("GET", "SmartSearch/Libraries").ParameterDescriptions);
    }
}
