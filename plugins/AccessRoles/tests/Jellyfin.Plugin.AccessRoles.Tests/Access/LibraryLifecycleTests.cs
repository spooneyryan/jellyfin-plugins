using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Tests.TestSupport;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Access;

/// <summary>
/// The library lifecycle (<c>LibraryCatalog</c>; the sequences observed on a 10.11.5
/// sandbox): libraries added, renamed (seen and backstop, a case-only rename on Windows, a rename that waits for a scan),
/// deleted (seen and backstop), re-created (within one interval, after an orphan, a <c>refreshLibrary=false</c> delete that
/// keeps <c>DateCreated</c>), the "folder without item" window, and the admin's "grant again" and purge.
/// </summary>
public sealed class LibraryLifecycleTests
{
    private static readonly DateTime Created = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlySet<Guid> NoneSeen = new HashSet<Guid>();

    [Fact]
    public void TheFirstReconcile_RecordsEveryListedLibrary_WithItsFolderTime_WithoutEvents_AndOrphansDanglingReferences()
    {
        var gone = Library(9, "Gone");
        var kid = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, gone]);
        var catalog = new LibraryCatalog([At(Movies, Created), At(Kids, Created)]);

        var result = LibraryLifecycle.Reconcile(Document(roles: [kid]), catalog, NoneSeen, Now);

        Assert.Empty(result.Events);
        Assert.Equal(["Kids", "Movies"], result.Document.KnownLibraries.Select(k => k.Name).Order());
        Assert.All(result.Document.KnownLibraries, k => Assert.Equal((Created, Now, AccessRolesLibraryState.Active), (k.CreatedUtc!.Value, k.FirstSeenUtc, k.State.Value!.Value)));
        var refs = result.Document.Roles[0].Libraries.Refs;
        Assert.False(refs.Single(r => r.Id.Equals(Kids.Id)).Orphaned);
        Assert.True(refs.Single(r => r.Id.Equals(gone.Id)).Orphaned);
    }

    [Fact]
    public void AnUnchangedCatalog_ChangesNothing()
    {
        var catalog = new LibraryCatalog([At(Movies, Created), At(Kids, Created)]);
        var known = LibraryLifecycle.Reconcile(Document(), catalog, NoneSeen, Now).Document;

        var again = LibraryLifecycle.Reconcile(known, catalog, NoneSeen, Now.AddMinutes(5));

        Assert.Same(known, again.Document);
        Assert.Empty(again.Events);
    }

    [Fact]
    public void ANewLibrary_IsRecordedOnceItHasItsItem_NotInTheAddWindow()
    {
        // On 10.11.5, during an AddVirtualFolder request GetVirtualFolders() lists the new library without ItemId for about
        // half a second; no lifecycle action then.
        var known = Known([At(Movies, Created)]);
        var anime = Library(6, "Anime");

        var window = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(anime, Created.AddDays(10), hasItem: false)]), NoneSeen, Now);
        var added = LibraryLifecycle.Reconcile(window.Document, new LibraryCatalog([At(Movies, Created), At(anime, Created.AddDays(10))]), NoneSeen, Now);

        Assert.Empty(window.Events);
        Assert.Single(window.Document.KnownLibraries);
        var change = Assert.Single(added.Events);
        Assert.Equal((LifecycleEventKind.Added, anime.Id, "Anime"), (change.Kind, change.Id, change.Name));
        Assert.Equal(Created.AddDays(10), added.Document.KnownLibraries.Single(k => k.Id.Equals(anime.Id)).CreatedUtc);
    }

    [Fact]
    public void ARemovalTheFilterSaw_OrphansTheLibraryAndItsReferences_AndKeepsTheLedgerAndOverrides()
    {
        var user = User(1, "kid");
        var kid = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies]);
        var known = Known([At(Movies, Created), At(Kids, Created)], roles: [kid], assignments: [Assign(user, [kid], add: [Kids])], ledger: new Dictionary<Guid, LedgerEntry> { [user.Id] = Ledger(false, Kids) });

        var result = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created)]), new HashSet<Guid> { Kids.Id }, Now);

        var change = Assert.Single(result.Events);
        Assert.Equal((LifecycleEventKind.Removed, Kids.Id, true), (change.Kind, change.Id, change.Exact));
        Assert.True(result.Document.KnownLibraries.Single(k => k.Id.Equals(Kids.Id)).State.Is(AccessRolesLibraryState.Orphaned));
        Assert.True(result.Document.Roles[0].Libraries.Refs.Single(r => r.Id.Equals(Kids.Id)).Orphaned);
        Assert.Equal([Kids.Id], result.Document.Assignments[0].Libraries.Add);
        Assert.Equal([Kids.Id], result.Document.Ledger[user.Id].OriginalEnabledFolders);
        Assert.Empty(result.Waiting);
    }

    [Fact]
    public void ARemovalTheBackstopFinds_WithNoLibraryOfTheSameFolders_IsADeletion()
    {
        var known = Known([At(Movies, Created), At(Kids, Created)]);

        var result = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created)]), NoneSeen, Now);

        var change = Assert.Single(result.Events);
        Assert.Equal((LifecycleEventKind.Removed, false), (change.Kind, change.Exact));
    }

    [Fact]
    public void ARenameTheFilterSaw_WaitsWhileTheNewNameHasNoItem_KeepingTheOldGrants_ThenRePointsEverything()
    {
        // On 10.11.5, with refreshLibrary=false the new name is listed at once with ItemId null, and gets its item only at
        // the next scan; the pending rename lives that long, and the old id's grants stay meanwhile.
        var user = User(1, "kid");
        var kid = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var other = Role(2, "Other", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Kids]);
        var renamed = Library(7, "Kids Movies") with { Locations = Kids.Locations };
        var known = Known(
            [At(Movies, Created), At(Kids, Created)],
            roles: [kid, other],
            assignments: [Assign(user, [kid], remove: [Kids])],
            ledger: new Dictionary<Guid, LedgerEntry> { [user.Id] = Ledger(false, Kids, Movies) });
        known = LibraryLifecycle.RecordRename(known, Kids.Id, "Kids", "Kids Movies", Now);

        var waiting = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(renamed, Created, hasItem: false)]), NoneSeen, Now);

        Assert.Empty(waiting.Events);
        Assert.Equal([Kids.Id], waiting.Waiting);
        Assert.True(waiting.Catalog.Contains(Kids.Id));
        Assert.False(waiting.Document.Roles[0].Libraries.Refs[0].Orphaned);
        Assert.Single(waiting.Document.PendingRenames);
        Assert.Contains(Kids.Id, EffectiveRulesResolver.Resolve(waiting.Document, waiting.Catalog, user, Assign(user, [kid])).Libraries.EnabledFolders);

        var done = LibraryLifecycle.Reconcile(waiting.Document, new LibraryCatalog([At(Movies, Created), At(renamed, Created)]), NoneSeen, Now.AddHours(3));

        var change = Assert.Single(done.Events);
        Assert.Equal((LifecycleEventKind.Renamed, renamed.Id, Kids.Id, "Kids", true), (change.Kind, change.Id, change.OldId!.Value, change.OldName, change.Exact));
        Assert.Empty(done.Document.PendingRenames);
        Assert.Equal((renamed.Id, "Kids Movies", false), (done.Document.Roles[0].Libraries.Refs[0].Id, done.Document.Roles[0].Libraries.Refs[0].NameAtSelection, done.Document.Roles[0].Libraries.Refs[0].Orphaned));
        Assert.Equal(renamed.Id, done.Document.Roles[1].Libraries.Refs[0].Id);
        Assert.Equal([renamed.Id], done.Document.Assignments[0].Libraries.Remove);
        Assert.Equal([renamed.Id, Movies.Id], done.Document.Ledger[user.Id].OriginalEnabledFolders);
        Assert.DoesNotContain(done.Document.KnownLibraries, k => k.Id.Equals(Kids.Id));
        Assert.Equal(Created, done.Document.KnownLibraries.Single(k => k.Id.Equals(renamed.Id)).CreatedUtc);
    }

    [Fact]
    public void ARenameTheBackstopFinds_ByTheSameFolders_IsRePointed()
    {
        var kid = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var renamed = Library(7, "Kids Movies") with { Locations = Kids.Locations };
        var known = Known([At(Movies, Created), At(Kids, Created)], roles: [kid]);

        var result = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(renamed, Created)]), NoneSeen, Now);

        var change = Assert.Single(result.Events);
        Assert.Equal((LifecycleEventKind.Renamed, false), (change.Kind, change.Exact));
        Assert.Equal(renamed.Id, result.Document.Roles[0].Libraries.Refs[0].Id);
    }

    [Fact]
    public void ACaseOnlyRenameOnWindows_IsARename_ToTheNewNamesId()
    {
        // On 10.11.5, Kids → kids gives a new id (GetNewItemId of the new folder), while GetVirtualFolders() keeps reporting
        // the old ItemId for the new name; the catalog's ids come from GetNewItemId, so it sees the old id go and the new
        // one come with the same folders.
        var kid = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var lower = Library(8, "kids") with { Locations = Kids.Locations };
        var known = LibraryLifecycle.RecordRename(Known([At(Movies, Created), At(Kids, Created)], roles: [kid]), Kids.Id, "Kids", "kids", Now);

        var result = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(lower, Created)]), NoneSeen, Now);

        var change = Assert.Single(result.Events);
        Assert.Equal((LifecycleEventKind.Renamed, lower.Id, Kids.Id, true), (change.Kind, change.Id, change.OldId!.Value, change.Exact));
        Assert.Equal(lower.Id, result.Document.Roles[0].Libraries.Refs[0].Id);
    }

    [Fact]
    public void TwoNewLibrariesWithTheSameFolders_OrNoFolders_AreNoRename()
    {
        var known = Known([At(Movies, Created), At(Kids, Created), At(Shows with { Locations = [] }, Created)]);
        var a = Library(6, "Kids A") with { Locations = Kids.Locations };
        var b = Library(7, "Kids B") with { Locations = Kids.Locations };
        var c = Library(8, "No paths") with { Locations = [] };

        var result = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(a, Created), At(b, Created), At(c, Created)]), NoneSeen, Now);

        var kids = result.Events.Single(e => e.Id.Equals(Kids.Id));
        Assert.Equal(LifecycleEventKind.Removed, kids.Kind);
        Assert.Equal([a.Id, b.Id], kids.RenameCandidates.Order());
        Assert.Equal(LifecycleEventKind.Removed, result.Events.Single(e => e.Id.Equals(Shows.Id)).Kind);
        Assert.Equal(3, result.Events.Count(e => e.Kind == LifecycleEventKind.Added));
    }

    [Fact]
    public void ALibraryDeletedAndCreatedAgainWithinOneInterval_IsMarkedRecreated_ByItsNewerFolder_AndNoOnlyListGrantsIt()
    {
        // On 10.11.5: same id, folder newer every time (no NTFS tunnelling); with refreshLibrary=false the item row and its
        // DateCreated are the old ones, which is why the folder's creation time is what counts.
        var user = User(1, "kid", native: Folders(Kids));
        var kid = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies]);
        var all = Role(2, "All", mode: AccessRolesRoleLibraryMode.All);
        var known = Known([At(Movies, Created), At(Kids, Created)], roles: [kid, all], assignments: [Assign(user, [kid])]);

        var result = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(Kids, Created.AddSeconds(4))]), NoneSeen, Now);

        var change = Assert.Single(result.Events);
        Assert.Equal((LifecycleEventKind.Recreated, Kids.Id), (change.Kind, change.Id));
        var record = result.Document.KnownLibraries.Single(k => k.Id.Equals(Kids.Id));
        Assert.Equal((Created, Now), (record.CreatedUtc!.Value, record.RecreatedUtc!.Value));
        Assert.True(EffectiveRulesResolver.IsRecreated(result.Document, result.Catalog, Kids.Id));
        var effective = EffectiveRulesResolver.Resolve(result.Document, result.Catalog, user);
        Assert.Equal([Movies.Id], effective.Libraries.EnabledFolders);
        Assert.Equal([Kids.Id], effective.Libraries.Recreated);
        Assert.True(EffectiveRulesResolver.Resolve(result.Document, result.Catalog, user, Assign(user, [all])).Libraries.EnableAllFolders);
        Assert.Empty(LibraryLifecycle.Reconcile(result.Document, result.Catalog, NoneSeen, Now.AddMinutes(5)).Events);
    }

    [Fact]
    public void AnOrphanedLibraryListedAgain_IsRecreated_AndActive()
    {
        var known = Known([At(Movies, Created), At(Kids, Created)]);
        var removed = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created)]), new HashSet<Guid> { Kids.Id }, Now).Document;

        var back = LibraryLifecycle.Reconcile(removed, new LibraryCatalog([At(Movies, Created), At(Kids, null)]), NoneSeen, Now.AddMinutes(5));

        var change = Assert.Single(back.Events);
        Assert.Equal(LifecycleEventKind.Recreated, change.Kind);
        var record = back.Document.KnownLibraries.Single(k => k.Id.Equals(Kids.Id));
        Assert.True(record.State.Is(AccessRolesLibraryState.Active));
        Assert.NotNull(record.RecreatedUtc);
        Assert.True(EffectiveRulesResolver.IsRecreated(back.Document, back.Catalog, Kids.Id));
    }

    [Fact]
    public void GrantAgain_RecordsTheFoldersTime_ClearsTheMark_AndUnOrphansTheReferences()
    {
        var user = User(1, "kid");
        var kid = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var known = Known([At(Movies, Created), At(Kids, Created)], roles: [kid], assignments: [Assign(user, [kid], add: [Kids])]);
        var removed = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created)]), new HashSet<Guid> { Kids.Id }, Now).Document;
        var catalog = new LibraryCatalog([At(Movies, Created), At(Kids, Created.AddDays(18))]);
        var recreated = LibraryLifecycle.Reconcile(removed, catalog, NoneSeen, Now).Document;
        Assert.True(EffectiveRulesResolver.Resolve(recreated, catalog, user).Libraries.NoLibraries);

        var granted = LibraryLifecycle.GrantAgain(recreated, catalog, Kids.Id, Now)!;

        var record = granted.KnownLibraries.Single(k => k.Id.Equals(Kids.Id));
        Assert.Equal((Created.AddDays(18), (DateTime?)null), (record.CreatedUtc!.Value, record.RecreatedUtc));
        Assert.False(granted.Roles[0].Libraries.Refs[0].Orphaned);
        Assert.False(EffectiveRulesResolver.IsRecreated(granted, catalog, Kids.Id));
        Assert.Equal([Kids.Id], EffectiveRulesResolver.Resolve(granted, catalog, user).Libraries.EnabledFolders);
        Assert.Null(LibraryLifecycle.GrantAgain(granted, catalog, Kids.Id, Now));
        Assert.Null(LibraryLifecycle.GrantAgain(granted, catalog, Library(9, "None").Id, Now));
    }

    [Fact]
    public void Purge_DropsADeletedLibrarysGrants_AndKeepsItsExclusions_AsAKnownLibraryTheServerDoesntList()
    {
        // Regression (1.0.1): the grants go, the user's own Remove list stays.
        var user = User(1, "solo");
        var kid = Role(1, "Kid", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies]);
        var known = Known(
            [At(Movies, Created), At(Kids, Created)],
            roles: [kid],
            assignments: [Assign(user, remove: [Kids])],
            ledger: new Dictionary<Guid, LedgerEntry> { [user.Id] = Ledger(false, Kids, Movies) });
        var removed = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created)]), new HashSet<Guid> { Kids.Id }, Now).Document;

        var purged = LibraryLifecycle.Purge(removed, Kids.Id)!;

        Assert.Equal([Movies.Id], purged.Roles[0].Libraries.Refs.Select(r => r.Id));
        Assert.Equal([Kids.Id], purged.Assignments[0].Libraries.Remove);
        Assert.Equal([Movies.Id], purged.Ledger[user.Id].OriginalEnabledFolders);
        Assert.True(purged.KnownLibraries.Single(k => k.Id.Equals(Kids.Id)).State.Is(AccessRolesLibraryState.Orphaned));
        Assert.True(LibraryLifecycle.IsExcluded(purged, Kids.Id));
        Assert.Null(LibraryLifecycle.Purge(purged, Kids.Id));

        // With the exclusion taken out, the known library goes too.
        var unexcluded = purged with { Assignments = [purged.Assignments[0] with { Libraries = purged.Assignments[0].Libraries with { Remove = [] } }] };
        var gone = LibraryLifecycle.Purge(unexcluded, Kids.Id)!;
        Assert.DoesNotContain(gone.KnownLibraries, k => k.Id.Equals(Kids.Id));
        Assert.Null(LibraryLifecycle.Purge(gone, Kids.Id));
    }

    [Fact]
    public void APurgedLibraryCreatedAgain_IsRecreated_StaysLeftOutOfItsAllExceptRolesAndRemoveLists_AndIsGrantedByNothing()
    {
        // Regression: purge Kids, create a library again under its id, and the kid of an
        // All except Kids role must not see it, before or after the admin grants it again.
        var kid = User(1, "kid");
        var removing = User(2, "removing");
        var listed = User(3, "listed");
        var adding = User(4, "adding");
        var original = User(5, "original");
        var allBut = Role(1, "All but Kids", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Kids]);
        var all = Role(2, "All", mode: AccessRolesRoleLibraryMode.All);
        var only = Role(3, "Kids only", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids, Movies]);
        var newer = Role(4, "Newer") with { Libraries = new RoleLibraries { Mode = LenientEnum<AccessRolesRoleLibraryMode>.Parse("Hidden"), Refs = [new LibraryReference { Id = Kids.Id, NameAtSelection = Kids.Name }] } };
        var known = Known(
            [At(Movies, Created), At(Kids, Created)],
            roles: [allBut, all, only, newer],
            assignments: [Assign(kid, [allBut]), Assign(removing, [all], remove: [Kids]), Assign(listed, [only]), Assign(adding, [allBut], add: [Kids]), Assign(original)],
            ledger: new Dictionary<Guid, LedgerEntry> { [original.Id] = Ledger(false, Kids, Movies) });
        var removed = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created)]), new HashSet<Guid> { Kids.Id }, Now).Document;

        var purged = LibraryLifecycle.Purge(removed, Kids.Id)!;

        Assert.Equal([Kids.Id], purged.Roles[0].Libraries.Refs.Select(r => r.Id));
        Assert.Equal([Movies.Id], purged.Roles[2].Libraries.Refs.Select(r => r.Id));
        Assert.Equal([Kids.Id], purged.Roles[3].Libraries.Refs.Select(r => r.Id));
        Assert.Empty(purged.Assignments[3].Libraries.Add);
        Assert.Equal([Kids.Id], purged.Assignments[1].Libraries.Remove);
        Assert.Equal([Movies.Id], purged.Ledger[original.Id].OriginalEnabledFolders);

        var catalog = new LibraryCatalog([At(Movies, Created), At(Kids, Created.AddDays(3))]);
        var back = LibraryLifecycle.Reconcile(purged, catalog, NoneSeen, Now.AddMinutes(5));

        Assert.Equal((LifecycleEventKind.Recreated, Kids.Id), (Assert.Single(back.Events).Kind, back.Events[0].Id));
        Assert.True(EffectiveRulesResolver.IsRecreated(back.Document, back.Catalog, Kids.Id));
        Assert.Equal([Movies.Id], Resolve(back, kid, back.Document.Assignments[0]).EnabledFolders);
        Assert.False(Resolve(back, removing, back.Document.Assignments[1]).EnableAllFolders);
        Assert.Equal([Movies.Id], Resolve(back, removing, back.Document.Assignments[1]).EnabledFolders);
        Assert.Equal([Movies.Id], Resolve(back, listed, back.Document.Assignments[2]).EnabledFolders);
        Assert.Equal([Movies.Id], Resolve(back, adding, back.Document.Assignments[3]).EnabledFolders);

        // Granted again, the exclusions still leave it out; nothing that named it as a grant was kept to give it.
        var granted = LibraryLifecycle.GrantAgain(back.Document, catalog, Kids.Id, Now.AddMinutes(10))!;
        Assert.False(EffectiveRulesResolver.IsRecreated(granted, catalog, Kids.Id));
        Assert.Equal([Movies.Id], EffectiveRulesResolver.Resolve(granted, catalog, kid, granted.Assignments[0]).Libraries.EnabledFolders);
        Assert.Equal([Movies.Id], EffectiveRulesResolver.Resolve(granted, catalog, removing, granted.Assignments[1]).Libraries.EnabledFolders);
        Assert.Equal([Movies.Id], EffectiveRulesResolver.Resolve(granted, catalog, listed, granted.Assignments[2]).Libraries.EnabledFolders);
    }

    [Fact]
    public void RecordRename_ChainsARenameOfAPendingRename_AndIgnoresANoOp()
    {
        var document = LibraryLifecycle.RecordRename(Document(), Kids.Id, "Kids", "Kids 2", Now);
        document = LibraryLifecycle.RecordRename(document, Library(7, "Kids 2").Id, "Kids 2", "Kids 3", Now.AddSeconds(1));

        var rename = Assert.Single(document.PendingRenames);
        Assert.Equal((Kids.Id, "Kids", "Kids 3"), (rename.OldId, rename.OldName, rename.NewName));
        Assert.Same(document, LibraryLifecycle.RecordRename(document, Kids.Id, "Same", "Same", Now));
    }

    [Fact]
    public void ARenameThatNeverCompletes_IsDroppedAfterAWeek_AndItsLibraryCountsAsDeleted()
    {
        var known = LibraryLifecycle.RecordRename(Known([At(Movies, Created), At(Kids, Created)]), Kids.Id, "Kids", "Elsewhere", Now);

        var weekLater = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created)]), NoneSeen, Now + LibraryLifecycle.PendingRenameLimit + TimeSpan.FromMinutes(1));

        Assert.Empty(weekLater.Document.PendingRenames);
        Assert.Equal(LifecycleEventKind.Removed, Assert.Single(weekLater.Events).Kind);
    }

    [Fact]
    public void TheEnabledSwitchAndTheFolders_AreRecordedWithoutAnEvent()
    {
        // On 10.11.5, disabling raises no library event; the backstop re-reads it.
        var known = Known([At(Movies, Created), At(Kids, Created)]);

        var result = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(Kids with { Enabled = false, Locations = [@"D:\Kids"] }, Created)]), NoneSeen, Now);

        Assert.Empty(result.Events);
        var record = result.Document.KnownLibraries.Single(k => k.Id.Equals(Kids.Id));
        Assert.Equal((false, @"D:\Kids"), (record.Enabled, record.Locations.Single()));
    }

    [Fact]
    public void WhileARenameWaits_TheNewNamesLibraryIsWithheld_SoNoAllExceptRoleOrRemoveGrantsIt_AndOnlyKeepsTheOldId()
    {
        // AllExcept [Kids] and a user's Remove [Kids] expanded against a catalog that listed the
        // new name's library granted it while the rename waited. It is withheld until the re-pointing, fail closed.
        var allBut = User(1, "allbut");
        var removed = User(2, "removed");
        var only = User(3, "only");
        var allButRole = Role(1, "AllBut", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Kids]);
        var allRole = Role(2, "All", mode: AccessRolesRoleLibraryMode.All);
        var onlyRole = Role(3, "Only", mode: AccessRolesRoleLibraryMode.Only, refs: [Kids]);
        var renamed = Library(7, "Secret2") with { Locations = Kids.Locations };
        var known = Known([At(Movies, Created), At(Kids, Created)], roles: [allButRole, allRole, onlyRole]);
        known = LibraryLifecycle.RecordRename(known, Kids.Id, "Kids", "Secret2", Now);

        var waiting = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(renamed, Created, hasItem: false)]), NoneSeen, Now);

        Assert.Equal([renamed.Id], waiting.Withheld);
        Assert.False(waiting.Catalog.Contains(renamed.Id));
        Assert.DoesNotContain(renamed.Id, Resolve(waiting, allBut, Assign(allBut, [allButRole])).EnabledFolders);
        Assert.DoesNotContain(renamed.Id, Resolve(waiting, removed, Assign(removed, [allRole], remove: [Kids])).EnabledFolders);
        Assert.False(Resolve(waiting, removed, Assign(removed, [allRole], remove: [Kids])).EnableAllFolders);
        Assert.Equal([Kids.Id], Resolve(waiting, only, Assign(only, [onlyRole])).EnabledFolders);

        // Re-pointed once the new name has its item: AllExcept and Remove name the new id, Only grants it.
        var done = LibraryLifecycle.Reconcile(waiting.Document, new LibraryCatalog([At(Movies, Created), At(renamed, Created)]), NoneSeen, Now);

        Assert.Empty(done.Withheld);
        Assert.Equal([Movies.Id], Resolve(done, allBut, Assign(allBut, [done.Document.Roles[0]])).EnabledFolders);
        Assert.Equal([renamed.Id], Resolve(done, only, Assign(only, [done.Document.Roles[2]])).EnabledFolders);
    }

    [Fact]
    public void WhileTheBackstopWaitsForACandidatesItem_EveryCandidateIsWithheld()
    {
        var allButRole = Role(1, "AllBut", mode: AccessRolesRoleLibraryMode.AllExcept, refs: [Kids]);
        var renamed = Library(7, "Secret2") with { Locations = Kids.Locations };
        var known = Known([At(Movies, Created), At(Kids, Created)], roles: [allButRole]);

        var waiting = LibraryLifecycle.Reconcile(known, new LibraryCatalog([At(Movies, Created), At(renamed, Created, hasItem: false)]), NoneSeen, Now);

        Assert.Equal([Kids.Id], waiting.Waiting);
        Assert.Equal([renamed.Id], waiting.Withheld);
        Assert.Equal(new[] { Kids.Id, Movies.Id }.Order(), waiting.Catalog.Ids.Order());
        Assert.Empty(LibraryLifecycle.Reconcile(waiting.Document, new LibraryCatalog([At(Movies, Created), At(renamed, Created)]), NoneSeen, Now).Withheld);
    }

    private static LibraryDecision Resolve(LifecycleResult result, ServerUser user, RoleAssignment assignment)
        => EffectiveRulesResolver.Resolve(result.Document, result.Catalog, user, assignment).Libraries;

    private static CatalogLibrary At(CatalogLibrary library, DateTime? created, bool hasItem = true) => library with { CreatedUtc = created, HasItem = hasItem };

    private static AccessRolesDocument Known(
        IReadOnlyList<CatalogLibrary> libraries,
        IEnumerable<AccessRole>? roles = null,
        IEnumerable<RoleAssignment>? assignments = null,
        IReadOnlyDictionary<Guid, LedgerEntry>? ledger = null)
        => LibraryLifecycle.Reconcile(Document(roles, assignments, ledger), new LibraryCatalog(libraries), NoneSeen, Now.AddDays(-1)).Document;
}
