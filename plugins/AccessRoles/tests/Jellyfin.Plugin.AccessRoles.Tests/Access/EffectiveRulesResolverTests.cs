using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Xunit;
using static Jellyfin.Plugin.AccessRoles.Tests.TestSupport.AccessFixtures;

namespace Jellyfin.Plugin.AccessRoles.Tests.Access;

/// <summary>
/// <see cref="EffectiveRulesResolver"/>: locks accumulate, libraries add up,
/// per-user overrides win, the per-user modes, the ledger starting set, <c>NoLibraries</c>, administrators, new, disabled,
/// deleted and re-created libraries, values a newer build wrote, and the provenance of every result.
/// </summary>
public sealed class EffectiveRulesResolverTests
{
    private static readonly AccessRole Kid = Role(1, "Kid", AccessPresets.Locks(AccessRolesPreset.Kid), AccessRolesRoleLibraryMode.Only, Kids);
    private static readonly AccessRole Family = Role(2, "Family", [AccessRolesLock.Home, AccessRolesLock.Playback], AccessRolesRoleLibraryMode.Only, Movies, Kids);
    private static readonly AccessRole Everything = Role(3, "Everything", [], AccessRolesRoleLibraryMode.All);
    private static readonly AccessRole NoHorror = Role(4, "No horror", [AccessRolesLock.QuickConnect], AccessRolesRoleLibraryMode.AllExcept, Horror);
    private static readonly AccessRole LocksOnly = Role(5, "Locks only", [AccessRolesLock.Profile, AccessRolesLock.UserDirectory]);

    [Fact]
    public void AUserWithoutAnAssignment_IsNotManaged_NoLockApplies_AndTheNativeAccessIsShown()
    {
        var user = User(1, "plain", native: Folders(Movies, Shows));

        var effective = EffectiveRulesResolver.Resolve(Document([Kid]), Catalog, user);

        Assert.Equal(AccessRolesUserState.NotManaged, effective.State);
        Assert.All(effective.Locks.Values, l => Assert.Equal(new LockDecision(false, AccessSources.NotManaged), l));
        Assert.False(effective.Libraries.Managed);
        Assert.Equal(AccessSources.NotManaged, effective.Libraries.StartingSet);
        Assert.Equal(["Movies", "Shows"], Names(effective.Libraries.Visible));
        Assert.All(effective.Libraries.Visible, v => Assert.Equal([AccessSources.Native], v.Sources));
        Assert.Equal(["Collections", "Horror", "Kids"], effective.Libraries.Hidden.Select(h => h.Library.Name));
    }

    [Fact]
    public void OneRole_LocksItsSections_WithTheRoleAsSource_AndGrantsOnlyItsLibraries()
    {
        var user = User(1, "kid");

        var effective = Resolve(user, Assign(user, [Kid]), Kid);

        Assert.Equal(AccessRolesUserState.Managed, effective.State);
        Assert.Equal(AccessPresets.Locks(AccessRolesPreset.Kid).ToHashSet(), effective.Locked);
        Assert.Equal(new LockDecision(true, "Role:Kid"), effective.Locks[AccessRolesLock.Profile]);
        Assert.Equal(new LockDecision(false, AccessSources.Default), effective.Locks[AccessRolesLock.Playback]);
        Assert.True(effective.Libraries.Managed);
        Assert.Equal(AccessSources.StartRoles, effective.Libraries.StartingSet);
        Assert.False(effective.Libraries.EnableAllFolders);
        Assert.Equal([Kids.Id], effective.Libraries.EnabledFolders);
        Assert.Equal(["Role:Kid"], Assert.Single(effective.Libraries.Visible).Sources);
        Assert.All(effective.Libraries.Hidden, h => Assert.Equal(AccessSources.NotGranted, h.Reason));
    }

