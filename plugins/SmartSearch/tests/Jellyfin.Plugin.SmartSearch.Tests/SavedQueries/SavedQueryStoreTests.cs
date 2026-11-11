using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using Jellyfin.Plugin.SmartSearch.Tests.Api;
using Xunit;
using static Jellyfin.Plugin.SmartSearch.Tests.SavedQueries.SavedQueryTestData;

namespace Jellyfin.Plugin.SmartSearch.Tests.SavedQueries;

/// <summary>
/// The saved-query store as the owner of its users' data (the deleted-user policy, remap), the reference guard's no-op
/// implementation, and the service's checks that need no HTTP: names, quota, <c>IfUpdatedAt</c>, the stored form.
/// </summary>
public sealed class SavedQueryStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADeletedUser_UnderDelete_LosesPrivateQueries_SharedAndReferencedStayAsDeletedUser()
    {
        var guard = new TestReferenceGuard();
        await using var host = new SearchApiHost { Guard = guard };
        var gone = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Private one"), Ct);
        var shared = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared one", shared: true), Ct);
        var referenced = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Referenced one"), Ct);
        var others = await host.SavedQueries.CreateAsync(host.Twin, false, Request("Private one"), Ct);
        guard.Referenced.Add(referenced.Id);
        Assert.True(host.Store.WouldChangeRecords(host.Kid.Id, DeletedUserPolicy.Delete));
        Assert.False(host.Store.WouldChangeRecords(host.Kid.Id, DeletedUserPolicy.KeepAsOrphan));

        host.Users.Remove(host.Kid);
        var removed = await host.Reconciler.HandleUserDeletedAsync(host.Kid.Id, Ct);

        Assert.Equal(1, removed);
        Assert.Equal(new[] { shared.Id, referenced.Id, others.Id }.Order(), host.Store.Current.Queries.Select(q => q.Id).Order());
        Assert.True(host.Store.KnownUsers.ContainsKey(host.Kid.Id));

        // The write's .bak was written again: no copy of the deleted query is left beside the file.
        var bak = host.Store.Documents.FilePath + ".bak";
        Assert.True(File.Exists(bak));
        Assert.DoesNotContain(gone.Id.ToString(), await File.ReadAllTextAsync(bak, Ct), StringComparison.OrdinalIgnoreCase);

        // Twin sees the shared orphan as "Deleted user", without its owner's id; an administrator gets the id.
        var listed = Assert.Single(host.SavedQueries.List(host.Twin, false, "Shared", 0, 100).Items);
        Assert.Equal(shared.Id, listed.Id);
        Assert.Equal(SavedQueryService.DeletedUserName, listed.OwnerName);
        Assert.True(listed.OwnerDeleted);
        Assert.Null(listed.OwnerUserId);
        Assert.Equal(host.Kid.Id, host.SavedQueries.Get(host.Admin, true, shared.Id).OwnerUserId);
        Assert.Null(host.SavedQueries.Get(host.Twin, false, shared.Id).OwnerUserId);
    }

    [Theory]
    [InlineData("KeepAsOrphan")]
    [InlineData("Anonymize")]
    public async Task ADeletedUser_UnderTheOtherPolicies_KeepsEveryQuery(string policy)
    {
        await using var host = new SearchApiHost { Policy = Enum.Parse<DeletedUserPolicy>(policy) };
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Private one"), Ct);
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared one", shared: true), Ct);

        host.Users.Remove(host.Kid);
        Assert.Equal(0, await host.Reconciler.HandleUserDeletedAsync(host.Kid.Id, Ct));

        Assert.Equal(2, host.Store.Current.Queries.Count);
        Assert.All(host.SavedQueriesAdmin.List(null, 0, 100).Items, q => Assert.True(q.OwnerDeleted));
    }

    [Fact]
    public async Task ApplyingThePolicyLater_RemovesWhatTheDeletionKept()
    {
        await using var host = new SearchApiHost { Policy = DeletedUserPolicy.KeepAsOrphan };
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Private one"), Ct);
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shared one", shared: true), Ct);
        host.Users.Remove(host.Kid);
        await host.Reconciler.HandleUserDeletedAsync(host.Kid.Id, Ct);

        host.Policy = DeletedUserPolicy.Delete;
        await host.SavedQueriesAdmin.ApplyPolicyAsync(new SmartSearchApplyPolicyDto { UserIds = [host.Kid.Id] }, Ct);

        Assert.Equal("Shared one", Assert.Single(host.Store.Current.Queries).Name);
    }

    [Fact]
    public async Task Remap_MovesOwnerAndEditor_AndNumbersANameTheNewOwnerHas()
    {
        await using var host = new SearchApiHost();
        await host.SavedQueries.CreateAsync(host.Twin, false, Request("Films"), Ct);
        var moved = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Films"), Ct);

        Assert.Equal(1, await host.Store.RemapUsersAsync(new System.Collections.Generic.Dictionary<Guid, Guid> { [host.Kid.Id] = host.Twin.Id }, Ct));

        var record = host.Store.Find(moved.Id)!;
        Assert.Equal(host.Twin.Id, record.OwnerUserId);
        Assert.Equal(host.Twin.Id, record.UpdatedByUserId);
        Assert.Equal("Films (2)", record.Name);
        Assert.False(host.Store.KnownUsers.ContainsKey(host.Kid.Id));
    }

    [Fact]
    public async Task TheNoOpGuard_CountsNothing_AndWritesEveryEditAndDeleteOnce()
    {
        var guard = new NoSavedQueryReferences();
        var record = Record(Guid.NewGuid(), "Q", Q());
        var writes = 0;

        Assert.Equal(0, guard.CountReferences(record.Id));
        var edit = new SavedQueryEdit(record, Jellyfin.Plugin.SmartSearch.Query.QueryJson.Read(System.Text.Encoding.UTF8.GetBytes(Q())).Definition!, false, Planning.PlannerWorld.NewUser("u"), false, false);
        Assert.Same(SavedQueryGuardOutcome.Done, await guard.UpdateAsync(edit, _ => { writes++; return Task.CompletedTask; }, Ct));
        Assert.Same(SavedQueryGuardOutcome.Done, await guard.DeleteAsync(new SavedQueryDeletion(record, edit.Caller, false, false), _ => { writes++; return Task.CompletedTask; }, Ct));
        Assert.Equal(2, writes);

        // A refusal of the write itself (a name taken) is the caller's, unchanged.
        var refusal = new PluginException(PluginErrorKind.Conflict, "taken");
        Assert.Same(refusal, await Assert.ThrowsAsync<PluginException>(() => guard.UpdateAsync(edit, _ => throw refusal, Ct)));
    }

    [Fact]
    public async Task Writes_CheckTheNameQuotaAndIfUpdatedAt_UnderTheLock()
    {
        await using var host = new SearchApiHost { MaxPerUser = 2 };
        var first = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Films"), Ct);

        Assert.Equal(SavedQueryErrors.DuplicateNameCode, (await Assert.ThrowsAsync<PluginException>(() => host.SavedQueries.CreateAsync(host.Kid, false, Request(" films "), Ct))).Code);
        await host.SavedQueries.CreateAsync(host.Twin, false, Request("Films"), Ct); // another owner's name is free
        await host.SavedQueries.CreateAsync(host.Kid, false, Request("Shows"), Ct);
        Assert.Equal(SavedQueryErrors.QuotaReachedCode, (await Assert.ThrowsAsync<PluginException>(() => host.SavedQueries.CreateAsync(host.Kid, false, Request("More"), Ct))).Code);
        Assert.Equal(SavedQueryErrors.QuotaReachedCode, (await Assert.ThrowsAsync<PluginException>(() => host.SavedQueries.DuplicateAsync(host.Kid, false, first.Id, new SmartSearchSavedQueryDuplicateDto(), Ct))).Code);

        // IfUpdatedAt: the time read back matches; an older one is refused, and the change moves the time on.
        host.Clock.Advance(TimeSpan.FromTicks(1234));
        var changed = await host.SavedQueries.UpdateAsync(host.Kid, false, first.Id, Request("Films renamed", ifUpdatedAt: first.UpdatedUtc), Ct);
        Assert.True(changed.UpdatedUtc > first.UpdatedUtc);
        Assert.Equal(0, changed.UpdatedUtc.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(SavedQueryErrors.ChangedCode, (await Assert.ThrowsAsync<PluginException>(() => host.SavedQueries.UpdateAsync(host.Kid, false, first.Id, Request("Again", ifUpdatedAt: first.UpdatedUtc), Ct))).Code);
        Assert.Equal("Films renamed", host.Store.Find(first.Id)!.Name);
    }

    [Fact]
    public async Task AStoredQuery_IsTheCompactCanonicalForm_WithItsHash()
    {
        await using var host = new SearchApiHost();
        var spaced = "{ \"Scope\" : { \"ItemKinds\" : [ \"Movie\" ] }, \"Where\" : { \"Children\" : [ " + Keep + " ] } }";
        var saved = await host.SavedQueries.CreateAsync(host.Kid, false, Request("Films", spaced), Ct);

        var record = host.Store.Find(saved.Id)!;
        Assert.DoesNotContain(" ", record.Query.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(host.Reader.Read(record).Hash, record.QueryHash);
        Assert.Equal(record.QueryHash, saved.QueryHash);
        Assert.False(saved.UsesPerUserFields);
        Assert.True((await host.SavedQueries.CreateAsync(host.Kid, false, Request("Unwatched", Q(PerUser)), Ct)).UsesPerUserFields);
    }
}
