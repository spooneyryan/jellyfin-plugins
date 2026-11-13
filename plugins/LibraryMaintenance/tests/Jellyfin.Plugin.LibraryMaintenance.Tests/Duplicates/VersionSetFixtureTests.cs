using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using Xunit;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// The kit's <see cref="VersionSets.Group"/> on Duplicates' own fixtures, one per broken version-set state, each made the
/// way Jellyfin 10.11.5 makes it ("Group versions", "Split versions", a deleted primary, a renamed file, a lookup that
/// failed once), and a library holding all of them at once. What Duplicates relies on: every indexed movie is in exactly
/// one version set (an entry), so a broken merge neither vanishes nor counts twice, and each set says what is
/// wrong with it. <see cref="VersionSets.Collapse"/> is unchanged for its callers (the kit's own tests pin it).
/// </summary>
public sealed class VersionSetFixtureTests
{
    [Fact]
    public void AMergedSet_IsOneEntry_AndAnUnmergedCopyIsAnother()
    {
        var library = new DuplicatesLibrary();
        var first = library.Add("Blade Runner", 1982, file: "Blade Runner (1982) - 1080p.mkv");
        var second = library.Add("Blade Runner", 1982, file: "Blade Runner (1982) - 2160p.mkv");
        var other = library.Add("Blade Runner", 1982, DuplicatesLibrary.MoreMovies);
        var primary = DuplicatesLibrary.MergeVersions(first, second);

        var sets = Grouped(library);

        Assert.Equal(2, sets.Count);
        var merged = sets[0];
        Assert.Equal(primary.Id, merged.RootId);
        Assert.Equal(new[] { first.Id, second.Id }, merged.MemberIds);
        Assert.Equal(VersionSetState.None, merged.State);
        Assert.True(merged.IsMerged);
        Assert.Equal(new[] { other.Id }, sets[1].MemberIds);
        Assert.Equal(VersionSetState.None, sets[1].State);
    }

    [Fact]
    public void DeletingThePrimary_LeavesOrphans_EachItsOwnEntry()
    {
        // "Orphaned secondary": nothing clears PrimaryVersionId when a primary is deleted on 10.11.5.
        var library = new DuplicatesLibrary();
        var a = library.Add("Alien", 1979, file: "Alien (1979) - Theatrical.mkv");
        var b = library.Add("Alien", 1979, file: "Alien (1979) - Director's Cut.mkv");
        var c = library.Add("Alien", 1979, file: "Alien (1979) - 4K.mkv");
        var primary = DuplicatesLibrary.MergeVersions(a, b, c);
        library.Delete(primary);

        var sets = Grouped(library);

        Assert.Equal(new[] { b.Id, c.Id }, sets.Select(s => s.RootId));
        Assert.All(sets, set =>
        {
            Assert.True(set.IsOrphaned);
            Assert.Equal(VersionSetState.Orphaned, set.State);
            Assert.Equal(primary.Id.ToString("N", CultureInfo.InvariantCulture), set.MissingPrimaryVersionId);
            Assert.False(set.IsMerged);
        });
    }

    [Fact]
    public void ARenamedOrDeletedVersion_IsAStaleLink_AndTheRenamedFileStandsAlone()
    {
        // "Stale link": the primary lists an id that doesn't resolve and a path FindByPath doesn't find.
        var library = new DuplicatesLibrary();
        var a = library.Add("Heat", 1995);
        var b = library.Add("Heat", 1995, file: "Heat (1995) - Remux.mkv");
        var c = library.Add("Heat", 1995, file: "Heat (1995) - Extended.mkv");
        var primary = DuplicatesLibrary.MergeVersions(a, b, c);
        var renamed = library.Rename(b, "Heat (1995) - Remux 2.mkv");
        library.Delete(c);

        var sets = Grouped(library);

        Assert.Equal(2, sets.Count);
        var set = sets.Single(s => s.RootId == primary.Id);
        Assert.Equal(new[] { primary.Id }, set.MemberIds);
        Assert.Equal(VersionSetState.StaleLinks, set.State);
        Assert.Equal(new[] { b.Id, c.Id }, set.StaleLinkIds);
        // Jellyfin 12's links carry the id alone.
        Assert.Equal(JellyfinLine.Is12 ? new string?[] { null, null } : new[] { b.Path, c.Path }, set.StaleLinks.Select(l => l.Path));

        // The renamed file is a movie of its own, which Duplicates then pairs with the set by title.
        var alone = sets.Single(s => s.RootId == renamed.Id);
        Assert.Equal(VersionSetState.None, alone.State);
    }