    [Fact]
    public void SeveralRoles_LocksAccumulate_LibrariesAreTheUnion_AndEachLibraryNamesEveryRoleThatGrantsIt()
    {
        var user = User(1, "family");

        var effective = Resolve(user, Assign(user, [Kid, Family]), Kid, Family);

        Assert.Equal(AccessPresets.Locks(AccessRolesPreset.Kid).Append(AccessRolesLock.Playback).ToHashSet(), effective.Locked);
        Assert.Equal("Role:Kid", effective.Locks[AccessRolesLock.Home].Source);
        Assert.Equal("Role:Family", effective.Locks[AccessRolesLock.Playback].Source);
        Assert.Equal(["Kids", "Movies"], Names(effective.Libraries.Visible));
        Assert.Equal(["Role:Kid", "Role:Family"], effective.Libraries.Visible.Single(v => v.Library.Name == "Kids").Sources);
        Assert.Equal(["Role:Family"], effective.Libraries.Visible.Single(v => v.Library.Name == "Movies").Sources);
        Assert.Equal(Catalog.Order([Kids.Id, Movies.Id]), effective.Libraries.EnabledFolders);
    }

    [Fact]
    public void UserOverrides_WinOverEveryRole_InBothDirections()
    {
        var user = User(1, "kid");
        var overrides = new Dictionary<AccessRolesLock, AccessRolesLockOverride>
        {
            [AccessRolesLock.QuickConnect] = AccessRolesLockOverride.Unlock,
            [AccessRolesLock.Subtitles] = AccessRolesLockOverride.Lock,
            [AccessRolesLock.Home] = AccessRolesLockOverride.Inherit
        };

        var effective = Resolve(user, Assign(user, [Kid, Family], overrides: overrides), Kid, Family);

        Assert.Equal(new LockDecision(false, AccessSources.UserOverride), effective.Locks[AccessRolesLock.QuickConnect]);
        Assert.Equal(new LockDecision(true, AccessSources.UserOverride), effective.Locks[AccessRolesLock.Subtitles]);
        Assert.Equal(new LockDecision(true, "Role:Kid"), effective.Locks[AccessRolesLock.Home]);
    }

    [Fact]
    public void AnAllRole_GrantsEveryLibrary_FutureOnesIncluded_AndWinsTheUnion()
    {
        var user = User(1, "u");

        var effective = Resolve(user, Assign(user, [Kid, Everything]), Kid, Everything);

        Assert.True(effective.Libraries.IncludesFuture);
        Assert.True(effective.Libraries.EnableAllFolders);
        Assert.Empty(effective.Libraries.EnabledFolders);
        Assert.Equal(5, effective.Libraries.Visible.Count);
        Assert.Empty(effective.Libraries.Hidden);
    }

    [Fact]
    public void AnAllExceptRole_GrantsEveryCurrentLibraryButTheListed_AsAnExpandedList()
    {
        var user = User(1, "u");

        var effective = Resolve(user, Assign(user, [NoHorror]), NoHorror);

        Assert.False(effective.Libraries.IncludesFuture);
        Assert.False(effective.Libraries.EnableAllFolders);
        Assert.Equal(["Collections", "Kids", "Movies", "Shows"], Names(effective.Libraries.Visible));
        Assert.Equal(AccessSources.NotGranted, Assert.Single(effective.Libraries.Hidden).Reason);
    }

    [Fact]
    public void ANewLibrary_GoesToAllAndAllExceptRoles_NeverToOnlyRoles()
    {
        // All and AllExcept include new libraries by definition; Only roles don't until the admin picks them.
        var anime = Library(9, "Anime", "tvshows");
        var grown = new LibraryCatalog([.. Catalog.Libraries, anime]);
        var everything = User(1, "all");
        var allBut = User(2, "allbut");
        var only = User(3, "only");
        var document = Document([Kid, Everything, NoHorror], [Assign(everything, [Everything]), Assign(allBut, [NoHorror]), Assign(only, [Kid])]);

        Assert.Contains(anime, EffectiveRulesResolver.Resolve(document, grown, everything).Libraries.Visible.Select(v => v.Library));
        Assert.Contains(anime.Id, EffectiveRulesResolver.Resolve(document, grown, allBut).Libraries.EnabledFolders);
        Assert.DoesNotContain(anime.Id, EffectiveRulesResolver.Resolve(document, grown, only).Libraries.EnabledFolders);
        Assert.Equal(AccessSources.NotGranted, EffectiveRulesResolver.Resolve(document, grown, only).Libraries.Hidden.Single(h => h.Library.Id == anime.Id).Reason);
    }

