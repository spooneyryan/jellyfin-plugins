using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Services;

/// <summary>
/// How an export's document combines with the stored one: libraries by id, then folders, then name,
/// never guessed; users only as the kit's map finds them; this server's bookkeeping kept.
/// </summary>
public sealed class DataImportTests
{
    private static readonly ServerUser Kid = User(1, "kid");
    private static readonly ServerUser Viewer = User(2, "viewer");
    private static readonly ServerUser Gone = User(3, "gone");

    [Fact]
    public void Libraries_AreMappedById_ThenByFolders_ThenByName_AndOneThatMatchesNothingIsReported()
    {
        // The export's server had Kids under another id (the same folder here), Shows under another id and folder (the same
        // name), and Anime, which this server doesn't have.
        var kidsThere = Library(12, "Children", "movies") with { Locations = [@"c:\media\KIDS\"] };
        var showsThere = Library(13, "Shows", "tvshows") with { Locations = [@"D:\TV"] };
        var anime = Library(14, "Anime", "tvshows");
        var incoming = Document([Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies, kidsThere, showsThere, anime])]);

        var map = AccessRolesDataImport.MapLibraries(Document(), incoming, Catalog);

        Assert.Equal(
            [(Movies.Id, "Id"), (Kids.Id, "Locations"), (Shows.Id, "Name")],
            map.Mapped.Values.OrderBy(m => m.Name, StringComparer.Ordinal).Select(m => (m.Id, m.By)).OrderBy(m => m.Id));
        var unmatched = Assert.Single(map.Unmatched.Values);
        Assert.Equal((anime.Id, "Anime", AccessRolesDataImport.NoSuchLibrary), (unmatched.ExportId, unmatched.Name, unmatched.Reason));
    }

    [Fact]
    public void ALibraryTwoExportIdsMatch_OrThatMatchesTwoLibraries_IsNeitherOne()
    {
        var first = Library(21, "Horror", "movies") with { Locations = [@"Q:\x"] };
        var second = Library(22, "HORROR", "movies") with { Locations = [@"Q:\y"] };
        var twin = Library(23, "Twin", "movies") with { Locations = [@"Q:\twin"] };
        var catalog = new LibraryCatalog([.. Catalog.Libraries, Library(24, "Twin", "movies"), Library(25, "twin", "movies")]);
        var incoming = Document([Role(1, "Scary", mode: AccessRolesRoleLibraryMode.Only, refs: [first, second, twin])]);

        var map = AccessRolesDataImport.MapLibraries(Document(), incoming, catalog);

        Assert.Empty(map.Mapped);
        Assert.All(map.Unmatched.Values, u => Assert.Equal(AccessRolesDataImport.Ambiguous, u.Reason));
        Assert.Equal(3, map.Unmatched.Count);
    }

