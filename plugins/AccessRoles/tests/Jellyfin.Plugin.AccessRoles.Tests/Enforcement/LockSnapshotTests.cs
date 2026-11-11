using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Storage;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Enforcement;

/// <summary>
/// The lock snapshot the settings filter reads (<c>EffectiveRulesCache</c>): the same
/// locks the resolver gives a non-administrator, from the store alone; and a store that can't be trusted locking every
/// server-enforced section for everyone.
/// </summary>
public sealed class LockSnapshotTests
{
    [Fact]
    public void TheSnapshot_GivesEveryUserTheLocksTheResolverGivesANonAdmin_Over400RandomDocuments()
    {
        var random = new Random(509);
        for (var round = 0; round < 400; round++)
        {
            var roles = Enumerable.Range(1, random.Next(0, 4)).Select(i => Role(i, "R" + i, AccessPresets.AllLocks.Where(_ => random.Next(3) == 0))).ToList();
            var users = Enumerable.Range(1, 4).Select(i => User(i, "u" + i, native: random.Next(4) == 0 ? new NativeAccess(true, [], true, false, [Movies.Id]) : AllFolders)).ToList();
            var assignments = users.Where(_ => random.Next(4) > 0).Select(u => Assign(
                u,
                roles.Where(_ => random.Next(2) == 0),
                overrides: AccessPresets.AllLocks.Where(_ => random.Next(5) == 0).ToDictionary(l => l, _ => (AccessRolesLockOverride)random.Next(3)),
                state: (AccessRolesAssignmentState)random.Next(3))).ToList();
            if (random.Next(10) == 0 && assignments.Count > 0)
            {
                assignments[0] = assignments[0] with { State = LenientEnum<AccessRolesAssignmentState>.Parse("Paused") };
            }

            var document = Document(roles, assignments);
            var snapshot = LockSnapshot.Build(document, StoreStatus.Ok);
            foreach (var user in users)
            {
                var resolved = EffectiveRulesResolver.Resolve(document, Catalog, user).Locked;
                Assert.Equal(resolved.Order(), snapshot.For(user.Id).Order());
                var state = EffectiveRulesResolver.Resolve(document, Catalog, user).State;
                Assert.Equal(state is AccessRolesUserState.Managed or AccessRolesUserState.CannotManage or AccessRolesUserState.UnknownValue, snapshot.IsManaged(user.Id));
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEmptyDocument_LocksOnlyWhomDegradedModeLocks(bool? everyNonAdmin)
    {
        // Since 0.9.13 the store's Degraded mode (DegradedModeTests) decides whom a store that can't be trusted locks; 0.9.5's
        // interim rule (a store recovered empty or read-only with no assignment locked everyone) is gone.
        var listed = Guid.NewGuid();
        var anyone = Guid.NewGuid();
        var state = everyNonAdmin is { } every ? new DegradedState(nameof(StoreStatus.ReadOnlyDataUnreadable), every, new HashSet<Guid> { listed }, null, Now, false) : null;
        var snapshot = LockSnapshot.Build(Document(), StoreStatus.ReadOnlyDataUnreadable, state);
        var anyoneLocked = everyNonAdmin == true;

        Assert.Equal(state is not null, snapshot.DegradedBecause is not null);
        // Since 0.9.7 the AdminSurfaces and UserDirectory locks are enforced, and fail closed too.
        Assert.Equal(state is not null ? SettingsSections.ServerEnforced.Append(AccessRolesLock.AdminSurfaces).Append(AccessRolesLock.UserDirectory).Order() : [], snapshot.For(listed).Order());
        Assert.Equal(state is not null ? AccessPresets.SettingsSections.Append(AccessRolesLock.AdminSurfaces).Append(AccessRolesLock.UserDirectory).Order() : [], snapshot.Reported(listed).Order());
        Assert.Equal(state is not null, snapshot.IsManaged(listed));
        Assert.Equal(anyoneLocked ? SettingsSections.ServerEnforced.Append(AccessRolesLock.AdminSurfaces).Append(AccessRolesLock.UserDirectory).Order() : [], snapshot.For(anyone).Order());
        Assert.Equal(anyoneLocked, snapshot.IsManaged(anyone));
    }

    [Fact]
    public void AReadOnlyDocumentWithAssignments_IsReadAsItIs()
    {
        var kid = User(1, "kid");
        var role = Role(1, "Kid", [AccessRolesLock.Home]);
        var snapshot = LockSnapshot.Build(Document([role], [Assign(kid, [role])]), StoreStatus.ReadOnlyNewerVersion);

        Assert.Null(snapshot.DegradedBecause);
        Assert.Equal([AccessRolesLock.Home], snapshot.For(kid.Id));
        Assert.Empty(snapshot.For(Guid.NewGuid()));
        Assert.Equal((1, 1), (snapshot.Managed, snapshot.Locked));
    }
}
