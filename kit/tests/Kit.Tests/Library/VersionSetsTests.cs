using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="VersionSets.Group"/> and <see cref="VersionSets.Collapse"/>: healthy sets, orphans, stale links, a chain
/// built exactly as native "Group versions" builds it, unlisted and listed-not-pointing members, a cycle, and the
/// representative's eligibility.
/// </summary>
public sealed class VersionSetsTests
{
    private static readonly Guid P = Id(1);
    private static readonly Guid S1 = Id(2);
    private static readonly Guid S2 = Id(3);
    private static readonly Guid X = Id(4);
    private static readonly Guid Y = Id(5);
    private static readonly Guid Gone = Id(99);

    // Group with the test's cancellation token.
    private static IReadOnlyList<VersionSet> Group(IEnumerable<VersionMember> members, Func<Guid, VersionMember?>? lookup = null, Func<VersionMember, string, Guid?>? findByPath = null)
        => VersionSets.Group(members, lookup, findByPath, TestContext.Current.CancellationToken);

    [Fact]
    public void AHealthySet_IsOneSet_WithTheRootFirst_AndNoState()
    {
        var sets = Group([VersionMember.Of(S1, P), VersionMember.Of(P, null, S1, S2), VersionMember.Of(S2, P), VersionMember.Of(X)]);

        Assert.Equal(2, sets.Count);
        var set = sets[0];
        Assert.Equal(P, set.RootId);
        Assert.Equal(new[] { P, S1, S2 }, set.MemberIds);
        Assert.Equal(VersionSetState.None, set.State);
        Assert.True(set.IsMerged);
        Assert.Equal(new[] { X }, sets[1].MemberIds);
        Assert.False(sets[1].IsMerged);
    }

    [Fact]
    public void TheSetsCome_InTheOrderOfTheirFirstMember_AndEveryVideoIsInExactlyOne()
    {
        var input = new[] { VersionMember.Of(X), VersionMember.Of(S1, P), VersionMember.Of(Y), VersionMember.Of(P, null, S1) };

        var sets = Group(input);

        Assert.Equal(new[] { X, P, Y }, sets.Select(s => s.RootId));
        Assert.Equal(input.Select(m => m.Id).Order(), sets.SelectMany(s => s.MemberIds).Order());
    }

    [Fact]
    public void AnOrphanedSecondary_IsItsOwnSet_AndSaysWhatItPointsAt()
    {
        // The primary was deleted; nothing clears the pointer on 10.11.5.
        var orphan = VersionMember.Of(S1, Gone);
        var alsoOrphan = VersionMember.Of(S2, Gone);

        var sets = Group([orphan, alsoOrphan]);

        Assert.Equal(2, sets.Count);
        Assert.All(sets, s => Assert.True(s.IsOrphaned));
        Assert.Equal(new[] { S1 }, sets[0].MemberIds);
        Assert.Equal(Gone.ToString("N", CultureInfo.InvariantCulture), sets[0].MissingPrimaryVersionId);
        Assert.Equal(VersionSetState.Orphaned, sets[0].State);
    }

    [Fact]
    public void APointerThatIsNoId_NamesNothing_SoTheItemIsAnOrphan()
    {
        var sets = Group([new VersionMember { Id = S1, PrimaryVersionId = "not a guid" }, new VersionMember { Id = S2, PrimaryVersionId = "00000000000000000000000000000000" }]);

        Assert.All(sets, s => Assert.True(s.IsOrphaned));
    }

    [Fact]
    public void AnItemChainedToAnOrphan_JoinsIt_AndIsNotChained()
    {
        // S2 -> S1 -> (gone): S1 is the root of an orphaned set that S2 points at directly.
        var sets = Group([VersionMember.Of(S1, Gone), VersionMember.Of(S2, S1)]);

        var set = Assert.Single(sets);
        Assert.Equal(S1, set.RootId);
        Assert.Equal(new[] { S1, S2 }, set.MemberIds);
        Assert.True(set.IsOrphaned);
        Assert.Empty(set.ChainedIds);
        Assert.Equal(new[] { S2 }, set.UnlistedIds);
    }

