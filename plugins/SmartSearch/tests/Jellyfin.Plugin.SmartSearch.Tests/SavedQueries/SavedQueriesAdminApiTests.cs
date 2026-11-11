using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport.Api;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Api;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Api;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.SavedQueries.SavedQueryTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>
/// <c>/SmartSearch/Admin</c>'s saved-query tools on the wire: administrators and API keys only; every user's queries, a
/// new owner, export and import (a round trip that keeps every query, those this version can't run included), the
/// deleted-user tools and the snapshots.
/// </summary>
public sealed class SavedQueriesAdminApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Routes => new()
    {
        { "GET", "/SmartSearch/Admin/Queries" },
        { "POST", "/SmartSearch/Admin/Queries/{id}/Owner" },
        { "GET", "/SmartSearch/Admin/Export" },
        { "POST", "/SmartSearch/Admin/Import" },
        { "GET", "/SmartSearch/Admin/Users" },
        { "POST", "/SmartSearch/Admin/Users/Remap" },
        { "POST", "/SmartSearch/Admin/Users/ApplyPolicy" },
        { "GET", "/SmartSearch/Admin/Snapshots" },
        { "POST", "/SmartSearch/Admin/Snapshots/Restore?name=none.json" }
    };

    private static async Task<(HttpStatusCode Status, string Text)> AsApiKeyAsync(SearchApiHost host, string method, string path, string? json = null)
    {
        using var response = await host.SendRawAsync(method, path, "apikey", json: json);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_Is401Anonymous_And403ForAUser_EvenWhileThePageIsOff(string method, string route)
    {
        await using var host = await new SearchApiHost().StartAsync();
        var mine = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Mine"), Ct);
        var path = route.Replace("{id}", mine.Id.ToString(), StringComparison.Ordinal);
        var body = method == "POST" ? "{}" : null;

        using (var anonymous = await host.SendRawAsync(method, path, null, json: body))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        foreach (var user in new[] { host.Kid, host.Twin })
        {
            using var forbidden = await host.SendRawAsync(method, path, "user", user, body);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        // The admin's tools don't depend on the search page.
        host.Enabled = false;
        if (method == "GET")
        {
            Assert.Equal(HttpStatusCode.OK, (await AsApiKeyAsync(host, method, path)).Status);
            Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(method, path, host.Admin)).Status);
        }

        Assert.Single(host.Store.Current.Queries);
    }

    [Fact]
    public async Task TheList_HoldsEveryUsersQueries_WithTheirOwners()
    {
        await using var host = await new SearchApiHost().StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kid's private"), Ct);
        await host.SavedQueries.CreateAsync(host.Twin, false, Request("Twin's private"), Ct);
        await host.SavedQueries.CreateAsync(host.Admin, true, Request("Admin's paths", Q(AdminOnly)), Ct);

        var all = await host.OkAsync("GET", "/SmartSearch/Admin/Queries", host.Admin);
        Assert.Equal(3, all.GetProperty("TotalRecordCount").GetInt32());
        var kid = await host.OkAsync("GET", "/SmartSearch/Admin/Queries?userId=" + host.Kid.Id, host.Admin);
        var item = Assert.Single(kid.GetProperty("Items").EnumerateArray());
        Assert.Equal("Kid's private", item.GetProperty("Name").GetString());
        Assert.Equal(host.Kid.Username, item.GetProperty("OwnerName").GetString());

        var (status, text) = await AsApiKeyAsync(host, "GET", "/SmartSearch/Admin/Queries?limit=0");
        Assert.True(status == HttpStatusCode.BadRequest, text);
    }

    [Fact]
    public async Task ANewOwner_TakesTheQuery_WithANumberedNameWhenTheirsIsTaken()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard }.StartAsync();
        var kids = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Films"), Ct);
        await host.SavedQueries.CreateAsync(host.Twin, false, Request("Films"), Ct);
        var path = "/SmartSearch/Admin/Queries/" + kids.Id + "/Owner";

        var (status, text) = await AsApiKeyAsync(host, "POST", path, "{\"UserId\":\"" + host.Twin.Id + "\"}");
        Assert.True(status == HttpStatusCode.NoContent, text);
        var moved = host.Store.Find(kids.Id)!;
        Assert.Equal(host.Twin.Id, moved.OwnerUserId);
        Assert.Equal("Films (2)", moved.Name);
        Assert.True(moved.UpdatedUtc > kids.UpdatedUtc);
        Assert.False(host.Store.KnownUsers.ContainsKey(host.Kid.Id));

        var (problemStatus, problem, problemText) = await host.SendAsync("POST", path, host.Admin, "{\"UserId\":\"" + Guid.NewGuid() + "\"}");
        Assert.True(problemStatus == HttpStatusCode.BadRequest, problemText);
        Assert.Equal(SavedQueryErrors.UnknownUserCode, problem.GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync("POST", path, host.Admin, "{\"UserId\":\"x\"}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync("POST", path, host.Admin, "{\"UserId\":\"" + host.Kid.Id + "\",\"Name\":\"x\"}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync("POST", "/SmartSearch/Admin/Queries/" + Guid.NewGuid() + "/Owner", host.Admin, "{\"UserId\":\"" + host.Kid.Id + "\"}")).Status);

        // A query an auto-collection uses moves with it, alone only when forced.
        guard.Referenced.Add(kids.Id);
        (problemStatus, problem, problemText) = await host.SendAsync("POST", path, host.Admin, "{\"UserId\":\"" + host.Kid.Id + "\"}");
        Assert.True(problemStatus == HttpStatusCode.Conflict, problemText);
        Assert.Equal(SavedQueryErrors.BreaksAutoCollectionsCode, problem.GetProperty("code").GetString());
        Assert.Single(problem.GetProperty("autoCollections").EnumerateArray());
        Assert.Equal(host.Twin.Id, host.Store.Find(kids.Id)!.OwnerUserId);
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync("POST", path + "?force=true", host.Admin, "{\"UserId\":\"" + host.Kid.Id + "\"}")).Status);
        Assert.Equal(host.Kid.Id, host.Store.Find(kids.Id)!.OwnerUserId);
    }

    [Fact]
    public async Task ExportThenImport_RestoresEveryQuery_ThoseThisVersionCantRunIncluded()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var shared = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared", shared: true, description: "Kept as it was"), Ct);
        await host.SavedQueries.CreateAsync(host.Twin, false, Request("Unwatched", Q(PerUser)), Ct);
        var newer = Record(host.Kid.Id, "From the future", "{\"Version\":99,\"Where\":{\"Children\":[{\"Field\":\"Mood\"}]}}");
        var broken = Record(host.Twin.Id, "Broken", "{\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Children\":[{\"Field\":\"Year\",\"Op\":\"Contains\",\"Value\":1}]}}");
        var adminOnly = Record(host.Admin.Id, "Admin's paths, shared by hand", Q(AdminOnly), shared: true);
        await AddAsync(host.Store, newer, broken, adminOnly);
        var before = host.Store.Current.Queries.ToDictionary(q => q.Id);

        string export;
        using (var response = await host.SendRawAsync("GET", "/SmartSearch/Admin/Export", "apikey"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("smartsearch-saved-queries", response.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
            export = await response.Content.ReadAsStringAsync(Ct);
        }

        using (var envelope = JsonDocument.Parse(export))
        {
            Assert.Equal(5, envelope.RootElement.GetProperty("Data").GetProperty("Queries").GetArrayLength());
            Assert.Equal(3, envelope.RootElement.GetProperty("Users").GetArrayLength());
        }

        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, []), Ct);

        // A dry run changes nothing.
        var dry = await host.OkAsync("POST", "/SmartSearch/Admin/Import", host.Admin, export);
        Assert.True(dry.GetProperty("DryRun").GetBoolean());
        Assert.Equal(5, dry.GetProperty("Records").GetProperty("Added").GetInt32());
        Assert.Empty(host.Store.Current.Queries);

        var report = await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, export);
        Assert.False(report.GetProperty("DryRun").GetBoolean());
        Assert.Contains(report.GetProperty("Notes").EnumerateArray(), n => n.GetString()!.Contains("administrator-only", StringComparison.Ordinal));

        var after = host.Store.Current.Queries.ToDictionary(q => q.Id);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (id, was) in before)
        {
            var now = after[id];
            Assert.Equal(was.OwnerUserId, now.OwnerUserId);
            Assert.Equal(was.Name, now.Name);
            Assert.Equal(was.Description, now.Description);
            // A query this version reads and finds valid comes back as a save writes it; any other as it was.
            var facts = host.Reader.Read(was);
            var expected = facts is { Definition: { } definition, Supported: true, Valid: true } ? Jellyfin.Plugin.SmartSearch.Query.QueryJson.Write(definition) : was.Query.GetRawText();
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(expected), System.Text.Json.Nodes.JsonNode.Parse(now.Query.GetRawText())), was.Name);
            Assert.Equal(was.UpdatedUtc, now.UpdatedUtc);
            Assert.Equal(id != adminOnly.Id && was.Shared, now.Shared);
        }

        Assert.Equal(before[shared.Id].QueryHash, after[shared.Id].QueryHash);
        Assert.False(host.Reader.Read(after[newer.Id]).Supported);
        Assert.False(host.Reader.Read(after[broken.Id]).Valid);

        // Again, as a merge: nothing new; a name taken since gets a number, the query already here keeps its own.
        await host.SavedQueries.DeleteAsync(host.Kid, false, shared.Id, false, Ct);
        var replacement = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared"), Ct);
        var again = await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false&mode=Merge", host.Admin, export);
        Assert.Equal(1, again.GetProperty("Records").GetProperty("Added").GetInt32());
        Assert.Equal("Shared", host.Store.Find(replacement.Id)!.Name);
        Assert.Equal("Shared (2)", host.Store.Find(shared.Id)!.Name);
        Assert.Contains(again.GetProperty("Notes").EnumerateArray(), n => n.GetString()!.Contains("renamed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnImport_SkipsTheQueriesOfOwnersNotFound_AndRefusesWhatIsntAnExport()
    {
        await using var host = await new SearchApiHost().StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kid's"), Ct);
        await host.SavedQueries.CreateAsync(host.Twin, false, Request("Twin's"), Ct);
        string export;
        using (var response = await host.SendRawAsync("GET", "/SmartSearch/Admin/Export", "apikey"))
        {
            export = await response.Content.ReadAsStringAsync(Ct);
        }

        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, []), Ct);
        host.Users.Remove(host.Twin);

        var report = await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, export);
        Assert.Equal(host.Kid.Id, Assert.Single(host.Store.Current.Queries).OwnerUserId);
        Assert.Single(report.GetProperty("Users").GetProperty("Unknown").EnumerateArray());

        var (status, _, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, "{\"Queries\":[]}");
        Assert.True(status == HttpStatusCode.BadRequest, text);
        (status, _, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Import?mode=Sideways", host.Admin, export);
        Assert.True(status == HttpStatusCode.BadRequest, text);
    }

    [Fact]
    public async Task TheDeletedUserTools_ListApplyAndRemap()
    {
        await using var host = await new SearchApiHost { Policy = DeletedUserPolicy.KeepAsOrphan }.StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Private"), Ct);
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared", shared: true), Ct);
        host.Users.Remove(host.Kid);
        await host.Reconciler.HandleUserDeletedAsync(host.Kid.Id, Ct);

        var users = await host.OkAsync("GET", "/SmartSearch/Admin/Users", host.Admin);
        Assert.Contains(host.Kid.Id.ToString("N"), users.GetRawText().Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

        // Remap by name: never a user deleted on this server; an owner from elsewhere (an import from another server, a
        // reinstall) goes to the user of their name here (a dry run first).
        var dry = await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name", host.Admin);
        Assert.Contains("DeletedOnThisServer", dry.GetRawText(), StringComparison.Ordinal);
        var foreign = Record(Guid.NewGuid(), "From elsewhere", Q());
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, foreign]) with { KnownUsers = d.KnownUsers.With(foreign.OwnerUserId, host.Twin.Username) }, Ct);
        dry = await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name", host.Admin);
        Assert.Contains(host.Twin.Id.ToString("N"), dry.GetRawText().Replace("-", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(foreign.OwnerUserId, host.Store.Find(foreign.Id)!.OwnerUserId);
        await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false", host.Admin);
        Assert.Equal(host.Twin.Id, host.Store.Find(foreign.Id)!.OwnerUserId);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Id", host.Admin)).Status);

        // The policy applied later to the deleted user: Delete takes the private queries.
        host.Policy = DeletedUserPolicy.Delete;
        await host.OkAsync("POST", "/SmartSearch/Admin/Users/ApplyPolicy", host.Admin, "{\"UserIds\":[\"" + host.Kid.Id + "\"]}");
        Assert.Equal(["From elsewhere", "Shared"], host.Store.Current.Queries.Select(q => q.Name).Order());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync("POST", "/SmartSearch/Admin/Users/ApplyPolicy", host.Admin, "{\"UserIds\":[\"x\"]}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync("POST", "/SmartSearch/Admin/Users/ApplyPolicy", host.Admin, "{}")).Status);
    }

    [Fact]
    public async Task ASnapshot_IsListedAndRestored()
    {
        await using var host = await new SearchApiHost().StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Before"), Ct);
        var snapshot = await host.Store.Documents.SnapshotAsync(host.Store.SnapshotDirectory, 7, Ct);
        Assert.NotNull(snapshot);
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("After"), Ct);

        var listed = await host.OkAsync("GET", "/SmartSearch/Admin/Snapshots", host.Admin);
        var name = System.IO.Path.GetFileName(snapshot);
        Assert.Contains(name, listed.GetRawText(), StringComparison.Ordinal);

        var restored = await host.OkAsync("POST", "/SmartSearch/Admin/Snapshots/Restore?name=" + Uri.EscapeDataString(name), host.Admin);
        Assert.Equal(1, restored.GetProperty("Queries").GetInt32());
        Assert.Equal("Before", Assert.Single(host.Store.Current.Queries).Name);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync("POST", "/SmartSearch/Admin/Snapshots/Restore", host.Admin)).Status);
    }
}
