using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Sync;

/// <summary>
/// An admin's restore landing while the library sync or a sign-in is under way: the restore
/// writes its hold before its save takes the store's writer, so a run that checks the pause again under the writer, and a
/// sign-in that checks it again after its plan read the document, never write the restored access while it is held.
/// </summary>
public sealed class RestoreRaceTests : IDisposable
{
    private readonly SyncHarness _h = new();

    [Fact]
    public async Task ARestoreLandingBetweenARunsPauseCheckAndItsPlan_IsHeld_AndTheRunWritesNothing()
    {
        // The catalog read after the run's first pause check lets the restore land, the hold file first,
        // then the save (as CombineAsync does).
        var (kid, role) = await ManagedKidAsync();
        using var sync = Sync(LandRestoreOnce(role));

        var report = await sync.RunAsync(new SyncRequest(SyncTrigger.Interval, AuditActor.System), TestContext.Current.CancellationToken);

        Assert.True(RestoreHold.IsHeld(_h.Paths, _h.Store));
        Assert.Equal(SyncReasons.StoreRestored, report.Paused);
        Assert.Equal(0, report.Written);
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ARestoreLandingWhileASignInIsPlanned_IsHeld_AndTheSignInWritesNothing()
    {
        // The sign-in path: CorrectUserAsync checks the pause, then plans the user from the document; the restore lands in
        // between (its plan is the restored access), so the second look holds the sign-in with the last write.
        var (kid, role) = await ManagedKidAsync();
        using var sync = Sync(LandRestoreOnce(role));

        var correction = await sync.CorrectUserAsync(kid.Id, SyncTrigger.SignIn, TimeSpan.FromSeconds(5), null, TestContext.Current.CancellationToken);

        Assert.True(RestoreHold.IsHeld(_h.Paths, _h.Store));
        Assert.Equal(SyncReasons.StoreRestored, correction.HeldBecause);
        Assert.Null(correction.Result);
        Assert.Equal([Kids.Id], correction.LastWrite!.EnabledFolders);
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task ARestoreLandingWhileAnAdminsPolicySaveIsMerged_MergesNothing()
    {
        // The Dashboard's policy save: the pre-merge decides from the document after its pause check; a restore landing in
        // between isn't merged into the save.
        var (kid, role) = await ManagedKidAsync();
        using var sync = Sync(LandRestoreOnce(role));

        var decision = sync.DecidePreMerge(kid.Id, LibraryFields.Of(true, []), postedAdministrator: false, postedDisabled: false);

        Assert.Null(decision.Merge);
        Assert.Equal(SyncReasons.StoreRestored, decision.Why);
    }

    [Fact]
    public async Task WithoutARestore_TheHookedRunAndSignInStillWrite()
    {
        // The control: the same catalog with nothing landing writes the role's change as before.
        var (kid, role) = await ManagedKidAsync();
        await _h.SaveAsync(d => d with { Roles = [Wider(role)] });
        using var sync = Sync(() => { });

        var correction = await sync.CorrectUserAsync(kid.Id, SyncTrigger.SignIn, TimeSpan.FromSeconds(5), null, TestContext.Current.CancellationToken);

        Assert.Null(correction.HeldBecause);
        Assert.Contains(Movies.Id, _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    public void Dispose() => _h.Dispose();

    private static AccessRole Wider(AccessRole role) => role with { Libraries = role.Libraries with { Refs = [.. role.Libraries.Refs, new LibraryReference { Id = Movies.Id }] } };

    private async Task<(ServerUserEntry Kid, AccessRole Role)> ManagedKidAsync()
    {
        var kid = _h.Server.Add("kid", allFolders: true);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with
        {
            Roles = [role],
            Assignments = [Assign(SyncHarness.Read(kid), [role])],
            Ledger = new Dictionary<Guid, LedgerEntry> { [kid.Id] = new LedgerEntry { OriginalEnableAllFolders = true, OriginalEnableUserPreferenceAccess = true, CapturedUtc = _h.Now } }
        });
        Assert.True(Assert.Single((await _h.RunAsync()).Users).Wrote);
        Assert.Equal([Kids.Id], _h.Server.Cached(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        return (new ServerUserEntry(kid.Id), role);
    }

    // The restore lands once, at the next catalog read: its hold, then its save of a role that also grants Movies.
    private Action LandRestoreOnce(AccessRole role)
    {
        var armed = true;
        return () =>
        {
            if (!armed)
            {
                return;
            }

            armed = false;
            RestoreHold.Begin(_h.Paths, _h.Store, _h.Now, "accessroles-20261019-000000.json");
            _h.Store.UpdateAsync(d => d with { Roles = [Wider(role)] }, CancellationToken.None).GetAwaiter().GetResult();
        };
    }

    private LibrarySync Sync(Action beforeRead)
    {
        var configuration = new FakeConfigurationProvider(() => _h.Configuration);
        return new LibrarySync(_h.Store, new HookedCatalog(_h.Catalog, beforeRead), new JellyfinServerUsers(_h.Server.Manager.Object), _h.Writer, _h.Audit, configuration, _h.Paths, _h.Status, NullLogger.Instance, _h.Clock, _h.Mirror);
    }

    private sealed record ServerUserEntry(Guid Id);

    private sealed class HookedCatalog(SyncHarness.FakeCatalogSource inner, Action before) : ILibraryCatalogSource
    {
        public LibraryCatalog Read()
        {
            before();
            return inner.Read();
        }

        public Guid IdOf(string name) => inner.IdOf(name);
    }
}