    [Fact]
    public void StaleLinks_ResolveNeitherByIdNorByPath()
    {
        var primary = new VersionMember
        {
            Id = P,
            Path = @"C:\media\Movies\Heat (1995)\Heat (1995).mkv",
            LinkedAlternateVersions =
            [
                new VersionLink(S1, @"C:\media\Movies\Heat (1995)\Heat 4K.mkv"),
                new VersionLink(Gone, @"C:\media\Movies\Heat (1995)\Gone.mkv"),
                new VersionLink(null, @"C:\media\Movies\Heat (1995)\Moved.mkv"),
                new VersionLink(null, "Heat 720p.mkv"),
            ]
        };
        var paths = new Dictionary<string, Guid>(StringComparer.Ordinal) { [@"C:\media\Movies\Heat (1995)\Heat 720p.mkv"] = S2 };
        var input = new[] { primary, VersionMember.Of(S1, P), VersionMember.Of(S2, P) };

        var set = Assert.Single(Group(input, findByPath: (root, path) => ResolvePath(root, path, paths)));

        // The relative path is found through the root's folder; the other two resolve to nothing.
        Assert.Equal(new[] { P, S1, S2 }, set.MemberIds);
        Assert.Equal(2, set.StaleLinks.Count);
        Assert.Equal(new[] { Gone }, set.StaleLinkIds);
        Assert.Empty(set.UnlistedIds);
        Assert.Equal(VersionSetState.StaleLinks, set.State);
    }

    [Fact]
    public void ALinkWhoseCachedIdIsEmpty_IsStale_EvenIfItsPathResolves_AsInJellyfin()
    {
        // BaseItem.GetLinkedChild returns null for an empty cached id and never looks at the path again.
        var primary = new VersionMember { Id = P, LinkedAlternateVersions = [new VersionLink(Guid.Empty, @"C:\x.mkv")] };

        var set = Group([primary, VersionMember.Of(S1, P)], findByPath: (_, _) => S1).First(s => s.RootId == P);

        Assert.Single(set.StaleLinks);
        Assert.Empty(set.StaleLinkIds);
        Assert.Equal(new[] { S1 }, set.UnlistedIds);
    }

    [Fact]
    public void ALinkWithoutAPath_ResolvesByItsLibraryItemId()
    {
        var primary = new VersionMember { Id = P, LinkedAlternateVersions = [new VersionLink(null, null, S1.ToString("N"))] };

        var set = Group([primary, VersionMember.Of(S1, P)]).Single();

        Assert.Empty(set.StaleLinks);
        Assert.Empty(set.UnlistedIds);
    }

    [Fact]
    public void AChainBuiltByNativeGroupVersions_IsOneSet_WithTheFoldedMemberChained()
    {
        // Two sets, {A: A, B} and {C: C, D}; then "Group versions" on A and C. The ids make C the first set by Id, which
        // native MergeVersions picks as the primary (the first item with more than one source and no pointer).
        var a = Movie(Id(20));
        var b = Movie(Id(21));
        var c = Movie(Id(10));
        var d = Movie(Id(11));
        NativeMergeVersions(a, b);
        NativeMergeVersions(c, d);
        NativeMergeVersions(a, c);

        // What native does: A points at C and C lists A and A's list (B); A's list is cleared; B still points at A.
        Assert.Equal(c.Id.ToString("N", CultureInfo.InvariantCulture), a.PrimaryVersionText());
        Assert.Equal(a.Id.ToString("N", CultureInfo.InvariantCulture), b.PrimaryVersionText());
        Assert.Empty(a.LinkedAlternateVersions);

        var set = Assert.Single(Group(new[] { a, b, c, d }.Select(VersionMember.From)));

        Assert.Equal(c.Id, set.RootId);
        Assert.Equal(new[] { c.Id, a.Id, b.Id, d.Id }, set.MemberIds);
        Assert.Equal(new[] { b.Id }, set.ChainedIds);
        Assert.Empty(set.UnlistedIds);
        Assert.Empty(set.ListedNotPointingIds);
        Assert.Equal(VersionSetState.Chained, set.State);
    }

    [Fact]
    public void AnUnlistedSecondary_PointsAtTheRoot_ButTheRootDoesntListIt()
    {
        var set = Group([VersionMember.Of(P, null, S1), VersionMember.Of(S1, P), VersionMember.Of(S2, P)]).Single();

        Assert.Equal(new[] { S2 }, set.UnlistedIds);
        Assert.Equal(VersionSetState.Unlisted, set.State);
    }

    [Fact]
    public void AListedStandaloneItem_IsListedNotPointing_AndAMemberOfTheSet()
    {
        // The intermediate state of the Duplicates writer: the primary lists S2 before S2 points at it.
        var sets = Group([VersionMember.Of(P, null, S1, S2), VersionMember.Of(S1, P), VersionMember.Of(S2)]);

        var set = Assert.Single(sets);
        Assert.Equal(new[] { P, S1, S2 }, set.MemberIds);
        Assert.Equal(new[] { S2 }, set.ListedNotPointingIds);
        Assert.Equal(VersionSetState.ListedNotPointing, set.State);
    }