    [Fact]
    public void UserModeAll_IsEveryLibrary_AndARemoveTurnsItIntoTheExpandedListWithoutIt()
    {
        var user = User(1, "u", native: Folders(Kids));

        var all = Resolve(user, Assign(user, [Kid], AccessRolesUserLibraryMode.All), Kid);
        var allButHorror = Resolve(user, Assign(user, [Kid], AccessRolesUserLibraryMode.All, remove: [Horror]), Kid);

        Assert.Equal(AccessSources.AllLibraries, all.Libraries.StartingSet);
        Assert.True(all.Libraries.EnableAllFolders);
        Assert.False(allButHorror.Libraries.EnableAllFolders);
        Assert.Equal(["Collections", "Kids", "Movies", "Shows"], Names(allButHorror.Libraries.Visible));
        Assert.Equal(AccessSources.RemovedForUser, Assert.Single(allButHorror.Libraries.Hidden).Reason);
    }

    [Fact]
    public void UserModeOnly_StartsEmpty_AndTheAddListSuppliesTheLibraries_WhateverTheRoles()
    {
        var user = User(1, "u");

        var effective = Resolve(user, Assign(user, [Family], AccessRolesUserLibraryMode.Only, add: [Shows]), Family);

        Assert.Equal(AccessSources.StartEmpty, effective.Libraries.StartingSet);
        Assert.Equal(["Shows"], Names(effective.Libraries.Visible));
        Assert.Equal([AccessSources.UserAdd], effective.Libraries.Visible[0].Sources);
    }

    [Fact]
    public void AddAndRemove_ApplyOnTopOfTheRoles_AndRemoveBeatsEveryGrant()
    {
        var user = User(1, "u");

        var effective = Resolve(user, Assign(user, [Family, Everything], add: [Shows], remove: [Kids]), Family, Everything);

        Assert.False(effective.Libraries.EnableAllFolders);
        Assert.DoesNotContain(Kids.Id, effective.Libraries.EnabledFolders);
        Assert.Equal(AccessSources.RemovedForUser, effective.Libraries.Hidden.Single(h => h.Library.Id == Kids.Id).Reason);
        Assert.Equal(["Role:Everything", AccessSources.UserAdd], effective.Libraries.Visible.Single(v => v.Library.Id == Shows.Id).Sources);
    }

    [Fact]
    public void ARemoveOnlyOverride_WithoutALibraryRole_StartsFromTheOriginalAccessInTheLedger()
    {
        // "Remove Horror for a user with no library role": only Horror goes.
        var solo = User(1, "solo", native: Folders(Kids));
        var ledger = new Dictionary<Guid, LedgerEntry> { [solo.Id] = Ledger(false, Movies, Shows, Horror) };
        var document = Document([LocksOnly], [Assign(solo, [LocksOnly], remove: [Horror])], ledger);

        var effective = EffectiveRulesResolver.Resolve(document, Catalog, solo);

        Assert.True(effective.Libraries.Managed);
        Assert.Equal(AccessSources.Original, effective.Libraries.StartingSet);
        Assert.Equal(["Movies", "Shows"], Names(effective.Libraries.Visible));
        Assert.All(effective.Libraries.Visible, v => Assert.Equal([AccessSources.Original], v.Sources));
    }

    [Fact]
    public void AnOriginalAccessOfEveryLibrary_WithARemove_IsEveryCurrentLibraryButThat()
    {
        var solo = User(1, "solo", native: Folders(Kids));
        var ledger = new Dictionary<Guid, LedgerEntry> { [solo.Id] = Ledger(true) };

        var effective = EffectiveRulesResolver.Resolve(Document([], [Assign(solo, remove: [Horror])], ledger), Catalog, solo);

        Assert.True(effective.Libraries.IncludesFuture);
        Assert.False(effective.Libraries.EnableAllFolders);
        Assert.Equal(["Collections", "Kids", "Movies", "Shows"], Names(effective.Libraries.Visible));
    }