    [Fact]
    public void ALinkWhoseLookupFailedOnce_StaysStale_AndItsVersionIsUnlisted()
    {
        // The drive was offline during a refresh: Jellyfin cached the empty GUID and never looks again, so after the drive
        // is back the version points at the primary while the primary's link is dead.
        var library = new DuplicatesLibrary();
        var a = library.Add("Ran", 1985);
        var b = library.Add("Ran", 1985, file: "Ran (1985) - Criterion.mkv");
        var primary = DuplicatesLibrary.MergeVersions(a, b);
        DuplicatesLibrary.LookupFailedOnce(primary, b);

        var set = Assert.Single(Grouped(library));

        Assert.Equal(new[] { a.Id, b.Id }, set.MemberIds);
        Assert.Equal(VersionSetState.StaleLinks | VersionSetState.Unlisted, set.State);
        Assert.Equal(new[] { b.Id }, set.UnlistedIds);
        Assert.Empty(set.StaleLinkIds);
        Assert.Equal(JellyfinLine.Is12 ? new string?[] { null } : new[] { b.Path }, set.StaleLinks.Select(l => l.Path));
    }

    [Fact]
    public void GroupVersionsOfTwoSets_IsAChain()
    {
        // "Chained": native "Group versions" folds a secondary's list into the new primary but leaves the folded
        // items pointing at their old primary (VideosController.cs:220-247).
        var library = new DuplicatesLibrary();
        var a = library.Add("The Thing", 1982);
        var b = library.Add("The Thing", 1982, file: "The Thing (1982) - Remastered.mkv");
        var c = library.Add("The Thing", 1982, DuplicatesLibrary.MoreMovies);
        var d = library.Add("The Thing", 1982, DuplicatesLibrary.MoreMovies, "The Thing (1982) - 4K.mkv");
        DuplicatesLibrary.MergeVersions(a, b);
        DuplicatesLibrary.MergeVersions(c, d);
        var root = DuplicatesLibrary.MergeVersions(a, c);

        var set = Assert.Single(Grouped(library));

        Assert.Equal(a.Id, root.Id);
        Assert.Equal(new[] { a.Id, b.Id, c.Id, d.Id }, set.MemberIds);
        Assert.Equal(new[] { d.Id }, set.ChainedIds);
        Assert.Equal(VersionSetState.Chained, set.State);
    }

