using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// The <see cref="VisibilityPolicy"/> matrix, the privacy-leak guard: author {self, other, hidden, disabled, former,
/// orphan} × Hide settings × viewer {user, hidden user, user not in <c>RaterUserIds</c>, admin} × surface {Items, Reviews,
/// OtherCopies, Summaries, Browse Top/Mine/Recent, the facade with a viewer and with <see cref="Guid.Empty"/>, the admin's
/// Moderation list}. The expected results are the visibility rules' table, encoded as data below; each surface is the
/// policy calls its endpoint makes. It asserts that the average and the count are the same for every viewer, that a
/// row the Hide settings (or an orphaned id) exclude reaches only its author and moderation, and that <c>MyStars</c> is
/// only ever the viewer's own.
/// </summary>
public sealed class VisibilityPolicyMatrixTests
{
    // The row rules, as data: whether an author state's rows count, by (HideReviewsFromHiddenUsers, HideReviewsFromDisabledUsers).
    private static readonly Dictionary<AuthorState, Func<bool, bool, bool>> CountsWhen = new()
    {
        [AuthorState.Member] = (_, _) => true,
        [AuthorState.Hidden] = (hideHidden, _) => !hideHidden,
        [AuthorState.Disabled] = (_, hideDisabled) => !hideDisabled,
        [AuthorState.Former] = (_, _) => true,
        [AuthorState.Orphan] = (_, _) => false,
    };

    // What a member sees on each surface, as data: for the viewer's own row and for another author's.
    private static readonly Dictionary<Surface, (Shows Own, Shows Other, bool HasSummary, bool HasMyStars)> Surfaces = new()
    {
        [Surface.Items] = (Shows.Always, Shows.IfCounted, true, true),
        [Surface.Reviews] = (Shows.Never, Shows.IfCounted, false, false),
        [Surface.OtherCopies] = (Shows.Never, Shows.Never, true, true),
        [Surface.Summaries] = (Shows.Never, Shows.Never, true, true),
        [Surface.BrowseTop] = (Shows.Never, Shows.Never, true, true),
        [Surface.BrowseMine] = (Shows.Always, Shows.Never, false, true),
        [Surface.BrowseRecent] = (Shows.Always, Shows.IfCounted, false, false),
        [Surface.FacadeViewer] = (Shows.Never, Shows.Never, true, true),
        [Surface.FacadeEmpty] = (Shows.Never, Shows.Never, true, false),
        [Surface.Moderation] = (Shows.Always, Shows.Always, false, false),
    };

    private enum AuthorState
    {
        Member,
        Hidden,
        Disabled,
        Former,
        Orphan
    }

    private enum ViewerKind
    {
        User,
        HiddenUser,
        NotARater,
        Admin
    }

    private enum Surface
    {
        Items,
        Reviews,
        OtherCopies,
        Summaries,
        BrowseTop,
        BrowseMine,
        BrowseRecent,
        FacadeViewer,
        FacadeEmpty,
        Moderation
    }

    private enum Shows
    {
        Never,
        IfCounted,
        Always
    }

    public static TheoryData<bool, bool> HideSettings => new() { { false, false }, { true, true }, { true, false }, { false, true } };