    [Fact]
    public void AnItemClaimedByTwoSets_StaysWithTheSetItPointsAt_AndBothSayIt()
    {
        // P lists S1, but S1 points at X, whose set it belongs to; nothing counts twice.
        var sets = Group([VersionMember.Of(P, null, S1), VersionMember.Of(X, null, S1), VersionMember.Of(S1, X)]);

        Assert.Equal(2, sets.Count);
        var p = sets.Single(s => s.RootId == P);
        var x = sets.Single(s => s.RootId == X);
        Assert.Equal(new[] { P }, p.MemberIds);
        Assert.Equal(new[] { S1 }, p.ListedNotPointingIds);
        Assert.Equal(new[] { X, S1 }, x.MemberIds);
        Assert.Empty(x.ListedNotPointingIds);
    }

    [Fact]
    public void AStandaloneItemTwoRootsList_GoesToTheFirstSet_AndBothSayIt()
    {
        var sets = Group([VersionMember.Of(P, null, S1), VersionMember.Of(X, null, S1), VersionMember.Of(S1)]);

        Assert.Equal(2, sets.Count);
        Assert.Equal(new[] { P, S1 }, sets[0].MemberIds);
        Assert.Equal(new[] { X }, sets[1].MemberIds);
        Assert.All(sets, s => Assert.Equal(new[] { S1 }, s.ListedNotPointingIds));
    }

    [Fact]
    public void ACycle_StopsTheWalk_AndTheSmallestIdIsTheRoot_WhereverTheWalkStarted()
    {
        // S1 -> S2 -> X -> S1: GetMediaSourceCount's call-stack guard is all Jellyfin has (Video.cs:253-272).
        var members = new[] { VersionMember.Of(X, S1), VersionMember.Of(S1, S2), VersionMember.Of(S2, X), VersionMember.Of(Y, S2) };

        foreach (var order in new[] { members, members.Reverse().ToArray() })
        {
            var set = Assert.Single(Group(order));
            Assert.Equal(S1, set.RootId);
            Assert.True(set.HasCycle);
            Assert.False(set.IsOrphaned);
            Assert.Equal(new[] { S1, S2, X, Y }.Order(), set.MemberIds.Order());
            Assert.True(set.State.HasFlag(VersionSetState.Cycle));

            // X points at the root; S2 and Y point at other members.
            Assert.Equal(new[] { S2, Y }.Order(), set.ChainedIds.Order());
        }
    }

    [Fact]
    public void AnItemThatPointsAtItself_IsACycleOfOne()
    {
        var set = Assert.Single(Group([VersionMember.Of(S1, S1)]));

        Assert.True(set.HasCycle);
        Assert.Equal(new[] { S1 }, set.MemberIds);
    }

    [Fact]
    public void ARootOutsideTheInput_IsFoundThroughTheLookup_AndNamedExternal()
    {
        // Duplicates' index holds the Movies library; the primary lives in Movies 2.
        var outside = new Dictionary<Guid, VersionMember> { [P] = VersionMember.Of(P, null, S1, S2) };

        var set = Assert.Single(Group([VersionMember.Of(S1, P), VersionMember.Of(S2, P)], id => outside.GetValueOrDefault(id)));

        Assert.Equal(P, set.RootId);
        Assert.Equal(new[] { P, S1, S2 }, set.MemberIds);
        Assert.Equal(new[] { P }, set.ExternalIds);
        Assert.Equal(VersionSetState.None, set.State);
    }

    [Fact]
    public void AListedItemOutsideTheInput_ThatPointsAtTheRoot_IsAMember()
    {
        var outside = new Dictionary<Guid, VersionMember> { [S2] = VersionMember.Of(S2, P) };

        var set = Assert.Single(Group([VersionMember.Of(P, null, S1, S2), VersionMember.Of(S1, P)], id => outside.GetValueOrDefault(id)));

        Assert.Equal(new[] { P, S1, S2 }, set.MemberIds);
        Assert.Equal(new[] { S2 }, set.ExternalIds);
        Assert.Empty(set.ListedNotPointingIds);
    }

    [Fact]
    public void TheLookup_IsNeverAskedForTheEmptyGuid_OrTwiceForAnId()
    {
        var asked = new List<Guid>();
        VersionMember? Lookup(Guid id)
        {
            asked.Add(id);
            return null;
        }

        Group([VersionMember.Of(S1, Gone), VersionMember.Of(S2, Gone), new VersionMember { Id = X, PrimaryVersionId = Guid.Empty.ToString("N") }, VersionMember.Of(Guid.Empty)], Lookup);

        Assert.Equal(new[] { Gone }, asked);
    }