    [Fact]
    public void ARoleLosesOnlyTheLibrariesNotFound_AndAReferenceOrphanedHereStaysOrphaned()
    {
        var anime = Library(14, "Anime", "tvshows");
        var current = Document() with { KnownLibraries = [new KnownLibrary { Id = Horror.Id, Name = "Horror", State = AccessRolesLibraryState.Orphaned }] };
        var incoming = Document([Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies, Horror, anime])]);
        var users = Users(incoming, MapUsersBy.Id);

        var combined = Combine(current, incoming, ImportMode.Merge, users);

        var role = Assert.Single(combined.Document.Roles);
        Assert.Equal([Movies.Id, Horror.Id], role.Libraries.Refs.Select(r => r.Id));
        Assert.Equal([false, true], role.Libraries.Refs.Select(r => r.Orphaned));
        var skipped = Assert.Single(combined.Skipped);
        Assert.Equal(("RoleLibrary", anime.Id, AccessRolesDataImport.NoSuchLibrary, "Family"), (skipped.Kind, skipped.Id, skipped.Reason, skipped.Of));
        Assert.Equal(current.KnownLibraries, combined.Document.KnownLibraries);
    }

    [Fact]
    public void AnOnlyRoleThatLosesEveryLibrary_IsLeftOut_AndAnAllExceptOneThatLosesAny()
    {
        // An Only… role losing some libraries only narrows (the test above); losing all, it would be an Only… role with
        // nothing, and an All except… role losing what it leaves out would grant it: both are left out, naming them.
        var anime = Library(14, "Anime", "tvshows");
        var cartoons = Library(15, "Cartoons", "tvshows");
        var incoming = Document(
        [
            Role(1, "AnimeOnly", mode: AccessRolesRoleLibraryMode.Only, refs: [anime, cartoons]),
            Role(2, "NoAnime", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Movies, anime]),
            Role(3, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies, anime])
        ]);

        var combined = Combine(Document(), incoming, ImportMode.Merge, Users(incoming, MapUsersBy.Id));

        Assert.Equal(["Family"], combined.Document.Roles.Select(r => r.Name));
        var left = combined.Skipped.Where(s => s.Kind == "Role").ToList();
        Assert.Equal(["AnimeOnly", "NoAnime"], left.Select(s => s.Name));
        Assert.All(left, s => Assert.Equal(AccessRolesDataImport.LibrariesUnmatched, s.Reason));
        Assert.Equal([anime.Id, cartoons.Id], left[0].Libraries!.Select(l => l.ExportId));
        Assert.Equal([anime.Id], left[1].Libraries!.Select(l => l.ExportId));
        Assert.Equal(("RoleLibrary", anime.Id, "Family"), Assert.Single(combined.Skipped, s => s.Kind == "RoleLibrary") is var r ? (r.Kind, r.Id, r.Of) : default);
    }

    [Fact]
    public void Merge_AddsNewRecords_ReplacesOlderCopies_KeepsNewerOnes_AndNeverMatchesARoleByName()
    {
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var teens = Role(2, "Teens", mode: AccessRolesRoleLibraryMode.Only, refs: [Shows]);
        var current = Document([family, teens], [Assign(Kid, [family])]);
        var incoming = Document(
            [
                family with { Name = "Family (newer)", UpdatedUtc = Now },
                teens with { Name = "Teens (older)", UpdatedUtc = Now.AddDays(-9) },
                Role(3, "teens", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]),
                Role(4, "Guests", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies])
            ],
            [Assign(Kid, [teens]) with { UpdatedUtc = Now }, Assign(Viewer, [family]), Assign(Gone, [family])]);

        var combined = Combine(current, incoming, ImportMode.Merge, Users(incoming, MapUsersBy.Id, Kid, Viewer));

        Assert.Equal(["Family (newer)", "Teens", "Guests"], combined.Document.Roles.Select(r => r.Name));
        Assert.Equal((1, 1, 1), (combined.Roles.Added, combined.Roles.Updated, combined.Roles.KeptNewer));
        Assert.Contains(combined.Skipped, s => s.Kind == "Role" && s.Name == "teens" && s.Reason == AccessRolesDataImport.NameTaken);
        Assert.Equal([(Kid.Id, teens.Id), (Viewer.Id, family.Id)], combined.Document.Assignments.Select(a => (a.UserId, a.RoleIds.Single())).OrderBy(a => a.UserId));
        Assert.DoesNotContain(combined.Document.Assignments, a => a.UserId.Equals(Gone.Id));
    }

    [Fact]
    public void Replace_MakesTheRolesAssignmentsAndDefaultRolesTheExports_AndKeepsThisServersBookkeeping()
    {
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var teens = Role(2, "Teens", mode: AccessRolesRoleLibraryMode.Only, refs: [Shows]);
        var mirror = ImmutableDictionary<Guid, MirroredPreference>.Empty.Add(Viewer.Id, new MirroredPreference { WrittenUtc = Now });
        var current = Document([family, teens], [Assign(Kid, [family]), Assign(Viewer, [teens])], new Dictionary<Guid, LedgerEntry> { [Viewer.Id] = Ledger(true) }) with
        {
            DefaultRoleIds = [teens.Id],
            PreferenceMirror = mirror,
            KnownLibraries = [new KnownLibrary { Id = Movies.Id, Name = "Movies" }]
        };
        var incoming = Document([family], [Assign(Kid, [family])], new Dictionary<Guid, LedgerEntry> { [Kid.Id] = Ledger(false, Kids) }) with { DefaultRoleIds = [family.Id] };

        var combined = Combine(current, incoming, ImportMode.Replace, Users(incoming, MapUsersBy.Id, Kid, Viewer));

        Assert.Equal([family.Id], combined.Document.Roles.Select(r => r.Id));
        Assert.Equal([Kid.Id], combined.Document.Assignments.Select(a => a.UserId));
        Assert.Equal([family.Id], combined.Document.DefaultRoleIds);
        Assert.Equal((1, 1), (combined.Roles.Removed, combined.Assignments.Removed));
        Assert.Same(mirror, combined.Document.PreferenceMirror);
        Assert.Equal(current.KnownLibraries, combined.Document.KnownLibraries);

        // Another server's ledger is never this server's: the viewer's entry stays and the kid gets none from the export.
        Assert.Equal([Viewer.Id], combined.Document.Ledger.Keys);
        Assert.Equal((1, 0, false), (combined.Ledger.Incoming, combined.Ledger.Added, combined.Ledger.SameServer));
    }

    [Fact]
    public void AnExportOfThisServer_FillsInAManagedUsersMissingLedgerEntry_WithoutItsLastWrite()
    {
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var entry = Ledger(false, Kids, Movies) with { LastWrite = new LedgerWrite { EnableAllFolders = false, Utc = Now } };
        var incoming = Document([family], [Assign(Kid, [family])], new Dictionary<Guid, LedgerEntry> { [Kid.Id] = entry, [Viewer.Id] = Ledger(true) });

        var combined = AccessRolesDataImport.Combine(Document(), incoming, ImportMode.Merge, Users(incoming, MapUsersBy.Id, Kid, Viewer), AccessRolesDataImport.MapLibraries(Document(), incoming, Catalog), sameServer: true);

        var kept = Assert.Single(combined.Document.Ledger);
        Assert.Equal(Kid.Id, kept.Key);
        Assert.Equal([Kids.Id, Movies.Id], kept.Value.OriginalEnabledFolders);
        Assert.Null(kept.Value.LastWrite);
        Assert.Equal((2, 1, true), (combined.Ledger.Incoming, combined.Ledger.Added, combined.Ledger.SameServer));
    }

    [Fact]
    public void AStoredLedgerEntry_IsKept_WhateverThisServersOwnExportHasForTheUser()
    {
        // The ledger is the access a user had before Access Roles first managed them: a file never replaces it, not even
        // this server's own export, which fills in only a managed user's missing entry.
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var current = Document([family], [Assign(Kid, [family])], new Dictionary<Guid, LedgerEntry> { [Kid.Id] = Ledger(true) });
        var incoming = Document([family], [Assign(Kid, [family])], new Dictionary<Guid, LedgerEntry> { [Kid.Id] = Ledger(false, Kids, Movies) });

        var combined = AccessRolesDataImport.Combine(current, incoming, ImportMode.Merge, Users(incoming, MapUsersBy.Id, Kid, Viewer), AccessRolesDataImport.MapLibraries(current, incoming, Catalog), sameServer: true);

        var kept = Assert.Single(combined.Document.Ledger);
        Assert.Equal(Kid.Id, kept.Key);
        Assert.True(kept.Value.OriginalEnableAllFolders);
        Assert.Empty(kept.Value.OriginalEnabledFolders);
        Assert.Equal((1, 0, true), (combined.Ledger.Incoming, combined.Ledger.Added, combined.Ledger.SameServer));
    }

    [Fact]
    public void ALockoutConfirmation_IsThisServersOnly()
    {
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var stored = Assign(Kid, [family]) with { ConfirmedNoFullAdminUtc = Now.AddDays(-3) };
        var incoming = Document([family], [Assign(Kid, [family]) with { UpdatedUtc = Now, ConfirmedNoFullAdminUtc = Now }, Assign(Viewer, [family]) with { ConfirmedNoFullAdminUtc = Now }]);

        var combined = Combine(Document([family], [stored]), incoming, ImportMode.Merge, Users(incoming, MapUsersBy.Id, Kid, Viewer));

        Assert.Equal(Now.AddDays(-3), combined.Document.Assignments.Single(a => a.UserId.Equals(Kid.Id)).ConfirmedNoFullAdminUtc);
        Assert.Null(combined.Document.Assignments.Single(a => a.UserId.Equals(Viewer.Id)).ConfirmedNoFullAdminUtc);
    }

    [Fact]
    public void UsersMappedByName_TakeThisServersIds_AndTheirLibraryOverridesAreMapped()
    {
        var theirKid = Guid.NewGuid();
        var kidsThere = Library(12, "Kids", "movies") with { Locations = [@"Q:\elsewhere"] };
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var incoming = Document([family], [Assign(Kid, [family], add: [kidsThere]) with { UserId = theirKid }]) with
        {
            // An override names only the id: the export's known libraries give its name and folders.
            KnownLibraries = [new KnownLibrary { Id = kidsThere.Id, Name = kidsThere.Name, Locations = kidsThere.Locations }]
        };
        var users = ImportUserMap.Build(
            AccessRolesDataImport.CountRecordsByUser(incoming),
            [new ExportUser(theirKid, "KID")],
            new Dictionary<Guid, string> { [Kid.Id] = "kid" },
            new HashSet<Guid>(),
            MapUsersBy.Name);

        var combined = Combine(Document(), incoming, ImportMode.Merge, users);

        var assignment = Assert.Single(combined.Document.Assignments);
        Assert.Equal((Kid.Id, Kids.Id), (assignment.UserId, assignment.Libraries.Add.Single()));
        Assert.Equal("kid", combined.Document.KnownUsers.NameOf(Kid.Id));
    }

    [Fact]
    public void AUserDeletedOnTheExportingServer_IsNeverMatchedByName()
    {
        var theirs = Guid.NewGuid();
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var incoming = Document([family], [Assign(Kid, [family]) with { UserId = theirs }]);
        var users = ImportUserMap.Build(
            AccessRolesDataImport.CountRecordsByUser(incoming),
            [new ExportUser(theirs, "kid", Deleted: true)],
            new Dictionary<Guid, string> { [Kid.Id] = "kid" },
            new HashSet<Guid>(),
            MapUsersBy.Name);

        var combined = Combine(Document(), incoming, ImportMode.Merge, users);

        Assert.Empty(combined.Document.Assignments);
        Assert.Equal(ImportUnknownUser.DeletedOnExportingServer, Assert.Single(users.Unknown).Reason);
    }

    [Fact]
    public void DataThatChangesNothing_GivesBackTheStoredDocument()
    {
        var family = Role(1, "Family", mode: AccessRolesRoleLibraryMode.Only, refs: [Movies]);
        var current = Document([family], [Assign(Kid, [family])]);

        var combined = Combine(current, current, ImportMode.Replace, Users(current, MapUsersBy.Id, Kid));

        Assert.Same(current, combined.Document);
    }

    private static AccessRolesDataImport.Combined Combine(AccessRolesDocument current, AccessRolesDocument incoming, ImportMode mode, ImportUserMap users)
        => AccessRolesDataImport.Combine(current, incoming, mode, users, AccessRolesDataImport.MapLibraries(current, incoming, Catalog), sameServer: false);

    private static ImportUserMap Users(AccessRolesDocument incoming, MapUsersBy by, params ServerUser[] here)
        => ImportUserMap.Build(
            AccessRolesDataImport.CountRecordsByUser(incoming),
            [.. AccessRolesDataImport.CountRecordsByUser(incoming).Keys.Select(id => new ExportUser(id, null))],
            here.ToDictionary(u => u.Id, u => u.Name),
            new HashSet<Guid>(),
            by);
}