    [Fact]
    public void WithoutALedgerEntryYet_TheOriginalAccessIsTheNativeOne()
    {
        var solo = User(1, "solo", native: Folders(Movies, Horror));

        var effective = EffectiveRulesResolver.Resolve(Document([], [Assign(solo, remove: [Horror])]), Catalog, solo);

        Assert.Equal(["Movies"], Names(effective.Libraries.Visible));
    }

    [Fact]
    public void TheLedgerStartingSet_DropsDeletedLibraries_AndLeavesOutARecreatedOne()
    {
        // The ledger: deleted ids are dropped; a library created again under the same id (a newer folder) isn't granted.
        var created = Now.AddDays(-30);
        var recreatedKids = Library(2, "Kids", "movies", created: Now.AddDays(-1));
        var catalog = new LibraryCatalog([Movies, recreatedKids, Shows]);
        var solo = User(1, "solo");
        var gone = Guid.NewGuid();
        var document = Document([], [Assign(solo, remove: [Shows])], new Dictionary<Guid, LedgerEntry> { [solo.Id] = Ledger(false, Movies, Kids, Shows) with { OriginalEnabledFolders = [Movies.Id, Kids.Id, Shows.Id, gone] } }) with
        {
            KnownLibraries = [new KnownLibrary { Id = Kids.Id, Name = "Kids", CreatedUtc = created, FirstSeenUtc = created }]
        };

        var effective = EffectiveRulesResolver.Resolve(document, catalog, solo);

        Assert.Equal(["Movies"], Names(effective.Libraries.Visible));
        Assert.Equal([Kids.Id], effective.Libraries.Recreated);
        Assert.Equal(AccessSources.Recreated, effective.Libraries.Hidden.Single(h => h.Library.Id == Kids.Id).Reason);
        Assert.Contains(effective.Warnings, w => w.Contains("created again", StringComparison.Ordinal));
        Assert.DoesNotContain(gone, effective.Libraries.EnabledFolders);
    }

    [Fact]
    public void ALibraryCreatedAgain_IsntGrantedByAnOnlyRoleOrAnAddList_NorByAnOrphanedReference()
    {
        // A library deleted and created again under the same id is new, so neither a
        // role's Only… list nor a user's Add list grants it until the admin grants it again. KnownLibraries is the library sync's to
        // fill; the test sets it. An Only… reference marked orphaned that the server lists again is left out too.
        var known = Now.AddDays(-30);
        var kids = Library(2, "Kids", "movies", created: Now.AddDays(-1));
        var horror = Library(4, "Horror", "movies", created: Now.AddDays(-1));
        var shows = Library(3, "Shows", "tvshows", created: known);
        var catalog = new LibraryCatalog([Movies, kids, shows, horror, Collections]);
        var family = Role(2, "Family", [], AccessRolesRoleLibraryMode.Only, Movies, Kids);
        var old = Role(6, "Old", [], AccessRolesRoleLibraryMode.Only, Shows);
        old = old with { Libraries = old.Libraries with { Refs = [old.Libraries.Refs[0] with { Orphaned = true }] } };
        var user = User(1, "kid");
        var everyone = User(2, "everyone");
        var document = Document([family, old, Everything, NoHorror], [Assign(user, [family, old], add: [Horror]), Assign(everyone, [Everything, NoHorror])]) with
        {
            KnownLibraries =
            [
                new KnownLibrary { Id = Kids.Id, Name = "Kids", CreatedUtc = known, FirstSeenUtc = known },
                new KnownLibrary { Id = Horror.Id, Name = "Horror", CreatedUtc = known, FirstSeenUtc = known },
                new KnownLibrary { Id = Shows.Id, Name = "Shows", CreatedUtc = known, FirstSeenUtc = known }
            ]
        };

        var effective = EffectiveRulesResolver.Resolve(document, catalog, user);
        var all = EffectiveRulesResolver.Resolve(document, catalog, everyone);

        Assert.Equal(["Movies"], Names(effective.Libraries.Visible));
        Assert.Equal([Movies.Id], effective.Libraries.EnabledFolders);
        Assert.Equal(["Horror", "Kids", "Shows"], effective.Libraries.Hidden.Where(h => h.Reason == AccessSources.Recreated).Select(h => h.Library.Name));
        Assert.Equal(catalog.Order([Kids.Id, Horror.Id, Shows.Id]), effective.Libraries.Recreated);
        Assert.Equal(3, effective.Warnings.Count(w => w.Contains("created again", StringComparison.Ordinal)));
        Assert.False(EffectiveRulesResolver.Grants(document, catalog, family, Kids.Id));
        Assert.True(EffectiveRulesResolver.Grants(document, catalog, family, Movies.Id));
        Assert.False(EffectiveRulesResolver.Grants(document, catalog, old, Shows.Id));

        // All and AllExcept include a new library by definition, and an AllExcept list keeps leaving its ids out.
        Assert.True(all.Libraries.EnableAllFolders);
        Assert.Empty(all.Libraries.Recreated);
        Assert.True(EffectiveRulesResolver.Grants(document, catalog, NoHorror, Kids.Id));
        Assert.False(EffectiveRulesResolver.Grants(document, catalog, NoHorror, Horror.Id));
    }

