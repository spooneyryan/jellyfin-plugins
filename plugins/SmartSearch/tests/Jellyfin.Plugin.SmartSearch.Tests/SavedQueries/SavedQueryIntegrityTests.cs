using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Api;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.SavedQueries.SavedQueryTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>
/// What keeps the saved queries whole across the administrator's bulk changes and odd records: an import, a restore, a
/// remap and an owner change go through the reference guard (a dry run lists what they would break, a real run refuses
/// it unless forced); an import skips what it can't store, with a note, and its dry run says what the real run does;
/// times are UTC; an owner change or a remap never gives a user an administrator-only query, nor takes them past the
/// quota; a query this version can't read is never another user's to open; a duplicate is a new query.
/// </summary>
public sealed class SavedQueryIntegrityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> ExportAsync(SearchApiHost host)
    {
        using var response = await host.SendRawAsync("GET", "/SmartSearch/Admin/Export", "apikey");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static string Mutate(string export, Action<JsonArray> change)
    {
        var root = JsonNode.Parse(export)!;
        change(root["Data"]!["Queries"]!.AsArray());
        return root.ToJsonString();
    }

    private static string Owner(Guid id) => "{\"UserId\":\"" + id + "\"}";

    private static List<string> Notes(JsonElement report) => report.GetProperty("Notes").EnumerateArray().Select(n => n.GetString()!).ToList();

    private static List<Guid> Broken(JsonElement report) => report.GetProperty("BrokenAutoCollections").EnumerateArray().Select(r => Guid.Parse(r.GetProperty("Id").GetString()!)).ToList();

    [Fact]
    public async Task AnImportRecordWithoutAQuery_IsSkippedWithANote_AndTheDryRunAndTheRealRunAgree()
    {
        await using var host = await new SearchApiHost().StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kid's"), Ct);
        var body = Mutate(await ExportAsync(host), q =>
        {
            var copy = q[0]!.DeepClone();
            copy["Id"] = Guid.NewGuid().ToString();
            copy["Name"] = "No query";
            copy.AsObject().Remove("Query");
            q.Add(copy);
            var empty = q[0]!.DeepClone();
            empty["Id"] = Guid.NewGuid().ToString();
            empty["Name"] = "Null query";
            empty["Query"] = null;
            q.Add(empty);
        });

        var dry = await host.OkAsync("POST", "/SmartSearch/Admin/Import", host.Admin, body);
        var real = await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, body);

        Assert.Equal(dry.GetProperty("Records").GetRawText(), real.GetProperty("Records").GetRawText());
        Assert.Equal(Notes(dry), Notes(real));
        Assert.Equal(2, real.GetProperty("Records").GetProperty("Skipped").GetInt32());
        Assert.Equal(3, real.GetProperty("Records").GetProperty("Incoming").GetInt32());
        Assert.Contains("Record 2 has no query and was skipped.", Notes(real));
        Assert.Contains("Record 3 has no query and was skipped.", Notes(real));
        Assert.Equal(["Kid's"], host.Store.Current.Queries.Select(q => q.Name));
        Assert.DoesNotContain("No query", await System.IO.File.ReadAllTextAsync(host.Store.Documents.FilePath, Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Created, (await host.SendAsync("POST", "/SmartSearch/Queries", host.Kid, Body("After"))).Status);
    }

    [Fact]
    public async Task AnImport_RefusesRepeatedAndEmptyIds_WithANoteEach_AndHonestCounts()
    {
        await using var host = await new SearchApiHost().StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kid's"), Ct);
        var body = Mutate(await ExportAsync(host), q =>
        {
            var again = q[0]!.DeepClone();
            again["Name"] = "Same id, other name";
            q.Add(again);
            var third = q[0]!.DeepClone();
            third["Name"] = "Same id, a third time";
            q.Add(third);
            var empty = q[0]!.DeepClone();
            empty["Id"] = Guid.Empty.ToString();
            empty["Name"] = "Empty id";
            q.Add(empty);
        });
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, []), Ct);

        var report = await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, body);

        Assert.Equal(["Kid's"], host.Store.Current.Queries.Select(q => q.Name));
        var records = report.GetProperty("Records");
        Assert.Equal(4, records.GetProperty("Incoming").GetInt32());
        Assert.Equal(1, records.GetProperty("Added").GetInt32());
        Assert.Equal(3, records.GetProperty("Skipped").GetInt32());
        Assert.Equal(
            ["Record 2 has the id of an earlier record in the file and was skipped.", "Record 3 has the id of an earlier record in the file and was skipped.", "Record 4 has no id and was skipped."],
            Notes(report));
    }

    [Fact]
    public async Task AnImport_SkipsAQueryLargerThanASavedOne_AndOneAdministratorOnlyForAUser_AndThosePastTheQuota()
    {
        await using var host = await new SearchApiHost { MaxPerUser = 2 }.StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kid's"), Ct);
        var body = Mutate(await ExportAsync(host), q =>
        {
            JsonNode Copy(string name)
            {
                var copy = q[0]!.DeepClone();
                copy["Id"] = Guid.NewGuid().ToString();
                copy["Name"] = name;
                return copy;
            }

            var large = Copy("Large");
            large["Query"]!["Pad"] = new string('x', 70 * 1024);
            q.Add(large);
            var paths = Copy("Paths");
            paths["Query"] = JsonNode.Parse(Q(AdminOnly));
            q.Add(paths);
            q.Add(Copy("Second"));
            q.Add(Copy("Third"));
        });
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, []), Ct);

        var dry = await host.OkAsync("POST", "/SmartSearch/Admin/Import", host.Admin, body);
        var report = await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, body);

        Assert.Equal(dry.GetProperty("Records").GetRawText(), report.GetProperty("Records").GetRawText());
        Assert.Equal(["Kid's", "Second"], host.Store.Current.Queries.Select(q => q.Name).Order());
        Assert.Equal(3, report.GetProperty("Records").GetProperty("Skipped").GetInt32());
        var notes = Notes(report);
        Assert.Contains("Record 2 has a query larger than a saved query may be (64 KB) and was skipped.", notes);
        Assert.Contains("Record 3 uses an administrator-only field, and its owner here isn't an administrator: it was skipped.", notes);
        Assert.Contains(notes, n => n.StartsWith("1 queries would take their owner past", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnImportThatWouldBreakReferences_ListsThemInItsDryRun_IsRefusedWithoutForce_AndGoesAheadWithIt()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard }.StartAsync();
        var kept = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kept"), Ct);
        var export = await ExportAsync(host);
        var used = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Used"), Ct);
        guard.Referenced.Add(used.Id);

        // Replace: the export doesn't have the query an auto-collection uses.
        var dry = await host.OkAsync("POST", "/SmartSearch/Admin/Import?mode=Replace", host.Admin, export);
        Assert.Single(Broken(dry));
        Assert.Equal(0, guard.BulkChanges);

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Import?mode=Replace&dryRun=false", host.Admin, export);
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.Equal(SavedQueryErrors.BreaksAutoCollectionsCode, problem.GetProperty("code").GetString());
        Assert.Single(problem.GetProperty("autoCollections").EnumerateArray());
        Assert.NotNull(host.Store.Find(used.Id));
        Assert.Equal(1, guard.BulkChanges);
        Assert.True(guard.Checked[^1].InsideBulk, "the real run checks inside the guard's bulk change");

        var forced = await host.OkAsync("POST", "/SmartSearch/Admin/Import?mode=Replace&dryRun=false&force=true", host.Admin, export);
        Assert.Single(Broken(forced));
        Assert.Null(host.Store.Find(used.Id));
        Assert.NotNull(host.Store.Find(kept.Id));

        // A merge that changes a used query's definition breaks it too.
        guard.Referenced.Add(kept.Id);
        var changed = Mutate(export, q =>
        {
            q[0]!["Query"] = JsonNode.Parse(Q(PerUser));
            q[0]!["UpdatedUtc"] = "2030-01-01T00:00:00Z";
        });
        (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, changed);
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.Contains("Its query would change", problem.GetProperty("autoCollections")[0].GetProperty("messages")[0].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARestoreThatWouldBreakReferences_ListsThemInItsDryRun_IsRefusedWithoutForce_AndGoesAheadWithIt()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard }.StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Before"), Ct);
        var name = System.IO.Path.GetFileName(await host.Store.Documents.SnapshotAsync(host.Store.SnapshotDirectory, 7, Ct));
        var used = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Used"), Ct);
        guard.Referenced.Add(used.Id);
        var path = "/SmartSearch/Admin/Snapshots/Restore?name=" + Uri.EscapeDataString(name!);

        var dry = await host.OkAsync("POST", path + "&dryRun=true", host.Admin);
        Assert.True(dry.GetProperty("DryRun").GetBoolean());
        Assert.Equal(1, dry.GetProperty("Queries").GetInt32());
        Assert.Single(Broken(dry));
        Assert.NotNull(host.Store.Find(used.Id));

        var (status, problem, text) = await host.SendAsync("POST", path, host.Admin);
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.Equal(SavedQueryErrors.BreaksAutoCollectionsCode, problem.GetProperty("code").GetString());
        Assert.NotNull(host.Store.Find(used.Id));
        Assert.True(guard.Checked[^1].InsideBulk);

        var forced = await host.OkAsync("POST", path + "&force=true", host.Admin);
        Assert.Single(Broken(forced));
        Assert.Equal("Before", Assert.Single(host.Store.Current.Queries).Name);
    }

    [Fact]
    public async Task ARemapThatWouldBreakReferences_OrGiveAUserAnAdministratorOnlyQuery_IsRefused_AndItsDryRunSaysSo()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard }.StartAsync();
        var foreign = Record(Guid.NewGuid(), "From elsewhere", Q());
        var paths = Record(foreign.OwnerUserId, "Paths from elsewhere", Q(AdminOnly));
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, foreign, paths]) with { KnownUsers = d.KnownUsers.With(foreign.OwnerUserId, host.Twin.Username) }, Ct);
        guard.Referenced.Add(foreign.Id);

        var dry = await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name", host.Admin);
        Assert.Single(Broken(dry));
        Assert.Contains("administrator-only", dry.GetProperty("Refusal").GetString(), StringComparison.Ordinal);

        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false", host.Admin);
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.Equal(SavedQueryErrors.BreaksAutoCollectionsCode, problem.GetProperty("code").GetString());

        // Forced past the references, the administrator-only query still can't go to a user.
        (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false&force=true", host.Admin);
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.Equal(QueryErrorCodes.AdminOnlyField, problem.GetProperty("code").GetString());
        Assert.Equal(foreign.OwnerUserId, host.Store.Find(foreign.Id)!.OwnerUserId);
        Assert.Equal(2, guard.BulkChanges);

        // Without it, the forced remap goes ahead.
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, d.Queries.Where(q => q.Id != paths.Id)), Ct);
        var forced = await host.OkAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false&force=true", host.Admin);
        Assert.Single(Broken(forced));
        Assert.Equal(host.Twin.Id, host.Store.Find(foreign.Id)!.OwnerUserId);
    }

    [Fact]
    public async Task AnOwnerChange_ThatWouldGiveAUserAnAdministratorOnlyQuery_OrTakeThemPastTheQuota_Is409()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard, MaxPerUser = 1 }.StartAsync();
        var adminQuery = await host.SavedQueries.CreateAsync(host.Admin, true, Request("Paths", Q(AdminOnly)), Ct);
        var path = "/SmartSearch/Admin/Queries/" + adminQuery.Id + "/Owner";

        using (var response = await host.SendRawAsync("POST", path, "apikey", json: Owner(host.Kid.Id)))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains(QueryErrorCodes.AdminOnlyField, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Equal(host.Admin.Id, host.Store.Find(adminQuery.Id)!.OwnerUserId);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync("GET", "/SmartSearch/Queries/" + adminQuery.Id, host.Kid)).Status);

        // The quota of the new owner holds.
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kid's only one"), Ct);
        var plain = await host.SavedQueries.CreateAsync(host.Twin, false, Request("Twin's"), Ct);
        var (status, problem, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Queries/" + plain.Id + "/Owner", host.Admin, Owner(host.Kid.Id));
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.Equal(SavedQueryErrors.QuotaReachedCode, problem.GetProperty("code").GetString());

        // Each check ran inside the guard's bulk change.
        Assert.Equal(2, guard.BulkChanges);
    }

    [Fact]
    public async Task ATimeWithoutAnOffset_IsReadAsUtc_AndItsIfUpdatedAtMatches()
    {
        await using var host = await new SearchApiHost().StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kid's"), Ct);
        var id = Guid.NewGuid();
        var offset = Guid.NewGuid();
        var body = Mutate(await ExportAsync(host), q =>
        {
            var copy = q[0]!.DeepClone();
            copy["Id"] = id.ToString();
            copy["Name"] = "Local time";
            copy["UpdatedUtc"] = "2026-10-01T12:00:00";
            copy["CreatedUtc"] = "2026-10-01T12:00:00";
            q.Add(copy);
            var other = q[0]!.DeepClone();
            other["Id"] = offset.ToString();
            other["Name"] = "With an offset";
            other["UpdatedUtc"] = "2026-10-01T14:00:00+02:00";
            q.Add(other);
        });
        await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, body);

        var stored = host.Store.Find(id)!;
        Assert.Equal(DateTimeKind.Utc, stored.UpdatedUtc.Kind);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), stored.UpdatedUtc);
        Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), host.Store.Find(offset)!.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, host.Store.Find(offset)!.UpdatedUtc.Kind);

        var updated = (await host.OkAsync("GET", "/SmartSearch/Queries/" + id, host.Kid)).GetProperty("UpdatedUtc").GetString();
        Assert.Equal("2026-10-01T12:00:00.0000000Z", updated);
        var put = "{\"Name\":\"Renamed\",\"Shared\":false,\"Query\":" + Q() + ",\"IfUpdatedAt\":\"" + updated + "\"}";
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("PUT", "/SmartSearch/Queries/" + id, host.Kid, put)).Status);

        // Read from the file the same way: a hand-written time without an offset.
        var file = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(host.Store.Documents.FilePath, Ct))!;
        Assert.EndsWith("Z", file["Queries"]!.AsArray().First(q => (string)q!["Id"]! == offset.ToString())!["UpdatedUtc"]!.GetValue<string>(), StringComparison.Ordinal);
        var record = JsonSerializer.Deserialize<SavedQueryRecord>("{\"Id\":\"" + id + "\",\"UpdatedUtc\":\"2026-10-01T12:00:00\",\"LastRunUtc\":\"2026-10-01T12:00:00\"}", Jellyfin.Plugin.Kit.Storage.JsonStoreFormat.Options)!;
        Assert.Equal(DateTimeKind.Utc, record.UpdatedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, record.LastRunUtc!.Value.Kind);
    }

    [Fact]
    public async Task ASharedQueryThisVersionCantRead_IsOnlyItsOwnersAndAdministrators()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var record = Record(host.Kid.Id, "Mixed", "{\"Version\":1,\"Scope\":{\"ItemKinds\":[\"Movie\"]},\"Where\":{\"Match\":\"All\",\"Children\":[" + AdminOnly + ",{\"Field\":\"Title\",\"Op\":\"FutureOp\",\"Value\":\"x\"}]}}", shared: true);
        await AddAsync(host.Store, record);
        Assert.False(host.Reader.Read(host.Store.Find(record.Id)!).Supported);

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync("GET", "/SmartSearch/Queries/" + record.Id, host.Twin)).Status);
        Assert.DoesNotContain("Mixed", (await host.SendAsync("GET", "/SmartSearch/Queries?scope=Shared", host.Twin)).Text, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("GET", "/SmartSearch/Queries/" + record.Id, host.Kid)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync("GET", "/SmartSearch/Queries/" + record.Id, host.Admin)).Status);
    }

    [Fact]
    public async Task ADuplicate_DropsTheOriginalsFieldsOfANewerVersion()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var shared = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared", shared: true), Ct);
        await host.Store.UpdateAsync(
            d => SavedQueryStore.WithQueries(d, d.Queries.Select(q => q.Id == shared.Id ? q with { Extra = new() { ["FutureLink"] = JsonDocument.Parse("\"private to its owner\"").RootElement.Clone() } } : q)),
            Ct);

        var copy = await host.SavedQueries.DuplicateAsync(host.Twin, false, shared.Id, new SmartSearchSavedQueryDuplicateDto(), Ct);

        Assert.Null(host.Store.Find(copy.Id)!.Extra);
        Assert.NotNull(host.Store.Find(shared.Id)!.Extra);
    }

    [Fact]
    public async Task TheOwnersIdReachesAdministratorsOnly()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var shared = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared", shared: true), Ct);

        foreach (var user in new[] { host.Kid, host.Twin })
        {
            Assert.DoesNotContain("OwnerUserId", (await host.SendAsync("GET", "/SmartSearch/Queries/" + shared.Id, user)).Text, StringComparison.Ordinal);
            Assert.DoesNotContain("OwnerUserId", (await host.SendAsync("GET", "/SmartSearch/Queries", user)).Text, StringComparison.Ordinal);
        }

        Assert.Equal(host.Kid.Id, Guid.Parse((await host.OkAsync("GET", "/SmartSearch/Queries/" + shared.Id, host.Admin)).GetProperty("OwnerUserId").GetString()!));
    }

    [Fact]
    public async Task WithoutReferences_TheGuardBreaksNothing_AndRunsEveryBulkChange()
    {
        var guard = new NoSavedQueryReferences();
        var id = Guid.NewGuid();
        Assert.Empty(guard.FindBrokenReferences(new SavedQueryBulkChange([id], [Guid.NewGuid()])));
        Assert.Equal(0, guard.CountReferences(id));
        Assert.Equal(42, await guard.BulkChangeAsync(_ => Task.FromResult(42), Ct));

        // What a change removes and changes: an owner or a query, not a name.
        var before = new SavedQueriesDocument { Queries = [Record(id, "A", Q()), Record(id, "B", Q()), Record(id, "C", Q())] };
        var after = before with
        {
            Queries = [before.Queries[0] with { Name = "Renamed" }, before.Queries[1] with { OwnerUserId = Guid.NewGuid() }]
        };
        var change = SavedQueryBulkChange.Between(before, after);
        Assert.Equal([before.Queries[2].Id], change.Removed);
        Assert.Equal([before.Queries[1].Id], change.Changed);
    }
}