    [Theory]
    [MemberData(nameof(HideSettings))]
    public void EverySurface_ForEveryViewer_ShowsWhatSection36Says(bool hideHidden, bool hideDisabled)
    {
        var world = new World(hideHidden, hideDisabled);
        var summaries = new List<RatingSummary>();

        foreach (var viewer in Enum.GetValues<ViewerKind>())
        {
            var viewerId = world.Viewers[viewer];
            foreach (var surface in Enum.GetValues<Surface>())
            {
                if (surface == Surface.Moderation && viewer != ViewerKind.Admin)
                {
                    continue; // admin-only (RequiresElevation); the controllers' tests pin that
                }

                var shown = world.Show(surface, viewerId);
                var (own, other, hasSummary, hasMyStars) = Surfaces[surface];
                foreach (var row in world.Rows)
                {
                    var isOwn = row.UserId == viewerId;
                    var expected = (isOwn ? own : other) switch
                    {
                        Shows.Always => true,
                        Shows.IfCounted => CountsWhen[world.StateOf(row.UserId)](hideHidden, hideDisabled),
                        _ => false
                    };
                    Assert.True(
                        expected == shown.Rows.Contains(row.Id),
                        $"{surface} for {viewer} (hide hidden {hideHidden}, disabled {hideDisabled}): the row of {world.Describe(row.UserId, viewerId)} should {(expected ? string.Empty : "not ")}show.");
                }

                if (hasSummary)
                {
                    summaries.Add(shown.Summary!);
                }
                else
                {
                    Assert.Null(shown.Summary);
                }

                // MyStars: only ever the viewer's own, and nothing without a viewer.
                var mine = world.Rows.Single(r => r.UserId == viewerId);
                Assert.Equal(hasMyStars ? mine.Stars2 / 2m : null, shown.MyStars);
            }
        }

        // The average and the count are the same for every viewer and every surface: the counted rows, nothing else.
        var counted = world.Rows.Where(r => CountsWhen[world.StateOf(r.UserId)](hideHidden, hideDisabled)).ToList();
        var expectedSummary = RatingSummary.Of(counted.Select(r => (r.Stars2, r.ReviewText is not null)));
        Assert.All(summaries, s => Assert.Equal(expectedSummary, s));
        Assert.Equal(counted.Count, expectedSummary.Count);
    }

    [Theory]
    [MemberData(nameof(HideSettings))]
    public void AnExcludedRow_ReachesOnlyItsAuthorAndModeration(bool hideHidden, bool hideDisabled)
    {
        var world = new World(hideHidden, hideDisabled);
        foreach (var excluded in world.Rows.Where(r => !CountsWhen[world.StateOf(r.UserId)](hideHidden, hideDisabled)))
        {
            foreach (var viewer in Enum.GetValues<ViewerKind>())
            {
                var viewerId = world.Viewers[viewer];
                var reached = Enum.GetValues<Surface>()
                    .Where(s => s != Surface.Moderation)
                    .Where(s => world.Show(s, viewerId).Rows.Contains(excluded.Id))
                    .ToList();
                if (excluded.UserId == viewerId)
                {
                    Assert.Contains(Surface.Items, reached);
                    Assert.Contains(Surface.BrowseMine, reached);
                }
                else
                {
                    Assert.Empty(reached);
                }
            }

            Assert.Contains(excluded.Id, world.Show(Surface.Moderation, world.Viewers[ViewerKind.Admin]).Rows);
        }
    }

    [Fact]
    public void TheRowRules_ByAuthorKind()
    {
        var world = new World(hideHidden: true, hideDisabled: true);

        Assert.Equal(AuthorKind.Member, world.Policy.KindOf(world.Authors[AuthorState.Hidden]));
        Assert.Equal(AuthorKind.FormerMember, world.Policy.KindOf(world.Authors[AuthorState.Former]));
        Assert.Equal("Former member", world.Policy.FormerMemberName(world.Authors[AuthorState.Former]));
        Assert.Equal(AuthorKind.Orphan, world.Policy.KindOf(world.Authors[AuthorState.Orphan]));
        Assert.Null(world.Policy.FormerMemberName(world.Authors[AuthorState.Orphan]));
        Assert.Null(world.Policy.MyStars(world.SummaryRows, Guid.Empty));
        Assert.False(world.Policy.IsListedFor(world.Authors[AuthorState.Orphan], Guid.Empty));
    }