    [Fact]
    public void APrimaryThatLostItsList_LeavesItsVersionsUnlisted()
    {
        // "Unlisted secondary": the versions point at the primary, which no longer lists them (its list was
        // rewritten, for example by an NFO saved without them).
        var library = new DuplicatesLibrary();
        var a = library.Add("Brazil", 1985);
        var b = library.Add("Brazil", 1985, file: "Brazil (1985) - Final Cut.mkv");
        var c = library.Add("Brazil", 1985, file: "Brazil (1985) - Love Conquers All.mkv");
        var primary = DuplicatesLibrary.MergeVersions(a, b, c);
        primary.LinkedAlternateVersions = [];

        var set = Assert.Single(Grouped(library));

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, set.MemberIds);
        Assert.Equal(new[] { b.Id, c.Id }, set.UnlistedIds);
        Assert.Equal(VersionSetState.Unlisted, set.State);
    }

    [Fact]
    public void AnInterruptedSplit_LeavesAListedVersionThatDoesntPoint_InTheSet()
    {
        // "Listed, not pointing": "Split versions" saved the first version and stopped; the primary still lists
        // both. The split version is standalone by its pointer, so it stays a member of the set that lists it.
        var library = new DuplicatesLibrary();
        var a = library.Add("Dune", 1984);
        var b = library.Add("Dune", 1984, file: "Dune (1984) - Extended.mkv");
        var c = library.Add("Dune", 1984, file: "Dune (1984) - TV Cut.mkv");
        var primary = DuplicatesLibrary.MergeVersions(a, b, c);
        library.SplitVersions(primary, savedVersions: 1);

        var set = Assert.Single(Grouped(library));

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, set.MemberIds);
        Assert.Equal(new[] { b.Id }, set.ListedNotPointingIds);
        Assert.Equal(VersionSetState.ListedNotPointing, set.State);
    }

    [Fact]
    public void AVersionClaimedByTwoSets_IsCountedOnce_AndBothSetsSayIt()
    {
        // The version was split off one set and grouped into another while the first primary kept its link.
        var library = new DuplicatesLibrary();
        var a = library.Add("Solaris", 1972);
        var b = library.Add("Solaris", 1972, file: "Solaris (1972) - Criterion.mkv");
        var c = library.Add("Solaris", 1972, DuplicatesLibrary.MoreMovies);
        var first = DuplicatesLibrary.MergeVersions(a, b);
        b.SetPrimaryVersion(null);
        var second = DuplicatesLibrary.MergeVersions(b, c);

        var sets = Grouped(library);

        Assert.Equal(2, sets.Count);
        var firstSet = sets.Single(s => s.RootId == first.Id);
        var secondSet = sets.Single(s => s.RootId == second.Id);
        Assert.Equal(new[] { a.Id }, firstSet.MemberIds);
        Assert.Equal(new[] { b.Id }, firstSet.ListedNotPointingIds);
        Assert.Equal(new[] { b.Id, c.Id }, secondSet.MemberIds);
        Assert.Equal(VersionSetState.ListedNotPointing, firstSet.State);
        Assert.Equal(VersionSetState.None, secondSet.State);
    }

    [Fact]
    public void ACycle_IsOneEntry_WithTheSmallestIdAsItsRoot_WhicheverWayItIsRead()
    {
        // "Cycle": pointers that go round (edited by hand, or two merges that raced).
        var library = new DuplicatesLibrary();
        var a = library.Add("Stalker", 1979);
        var b = library.Add("Stalker", 1979, file: "Stalker (1979) - Restored.mkv");
        var c = library.Add("Stalker", 1979, file: "Stalker (1979) - 4K.mkv");
        a.SetPrimaryVersion(c.Id);
        b.SetPrimaryVersion(a.Id);
        c.SetPrimaryVersion(b.Id);

        var set = Assert.Single(Grouped(library));

        Assert.Equal(a.Id, set.RootId);
        Assert.True(set.HasCycle);
        Assert.False(set.IsOrphaned);
        Assert.True(set.State.HasFlag(VersionSetState.Cycle));
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, set.MemberIds);

        // Read from another library order, the root is the same.
        var reversed = VersionSets.Group(library.Indexed().Reverse().Select(VersionMember.From), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(a.Id, Assert.Single(reversed).RootId);
    }

    [Fact]
    public void APrimaryInALibraryTheIndexDoesntRead_IsFoundByTheLookup_AndNamedExternal()
    {
        // The version is in Movies, its primary in Kids, which the settings leave out (the index's GetItemById
        // fallback): the set is still one entry, and it isn't taken for an orphan.
        var library = new DuplicatesLibrary();
        var kids = library.Add("Paddington", 2014, DuplicatesLibrary.Kids);
        var movies = library.Add("Paddington", 2014);
        var primary = DuplicatesLibrary.MergeVersions(kids, movies);

        var set = Assert.Single(Grouped(library));

        Assert.Equal(kids.Id, primary.Id);
        Assert.Equal(kids.Id, set.RootId);
        Assert.Equal(new[] { kids.Id, movies.Id }, set.MemberIds);
        Assert.Equal(new[] { kids.Id }, set.ExternalIds);
        Assert.False(set.IsOrphaned);
        Assert.Equal(VersionSetState.None, set.State);
    }

    [Fact]
    public void TheMixedLibrary_PutsEveryIndexedMovieInExactlyOneEntry_AndCountsEachState()
    {
        var library = Mixed(out var expected);

        var sets = Grouped(library);

        // GET /Index's States: the number of entries in each state.
        var counts = Enum.GetValues<VersionSetState>()
            .Where(s => s != VersionSetState.None)
            .ToDictionary(s => s, s => sets.Count(set => set.State.HasFlag(s)));
        Assert.Equal(expected, counts);
        Assert.Equal(8, sets.Count(s => s.State == VersionSetState.None));
    }

    [Fact]
    public void Collapse_OverTheMixedLibrary_GivesOneRepresentativePerEntry()
    {
        // Collapse is Group plus a representative per set (Smart Search's and auto-collections' caller);
        // over Duplicates' fixtures it agrees with Group set by set.
        var library = Mixed(out _);
        var index = library.Indexed().Select(VersionMember.From).ToList();
        var byId = index.ToDictionary(m => m.Id);
        var lookup = VersionSets.LibraryLookup(library.Fake.Object);
        VersionMember? Find(Guid id) => byId.TryGetValue(id, out var member) ? member : lookup(id);

        var sets = VersionSets.Group(index, Find, VersionSets.LibraryPaths(library.Fake.Object), TestContext.Current.CancellationToken);
        var asked = new List<IReadOnlyCollection<Guid>>();
        var representatives = VersionSets.Collapse(index, ids => { asked.Add(ids); return new HashSet<Guid>(); }, Find, VersionSets.LibraryPaths(library.Fake.Object));

        Assert.Equal(sets.Select(s => s.RootId), representatives.Select(r => r.Set.RootId));
        Assert.Equal(sets.Select(s => s.MemberIds.Where(byId.ContainsKey)), representatives.Select(r => r.MatchedIds));

        // Only the external root (Kids) wasn't a match; ineligible, its set is represented by the indexed member.
        var external = Assert.Single(asked.SelectMany(a => a));
        var outside = representatives.Single(r => r.Set.RootId == external);
        Assert.False(outside.IsRoot);
        Assert.All(representatives.Where(r => r.Set.RootId != external), r => Assert.True(r.IsRoot));
    }

    [Fact]
    public void TheStates_HaveTheirDocumentedValues()
    {
        // Duplicates' VersionState is the kit's VersionSetState; GET /Index reports it by name, with the documented values.
        Assert.Equal(
            new[] { ("None", 0), ("Orphaned", 1), ("StaleLinks", 2), ("Chained", 4), ("Unlisted", 8), ("ListedNotPointing", 16), ("Cycle", 32) },
            Enum.GetValues<VersionSetState>().Select(s => (s.ToString(), (int)s)));
    }

    // Groups the library's indexed movies, and checks the index's rule: every indexed movie in exactly one set.
    private static IReadOnlyList<VersionSet> Grouped(DuplicatesLibrary library)
    {
        var sets = library.Group();
        var indexed = library.Indexed().Select(v => v.Id).Order().ToList();
        var inSets = sets.SelectMany(s => s.MemberIds).Where(indexed.Contains).Order().ToList();
        Assert.Equal(indexed, inSets);
        Assert.All(sets, s => Assert.Equal(s.RootId, s.MemberIds[0]));
        return sets;
    }

    // Every fixture above in one library, with plain movies between them.
    private static DuplicatesLibrary Mixed(out Dictionary<VersionSetState, int> expected)
    {
        var library = new DuplicatesLibrary();
        foreach (var (name, year) in new[] { ("Amélie", 2001), ("Se7en", 1995), ("1917", 2019), ("Blade Runner 2049", 2017), ("Heat", 1995) })
        {
            library.Add(name, year, DuplicatesLibrary.MoreMovies);
        }

        // Healthy (1 entry), orphans (2), stale links (1, plus the renamed file's own entry).
        DuplicatesLibrary.MergeVersions(library.Add("Blade Runner", 1982), library.Add("Blade Runner", 1982, file: "b.mkv"));
        var alien = DuplicatesLibrary.MergeVersions(library.Add("Alien", 1979), library.Add("Alien", 1979, file: "b.mkv"), library.Add("Alien", 1979, file: "c.mkv"));
        library.Delete(alien);
        var heatA = library.Add("Heat", 1995, file: "a.mkv");
        var heatB = library.Add("Heat", 1995, file: "b.mkv");
        DuplicatesLibrary.MergeVersions(heatA, heatB);
        library.Rename(heatB, "b2.mkv");

        // A chain (1), unlisted (1), listed-not-pointing (1), a cycle (1, its member unlisted too: the root lists nothing),
        // an external primary (1).
        var thing = new[] { library.Add("The Thing", 1982), library.Add("The Thing", 1982, file: "b.mkv"), library.Add("The Thing", 1982, file: "c.mkv"), library.Add("The Thing", 1982, file: "d.mkv") };
        DuplicatesLibrary.MergeVersions(thing[0], thing[1]);
        DuplicatesLibrary.MergeVersions(thing[2], thing[3]);
        DuplicatesLibrary.MergeVersions(thing[0], thing[2]);
        var brazil = DuplicatesLibrary.MergeVersions(library.Add("Brazil", 1985), library.Add("Brazil", 1985, file: "b.mkv"));
        brazil.LinkedAlternateVersions = [];
        var dune = DuplicatesLibrary.MergeVersions(library.Add("Dune", 1984), library.Add("Dune", 1984, file: "b.mkv"), library.Add("Dune", 1984, file: "c.mkv"));
        library.SplitVersions(dune, savedVersions: 1);
        var stalker = new[] { library.Add("Stalker", 1979), library.Add("Stalker", 1979, file: "b.mkv") };
        stalker[0].SetPrimaryVersion(stalker[1].Id);
        stalker[1].SetPrimaryVersion(stalker[0].Id);
        DuplicatesLibrary.MergeVersions(library.Add("Paddington", 2014, DuplicatesLibrary.Kids), library.Add("Paddington", 2014));

        expected = new()
        {
            [VersionSetState.Orphaned] = 2,
            [VersionSetState.StaleLinks] = 1,
            [VersionSetState.Chained] = 1,
            [VersionSetState.Unlisted] = 2,
            [VersionSetState.ListedNotPointing] = 1,
            [VersionSetState.Cycle] = 1,
        };
        return library;
    }
}
