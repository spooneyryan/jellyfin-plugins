using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Services;

/// <summary>
/// The store as the kit's user-data owner: a deleted user's assignment and ledger
/// entry go, with a <c>UserDeleted</c> audit line; counts, names and <i>Remap by name</i>.
/// </summary>
public sealed class UserDataOwnerTests : IDisposable
{
    private readonly TempDirectory _directory = new();
    private readonly AccessRolesStore _store;
    private readonly AccessRolesAuditLog _audit;
    private readonly AccessRolesUserDataOwner _owner;
    private readonly Guid _kid = Guid.NewGuid();
    private readonly Guid _viewer = Guid.NewGuid();

    public UserDataOwnerTests()
    {
        _store = AccessRolesStore.Create(_directory.Combine("accessroles.json"), NullLogger.Instance);
        _audit = new AccessRolesAuditLog(_directory.Combine("audit"), () => 12, NullLogger.Instance);
        _owner = new AccessRolesUserDataOwner(_store, _audit, _directory.Combine("snapshots"), NullLogger.Instance);
    }

    [Fact]
    public async Task ADeletedUser_LosesTheirAssignmentAndLedgerEntry_AndItIsAudited()
    {
        await SeedAsync();

        var removed = await _owner.HandleUserDeletedAsync(_kid, DeletedUserPolicy.Delete, TestContext.Current.CancellationToken);
        var nothing = await _owner.HandleUserDeletedAsync(Guid.NewGuid(), DeletedUserPolicy.Delete, TestContext.Current.CancellationToken);

        Assert.Equal(2, removed);
        Assert.Equal(0, nothing);
        Assert.Equal([_viewer], _store.Current.Assignments.Select(a => a.UserId));
        Assert.False(_store.Current.Ledger.ContainsKey(_kid));
        Assert.Null(_store.Current.KnownUsers.NameOf(_kid));
        var line = Assert.Single(_audit.Read(0, 10, null, null, null).Items);
        Assert.Equal(("UserDeleted", AuditActor.SystemKind, _kid, "kid"), (line.Action, line.Actor.Kind, line.TargetUserId, line.TargetName));
    }

    [Fact]
    public async Task Counts_AreTheAssignmentAndTheLedgerEntry_AndNamesRefreshFromTheServer()
    {
        await SeedAsync();

        var counts = _owner.CountRecordsByUser();
        await _owner.RefreshKnownUsersAsync(new Dictionary<Guid, string> { [_kid] = "kid renamed" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, counts[_kid]);
        Assert.Equal(1, counts[_viewer]);
        Assert.Equal("kid renamed", _owner.KnownUsers.NameOf(_kid));
        Assert.Equal("viewer", _owner.KnownUsers.NameOf(_viewer));
        Assert.Equal("accessroles", _owner.Name);
        Assert.False(_owner.IsReadOnly);
    }

    [Fact]
    public async Task Remap_MovesAnAssignmentAndLedgerEntry_ButANewIdKeepsItsOwn()
    {
        await SeedAsync();
        var newKid = Guid.NewGuid();

        var moved = await _owner.RemapUsersAsync(new Dictionary<Guid, Guid> { [_kid] = newKid, [_viewer] = newKid }, TestContext.Current.CancellationToken);

        Assert.Equal(2, moved);
        Assert.Equal([newKid], _store.Current.Assignments.Select(a => a.UserId));
        Assert.True(_store.Current.Ledger.ContainsKey(newKid));
        Assert.Equal("kid", _store.Current.KnownUsers.NameOf(newKid));
        Assert.Single(_audit.Read(0, 10, null, "UsersRemapped", null).Items);
        Assert.NotNull(await _owner.SnapshotBeforeAsync("remap", TestContext.Current.CancellationToken));
    }

    // The optional mirror's records: a deleted user's goes and is counted; a remapped id's goes and never moves to the
    // new id, whose flag the mirror didn't turn off (so it must never turn it on).
    [Fact]
    public async Task TheMirrorsRecord_GoesWithADeletedUser_AndWithARemappedId_WithoutMovingToTheNewOne()
    {
        await SeedAsync();
        var newViewer = Guid.NewGuid();
        await _store.UpdateAsync(d => d with { PreferenceMirror = new Dictionary<Guid, MirroredPreference> { [_kid] = new(), [_viewer] = new() } }, TestContext.Current.CancellationToken);
        Assert.Equal((3, 2), (_owner.CountRecordsByUser()[_kid], _owner.CountRecordsByUser()[_viewer]));

        var removed = await _owner.HandleUserDeletedAsync(_kid, DeletedUserPolicy.Delete, TestContext.Current.CancellationToken);
        await _owner.RemapUsersAsync(new Dictionary<Guid, Guid> { [_viewer] = newViewer }, TestContext.Current.CancellationToken);

        Assert.Equal(3, removed);
        Assert.Empty(_store.Current.PreferenceMirror);
        Assert.Equal([newViewer], _store.Current.Assignments.Select(a => a.UserId));
    }

    public void Dispose()
    {
        _store.Dispose();
        _audit.Dispose();
        _directory.Dispose();
    }

    private Task SeedAsync() => _store.UpdateAsync(
        d => d with
        {
            Assignments = [new RoleAssignment { UserId = _kid }, new RoleAssignment { UserId = _viewer }],
            Ledger = new Dictionary<Guid, LedgerEntry> { [_kid] = Ledger(true) },
            KnownUsers = d.KnownUsers.With(_kid, "kid").With(_viewer, "viewer")
        },
        TestContext.Current.CancellationToken);
}