    [Fact]
    public void Group_StopsWhenCancelled_BeforeTheNextLookup()
    {
        using var cancel = new System.Threading.CancellationTokenSource();
        var asked = 0;
        VersionMember? Lookup(Guid id)
        {
            asked++;
            cancel.Cancel();
            return null;
        }

        Assert.Throws<OperationCanceledException>(() => VersionSets.Group([VersionMember.Of(S1, Gone), VersionMember.Of(S2, X)], Lookup, cancellationToken: cancel.Token));
        Assert.Equal(1, asked);
        Assert.Throws<OperationCanceledException>(() => VersionSets.Group([VersionMember.Of(S1)], cancellationToken: cancel.Token));
    }

    [Fact]
    public void Collapse_ThePrimaryRepresents_WhenItMatched()
    {
        var eligibleAsked = new List<IReadOnlyCollection<Guid>>();
        var rows = VersionSets.Collapse([VersionMember.Of(S1, P), VersionMember.Of(P, null, S1)], ids => Remember(eligibleAsked, ids, []));

        var row = Assert.Single(rows);
        Assert.Equal(P, row.Id);
        Assert.True(row.IsRoot);
        Assert.Equal(new[] { S1, P }, row.MatchedIds);
        Assert.Empty(eligibleAsked);
    }

    [Fact]
    public void Collapse_AnEligiblePrimaryThatDidntMatch_Represents_ItsMatchingSecondary()
    {
        // A 4K secondary matches Resolution = 2160p; its 1080p primary didn't, but it is in the caller's libraries.
        var outside = new Dictionary<Guid, VersionMember> { [P] = VersionMember.Of(P, null, S1) };
        var eligibleAsked = new List<IReadOnlyCollection<Guid>>();

        var rows = VersionSets.Collapse([VersionMember.Of(S1, P), VersionMember.Of(X)], ids => Remember(eligibleAsked, ids, [P]), id => outside.GetValueOrDefault(id));

        Assert.Equal(new[] { P, X }, rows.Select(r => r.Id));
        Assert.Equal(new[] { S1 }, rows[0].MatchedIds);
        var asked = Assert.Single(eligibleAsked);
        Assert.Equal(new[] { P }, asked);
    }

    [Fact]
    public void Collapse_AnIneligiblePrimary_LeavesTheFirstMatchingMemberBySortName()
    {
        // A pair split between Movies and a library the viewer can't open: the Movies member shows.
        var outside = new Dictionary<Guid, VersionMember> { [P] = VersionMember.Of(P, null, S1, S2) };
        var matches = new[]
        {
            new VersionMember { Id = S2, PrimaryVersionId = P.ToString("N", CultureInfo.InvariantCulture), SortName = "heat 4k" },
            new VersionMember { Id = S1, PrimaryVersionId = P.ToString("N", CultureInfo.InvariantCulture), SortName = "Heat 1080p" },
        };

        var row = Assert.Single(VersionSets.Collapse(matches, _ => new HashSet<Guid>(), id => outside.GetValueOrDefault(id)));

        Assert.Equal(S1, row.Id);
        Assert.False(row.IsRoot);
        Assert.Equal(new[] { S2, S1 }, row.MatchedIds);
    }

    [Fact]
    public void Collapse_AnOrphan_RepresentsItself_AndEligibilityIsAskedOnce_ForEveryUnmatchedRoot()
    {
        var outside = new Dictionary<Guid, VersionMember> { [P] = VersionMember.Of(P, null, S1), [X] = VersionMember.Of(X, null, S2) };
        var eligibleAsked = new List<IReadOnlyCollection<Guid>>();

        var rows = VersionSets.Collapse(
            [VersionMember.Of(S1, P), VersionMember.Of(S2, X), VersionMember.Of(Y, Gone)],
            ids => Remember(eligibleAsked, ids, [X]),
            id => outside.GetValueOrDefault(id));

        Assert.Equal(new[] { S1, X, Y }, rows.Select(r => r.Id));
        Assert.True(rows[2].Set.IsOrphaned);
        var asked = Assert.Single(eligibleAsked);
        Assert.Equal(new[] { P, X }.Order(), asked.Order());
    }