    [Fact]
    public void AnOnlyUserWithNothing_IsNoLibraries_ConfirmedOnlyWithConfirmNoLibraries()
    {
        var user = User(1, "u");

        var unconfirmed = Resolve(user, Assign(user, mode: AccessRolesUserLibraryMode.Only));
        var confirmed = Resolve(user, Assign(user, mode: AccessRolesUserLibraryMode.Only, confirmNoLibraries: true));

        Assert.True(unconfirmed.Libraries.NoLibraries);
        Assert.False(unconfirmed.Libraries.NoLibrariesConfirmed);
        Assert.Contains("u would have no libraries.", unconfirmed.Warnings);
        Assert.True(confirmed.Libraries.NoLibraries);
        Assert.True(confirmed.Libraries.NoLibrariesConfirmed);
        Assert.Empty(confirmed.Libraries.EnabledFolders);
    }

    [Fact]
    public void ARoleWhoseOnlyLibraryWasDeleted_LeavesItsMembersWithNoLibraries_AndNamesTheMissingReference()
    {
        var user = User(1, "kid");
        var catalog = new LibraryCatalog(Catalog.Libraries.Where(l => l.Id != Kids.Id));

        var effective = EffectiveRulesResolver.Resolve(Document([Kid], [Assign(user, [Kid])]), catalog, user);

        Assert.True(effective.Libraries.NoLibraries);
        Assert.Equal([Kids.Id], effective.Libraries.MissingReferences);
        Assert.Contains(effective.Warnings, w => w.Contains("\"Kids\" no longer exists", StringComparison.Ordinal));
    }

    [Fact]
    public void ADisabledLibrary_StaysGranted_AndShowsAsDisabled()
    {
        // Managed users keep the disabled library's id, so re-enabling gives it back.
        var disabledKids = Library(2, "Kids", "movies", enabled: false);
        var catalog = new LibraryCatalog([Movies, disabledKids, Shows]);
        var user = User(1, "kid");

        var effective = EffectiveRulesResolver.Resolve(Document([Kid], [Assign(user, [Kid])]), catalog, user);

        Assert.Equal([Kids.Id], effective.Libraries.EnabledFolders);
        Assert.False(Assert.Single(effective.Libraries.Visible).Library.Enabled);
    }

    [Fact]
    public void AnAdministrator_GetsNoLock_ButLibraryRolesApply_WithBothWarnings()
    {
        // No administrator is restricted, but library roles do apply (Folder.IsVisible has no admin bypass).
        var admin = User(1, "admin", admin: true);

        var effective = Resolve(admin, Assign(admin, [Kid]), Kid);

        Assert.All(effective.Locks.Values, l => Assert.Equal(new LockDecision(false, AccessSources.Administrator), l));
        Assert.Equal([Kids.Id], effective.Libraries.EnabledFolders);
        Assert.Contains(effective.Warnings, w => w.StartsWith("Administrators are not restricted", StringComparison.Ordinal));
        Assert.Contains(effective.Warnings, w => w.StartsWith("Library roles apply to administrators too", StringComparison.Ordinal));
    }

