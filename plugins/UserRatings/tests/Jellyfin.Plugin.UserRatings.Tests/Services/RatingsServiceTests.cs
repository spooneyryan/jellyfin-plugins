using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// <see cref="RatingsService"/>: the order of the checks (the switch, writable,
/// visible and rateable, <c>WhoCanRate</c>, <c>RequirePlayedToRate</c>, reviews), <c>WhoCanRate</c> with one listed user, the
/// <c>IfUpdatedUtc</c> 409 checked inside the transaction, a row deleted with its tombstone when its last half is cleared,
/// <c>EditCount</c>, <c>DELETE Mine/{ratingId}</c> without item access and with one 404, the double click, the details
/// read, and the summary cache following the writes.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RatingsServiceTests : IDisposable
{
    private readonly ServicesFixture _fx = new();
    private readonly Movie _matrix;

    public RatingsServiceTests() => _matrix = _fx.AddMovie(_fx.Movies, "The Matrix", "603");

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task AMembersWrite_RecordsTheMembersName_ForRemapByName()
    {
        // The last seen names, refreshed on writes, so a restore onto a rebuilt server can be remapped.
        await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4m, ServicesFixture.Ct);
        await _fx.Service.SetReviewAsync(_fx.Viewer2, _matrix.Id, "Synthetic words.", false, null, ServicesFixture.Ct);

        var owner = new Jellyfin.Plugin.UserRatings.Lifecycle.RatingsUserDataOwner(_fx.Store.Store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.Equal(("viewer", "viewer2"), (owner.KnownUsers.NameOf(_fx.Viewer.Id), owner.KnownUsers.NameOf(_fx.Viewer2.Id)));
    }

    // ------------------------------------------------------------------------------------- the order of the checks

    [Fact]
    public async Task RatingsOff_IsTheFirstAnswer_ThenReadOnly_ThenTheItem()
    {
        var hidden = _fx.AddMovie(_fx.Movies2, "Hidden");
        _fx.Configuration = new PluginConfiguration { EnableRatings = false, WhoCanRate = "SelectedUsers" };

        var off = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetStarsAsync(_fx.Viewer2, hidden.Id, 3.5m, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.FeatureDisabled, RatingsService.RatingsOffMessage), (off.Kind, off.Message));
        Assert.Equal(PluginErrorKind.FeatureDisabled, Assert.Throws<PluginException>(() => _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false)).Kind);

        // Read-only before the lookup: an item the member can't open and a newer schema's database give the 503.
        _fx.Configuration = new PluginConfiguration();
        await MakeReadOnlyAsync();
        var service = _fx.NewService();
        var readOnly = await Assert.ThrowsAsync<PluginException>(() => service.SetStarsAsync(_fx.Viewer2, hidden.Id, 3.5m, ServicesFixture.Ct));
        Assert.Equal(PluginErrorKind.Unavailable, readOnly.Kind);
        Assert.Equal(new ItemCapabilities(false, false, false, RatingsService.ReadOnlyReason), service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Can);
    }

    [Fact]
    public async Task ACacheGhostOfADeletedArtistFolder_IsReadAsCoreAnswersIt_ButEveryWriteOnItIsTheOne404()
    {
        // 10.11.5's GetItemById still answers a deleted music artist folder's albums from its cache, and a rating written on
        // one would be detached by the next maintenance.
        var artist = _fx.Add(_fx.Movies, new MusicArtist { Name = "Artist A" });
        var album = _fx.Add(_fx.Movies, new MusicAlbum { Name = "Album A", ParentId = artist.Id });
        var other = _fx.Add(_fx.Movies, new MusicAlbum { Name = "Album B", ParentId = artist.Id });
        await _fx.Service.SetReviewAsync(_fx.Viewer, album.Id, "Before the delete.", false, null, ServicesFixture.Ct);
        await _fx.Service.SetStarsAsync(_fx.Viewer, album.Id, 4m, ServicesFixture.Ct);
        Assert.Equal(new ItemCapabilities(true, true, false), _fx.Service.GetItem(_fx.Viewer, album.Id, isModerator: false).Can);
        _fx.Library.Remove(artist, raiseEvent: false, cacheKeepsDescendants: true);
        var before = _fx.Store.Store.GetAttached(_fx.Viewer.Id, album.Id)!;

        // The read answers as core's GetItemById does, and says the item can't be rated.
        var read = _fx.Service.GetItem(_fx.Viewer, album.Id, isModerator: false);
        Assert.Equal((before.Id, new ItemCapabilities(false, false, false, RatingsService.NotInLibraryReason)), (read.Mine!.Id, read.Can));

        var writes = new Func<Task>[]
        {
            () => _fx.Service.SetStarsAsync(_fx.Viewer, album.Id, 2m, ServicesFixture.Ct),
            () => _fx.Service.SetReviewAsync(_fx.Viewer, album.Id, "After.", false, null, ServicesFixture.Ct),
            () => _fx.Service.ClearStarsAsync(_fx.Viewer, album.Id, ServicesFixture.Ct),
            () => _fx.Service.ClearReviewAsync(_fx.Viewer, album.Id, ServicesFixture.Ct),
            () => _fx.Service.DeleteMineAsync(_fx.Viewer, album.Id, ServicesFixture.Ct),
            () => _fx.Service.SetStarsAsync(_fx.Viewer2, other.Id, 3m, ServicesFixture.Ct)
        };
        foreach (var write in writes)
        {
            var refused = await Assert.ThrowsAsync<PluginException>(write);
            Assert.Equal((PluginErrorKind.NotFound, ItemAccess.NotFoundMessage), (refused.Kind, refused.Message));
        }

        Assert.Equal(before, _fx.Store.Store.GetAttached(_fx.Viewer.Id, album.Id));
        Assert.Null(_fx.Store.Store.GetAttached(_fx.Viewer2.Id, other.Id));

        // While read-only the 503 still comes first.
        await MakeReadOnlyAsync();
        var service = _fx.NewService();
        Assert.Equal(PluginErrorKind.Unavailable, (await Assert.ThrowsAsync<PluginException>(() => service.SetStarsAsync(_fx.Viewer, album.Id, 2m, ServicesFixture.Ct))).Kind);
        Assert.Equal(RatingsService.ReadOnlyReason, service.GetItem(_fx.Viewer, album.Id, isModerator: false).Can.Reason);
    }

    [Fact]
    public async Task AnItemTheMemberCantOpen_AndOneThatIsntRateable_AreTheSame404_BeforeWhoCanRate()
    {
        var hidden = _fx.AddMovie(_fx.Movies2, "Hidden");
        var boxSet = _fx.Add(_fx.Movies, new BoxSet { Name = "Box" });
        _fx.Configuration = new PluginConfiguration { WhoCanRate = "SelectedUsers", RaterUserIds = [_fx.Viewer.Id] };

        var invisible = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetStarsAsync(_fx.Viewer2, hidden.Id, 3.5m, ServicesFixture.Ct));
        var unrateable = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetStarsAsync(_fx.Viewer2, boxSet.Id, 3.5m, ServicesFixture.Ct));
        var missing = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetStarsAsync(_fx.Viewer2, Guid.NewGuid(), 3.5m, ServicesFixture.Ct));

        Assert.All(new[] { invisible, unrateable, missing }, e => Assert.Equal((PluginErrorKind.NotFound, ItemAccess.NotFoundMessage), (e.Kind, e.Message)));
        Assert.Throws<PluginException>(() => _fx.Service.GetItem(_fx.Viewer2, hidden.Id, isModerator: false));
        Assert.Throws<PluginException>(() => _fx.Service.GetItem(_fx.Viewer, boxSet.Id, isModerator: false));
    }

    [Fact]
    public async Task WhoCanRate_ComesBeforeThePlayedCheck_WhichComesBeforeTheReviewsSwitch_ThenTheInput()
    {
        _fx.Configuration = new PluginConfiguration { WhoCanRate = "SelectedUsers", RaterUserIds = [_fx.Viewer.Id], RequirePlayedToRate = true, EnableReviews = false };
        Played(_matrix, _fx.Viewer, false);

        var notARater = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer2, _matrix.Id, string.Empty, false, null, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.Forbidden, RatingsService.NotARaterMessage), (notARater.Kind, notARater.Message));

        var notPlayed = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, string.Empty, false, null, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.Forbidden, RatingsService.NotPlayedMessage), (notPlayed.Kind, notPlayed.Message));

        Played(_matrix, _fx.Viewer, true);
        var reviewsOff = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, string.Empty, false, null, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.FeatureDisabled, RatingsService.ReviewsOffMessage), (reviewsOff.Kind, reviewsOff.Message));

        // Stars are still allowed with reviews off; then the input itself.
        var badStars = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 3.3m, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.Invalid, StarValue.InvalidMessage), (badStars.Kind, badStars.Message));
        Assert.Equal(7, (await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 3.5m, ServicesFixture.Ct)).Mine!.Stars2);

        _fx.Configuration = new PluginConfiguration();
        var empty = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, " \r\n ", false, null, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.Invalid, ReviewText.EmptyMessage), (empty.Kind, empty.Message));
    }

    [Fact]
    public async Task RequirePlayedToRate_ReadsTheUserDataFresh_NotCoresBlankCachedSnapshot()
    {
        // The cached instance has no snapshot (as after an episode refresh); the database row says played.
        _fx.Configuration = new PluginConfiguration { RequirePlayedToRate = true };
        _matrix.UserData = null;
        Played(_matrix, _fx.Viewer, true);

        Assert.Equal(9, (await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4.5m, ServicesFixture.Ct)).Mine!.Stars2);

        // Unreadable (the item can't be loaded fresh): 503, never a guess either way.
        _fx.Library.Mock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.DtoOptions.EnableUserData && q.User == null))).Returns([]);
        var unknown = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4.5m, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.Unavailable, RatingsService.PlayedUnknownMessage), (unknown.Kind, unknown.Message));
    }

    // ---------------------------------------------------------------------------------------------- WhoCanRate

    [Fact]
    public async Task WhoCanRate_WithOneListedUser_OnlyThatUserCreatesOrEdits_EveryoneReadsAndDeletesTheirOwn()
    {
        var older = await _fx.Service.SetReviewAsync(_fx.Viewer2, _matrix.Id, "Rated before the limit.", false, null, ServicesFixture.Ct);
        await _fx.Service.SetStarsAsync(_fx.Viewer2, _matrix.Id, 2m, ServicesFixture.Ct);
        _fx.Configuration = new PluginConfiguration { WhoCanRate = "SelectedUsers", RaterUserIds = [_fx.Viewer.Id] };

        // The listed user rates and reviews.
        await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 5m, ServicesFixture.Ct);
        await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "The only rater.", false, null, ServicesFixture.Ct);

        // Everyone else: 403 on create and edit, whatever they send.
        foreach (var other in new[] { _fx.Viewer2, _fx.Admin })
        {
            var stars = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetStarsAsync(other, _matrix.Id, 3m, ServicesFixture.Ct));
            var review = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(other, _matrix.Id, "No.", false, null, ServicesFixture.Ct));
            Assert.All(new[] { stars, review }, e => Assert.Equal((PluginErrorKind.Forbidden, RatingsService.NotARaterMessage), (e.Kind, e.Message)));
        }

        // viewer2's older row stays, counts and shows; viewer2 reads every rating and review, and may not rate here.
        var asViewer2 = _fx.Service.GetItem(_fx.Viewer2, _matrix.Id, isModerator: false);
        Assert.Equal((2, 3.5), (asViewer2.Summary.Count, asViewer2.Summary.Average!.Value));
        Assert.Equal(older.Mine!.Id, asViewer2.Mine!.Id);
        Assert.Equal(_fx.Viewer.Id, Assert.Single(asViewer2.Others).UserId);
        Assert.Equal(new ItemCapabilities(false, false, false, RatingsService.NotARaterReason), asViewer2.Can);
        Assert.Equal(new ItemCapabilities(true, true, false), _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Can);

        // ...and may still delete their own: the stars first (the review stays), then the rest.
        var cleared = await _fx.Service.ClearStarsAsync(_fx.Viewer2, _matrix.Id, ServicesFixture.Ct);
        Assert.Equal((null, "Rated before the limit."), (cleared.Mine!.Stars2, cleared.Mine.ReviewText));
        await _fx.Service.DeleteMineAsync(_fx.Viewer2, _matrix.Id, ServicesFixture.Ct);
        Assert.Null(_fx.Store.Store.GetAttached(_fx.Viewer2.Id, _matrix.Id));

        // Back to everyone: no data changed by the switch.
        var before = _fx.Store.Store.GetAttachedForItem(_matrix.Id);
        _fx.Configuration = new PluginConfiguration();
        Assert.Equal(before, _fx.Store.Store.GetAttachedForItem(_matrix.Id));
        await _fx.Service.SetStarsAsync(_fx.Viewer2, _matrix.Id, 1m, ServicesFixture.Ct);
    }

    [Fact]
    public async Task AnUnknownWhoCanRate_LetsNobodyNew_Rate()
    {
        _fx.Configuration = new PluginConfiguration { WhoCanRate = "Moderators", RaterUserIds = [_fx.Viewer.Id] };

        await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4m, ServicesFixture.Ct);
        await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetStarsAsync(_fx.Viewer2, _matrix.Id, 4m, ServicesFixture.Ct));
    }

    // ------------------------------------------------------------------------------ inside the transaction

    [Fact]
    public async Task IfUpdatedUtc_IsCheckedInsideTheTransaction_A409WhenTheRowChangedOrWentAway()
    {
        var first = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "First.", false, null, ServicesFixture.Ct)).Mine!;
        _fx.Advance(TimeSpan.FromMinutes(1));

        // The row as last seen: accepted, an edit.
        var second = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Second.", false, first.UpdatedUtc, ServicesFixture.Ct)).Mine!;
        Assert.Equal(1, second.EditCount);

        // The same stale time again: changed in another window.
        _fx.Advance(TimeSpan.FromMinutes(1));
        var stale = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Third.", false, first.UpdatedUtc, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.Conflict, RatingsService.ChangedElsewhereMessage), (stale.Kind, stale.Message));
        Assert.Equal("Second.", _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id)!.ReviewText);

        // A star change elsewhere moves UpdatedUtc too.
        await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4m, ServicesFixture.Ct);
        await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Third.", false, second.UpdatedUtc, ServicesFixture.Ct));

        // Two tabs with the same last-seen time at once: exactly one wins, the other is the 409.
        var current = _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id)!.UpdatedUtc;
        _fx.Advance(TimeSpan.FromMinutes(1));
        var tabs = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            try
            {
                await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Tab " + i, false, current, ServicesFixture.Ct);
                return true;
            }
            catch (PluginException e) when (e.Kind == PluginErrorKind.Conflict)
            {
                return false;
            }
        })));
        Assert.Equal(1, tabs.Count(won => won));

        // Deleted in another window: the 409 too; without IfUpdatedUtc it overwrites.
        await _fx.Service.DeleteMineAsync(_fx.Viewer, _matrix.Id, ServicesFixture.Ct);
        await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Gone?", false, current, ServicesFixture.Ct));
        Assert.Equal("Fresh.", (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Fresh.", false, null, ServicesFixture.Ct)).Mine!.ReviewText);
    }

    [Fact]
    public async Task TheReviewsOwnPrecondition_IgnoresTheStars_AndAFirstReviewInTwoWindowsIsA409_NeverAnOverwrite()
    {
        // Before: IfUpdatedUtc was the whole row's, so a rating saved elsewhere made a review
        // write a 409; and a first review had nothing to send, so two windows wrote over each other.
        static ReviewPrecondition Review(DateTime? at) => new(null, at, false);
        var noReview = new ReviewPrecondition(null, null, true);
        var stars = (await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 3m, ServicesFixture.Ct)).Mine!;
        Assert.Null(stars.ReviewUpdatedUtc);

        // Two windows open "Write a review" on the stars-only row; each sends IfNoReview. The first wins, the second is the
        // 409 and changes nothing.
        _fx.Advance(TimeSpan.FromSeconds(1));
        var first = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Window A.", false, noReview, ServicesFixture.Ct)).Mine!;
        var second = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Window B.", false, noReview, ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.Conflict, RatingsService.ReviewChangedElsewhereMessage), (second.Kind, second.Message));
        Assert.Equal("Window A.", _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id)!.ReviewText);

        // The stars changed elsewhere (twice): a review write with the review's own time is no 409.
        _fx.Advance(TimeSpan.FromSeconds(1));
        await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4m, ServicesFixture.Ct);
        await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4.5m, ServicesFixture.Ct);
        _fx.Advance(TimeSpan.FromSeconds(1));
        var edited = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Window A, edited.", false, Review(first.ReviewUpdatedUtc), ServicesFixture.Ct)).Mine!;
        Assert.Equal(9, edited.Stars2);
        Assert.NotEqual(first.ReviewUpdatedUtc, edited.ReviewUpdatedUtc);

        // Other words saved elsewhere since: the stale review time is the 409.
        var stale = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Late.", false, Review(first.ReviewUpdatedUtc), ServicesFixture.Ct));
        Assert.Equal(RatingsService.ReviewChangedElsewhereMessage, stale.Message);
        Assert.Equal("Window A, edited.", _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id)!.ReviewText);

        // The same words saved again with the flag changed move the review's time, not its edit count
        // (ASpoilerFlagChangedElsewhere_... below).
        _fx.Advance(TimeSpan.FromSeconds(1));
        var flagged = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Window A, edited.", true, ReviewPrecondition.None, ServicesFixture.Ct)).Mine!;
        Assert.NotEqual(edited.ReviewUpdatedUtc, flagged.ReviewUpdatedUtc);
        Assert.Equal(edited.EditCount, flagged.EditCount);
        _fx.Advance(TimeSpan.FromSeconds(1));
        var again = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Window A, again.", false, Review(flagged.ReviewUpdatedUtc), ServicesFixture.Ct)).Mine!;

        // Two windows with the same review time at once: exactly one wins.
        _fx.Advance(TimeSpan.FromSeconds(1));
        var tabs = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            try
            {
                await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Tab " + i, false, Review(again.ReviewUpdatedUtc), ServicesFixture.Ct);
                return true;
            }
            catch (PluginException e) when (e.Kind == PluginErrorKind.Conflict)
            {
                return false;
            }
        })));
        Assert.Equal(1, tabs.Count(won => won));

        // The review deleted elsewhere (the stars stay): the review time is a 409, "no review yet" saves.
        var latest = _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id)!;
        await _fx.Service.ClearReviewAsync(_fx.Viewer, _matrix.Id, ServicesFixture.Ct);
        await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Back?", false, Review(latest.ReviewUpdatedUtc), ServicesFixture.Ct));
        Assert.Equal("Back.", (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Back.", false, noReview, ServicesFixture.Ct)).Mine!.ReviewText);

        // No row at all: "no review yet" holds too.
        await _fx.Service.DeleteMineAsync(_fx.Viewer, _matrix.Id, ServicesFixture.Ct);
        Assert.Equal("Fresh.", (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Fresh.", false, noReview, ServicesFixture.Ct)).Mine!.ReviewText);
    }

    [Fact]
    public async Task ASpoilerFlagChangedElsewhere_IsA409_ForAWindowThatReadTheReviewBefore_AndIsNoEdit()
    {
        // Before, the flag alone left the review's time, so a window that read the review
        // unmarked saved it unmarked again with a 200, silently taking back the other window's "Contains spoilers".
        static ReviewPrecondition Review(DateTime? at) => new(null, at, false);
        var first = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Words.", false, new ReviewPrecondition(null, null, true), ServicesFixture.Ct)).Mine!;

        // Window B marks it, from the same starting point: saved; the review's time moves, the edit count doesn't.
        _fx.Advance(TimeSpan.FromSeconds(1));
        var marked = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Words.", true, Review(first.ReviewUpdatedUtc), ServicesFixture.Ct)).Mine!;
        Assert.Equal((true, 0), (marked.IsSpoiler, marked.EditCount));
        Assert.True(marked.ReviewUpdatedUtc > first.ReviewUpdatedUtc);

        // Window A, still at the first time, saves the words unmarked: the 409, and the mark stays.
        _fx.Advance(TimeSpan.FromSeconds(1));
        var stale = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Words.", false, Review(first.ReviewUpdatedUtc), ServicesFixture.Ct));
        Assert.Equal((PluginErrorKind.Conflict, RatingsService.ReviewChangedElsewhereMessage), (stale.Kind, stale.Message));
        Assert.True(_fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id)!.IsSpoiler);

        // Saved again as it is: nothing about the review changed, so its time stays; unmarked from the new time: saved.
        _fx.Advance(TimeSpan.FromSeconds(1));
        var same = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Words.", true, Review(marked.ReviewUpdatedUtc), ServicesFixture.Ct)).Mine!;
        Assert.Equal((marked.ReviewUpdatedUtc, 0), (same.ReviewUpdatedUtc, same.EditCount));
        _fx.Advance(TimeSpan.FromSeconds(1));
        var unmarked = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Words.", false, Review(same.ReviewUpdatedUtc), ServicesFixture.Ct)).Mine!;
        Assert.Equal((false, 0), (unmarked.IsSpoiler, unmarked.EditCount));

        // The stars still never move it.
        _fx.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(unmarked.ReviewUpdatedUtc, (await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4m, ServicesFixture.Ct)).Mine!.ReviewUpdatedUtc);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task TwoPreconditionsAtOnce_AreA400_AndChangeNothing(bool row, bool review, bool none)
    {
        var saved = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Kept.", false, ReviewPrecondition.None, ServicesFixture.Ct)).Mine!;
        var both = new ReviewPrecondition(row ? saved.UpdatedUtc : null, review ? saved.ReviewUpdatedUtc : null, none);

        var refused = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Changed?", false, both, ServicesFixture.Ct));

        Assert.Equal(PluginErrorKind.Invalid, refused.Kind);
        Assert.Equal(row ? ReviewPrecondition.RowAndReviewMessage : ReviewPrecondition.BothReviewMessage, refused.Message);
        Assert.Equal(saved, _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id));
    }

    [Fact]
    public async Task ADoubleClick_IsTheLastWrite_OneRow_NoConflict()
    {
        var clicks = await Task.WhenAll(Enumerable.Range(1, 6).Select(i => Task.Run(() => _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, i / 2m, ServicesFixture.Ct))));

        var last = clicks.MaxBy(c => c.Generation)!;
        Assert.Equal(last.Mine, _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id));
        Assert.Single(_fx.Store.Store.GetAttachedForItem(_matrix.Id));
        Assert.Single(clicks.Select(c => c.Mine!.Id).Distinct());
        Assert.Equal(last.Generation, _fx.Store.Store.Generation);
    }

    [Fact]
    public async Task ClearingTheLastHalf_DeletesTheRow_WithItsTombstone()
    {
        var stars = (await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 3m, ServicesFixture.Ct)).Mine!;
        var cleared = await _fx.Service.ClearStarsAsync(_fx.Viewer, _matrix.Id, ServicesFixture.Ct);
        Assert.Null(cleared.Mine);
        Assert.Equal(RatingValues.DeletedBy.User, _fx.Store.Store.GetTombstone(stars.Id)!.DeletedBy);

        var text = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Words only.", true, null, ServicesFixture.Ct)).Mine!;
        Assert.True(text.IsSpoiler);
        Assert.Null((await _fx.Service.ClearReviewAsync(_fx.Viewer, _matrix.Id, ServicesFixture.Ct)).Mine);
        Assert.NotNull(_fx.Store.Store.GetTombstone(text.Id));

        // Nothing to clear: nothing written.
        var nothing = await _fx.Service.ClearReviewAsync(_fx.Viewer, _matrix.Id, ServicesFixture.Ct);
        Assert.False(nothing.Changed);
        Assert.Equal(2L, _fx.Store.Count("Tombstones"));
    }

    [Fact]
    public async Task TheSpoilerFlag_IsKeptOnlyWhileAllowed()
    {
        _fx.Configuration = new PluginConfiguration { AllowSpoilerFlag = false };

        Assert.False((await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Ending inside.", true, null, ServicesFixture.Ct)).Mine!.IsSpoiler);
    }

    [Fact]
    public async Task TheReviewLimit_IsTheSettings()
    {
        _fx.Configuration = new PluginConfiguration { MaxReviewLength = 2000 };

        await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, new string('x', 2000), false, null, ServicesFixture.Ct);
        await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, new string('x', 2001), false, null, ServicesFixture.Ct));
    }

    // ------------------------------------------------------------------------------ DELETE Mine/{ratingId}

    [Fact]
    public async Task DeleteOwnRow_WorksWithoutItemAccess_AndIsOne404ForNotYoursAndNotFound()
    {
        var copy = _fx.AddMovie(_fx.Movies, "Copy");
        var mine = (await _fx.Service.SetStarsAsync(_fx.Viewer2, copy.Id, 4m, ServicesFixture.Ct)).Mine!;
        var theirs = (await _fx.Service.SetStarsAsync(_fx.Viewer, copy.Id, 4m, ServicesFixture.Ct)).Mine!;

        // viewer2 loses access to the item: the item lookup is 404 now, the own row is still theirs to delete.
        _fx.Library.Hide(_fx.Viewer2, copy);
        await Assert.ThrowsAsync<PluginException>(() => _fx.Service.DeleteMineAsync(_fx.Viewer2, copy.Id, ServicesFixture.Ct));

        var notYours = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.DeleteOwnRowAsync(_fx.Viewer2, theirs.Id, ServicesFixture.Ct));
        var notFound = await Assert.ThrowsAsync<PluginException>(() => _fx.Service.DeleteOwnRowAsync(_fx.Viewer2, Guid.NewGuid(), ServicesFixture.Ct));
        Assert.Equal((notYours.Kind, notYours.Message), (notFound.Kind, notFound.Message));
        Assert.Equal((PluginErrorKind.NotFound, RatingsService.NotYoursMessage), (notFound.Kind, notFound.Message));

        _fx.Configuration = new PluginConfiguration { WhoCanRate = "SelectedUsers", EnableReviews = false };
        var generation = await _fx.Service.DeleteOwnRowAsync(_fx.Viewer2, mine.Id, ServicesFixture.Ct);
        Assert.Null(_fx.Store.Store.Get(mine.Id));
        Assert.NotNull(_fx.Store.Store.Get(theirs.Id));
        Assert.Equal(_fx.Store.Store.Generation, generation);
    }

    // ------------------------------------------------------------------------------------------- the read

    [Fact]
    public async Task GetItem_IsTheSummaryForEveryone_MineApart_AndTheOthersThePolicyShows()
    {
        var hiddenUser = ServicesFixture.NewUser("hidden");
        hiddenUser.SetPermission(PermissionKind.IsHidden, true);
        _fx.Users.Add(hiddenUser);
        await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4m, ServicesFixture.Ct);
        _fx.Advance(TimeSpan.FromMinutes(1));
        await _fx.Service.SetReviewAsync(_fx.Viewer2, _matrix.Id, "Second.", true, null, ServicesFixture.Ct);
        _fx.Advance(TimeSpan.FromMinutes(1));
        await _fx.Service.SetStarsAsync(hiddenUser, _matrix.Id, 1m, ServicesFixture.Ct);
        await _fx.Store.Store.UpsertAsync(Tests.Storage.StoreFixture.Movie(Guid.NewGuid(), _matrix.Id, 10) with { Identity = (await _fx.Store.Store.WriteAsync(w => w.GetAttached(_fx.Viewer.Id, _matrix.Id)!, ServicesFixture.Ct)).Identity }, ServicesFixture.Ct);

        // An orphaned id never counts; hidden users count while HideReviewsFromHiddenUsers is off (the default).
        var view = _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false);
        Assert.Equal((2, 1, 2.5), (view.Summary.Count, view.Summary.ReviewCount, view.Summary.Average!.Value));
        Assert.Equal(8, view.Mine!.Stars2);
        Assert.Equal([hiddenUser.Id, _fx.Viewer2.Id], view.Others.Select(r => r.UserId));
        Assert.Same(_matrix, view.Item);

        _fx.Configuration = new PluginConfiguration { HideReviewsFromHiddenUsers = true };
        var hiding = _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false);
        Assert.Equal((1, 4.0), (hiding.Summary.Count, hiding.Summary.Average!.Value));
        Assert.Equal([_fx.Viewer2.Id], hiding.Others.Select(r => r.UserId));

        // The hidden author still sees their own row, and the same summary as everyone.
        var asHidden = _fx.Service.GetItem(hiddenUser, _matrix.Id, isModerator: false);
        Assert.Equal(2, asHidden.Mine!.Stars2);
        Assert.Equal(hiding.Summary, asHidden.Summary);

        // Moderate is the request's RequiresElevation (the controller passes it), never the stored flag.
        Assert.True(_fx.Service.GetItem(_fx.Admin, _matrix.Id, isModerator: true).Can.Moderate);
        Assert.False(_fx.Service.GetItem(_fx.Admin, _matrix.Id, isModerator: false).Can.Moderate);
    }

    [Fact]
    public async Task AWrite_IsSeenByTheNextRead_ThroughTheCache()
    {
        Assert.Equal(RatingSummary.Empty, _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Summary);
        Assert.Equal(1, _fx.Cache.Count);

        await _fx.Service.SetStarsAsync(_fx.Viewer2, _matrix.Id, 5m, ServicesFixture.Ct);

        Assert.Equal(5.0, _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Summary.Average);
    }

    [Fact]
    public async Task OtherCopies_AreTheVisibleCopiesWithRatings()
    {
        var copy = _fx.AddMovie(_fx.Movies2, "The Matrix (copy)", "603");
        await _fx.Service.SetStarsAsync(_fx.Viewer, copy.Id, 4m, ServicesFixture.Ct);

        var sibling = Assert.Single(_fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).OtherCopies);
        Assert.Equal((copy.Id, "Movies 2", 1, 4m), (sibling.ItemId, sibling.LibraryName, sibling.Summary.Count, sibling.MyStars));

        // viewer2 can't open Movies 2: no hint.
        Assert.Empty(_fx.Service.GetItem(_fx.Viewer2, _matrix.Id, isModerator: false).OtherCopies);
    }

    // ------------------------------------------------------------------------------------------ a review's findings

    [Fact]
    public async Task ACopyAParentHides_IsNoOtherCopy_AndTheOwnRowOnItIsInaccessible()
    {
        // A PG copy inside a folder the viewer can't see (rated R, say). Its page is a 404 for the viewer, so the hint on
        // the other copy never names it, and the viewer's own row on it is in Mine/Inaccessible. The admin still sees both.
        var folder = _fx.Library.Add(new Folder { Name = "Rated Folder" });
        var copy = _fx.AddMovie(_fx.Movies, "The Matrix (in a folder)", "603");
        copy.ParentId = folder.Id;
        var own = (await _fx.Service.SetStarsAsync(_fx.Viewer, copy.Id, 2m, ServicesFixture.Ct)).Mine!;
        await _fx.Service.SetStarsAsync(_fx.Admin, copy.Id, 5m, ServicesFixture.Ct);
        Assert.Equal(copy.Id, Assert.Single(_fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).OtherCopies).ItemId);
        Assert.Empty(_fx.Service.GetInaccessible(_fx.Viewer));

        _fx.Library.Hide(_fx.Viewer, folder);

        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<PluginException>(() => _fx.Service.GetItem(_fx.Viewer, copy.Id, isModerator: false)).Kind);
        Assert.Empty(_fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).OtherCopies);
        Assert.Equal(own.Id, Assert.Single(_fx.Service.GetInaccessible(_fx.Viewer)).Id);
        Assert.Equal(copy.Id, Assert.Single(_fx.Service.GetItem(_fx.Admin, _matrix.Id, isModerator: false).OtherCopies).ItemId);

        await _fx.Service.DeleteOwnRowAsync(_fx.Viewer, own.Id, ServicesFixture.Ct);
        Assert.Empty(_fx.Service.GetInaccessible(_fx.Viewer));
    }

    [Fact]
    public async Task MineInaccessible_HoldsEveryOwnRowTheItemRoutesCantReach_AndTheDeleteByIdWorksForEach()
    {
        // A member can always find and delete their own rows: a kind no longer rateable, and ratings off.
        var episode = _fx.Add(_fx.Movies, new Episode { Name = "Pilot" });
        var movieRow = (await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 4m, ServicesFixture.Ct)).Mine!;
        _fx.Advance(TimeSpan.FromMinutes(1));
        var episodeRow = (await _fx.Service.SetReviewAsync(_fx.Viewer, episode.Id, "An episode.", false, null, ServicesFixture.Ct)).Mine!;
        Assert.Empty(_fx.Service.GetInaccessible(_fx.Viewer));

        // Movie taken out of RateableItemTypes: the item routes are the one 404, so the list has the movie's row.
        _fx.Configuration = new PluginConfiguration { RateableItemTypes = ["Episode"] };
        Assert.Throws<PluginException>(() => _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false));
        await Assert.ThrowsAsync<PluginException>(() => _fx.Service.DeleteMineAsync(_fx.Viewer, _matrix.Id, ServicesFixture.Ct));
        Assert.Equal([movieRow.Id], _fx.Service.GetInaccessible(_fx.Viewer).Select(r => r.Id));

        // Ratings off: every item route is the 403, so the list has every own row (newest change first), openable or not.
        _fx.Configuration = new PluginConfiguration { EnableRatings = false };
        Assert.Equal([episodeRow.Id, movieRow.Id], _fx.Service.GetInaccessible(_fx.Viewer).Select(r => r.Id));
        Assert.Empty(_fx.Service.GetInaccessible(_fx.Viewer2));

        // The delete by id works in either state.
        await _fx.Service.DeleteOwnRowAsync(_fx.Viewer, episodeRow.Id, ServicesFixture.Ct);
        _fx.Configuration = new PluginConfiguration { RateableItemTypes = ["Episode"] };
        await _fx.Service.DeleteOwnRowAsync(_fx.Viewer, movieRow.Id, ServicesFixture.Ct);
        Assert.Empty(_fx.Service.GetInaccessible(_fx.Viewer));
        Assert.Equal(0, _fx.Store.Store.Count().Attached);
    }

    [Fact]
    public void Can_TellsWhyRateIsFalse_InTheWritesOrder_AndFollowsRequirePlayedToRate()
    {
        // Can used to say Rate and Review while the PUT was the 403 "Play this first".
        _fx.Configuration = new PluginConfiguration { RequirePlayedToRate = true };
        Played(_matrix, _fx.Viewer, false);
        Assert.Equal(new ItemCapabilities(false, false, false, RatingsService.NeedsPlayReason), _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Can);

        Played(_matrix, _fx.Viewer, true);
        Assert.Equal(new ItemCapabilities(true, true, false), _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Can);

        // WhoCanRate comes before the played check, as in the write.
        _fx.Configuration = new PluginConfiguration { RequirePlayedToRate = true, WhoCanRate = "SelectedUsers", RaterUserIds = [_fx.Viewer2.Id] };
        Played(_matrix, _fx.Viewer, false);
        Assert.Equal(RatingsService.NotARaterReason, _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Can.Reason);

        // Unreadable just now: Rate stays true and the write gives its own 503. Reviews off: Review false, no reason.
        _fx.Configuration = new PluginConfiguration { RequirePlayedToRate = true };
        _fx.Library.Mock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.DtoOptions.EnableUserData && q.User == null))).Returns([]);
        Assert.Equal(new ItemCapabilities(true, true, false), _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Can);
        _fx.Configuration = new PluginConfiguration { EnableReviews = false };
        Assert.Equal(new ItemCapabilities(true, false, false), _fx.Service.GetItem(_fx.Viewer, _matrix.Id, isModerator: false).Can);
    }

    [Fact]
    public async Task WriteTimes_AreWholeMilliseconds_ComparedAtTheMillisecond_AndNeverAtOrBeforeTheRowsLast()
    {
        // A JavaScript Date keeps milliseconds, so "…13.804Z" came back for a stored "…13.8048619Z": a 409 every time.
        var t = StoreFixture.T0.AddTicks(138_048_619);
        _fx.Store.Clock.Set(new DateTimeOffset(t));
        var ms = RatingsService.ToMilliseconds(t);
        var first = (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "First.", false, null, ServicesFixture.Ct)).Mine!;
        Assert.Equal((ms, ms, ms), (first.CreatedUtc, first.UpdatedUtc, first.ReviewUpdatedUtc!.Value));
        Assert.Equal(new DateTime(2026, 10, 19, 3, 20, 13, 804, DateTimeKind.Utc), ms);

        // The same instant in each form a client's JSON gives: UTC, an offset (read as local time), none (taken as UTC).
        _fx.Advance(TimeSpan.FromSeconds(1));
        foreach (var sent in new[] { ms.ToLocalTime(), DateTime.SpecifyKind(ms, DateTimeKind.Unspecified), ms })
        {
            var before = _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id)!.UpdatedUtc;
            var seen = sent.Kind == DateTimeKind.Utc ? before : sent.Kind == DateTimeKind.Local ? before.ToLocalTime() : DateTime.SpecifyKind(before, DateTimeKind.Unspecified);
            await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Edit " + sent.Kind, false, seen, ServicesFixture.Ct);
        }

        // Within one millisecond (the clock stands still): one millisecond later each time, so a window that saw the time
        // before still gets the 409.
        var stoodStill = _fx.Store.Store.GetAttached(_fx.Viewer.Id, _matrix.Id)!.UpdatedUtc;
        var stars = (await _fx.Service.SetStarsAsync(_fx.Viewer, _matrix.Id, 3m, ServicesFixture.Ct)).Mine!;
        Assert.Equal(stoodStill.AddMilliseconds(1), stars.UpdatedUtc);
        await Assert.ThrowsAsync<PluginException>(() => _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Stale.", false, stoodStill, ServicesFixture.Ct));
        Assert.Equal(stars.UpdatedUtc.AddMilliseconds(1), (await _fx.Service.SetReviewAsync(_fx.Viewer, _matrix.Id, "Fresh.", false, stars.UpdatedUtc, ServicesFixture.Ct)).Mine!.UpdatedUtc);

        // A row written before, to the 100 ns: the time a client holds of it, cut to the millisecond, still matches.
        await _fx.Store.Store.UpsertAsync(StoreFixture.Movie(_fx.Viewer2.Id, _matrix.Id, 6, "Old.", at: t) with { Identity = first.Identity }, ServicesFixture.Ct);
        await _fx.Service.SetReviewAsync(_fx.Viewer2, _matrix.Id, "Edited.", false, ms, ServicesFixture.Ct);
        Assert.Equal("Edited.", _fx.Store.Store.GetAttached(_fx.Viewer2.Id, _matrix.Id)!.ReviewText);
    }

    private async Task MakeReadOnlyAsync()
    {
        await _fx.Store.Database.WriteAsync(
            (c, t) =>
            {
                Tests.Storage.StoreFixture.Execute(c, t, "PRAGMA user_version = 99;");
                return 0;
            },
            ServicesFixture.Ct);
        _fx.Store.Reopen();
        Assert.True(_fx.Store.Store.IsReadOnly);
    }

    // The fresh read's answer for an item and a user: a copy of the item with the user's row (never the cached one).
    private void Played(BaseItem item, User user, bool played)
    {
        var fresh = new Movie
        {
            Id = item.Id,
            Name = item.Name,
            UserData =
            [
                new UserData
                {
                    ItemId = item.Id,
                    Item = null!,
                    UserId = user.Id,
                    User = null!,
                    CustomDataKey = item.Id.ToString("N"),
                    Played = played
                }
            ]
        };
        _fx.Library.Mock.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.DtoOptions.EnableUserData && q.User == null))).Returns([fresh]);
    }
}