    [Fact]
    public void LibraryLookup_UsesGetItemById_AndNeverTheEmptyGuid()
    {
        var library = new FakeLibrary();
        var primary = library.Add(new Movie { Name = "Heat", SortName = "Heat" });
        var secondary = library.Add(new Movie { Name = "Heat 4K", SortName = "Heat 4K" });
        secondary.SetPrimaryVersion(primary.Id);
        primary.LinkedAlternateVersions = [JellyfinLine.Link(secondary.Id, @"C:\media\Heat 4K.mkv")];

        var lookup = VersionSets.LibraryLookup(library.Object);

        Assert.Null(lookup(Guid.Empty));
        Assert.Null(lookup(Gone));
        var member = lookup(primary.Id)!;
        Assert.Equal(secondary.Id, member.LinkedAlternateVersions.Single().ItemId);
        Assert.Equal(primary.Id, lookup(secondary.Id)!.PointsAt);
        Assert.DoesNotContain(Guid.Empty, library.IdLookups);
    }

    [Fact]
    public void LibraryPaths_MakesARelativePathAbsolute_AndNeverAsksForABlankOne()
    {
        var library = new FakeLibrary();
        var movie = library.Add(new Movie { Name = "Heat 720p", Path = @"C:\media\Movies\Heat (1995)\Heat 720p.mkv" });
        var find = VersionSets.LibraryPaths(library.Object);
        var root = new VersionMember { Id = P, Path = @"C:\media\Movies\Heat (1995)\Heat (1995).mkv" };

        Assert.Equal(movie.Id, find(root, "Heat 720p.mkv"));
        Assert.Equal(movie.Id, find(root, @"C:\media\Movies\Heat (1995)\Heat 720p.mkv"));
        Assert.Null(find(root, " "));
        Assert.DoesNotContain(library.PathLookups, string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void From_ReadsAVideosVersionFields_AndAnyOtherItemIsStandalone()
    {
        var video = new Movie { Id = S1, Path = @"C:\a.mkv", SortName = "a" };
        video.SetPrimaryVersion(P);
        var folder = new Folder { Id = X, Name = "Folder", SortName = "folder" };

        var member = VersionMember.From(video);
        Assert.Equal(P, member.PointsAt);
        Assert.Equal("a", member.SortName);
        Assert.Null(VersionMember.From(folder).PointsAt);
        Assert.Empty(VersionMember.From(folder).LinkedAlternateVersions);
    }

    private static Guid Id(int n) => new(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);

    private static Movie Movie(Guid id) => new() { Id = id, Name = "M" + id.ToString("N", CultureInfo.InvariantCulture)[..4], SortName = id.ToString("N", CultureInfo.InvariantCulture), Path = @"C:\media\" + id.ToString("N", CultureInfo.InvariantCulture) + ".mkv" };

    private static Guid? ResolvePath(VersionMember root, string path, IReadOnlyDictionary<string, Guid> paths)
    {
        var full = System.IO.Path.IsPathFullyQualified(path) ? path : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root.Path)!, path);
        return paths.TryGetValue(full, out var id) ? id : null;
    }

    private static IReadOnlySet<Guid> Remember(List<IReadOnlyCollection<Guid>> asked, IReadOnlyCollection<Guid> ids, Guid[] eligible)
    {
        asked.Add(ids.ToList());
        return eligible.ToHashSet();
    }

    // Jellyfin 10.11.5's POST /Videos/MergeVersions (VideosController.cs:185-253), on in-memory videos: the items ordered by
    // Id; the primary is the first with more than one source and no pointer (MediaSourceCount, which for such an item is
    // its list plus one, Video.cs:253-272), else the first by the width rule (all equal here); every other item points at
    // it, is added to its list unless a listed path is the same (ignoring case), and brings its own list along, which is
    // then cleared; the primary's list is saved last.
    private static void NativeMergeVersions(params Video[] videos)
    {
        var items = videos.OrderBy(i => i.Id).ToList();
        var primary = items.FirstOrDefault(i => i.LinkedAlternateVersions.Length + 1 > 1 && i.PrimaryVersionText() is null) ?? items[0];
        var list = primary.LinkedAlternateVersions.ToList();
        foreach (var item in items.Where(i => i.Id != primary.Id))
        {
            item.SetPrimaryVersion(primary.Id);
            if (!list.Any(l => Same(l, item.Id, item.Path)))
            {
                list.Add(JellyfinLine.Link(item.Id, item.Path));
            }

            foreach (var linked in item.LinkedAlternateVersions)
            {
                if (!list.Any(l => Same(l, linked.ItemId, linked.LinkPath())))
                {
                    list.Add(linked);
                }
            }

            item.LinkedAlternateVersions = [];
        }

        primary.LinkedAlternateVersions = list.ToArray();
    }

    // 10.11 compares the listed paths, ignoring case; 12's links carry only the item id.
    private static bool Same(LinkedChild link, Guid? id, string? path)
        => JellyfinLine.Is12 ? link.ItemId == id : string.Equals(link.LinkPath(), path, StringComparison.OrdinalIgnoreCase);
}