    // One item rated by every author state and every viewer, each row with its own stars and a review, so a leak shows.
    private sealed class World
    {
        public World(bool hideHidden, bool hideDisabled)
        {
            foreach (var state in Enum.GetValues<AuthorState>())
            {
                Authors[state] = Guid.NewGuid();
            }

            foreach (var viewer in Enum.GetValues<ViewerKind>())
            {
                Viewers[viewer] = Guid.NewGuid();
            }

            var users = new Dictionary<Guid, RatingAuthor>
            {
                [Authors[AuthorState.Member]] = new(false, false),
                [Authors[AuthorState.Hidden]] = new(true, false),
                [Authors[AuthorState.Disabled]] = new(false, true),
                [Viewers[ViewerKind.User]] = new(false, false),
                [Viewers[ViewerKind.HiddenUser]] = new(true, false),
                [Viewers[ViewerKind.NotARater]] = new(false, false),
                [Viewers[ViewerKind.Admin]] = new(false, false),
            };
            Policy = new VisibilityPolicy(hideHidden, hideDisabled, users, new Dictionary<Guid, string> { [Authors[AuthorState.Former]] = "Former member" });

            var item = Guid.NewGuid();
            var stars = 1;
            Rows = Authors.Values.Concat(Viewers.Values)
                .Select(user => new RatingRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = user,
                    Identity = new IdentitySnapshot { ItemId = item, Kind = "Movie", Name = "The Matrix" },
                    Stars2 = stars++,
                    ReviewText = "Review " + stars,
                    CreatedUtc = DateTime.UnixEpoch,
                    UpdatedUtc = DateTime.UnixEpoch.AddMinutes(stars)
                })
                .ToList();
            SummaryRows = Rows.Select(r => new SummaryRow(r.ItemId, r.UserId, r.Stars2, r.ReviewText is not null)).ToList();
        }

        public Dictionary<AuthorState, Guid> Authors { get; } = [];

        public Dictionary<ViewerKind, Guid> Viewers { get; } = [];

        public VisibilityPolicy Policy { get; }

        public List<RatingRecord> Rows { get; }

        public List<SummaryRow> SummaryRows { get; }

        public AuthorState StateOf(Guid userId)
            => Authors.FirstOrDefault(a => a.Value == userId) is { Value: var id } found && id != Guid.Empty
                ? found.Key
                : userId == Viewers[ViewerKind.HiddenUser] ? AuthorState.Hidden : AuthorState.Member;

        public string Describe(Guid author, Guid viewer)
            => author == viewer ? "the viewer" : StateOf(author) + " author " + author;

        // Each surface as its endpoint builds it from the policy (Items and Reviews, 1.1's Summaries, Browse and the
        // facade, the Moderation list): which rows it lists, the summary, MyStars.
        public (IReadOnlySet<Guid> Rows, RatingSummary? Summary, decimal? MyStars) Show(Surface surface, Guid viewer)
        {
            var summary = Policy.Summarize(SummaryRows);
            var myStars = Policy.MyStars(SummaryRows, viewer);
            HashSet<Guid> Ids(IEnumerable<RatingRecord> rows) => rows.Select(r => r.Id).ToHashSet();
            return surface switch
            {
                Surface.Items => (Ids(Policy.Others(Rows, viewer).Concat(Rows.Where(r => r.UserId == viewer))), summary, myStars),
                Surface.Reviews => (Ids(Policy.Others(Rows, viewer)), null, null),
                Surface.OtherCopies or Surface.Summaries or Surface.BrowseTop or Surface.FacadeViewer => (Ids([]), summary, myStars),
                Surface.FacadeEmpty => (Ids([]), summary, Policy.MyStars(SummaryRows, Guid.Empty)),
                Surface.BrowseMine => (Ids(Rows.Where(r => r.UserId == viewer)), null, myStars),
                Surface.BrowseRecent => (Ids(Policy.Listed(Rows.Where(r => r.ReviewText is not null), viewer)), null, null),
                Surface.Moderation => (Ids(Rows), null, null),
                _ => throw new ArgumentOutOfRangeException(nameof(surface))
            };
        }
    }
}
