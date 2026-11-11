using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Services;

/// <summary>
/// <c>ReleaseLocks</c> against the users' policies as they are at the write, with the sync's harness: a
/// release must never write over a change made between its plan and its write.
/// </summary>
public sealed class RecoveryServiceTests : IDisposable
{
    private readonly SyncHarness _h = new();

    // The library write sends the access the plan found as its expected value, so an admin's native change between the
    // plan and the write is kept (Aborted), never written over with the ledger's original.
    [Fact]
    public async Task ReleasingTheLibraries_KeepsAnAccessChangedBetweenThePlanAndTheWrite()
    {
        var kid = _h.Server.Add("kid", allFolders: false, folders: [Kids.Id]);
        var role = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        await _h.SaveAsync(d => d with
        {
            Roles = [role],
            Assignments = [new RoleAssignment { UserId = kid.Id, RoleIds = [role.Id], UpdatedUtc = _h.Now }],
            Ledger = new Dictionary<Guid, LedgerEntry> { [kid.Id] = Ledger(true) }
        });
        var recovery = new AccessRolesRecoveryService(_h.Store, new JellyfinServerUsers(_h.Server.Manager.Object), _h.Audit, NullLogger.Instance, _h.Catalog, _h.Writer, _h.Mirror, _h.Clock);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-UserId", Guid.NewGuid().ToString("N")), new Claim(ClaimTypes.Role, "Administrator")], "Test"));
        var token = TestContext.Current.CancellationToken;
        var dry = await recovery.ReleaseLocksAsync(null, restoreLibraries: true, new AccessRolesWriteOptions { DryRun = true }, caller, token);
        Assert.True(Assert.Single(dry.Released).Libraries!.EnableAllFolders);

        // An admin gives the kid Movies natively while the confirmation runs: after its plan, before its write.
        var changed = false;
        _h.Server.BeforeGetUserById = (id, _) =>
        {
            if (id.Equals(kid.Id) && !changed)
            {
                changed = true;
                _h.Server.AdminPost(kid.Id, p => p.EnabledFolders = [Movies.Id]);
            }
        };
        var released = await recovery.ReleaseLocksAsync(null, restoreLibraries: true, new AccessRolesWriteOptions { IfPreview = dry.PreviewToken }, caller, token);

        Assert.True(changed);
        Assert.NotEqual("Written", Assert.Single(released.Released).LibrariesOutcome);
        Assert.False(_h.Server.Stored(kid.Id).HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal([Movies.Id], _h.Server.Stored(kid.Id).GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    public void Dispose() => _h.Dispose();
}