    [Fact]
    public void AnExcludedUser_KeepsTheirRoles_ButNothingApplies()
    {
        var user = User(1, "paused", native: Folders(Shows));

        var effective = Resolve(user, Assign(user, [Kid], state: AccessRolesAssignmentState.Excluded), Kid);

        Assert.Equal(AccessRolesUserState.Excluded, effective.State);
        Assert.Empty(effective.Locked);
        Assert.False(effective.Libraries.Managed);
        Assert.Equal(["Shows"], Names(effective.Libraries.Visible));
        Assert.Single(effective.Roles);
    }

    [Fact]
    public void ALocksOnlyRole_ManagesNoLibrary()
    {
        var user = User(1, "u", native: Folders(Movies));

        var effective = Resolve(user, Assign(user, [LocksOnly]), LocksOnly);

        Assert.Equal(AccessRolesUserState.Managed, effective.State);
        Assert.Equal([AccessRolesLock.Profile, AccessRolesLock.UserDirectory], effective.Locked.Order());
        Assert.False(effective.Libraries.Managed);
        Assert.Equal(["Movies"], Names(effective.Libraries.Visible));
    }

    [Fact]
    public void ALegacyBlockedMediaFoldersList_MakesLibrariesUnmanageable_ButTheLocksStillApply()
    {
        var user = User(1, "legacy", native: AllFolders with { BlockedMediaFolders = [Horror.Id] });

        var effective = Resolve(user, Assign(user, [Kid]), Kid);

        Assert.Equal(AccessRolesUserState.CannotManage, effective.State);
        Assert.True(effective.Locks[AccessRolesLock.Profile].Locked);
        Assert.False(effective.Libraries.Managed);
        Assert.Contains(effective.Warnings, w => w.Contains("BlockedMediaFolders", StringComparison.Ordinal));
    }

    [Fact]
    public void AValueANewerBuildWrote_LocksEverySettingsSection_AndBlocksEveryWrite()
    {
        // An unknown library mode makes the user managed with every settings section locked, and nothing is written.
        var user = User(1, "u", native: Folders(Movies));
        var newer = Role(6, "Newer", [AccessRolesLock.UserDirectory]) with { Libraries = new RoleLibraries { Mode = LenientEnum<AccessRolesRoleLibraryMode>.Parse("Hidden") } };

        var effective = Resolve(user, Assign(user, [newer]), newer);

        Assert.Equal(AccessRolesUserState.UnknownValue, effective.State);
        Assert.All(AccessPresets.SettingsSections, s => Assert.Equal(new LockDecision(true, AccessSources.UnknownValue), effective.Locks[s]));
        Assert.Equal("Role:Newer", effective.Locks[AccessRolesLock.UserDirectory].Source);
        Assert.False(effective.Locks[AccessRolesLock.AdminSurfaces].Locked);
        Assert.True(effective.Libraries.Managed);
        Assert.True(effective.Libraries.WriteBlocked);
        Assert.False(effective.Libraries.Writes);
        Assert.Equal([Movies.Id], effective.Libraries.EnabledFolders);
        Assert.Contains("Role Newer: Libraries.Mode=Hidden", effective.UnknownValues);
    }

    [Fact]
    public void AnUnknownLockName_IsKeptAndIgnored()
    {
        var user = User(1, "u");
        var role = Role(7, "Future", [AccessRolesLock.Home]) with { Locks = [AccessRolesLock.Home, LenientEnum<AccessRolesLock>.Parse("Downloads")] };

        var effective = Resolve(user, Assign(user, [role]), role);

        Assert.Equal(AccessRolesUserState.Managed, effective.State);
        Assert.Equal([AccessRolesLock.Home], effective.Locked);
        Assert.Contains("Role Future: Locks=Downloads", effective.UnknownValues);
    }

