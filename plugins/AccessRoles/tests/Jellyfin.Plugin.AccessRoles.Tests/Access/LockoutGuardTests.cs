using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Storage;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Access;

/// <summary>
/// <c>LockoutGuard</c> as the library sync applies it: a sync never leaves no enabled
/// administrator with every library unless the narrower access was confirmed.
/// </summary>
public sealed class LockoutGuardTests
{
    private static readonly LibraryFields KidsOnly = LibraryFields.Of(false, [Kids.Id]);

    [Fact]
    public void NarrowingTheOnlyFullAdministrator_IsRefused_UnlessConfirmed()
    {
        var admin = User(1, "admin", admin: true);
        var planned = new Dictionary<Guid, LibraryFields> { [admin.Id] = KidsOnly };

        Assert.Equal([admin.Id], LockoutGuard.Refused(Catalog, [admin], planned, _ => false));
        Assert.Empty(LockoutGuard.Refused(Catalog, [admin], planned, id => id.Equals(admin.Id)));
    }

    [Fact]
    public void AnotherEnabledFullAdministrator_LetsTheWriteThrough_ADisabledOneDoesnt()
    {
        var narrowed = User(1, "admin2", admin: true);
        var other = User(2, "admin", admin: true);
        var disabled = User(3, "off", admin: true, disabled: true);
        var planned = new Dictionary<Guid, LibraryFields> { [narrowed.Id] = KidsOnly };

        Assert.Empty(LockoutGuard.Refused(Catalog, [narrowed, other], planned, _ => false));
        Assert.Equal([narrowed.Id], LockoutGuard.Refused(Catalog, [narrowed, disabled], planned, _ => false));
    }

    [Fact]
    public void WhenNoAdministratorHasEveryLibraryNow_TheSyncChangesNothingTheGuardCaresAbout()
    {
        // The roles API refused or confirmed the change that got there; the sync can't make it worse.
        var admin = User(1, "admin", admin: true, native: Folders(Movies));

        Assert.Empty(LockoutGuard.Refused(Catalog, [admin], new Dictionary<Guid, LibraryFields> { [admin.Id] = KidsOnly }, _ => false));
    }

    [Fact]
    public void EveryLibraryByList_CountsAsEveryLibrary_AndWritesToOtherUsersDontMatter()
    {
        var admin = User(1, "admin", admin: true);
        var kid = User(2, "kid");
        var everyByList = LibraryFields.Of(false, Catalog.Ids);
        var planned = new Dictionary<Guid, LibraryFields> { [admin.Id] = everyByList, [kid.Id] = KidsOnly };

        Assert.True(LockoutGuard.HasEveryLibrary(Catalog, everyByList));
        Assert.Empty(LockoutGuard.Refused(Catalog, [admin, kid], planned, _ => false));
    }

    [Fact]
    public void ASavedConfirmation_IsRecordedOnTheNarrowedAdministratorsAssignment_AndIsNoPartOfWhatItSays()
    {
        var admin = User(1, "admin", admin: true);
        var role = Role(1, "Kid", mode: Model.AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var before = Document(roles: [role]);
        var after = Document(roles: [role], assignments: [Assign(admin, [role])]);
        var report = ChangeAnalysis.Analyze(before, after, Catalog, [admin], null);

        var confirmed = ChangeAnalysis.KeepConfirmations(after, report, confirmed: true, Catalog, Now);
        var unconfirmed = ChangeAnalysis.KeepConfirmations(after, report, confirmed: false, Catalog, Now);

        Assert.True(report.NoFullAdministrator);
        Assert.Equal(Now, confirmed.Assignments[0].ConfirmedNoFullAdminUtc);
        Assert.Same(after, unconfirmed);
        Assert.True(ChangeAnalysis.SameAssignment(after.Assignments[0], confirmed.Assignments[0]));
        Assert.Equal(Now, Jellyfin.Plugin.AccessRoles.Services.AccessRolesRequests.BuildAssignment(new Jellyfin.Plugin.AccessRoles.Api.Models.AccessRolesUserRequest { RoleIds = [role.Id] }, admin.Id, confirmed.Assignments[0], confirmed, Catalog, Now.AddMinutes(1))!.ConfirmedNoFullAdminUtc);
    }
}
