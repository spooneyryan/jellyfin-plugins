using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.SavedQueries.SavedQueryTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>
/// Imports in another casing, refused bulk changes and their snapshots, and remap by name with the lock order: the
/// reconciler runs with no lock of the guard's or a store's held, and a refusal part-way names what was already remapped.
/// </summary>
public sealed class SavedQueryBulkChangeTests
{
    private const string LowerCase = """{"version":1,"scope":{"itemKinds":["movie"],"libraryIds":[]},"where":{"match":"any","children":[{"field":"Title","op":"contains","value":"a"},{"field":"Title","op":"contains","value":"b"}]},"sort":[{"key":"productionyear","order":"descending"}]}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> ExportAsync(SearchApiHost host)
    {
        using var response = await host.SendRawAsync("GET", "/SmartSearch/Admin/Export", "apikey");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }

    private static string WithQuery(string export, string query, string updatedUtc = "2030-01-01T00:00:00Z", Guid? id = null)
    {
        var root = JsonNode.Parse(export)!;
        var records = root["Data"]!["Queries"]!.AsArray();
        var record = id is { } wanted ? records.First(r => Guid.Parse(r!["Id"]!.GetValue<string>()) == wanted)! : records[0]!;
        record["Query"] = JsonNode.Parse(query);
        record["UpdatedUtc"] = updatedUtc;
        return root.ToJsonString();
    }

    [Fact]
    public async Task AnImportedQueryInAnotherCasing_IsStoredAsASaveWritesIt_WhileAnInvalidOneStaysAsItCame()
    {
        await using var host = await new SearchApiHost().StartAsync();
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Kid's"), Ct);
        var export = await ExportAsync(host);

        await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, WithQuery(export, LowerCase));

        var id = host.Store.Current.Queries.Single().Id;
        var dto = await host.OkAsync("GET", "/SmartSearch/Queries/" + id, host.Kid);
        Assert.True(dto.GetProperty("Supported").GetBoolean());
        Assert.True(dto.GetProperty("Valid").GetBoolean(), dto.GetRawText());
        var stored = dto.GetProperty("Query");
        Assert.Equal("Any", stored.GetProperty("Where").GetProperty("Match").GetString());
        Assert.Equal(2, stored.GetProperty("Where").GetProperty("Children").GetArrayLength());
        Assert.Equal("Contains", stored.GetProperty("Where").GetProperty("Children")[0].GetProperty("Op").GetString());
        Assert.Equal("Movie", stored.GetProperty("Scope").GetProperty("ItemKinds")[0].GetString());
        Assert.Equal("ProductionYear", stored.GetProperty("Sort")[0].GetProperty("Key").GetString(), ignoreCase: true);
        Assert.Equal("Descending", stored.GetProperty("Sort")[0].GetProperty("Order").GetString());
        Assert.DoesNotContain("\"children\"", stored.GetRawText(), StringComparison.Ordinal);

        // The same query saved through the API is stored the same way, with the same hash.
        var saved = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Saved", LowerCase), Ct);
        var record = host.Store.Find(id)!;
        Assert.Equal(host.Store.Find(saved.Id)!.Query.GetRawText(), record.Query.GetRawText());
        Assert.Equal(host.Store.Find(saved.Id)!.QueryHash, record.QueryHash);

        // A query with an error is kept as it came, to be shown with its errors.
        const string Invalid = """{"version":1,"scope":{"itemKinds":["movie"]},"where":{"match":"all","children":[{"field":"Year","op":"eq","value":1500}]}}""";
        await host.OkAsync("POST", "/SmartSearch/Admin/Import?dryRun=false", host.Admin, WithQuery(await ExportAsync(host), Invalid, "2031-01-01T00:00:00Z", id));
        var kept = host.Store.Find(id)!;
        Assert.Contains("\"children\"", kept.Query.GetRawText(), StringComparison.Ordinal);
        Assert.False(host.Reader.Read(kept).Valid);
    }

    [Fact]
    public async Task ARefusedImportOrRemap_TakesNoPreOperationSnapshot()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard }.StartAsync();
        var export = await ExportAsync(host); // no queries
        var used = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Used"), Ct);
        guard.Referenced.Add(used.Id);
        int Snapshots() => host.Store.Documents.ListSnapshots(host.Store.SnapshotDirectory).Count;
        var before = Snapshots();

        var (status, _, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Import?mode=Replace&dryRun=false", host.Admin, export);
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.NotNull(host.Store.Find(used.Id));
        Assert.Equal(before, Snapshots());

        // A remap that would break a reference: refused before the reconciler snapshots the store.
        var foreign = Record(Guid.NewGuid(), "From elsewhere", Q());
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, foreign]) with { KnownUsers = d.KnownUsers.With(foreign.OwnerUserId, host.Twin.Username) }, Ct);
        guard.Referenced.Add(foreign.Id);
        (status, _, text) = await host.SendAsync("POST", "/SmartSearch/Admin/Users/Remap?by=Name&dryRun=false", host.Admin);
        Assert.True(status == HttpStatusCode.Conflict, text);
        Assert.Equal(foreign.OwnerUserId, host.Store.Find(foreign.Id)!.OwnerUserId);
        Assert.Equal(before, Snapshots());

        // Forced, the import goes ahead after its snapshot.
        await host.OkAsync("POST", "/SmartSearch/Admin/Import?mode=Replace&dryRun=false&force=true", host.Admin, export);
        Assert.Equal(before + 1, Snapshots());
    }

    [Fact]
    public async Task RemapByName_RunsTheReconcilerWithoutTheGuardsLock_SoAnOwnerThatTakesItFinishes()
    {
        await using var host = await new SearchApiHost().StartAsync();
        var gate = new SemaphoreSlim(1, 1);
        var guard = new LockingGuard(gate);
        var owner = new LockingOwner(gate, Guid.NewGuid(), host.Twin.Username);
        var foreign = Record(owner.ForeignId, "From elsewhere", Q());
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, foreign]) with { KnownUsers = d.KnownUsers.With(foreign.OwnerUserId, host.Twin.Username) }, Ct);
        using var tools = new Tools(host, guard, owner, host.Store);

        var dry = await tools.Admin.RemapUsersAsync("Name", true, false, Ct);
        Assert.NotEmpty((System.Collections.IEnumerable)dry["Mapped"]!);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = tools.Admin.RemapUsersAsync("Name", false, false, timeout.Token);
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10), Ct)));
        var report = await run;

        Assert.True(owner.Entered);
        Assert.Equal(2, (int)report["MovedRecords"]!);
        Assert.Equal(host.Twin.Id, host.Store.Find(foreign.Id)!.OwnerUserId);
        Assert.Equal(1, guard.BulkChanges);
        Assert.Equal(1, gate.CurrentCount);
    }

    [Fact]
    public async Task ARemapRefusedPartWay_NamesTheOwnersAlreadyRemapped_AndARetryRemapsTheRest()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard }.StartAsync();
        var foreign = Record(Guid.NewGuid(), "From elsewhere", Q());
        await host.Store.UpdateAsync(d => SavedQueryStore.WithQueries(d, [.. d.Queries, foreign]) with { KnownUsers = d.KnownUsers.With(foreign.OwnerUserId, host.Twin.Username) }, Ct);

        // The other owner comes first and, as it remaps, a reference to the query appears (between the plan's check and
        // the saved-query store's own): the store refuses its part.
        var first = new LockingOwner(new SemaphoreSlim(1, 1), foreign.OwnerUserId, host.Twin.Username) { OnRemap = () => guard.Referenced.Add(foreign.Id) };
        using var tools = new Tools(host, guard, first, host.Store);

        var refused = await Assert.ThrowsAsync<PluginException>(() => tools.Admin.RemapUsersAsync("Name", false, false, Ct));
        Assert.Equal(SavedQueryErrors.BreaksAutoCollectionsCode, refused.Code);
        Assert.Equal(["other-owner"], Assert.IsAssignableFrom<IEnumerable<string>>(refused.Extensions!["remappedOwners"]));
        Assert.NotNull(refused.Extensions!["autoCollections"]);
        Assert.Contains("already remapped", refused.Message, StringComparison.Ordinal);
        Assert.Equal(foreign.OwnerUserId, host.Store.Find(foreign.Id)!.OwnerUserId);
        Assert.Equal(1, first.Remaps);

        // Again, with the reference gone: the remapped owner is skipped, the store remaps its query.
        guard.Referenced.Clear();
        first.OnRemap = null;
        var report = await tools.Admin.RemapUsersAsync("Name", false, false, Ct);
        Assert.Equal(1, (int)report["MovedRecords"]!);
        Assert.Equal(1, first.Remaps);
        Assert.Equal(host.Twin.Id, host.Store.Find(foreign.Id)!.OwnerUserId);
    }

    [Fact]
    public async Task TheAutoCollectionCount_IsForTheOwnerAndAdministratorsOnly()
    {
        var guard = new TestReferenceGuard();
        await using var host = await new SearchApiHost { Guard = guard }.StartAsync();
        var shared = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared", shared: true), Ct);
        guard.Referenced.Add(shared.Id);

        foreach (var (who, count) in new[] { (host.Kid, 1), (host.Admin, 1), (host.Twin, 0) })
        {
            var one = await host.OkAsync("GET", "/SmartSearch/Queries/" + shared.Id, who);
            Assert.Equal(count, one.GetProperty("AutoCollectionCount").GetInt32());
            var list = await host.OkAsync("GET", "/SmartSearch/Queries?scope=All", who);
            var row = list.GetProperty("Items").EnumerateArray().Single(i => Guid.Parse(i.GetProperty("Id").GetString()!) == shared.Id);
            Assert.Equal(count, row.GetProperty("AutoCollectionCount").GetInt32());
        }
    }

    // The administrator's tools over a reconciler of the given owners, in that order.
    private sealed class Tools : IDisposable
    {
        private readonly KitUserMarks _marks;
        private readonly UserReconciler _reconciler;

        public Tools(SearchApiHost host, ISavedQueryReferenceGuard guard, params IUserDataOwner[] owners)
        {
            var all = new List<User> { host.Admin, host.Kid, host.Twin };
            var users = new Mock<IUserManager>();
            users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => all.Find(u => u.Id == id));
            users.SetupGet(m => m.Users).Returns(all);
            var server = new Mock<IServerApplicationHost>();
            server.SetupGet(h => h.SystemId).Returns("server-1");
            _marks = new KitUserMarks(host.Data.Combine("tools", KitUsersDocument.FileName), NullLogger.Instance, host.Clock);
            _reconciler = new UserReconciler(() => all.ToDictionary(u => u.Id, u => u.Username), owners, _marks, () => DeletedUserPolicy.KeepAsOrphan, NullLogger.Instance, host.Clock);
            Admin = new SavedQueryAdmin(host.Store, host.Reader, () => guard, _reconciler, users.Object, server.Object, () => new { }, () => new SavedQuerySettings(true, true, 100, host.AllowedKinds), host.Clock, NullLogger.Instance, () => owners);
        }

        public SavedQueryAdmin Admin { get; }

        public void Dispose()
        {
            _reconciler.Dispose();
            _marks.Dispose();
        }
    }

    // A guard whose lock is the one an owner's writes take, as the auto-collections' guard and store share one.
    private sealed class LockingGuard(SemaphoreSlim gate) : ISavedQueryReferenceGuard
    {
        public int BulkChanges { get; private set; }

        public bool ReferencesUnknown => false;

        public int CountReferences(Guid savedQueryId) => 0;

        public IReadOnlyList<SmartSearchAutoCollectionRefDto> FindBrokenReferences(SavedQueryBulkChange change) => [];

        public async Task<T> BulkChangeAsync<T>(Func<CancellationToken, Task<T>> write, CancellationToken cancellationToken)
        {
            BulkChanges++;
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await write(cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<SavedQueryGuardOutcome> UpdateAsync(SavedQueryEdit edit, Func<CancellationToken, Task> save, CancellationToken cancellationToken)
        {
            await save(cancellationToken);
            return SavedQueryGuardOutcome.Done;
        }

        public async Task<SavedQueryGuardOutcome> DeleteAsync(SavedQueryDeletion deletion, Func<CancellationToken, Task> delete, CancellationToken cancellationToken)
        {
            await delete(cancellationToken);
            return SavedQueryGuardOutcome.Done;
        }
    }

    // An owner with one record of an unknown user whose last known name is a user's here; its remap takes the given lock,
    // as a store's write does, and then holds nothing more of that user.
    private sealed class LockingOwner(SemaphoreSlim gate, Guid foreignId, string lastName) : IUserDataOwner
    {
        private bool _remapped;

        public Guid ForeignId => foreignId;

        public bool Entered { get; private set; }

        public int Remaps { get; private set; }

        public Action? OnRemap { get; set; }

        public string Name => "other-owner";

        public bool IsReadOnly => false;

        public KnownUsers KnownUsers => _remapped ? KnownUsers.Empty : KnownUsers.Empty.With(foreignId, lastName);

        public IReadOnlyDictionary<Guid, int> CountRecordsByUser() => _remapped ? new Dictionary<Guid, int>() : new Dictionary<Guid, int> { [foreignId] = 1 };

        public Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken) => Task.FromResult(0);

        public async Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
        {
            Entered = true;
            await gate.WaitAsync(cancellationToken);
            try
            {
                Remaps++;
                _remapped = true;
                OnRemap?.Invoke();
                return 1;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