    [Fact]
    public void ARoleIdThatNoRoleHas_IsIgnoredWithAWarning()
    {
        var user = User(1, "u");
        var ghost = Role(8, "Ghost");

        var effective = Resolve(user, Assign(user, [ghost, LocksOnly]), LocksOnly);

        Assert.Equal([ghost.Id], effective.MissingRoleIds);
        Assert.Contains(effective.Warnings, w => w.Contains("no longer exists and is ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void LiveTvManagement_IsWarnedAbout_ForNonAdmins()
    {
        var user = User(1, "dvr", native: AllFolders with { EnableLiveTvManagement = true });

        var open = Resolve(user, Assign(user, [LocksOnly]), LocksOnly);
        var locked = Resolve(user, Assign(user, [Kid]), Kid);

        Assert.Contains(open.Warnings, w => w.Contains("without the AdminSurfaces lock", StringComparison.Ordinal));
        Assert.Contains("EnableLiveTvManagement is on: this user can schedule and delete recordings.", locked.Warnings);
    }

    [Fact]
    public void AddingARole_NeverUnlocksASection()
    {
        // A property: with the overrides unchanged, adding any role to any set of roles only ever adds locks.
        var random = new Random(9);
        var locks = Enum.GetValues<AccessRolesLock>();
        var roles = Enumerable.Range(1, 8).Select(n => Role(n, "R" + n, locks.Where(_ => random.Next(3) == 0).ToList())).ToList();
        for (var run = 0; run < 300; run++)
        {
            var user = User(run, "u" + run, admin: random.Next(10) == 0);
            var chosen = roles.Where(_ => random.Next(2) == 0).ToList();
            var extra = roles[random.Next(roles.Count)];
            var overrides = locks.Where(_ => random.Next(4) == 0).ToDictionary(l => l, _ => (AccessRolesLockOverride)random.Next(3));

            var before = EffectiveRulesResolver.Resolve(Document(roles, [Assign(user, chosen, overrides: overrides)]), Catalog, user);
            var after = EffectiveRulesResolver.Resolve(Document(roles, [Assign(user, [.. chosen, extra], overrides: overrides)]), Catalog, user);

            Assert.True(before.Locked.IsSubsetOf(after.Locked), $"run {run}: adding {extra.Name} unlocked {string.Join(", ", before.Locked.Except(after.Locked))}");
        }
    }

    [Fact]
    public void AnEmptyResult_IsEitherTheAdminsExplicitChoice_OrFlagged()
    {
        // A second property, as the resolver states it: a managed result with no library is marked NoLibraries, and
        // only Only… with nothing added and ConfirmNoLibraries counts as chosen (ChangeAnalysis refuses the rest).
        var random = new Random(11);
        var libraries = Catalog.Libraries;
        var roles = new[] { Kid, Family, Everything, NoHorror, LocksOnly };
        for (var run = 0; run < 400; run++)
        {
            var user = User(run, "u" + run, native: Folders([.. libraries.Where(_ => random.Next(2) == 0)]));
            var assignment = Assign(
                user,
                roles.Where(_ => random.Next(3) == 0),
                (AccessRolesUserLibraryMode)random.Next(3),
                libraries.Where(_ => random.Next(5) == 0),
                libraries.Where(_ => random.Next(3) == 0),
                confirmNoLibraries: random.Next(2) == 0);
            var ledger = new Dictionary<Guid, LedgerEntry> { [user.Id] = Ledger(random.Next(4) == 0, [.. libraries.Where(_ => random.Next(2) == 0)]) };

            var effective = EffectiveRulesResolver.Resolve(Document(roles, [assignment], ledger), Catalog, user);

            var empty = effective.Libraries.Writes && !effective.Libraries.EnableAllFolders && effective.Libraries.EnabledFolders.Count == 0;
            Assert.Equal(empty, effective.Libraries.NoLibraries);
            if (effective.Libraries.NoLibrariesConfirmed)
            {
                Assert.True(assignment.Libraries.Mode.Is(AccessRolesUserLibraryMode.Only) && assignment.Libraries.Add.Count == 0 && assignment.Libraries.ConfirmNoLibraries);
            }
        }
    }

    private static EffectiveAccess Resolve(ServerUser user, RoleAssignment assignment, params AccessRole[] roles)
        => EffectiveRulesResolver.Resolve(Document(roles, [assignment]), Catalog, user);
}
