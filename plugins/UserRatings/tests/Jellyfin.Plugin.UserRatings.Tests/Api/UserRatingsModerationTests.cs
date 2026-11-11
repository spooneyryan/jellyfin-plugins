using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Api;

/// <summary>
/// The moderation routes on the wire: <c>GET Admin/Reviews</c>,
/// <c>DELETE Admin/Reviews/{ratingId}</c> and <c>GET Admin/ModerationLog</c> behind the admin controller, over the real
/// store: admins and API keys only; a signed-in admin's rows only on items they can open (an API key every row); a removal
/// is a dry run by default, its real run bound to the dry run's token (400 without, 409 <c>Changed</c> for a stale one,
/// nothing written either way), with a pre-operation snapshot only when something changes, one moderation-log row of ids
/// and counts, a <c>Moderator</c> tombstone for a deleted rating, and no byte of the removed text left in the database,
/// its <c>-wal</c> or the archive.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class UserRatingsModerationTests : IAsyncLifetime
{
    private const string Reviews = "/UserRatings/Admin/Reviews";
    private const string Log = "/UserRatings/Admin/ModerationLog";
    private const string Marker = "Moderation marker: the lighthouse was painted green.";

    private readonly ApiFixture _fx = new();
    private readonly Movie _matrix;
    private readonly Movie _heat;
    private readonly Movie _elsewhere;

    public UserRatingsModerationTests()
    {
        _matrix = S.AddMovie(S.Movies, "The Matrix", "603");
        _heat = S.AddMovie(S.Movies, "Heat", "949");
        _elsewhere = S.AddMovie(S.Movies2, "Ronin", "8195");
        S.Admin.SetPermission(PermissionKind.EnableAllFolders, true);
    }

    private ServicesFixture S => _fx.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // viewer2 with the Administrator role: an admin whose libraries are Movies only (ServicesFixture's access).
    private ApiFixture.AsCaller RestrictedAdmin => new("admin", S.Viewer2.Id);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    [Fact]
    public async Task TheModerationRoutes_AreForAnAdminOrAnApiKeyOnly_AndAMemberChangesNothing()
    {
        await _fx.StartAsync();
        var row = await RateAsync(S.Viewer, _matrix, 4, Marker);

        foreach (var (method, path) in new[] { (HttpMethod.Get, Reviews), (HttpMethod.Get, Log), (HttpMethod.Delete, $"{Reviews}/{row.Id:N}"), (HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview=m1-00000000000000000000") })
        {
            Assert.Equal(401, await StatusAsync(null, method, path));
            Assert.Equal(403, await StatusAsync(S.Viewer, method, path));
            Assert.Equal(403, await StatusAsync(new ApiFixture.AsCaller("user", S.Admin.Id), method, path));
        }

        Assert.Equal(Marker, S.Store.Store.Get(row.Id)!.ReviewText);
        Assert.Empty(S.Store.Store.GetModerationLog(0, 10));
        Assert.False(Directory.Exists(PreOp));

        Assert.Equal(200, await StatusAsync(S.Admin, HttpMethod.Get, Reviews));
        Assert.Equal(200, await StatusAsync(ApiFixture.ApiKey, HttpMethod.Get, Reviews));
        Assert.Equal(200, await StatusAsync(ApiFixture.ApiKey, HttpMethod.Get, Log));
    }

    // No browser or proxy keeps an answer that holds reviews or the log, a 404's either.
    [Fact]
    public async Task TheAnswersThatHoldReviewsOrTheLog_AreNoStore_AnErrorsToo_AndTheCapabilitiesAreNot()
    {
        await _fx.StartAsync();
        var row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        (object Caller, HttpMethod Method, string Path, int Status)[] answers =
        [
            (S.Admin, HttpMethod.Get, Reviews, 200),
            (S.Admin, HttpMethod.Get, Log, 200),
            (S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}", 200),
            (S.Admin, HttpMethod.Delete, $"{Reviews}/{Guid.NewGuid():N}", 404),
            (S.Viewer2, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}", 200),
            (S.Viewer2, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}/Reviews", 200),
            (S.Viewer2, HttpMethod.Get, $"/UserRatings/Items/{_elsewhere.Id:N}", 404),
            (S.Viewer, HttpMethod.Get, "/UserRatings/Mine/Inaccessible", 200),
            (S.Viewer, HttpMethod.Delete, $"/UserRatings/Items/{_matrix.Id:N}/Mine/Review", 200),
        ];
        foreach (var (caller, method, path, status) in answers)
        {
            using var response = await _fx.SendAsync(caller, method, path);
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore, $"{method} {path}");
        }

        using var capabilities = await _fx.SendAsync(S.Viewer, HttpMethod.Get, "/UserRatings/Capabilities");
        Assert.NotEqual(true, capabilities.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task TheList_FiltersSearchesAndPages_AndShowsEveryAuthorsRow_HiddenFormerAndOrphanToo()
    {
        await _fx.StartAsync();
        S.Configuration.HideReviewsFromHiddenUsers = true;
        S.Viewer.SetPermission(PermissionKind.IsHidden, true);
        var hidden = await RateAsync(S.Viewer, _matrix, 4, "A hidden member's review.");
        S.Advance(TimeSpan.FromMinutes(1));
        var plain = await RateAsync(S.Admin, _heat, 3);
        S.Advance(TimeSpan.FromMinutes(1));
        var former = Guid.NewGuid();
        await S.Store.Store.WriteAsync(w => { w.AddFormerUser(former, Guid.NewGuid(), "Former member", S.Store.Clock.GetUtcNow().UtcDateTime); return 0; }, Ct);
        var formers = await UpsertAsync(StoreFixture.Movie(former, _heat.Id, 6, "Kept as a former member's.", at: Now) with { Identity = Identity(_heat) });
        S.Advance(TimeSpan.FromMinutes(1));
        var orphan = await UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), _matrix.Id, 2, at: Now) with { Identity = Identity(_matrix) });
        S.Advance(TimeSpan.FromMinutes(1));
        var detached = await UpsertAsync(StoreFixture.Movie(S.Viewer2.Id, Guid.NewGuid(), 9, "On a film that left.", at: Now) with { DetachedUtc = Now, DetachReason = RatingValues.DetachReason.Removed });

        var all = await _fx.JsonAsync(S.Admin, HttpMethod.Get, Reviews);
        Assert.Equal(5, all.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal([detached.Id, orphan.Id, formers.Id, plain.Id, hidden.Id], Ids(all));
        var first = Entry(all, hidden.Id);
        Assert.Equal(("viewer", "Member", true, "The Matrix", "Attached"), (first.GetProperty("UserName").GetString(), first.GetProperty("AuthorKind").GetString(), first.GetProperty("IsHiddenUser").GetBoolean(), first.GetProperty("ItemName").GetString(), first.GetProperty("State").GetString()));
        Assert.Equal(("A hidden member's review.", 4.0), (first.GetProperty("Text").GetString(), first.GetProperty("Stars").GetDouble()));
        Assert.Equal(("Former member", "FormerMember"), (Entry(all, formers.Id).GetProperty("UserName").GetString(), Entry(all, formers.Id).GetProperty("AuthorKind").GetString()));
        Assert.False(Entry(all, orphan.Id).TryGetProperty("UserName", out _));
        Assert.Equal("Orphan", Entry(all, orphan.Id).GetProperty("AuthorKind").GetString());
        Assert.Equal(("Detached", "Removed"), (Entry(all, detached.Id).GetProperty("State").GetString(), Entry(all, detached.Id).GetProperty("DetachReason").GetString()));

        // The filters, alone and together; the state in any case; paging with the total.
        Assert.Equal([hidden.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?userId={S.Viewer.Id:N}")));
        Assert.Equal([formers.Id, plain.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?itemId={_heat.Id:N}")));
        Assert.Equal([detached.Id, formers.Id, hidden.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?hasText=true")));
        Assert.Equal([orphan.Id, plain.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?hasText=false")));
        Assert.Equal([detached.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?state=detached")));
        Assert.Equal(4, (await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?state=Attached")).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal([detached.Id, orphan.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?since={Uri.EscapeDataString(orphan.UpdatedUtc.ToString("O"))}")));
        Assert.Equal([formers.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?itemId={_heat.Id:N}&hasText=true")));
        var page = await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?startIndex=1&limit=2");
        Assert.Equal((5, 1), (page.GetProperty("TotalRecordCount").GetInt32(), page.GetProperty("StartIndex").GetInt32()));
        Assert.Equal([orphan.Id, formers.Id], Ids(page));

        // Search: the author's name, the item's name, the review, in any case.
        Assert.Equal([detached.Id, hidden.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?searchTerm=VIEWER")));
        Assert.Equal([detached.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?searchTerm=Viewer2")));
        Assert.Equal([formers.Id, plain.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?searchTerm=heat")));
        Assert.Equal([detached.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?searchTerm=film%20that")));
        Assert.Equal([formers.Id], Ids(await _fx.JsonAsync(S.Admin, HttpMethod.Get, $"{Reviews}?searchTerm=former")));

        // Bad values are 400s.
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Get, $"{Reviews}?state=1"));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Get, $"{Reviews}?state=Gone"));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Get, $"{Reviews}?limit=0"));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Get, $"{Reviews}?limit=201"));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Get, $"{Reviews}?startIndex=-1"));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Get, $"{Reviews}?userId=nobody"));
        Assert.Equal(RatingsModeration.SearchMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Get, $"{Reviews}?searchTerm={new string('x', 201)}", 400));
    }

    [Fact]
    public async Task ASignedInAdmin_ModeratesOnlyRowsOnItemsTheyCanOpen_AndAnApiKeyEveryRow()
    {
        await _fx.StartAsync();
        var open = await RateAsync(S.Viewer, _matrix, 4, "In Movies.");
        var closed = await RateAsync(S.Viewer, _elsewhere, 3, "In Movies 2.");
        var detached = await UpsertAsync(StoreFixture.Movie(S.Viewer.Id, Guid.NewGuid(), 9, "Detached.") with { DetachedUtc = Now, DetachReason = RatingValues.DetachReason.Missing });

        Assert.Equal([open.Id], Ids(await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, Reviews)));
        Assert.Equal(0, (await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, $"{Reviews}?itemId={_elsewhere.Id:N}")).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(3, (await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Get, Reviews)).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(3, (await _fx.JsonAsync(S.Admin, HttpMethod.Get, Reviews)).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal([closed.Id], Ids(await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Get, $"{Reviews}?itemId={_elsewhere.Id:N}")));

        // Without every library an admin sees no detached row either; a rating they can't moderate is the 404 of none.
        foreach (var id in new[] { closed.Id, detached.Id, Guid.NewGuid() })
        {
            Assert.Equal(RatingsModeration.NotFoundMessage, await _fx.ProblemAsync(RestrictedAdmin, HttpMethod.Delete, $"{Reviews}/{id:N}", 404));
        }

        Assert.Equal(200, await StatusAsync(RestrictedAdmin, HttpMethod.Delete, $"{Reviews}/{open.Id:N}"));
        Assert.Equal(200, await StatusAsync(ApiFixture.ApiKey, HttpMethod.Delete, $"{Reviews}/{closed.Id:N}"));
        Assert.Equal(200, await StatusAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{detached.Id:N}"));

        // An admin id that is no user any more sees nothing.
        Assert.Equal(0, (await _fx.JsonAsync(new ApiFixture.AsCaller("admin", Guid.NewGuid()), HttpMethod.Get, Reviews)).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(404, await StatusAsync(new ApiFixture.AsCaller("admin", Guid.NewGuid()), HttpMethod.Delete, $"{Reviews}/{open.Id:N}"));
    }

    // ForModeration's bound: an API key's list without a search reads its page and count only; a
    // signed-in admin's pages reuse what the last minute found about each of their items, while a removal checks its row
    // afresh.
    [Fact]
    public async Task TheList_IsReadAsAPageForAnApiKey_AndReusesAnAdminsItemChecksForAMinute_ButARemovalChecksAfresh()
    {
        await _fx.StartAsync();
        var rows = new System.Collections.Generic.List<RatingRecord>();
        foreach (var movie in new[] { _matrix, _matrix, _matrix, _heat, _heat })
        {
            S.Advance(TimeSpan.FromSeconds(1));
            rows.Add(await UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), movie.Id, 6, "Synthetic list review.", at: Now) with { Identity = Identity(movie) }));
        }

        var page = await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Get, $"{Reviews}?startIndex=1&limit=2");
        Assert.Equal(5, page.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal([rows[3].Id, rows[2].Id], Ids(page));
        var since = await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Get, $"{Reviews}?since={rows[2].UpdatedUtc:O}&limit=1");
        Assert.Equal((3, rows[4].Id), (since.GetProperty("TotalRecordCount").GetInt32(), Assert.Single(Ids(since))));
        Assert.Equal(5, (await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Get, $"{Reviews}?searchTerm=synthetic&limit=1")).GetProperty("TotalRecordCount").GetInt32());

        Assert.Equal(5, (await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, Reviews)).GetProperty("TotalRecordCount").GetInt32());
        S.Library.Hide(S.Viewer2, _heat);

        // Within the minute the list still shows Heat's rows; removing one checks it now and is the 404.
        Assert.Equal(5, (await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, $"{Reviews}?startIndex=2")).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(RatingsModeration.NotFoundMessage, await _fx.ProblemAsync(RestrictedAdmin, HttpMethod.Delete, $"{Reviews}/{rows[4].Id:N}", 404));

        S.Advance(RatingsModeration.VisibleCacheTtl + TimeSpan.FromSeconds(1));
        var after = await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, Reviews);
        Assert.Equal([rows[2].Id, rows[1].Id, rows[0].Id], Ids(after));
    }

    // The minute's item checks belong to the admin's account as it was (AccountOf). A change
    // of the account within the minute (its libraries, or any save, which moves RowVersion) makes the next page ask again.
    [Theory]
    [InlineData("EnabledFolders")]
    [InlineData("RowVersion")]
    public async Task TheMinutesItemChecks_AreDropped_WhenTheAdminsAccountChangesWithinIt(string change)
    {
        await _fx.StartAsync();
        var rows = new System.Collections.Generic.List<RatingRecord>();
        foreach (var movie in new[] { _matrix, _matrix, _heat })
        {
            S.Advance(TimeSpan.FromSeconds(1));
            rows.Add(await UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), movie.Id, 6, "Synthetic list review.", at: Now) with { Identity = Identity(movie) }));
        }

        Assert.Equal(3, (await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, Reviews)).GetProperty("TotalRecordCount").GetInt32());
        S.Library.Hide(S.Viewer2, _heat);
        if (change == "EnabledFolders")
        {
            S.Viewer2.SetPreference(PreferenceKind.EnabledFolders, new[] { S.Movies.Id });
        }
        else
        {
            S.Viewer2.OnSavingChanges();
        }

        S.Advance(TimeSpan.FromSeconds(5));
        var after = await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, Reviews);
        Assert.Equal([rows[1].Id, rows[0].Id], Ids(after));
        Assert.Equal(2, after.GetProperty("TotalRecordCount").GetInt32());
    }

    [Fact]
    public async Task RemoveTextOnly_IsADryRunByDefault_ThenTheTokensRealRun_KeepsTheStars_WithASnapshotAndALogRowOfIdsAndCounts()
    {
        await _fx.StartAsync();
        var row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        S.Advance(TimeSpan.FromSeconds(5));

        var dry = await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?reason=Spoiler");
        Assert.True(dry.GetProperty("DryRun").GetBoolean());
        Assert.Equal(("RemoveText", false, true, false, false), (dry.GetProperty("Action").GetString(), dry.GetProperty("RemovesStars").GetBoolean(), dry.GetProperty("RemovesReview").GetBoolean(), dry.GetProperty("DeletesRating").GetBoolean(), dry.GetProperty("ArchiveLines").GetBoolean()));
        Assert.Equal(("viewer", "The Matrix"), (dry.GetProperty("UserName").GetString(), dry.GetProperty("ItemName").GetString()));
        var token = dry.GetProperty("PreviewToken").GetString()!;
        Assert.Matches("^m1-[0-9a-f]{20}$", token);
        Assert.False(dry.TryGetProperty("LogId", out _));
        AssertUnchanged(row);

        // The real run is bound to the dry run: no token a 400, a malformed one a 400, another one a 409; nothing written.
        Assert.Equal(RatingsModeration.PreviewRequiredMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false", 400));
        Assert.Equal(RatingsModeration.PreviewMalformedMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview=p1-00000000000000000000", 400));
        Assert.Equal(RatingsModeration.PreviewChangedMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview=m1-00000000000000000000", 409));

        // A token of the other kind of removal is another token.
        var entry = (await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?textOnly=false")).GetProperty("PreviewToken").GetString()!;
        Assert.NotEqual(token, entry);
        Assert.Equal(409, await StatusAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview={entry}"));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?reason=Rude"));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?reason=1"));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=maybe"));
        Assert.Equal(404, await StatusAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/not-a-guid"));
        AssertUnchanged(row);

        // For real, the token in any case: the review goes, the stars stay, a new UpdatedUtc, no tombstone.
        var done = await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?reason=spoiler&dryRun=false&ifPreview={token.ToUpperInvariant().Replace("M1-", "m1-", StringComparison.Ordinal)}");
        Assert.False(done.GetProperty("DryRun").GetBoolean());
        var after = S.Store.Store.Get(row.Id)!;
        Assert.Equal((8, (string?)null, false, 0), (after.Stars2, after.ReviewText, after.IsSpoiler, after.EditCount));
        Assert.True(after.UpdatedUtc > row.UpdatedUtc);
        Assert.Null(S.Store.Store.GetTombstone(row.Id));

        // One log row, ids and counts only.
        var log = Assert.Single(S.Store.Store.GetModerationLog(0, 10));
        Assert.Equal((done.GetProperty("LogId").GetInt64(), RatingValues.ModerationAction.RemoveText, (Guid?)S.Admin.Id, (Guid?)row.Id, (Guid?)S.Viewer.Id, (Guid?)_matrix.Id), (log.Id, log.Action, log.ActorUserId, log.RatingId, log.TargetUserId, log.ItemId));
        Assert.Equal(("Spoiler", "{\"Stars\":0,\"Reviews\":1,\"Deleted\":0}", (string?)null), (log.Reason, log.Details, log.TextSnapshot));

        // The snapshot before, the only file that still holds the text; the database and its -wal don't.
        var snapshot = done.GetProperty("SafetySnapshot").GetString()!;
        Assert.StartsWith("userratings-moderate-", snapshot, StringComparison.Ordinal);
        Assert.Equal([snapshot], Directory.GetFiles(PreOp).Select(Path.GetFileName));
        Assert.True(Holds(Path.Combine(PreOp, snapshot), Marker));
        await AssertTextGoneAsync(Marker);

        // Again: nothing to remove.
        Assert.Equal(RatingsModeration.NoReviewMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}", 409));
        Assert.Equal(RatingsModeration.NoReviewMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview={token}", 409));
        Assert.Single(S.Store.Store.GetModerationLog(0, 10));
    }

    [Fact]
    public async Task RemoveRatingAndReview_DeletesItWithAModeratorTombstone_AfterAStaleTokensRefusalThatWroteNothing()
    {
        await _fx.StartAsync();
        var row = await RateAsync(S.Viewer, _matrix, 4, "The first words.");
        var dry = await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?textOnly=false");
        Assert.Equal(("RemoveEntry", true, true, true), (dry.GetProperty("Action").GetString(), dry.GetProperty("RemovesStars").GetBoolean(), dry.GetProperty("RemovesReview").GetBoolean(), dry.GetProperty("DeletesRating").GetBoolean()));
        var stale = dry.GetProperty("PreviewToken").GetString()!;

        // The member edits the review after the dry run: the real run removes nothing it didn't show.
        S.Advance(TimeSpan.FromSeconds(3));
        await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, Marker, false, (DateTime?)null, Ct);
        var edited = S.Store.Store.Get(row.Id)!;
        Assert.Equal(RatingsModeration.PreviewChangedMessage, await _fx.ProblemAsync(ApiFixture.ApiKey, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?textOnly=false&dryRun=false&ifPreview={stale}", 409));
        AssertUnchanged(edited);

        var fresh = (await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?textOnly=false")).GetProperty("PreviewToken").GetString()!;
        Assert.NotEqual(stale, fresh);
        var done = await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?textOnly=false&reason=Offensive&dryRun=false&ifPreview={fresh}");

        Assert.Null(S.Store.Store.Get(row.Id));
        var tombstone = S.Store.Store.GetTombstone(row.Id)!;
        Assert.Equal((S.Viewer.Id, _matrix.Id, RatingValues.DeletedBy.Moderator), (tombstone.UserId, tombstone.ItemId, tombstone.DeletedBy));
        var log = Assert.Single(S.Store.Store.GetModerationLog(0, 10));
        Assert.Equal((RatingValues.ModerationAction.RemoveEntry, (Guid?)null, "Offensive", "{\"Stars\":1,\"Reviews\":1,\"Deleted\":1}"), (log.Action, log.ActorUserId, log.Reason, log.Details));
        Assert.StartsWith("userratings-moderate-", done.GetProperty("SafetySnapshot").GetString(), StringComparison.Ordinal);
        await AssertTextGoneAsync(Marker);
        Assert.Equal(404, await StatusAsync(ApiFixture.ApiKey, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?textOnly=false"));

        // The member's page: the rating is gone, the summary with it.
        var item = await _fx.JsonAsync(S.Viewer, HttpMethod.Get, $"/UserRatings/Items/{_matrix.Id:N}");
        Assert.False(item.TryGetProperty("Mine", out var mine) && mine.ValueKind != JsonValueKind.Null);
        Assert.Equal(0, item.GetProperty("Summary").GetProperty("Count").GetInt32());
    }

    [Fact]
    public async Task RemoveTextOnly_OfAReviewWithoutStars_DeletesTheRating_WithATombstone()
    {
        await _fx.StartAsync();
        await S.Service.SetReviewAsync(S.Viewer, _heat.Id, Marker, true, (DateTime?)null, Ct);
        var row = S.Store.Store.GetAttached(S.Viewer.Id, _heat.Id)!;
        Assert.Null(row.Stars2);

        var dry = await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}");
        Assert.Equal(("RemoveText", false, true), (dry.GetProperty("Action").GetString(), dry.GetProperty("RemovesStars").GetBoolean(), dry.GetProperty("DeletesRating").GetBoolean()));
        await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview={dry.GetProperty("PreviewToken").GetString()}");

        Assert.Null(S.Store.Store.Get(row.Id));
        Assert.Equal(RatingValues.DeletedBy.Moderator, S.Store.Store.GetTombstone(row.Id)!.DeletedBy);
        Assert.Equal("{\"Stars\":0,\"Reviews\":1,\"Deleted\":1}", Assert.Single(S.Store.Store.GetModerationLog(0, 10)).Details);
        await AssertTextGoneAsync(Marker);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARemovedReview_OfARatingArchivedOnceAndBack_LeavesNoArchiveLine_AndTheArchiveNoCopy(bool textOnly)
    {
        await _fx.StartAsync();
        var row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        ArchivedAndBack(row);
        KeptArchived();

        var dry = await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?textOnly={textOnly}");
        Assert.True(dry.GetProperty("ArchiveLines").GetBoolean());
        Assert.True(Holds(S.Archive.FilePath, Marker));
        await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?textOnly={textOnly}&dryRun=false&ifPreview={dry.GetProperty("PreviewToken").GetString()}");

        Assert.False(Holds(S.Archive.FilePath, Marker));
        Assert.True(Holds(S.Archive.FilePath, "Another member keeps an archived line."));
        Assert.DoesNotContain(S.Archive.Read().HistoryIds, id => Guid.Parse(id) == row.Id);
        Assert.Empty(ArchivedLinePurges.PendingOf(S.Store.Store.GetMeta(ArchivedLinePurges.Key)));
        Assert.DoesNotContain(Directory.GetFiles(PreOp), f => f.EndsWith(".jsonl", StringComparison.Ordinal));
        await AssertTextGoneAsync(Marker);
    }

    [Fact]
    public async Task AnEditBetweenTheTokensFirstCheckAndTheWrite_IsA409_AndRemovesNothing()
    {
        // The member edits the review while the real run takes its snapshot, after the token's first check: the check
        // under the writer refuses, so the moderator never removes words the dry run didn't show.
        var row = await RateAsync(S.Viewer, _matrix, 4, "The words the moderator saw.");
        var racing = RacingStore.Wrap(S.Store.Store);
        var moderation = new RatingsModeration(racing, S.ItemAccess, S.UserManager.Object, S.Library.Object, S.ArchivedLines, S.Store.Clock, S.Store.Logger);
        var token = (await moderation.RemoveAsync(null, row.Id, textOnly: false, null, dryRun: null, null, Ct)).PreviewToken;

        ((RacingStore)(object)racing).BeforeSnapshot = async () =>
        {
            S.Advance(TimeSpan.FromSeconds(2));
            await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, Marker, false, (DateTime?)null, Ct);
        };
        var refused = await Assert.ThrowsAsync<PluginException>(() => moderation.RemoveAsync(null, row.Id, textOnly: false, UserRatingsModerationReason.Spam, dryRun: false, token, Ct));

        Assert.Equal((PluginErrorKind.Conflict, RatingsModeration.PreviewChangedMessage), (refused.Kind, refused.Message));
        var now = S.Store.Store.Get(row.Id)!;
        Assert.Equal(((int?)8, Marker), (now.Stars2, now.ReviewText));
        Assert.Null(S.Store.Store.GetTombstone(row.Id));
        Assert.Empty(S.Store.Store.GetModerationLog(0, 10));
    }

    // The token covers the row's item and its detach, which a move or a detach changes
    // with no new UpdatedUtc; either between the dry run and the real run is the 409, with nothing written and no snapshot.
    [Theory]
    [InlineData("detach")]
    [InlineData("move")]
    public async Task ADetachOrAMove_BetweenTheDryRunAndTheRealRun_IsA409_AndRemovesNothing(string what)
    {
        await _fx.StartAsync();
        var row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        var token = (await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Delete, $"{Reviews}/{row.Id:N}")).GetProperty("PreviewToken").GetString();
        var changed = what == "detach"
            ? row with { DetachedUtc = Now, DetachReason = RatingValues.DetachReason.Missing }
            : row with { Identity = Identity(_heat) };
        await S.Store.Store.UpsertAsync(changed, Ct);
        Assert.Equal(row.UpdatedUtc, S.Store.Store.Get(row.Id)!.UpdatedUtc);

        Assert.Equal(RatingsModeration.PreviewChangedMessage, await _fx.ProblemAsync(ApiFixture.ApiKey, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview={token}", 409));

        Assert.Equal(Marker, S.Store.Store.Get(row.Id)!.ReviewText);
        Assert.Empty(S.Store.Store.GetModerationLog(0, 10));
        Assert.False(Directory.Exists(PreOp));
    }

    // The moderate pre-operation snapshot, the one copy that keeps a removed review, goes with
    // the first daily snapshot once it is older than 7 days, and not before.
    [Fact]
    public async Task TheModeratePreOpSnapshot_ExpiresWithTheFirstDailySnapshotAfter7Days()
    {
        await _fx.StartAsync();
        var row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        var token = (await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}")).GetProperty("PreviewToken").GetString();
        var done = await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview={token}");
        var copy = Path.Combine(PreOp, done.GetProperty("SafetySnapshot").GetString()!);
        Assert.True(Holds(copy, Marker));
        await AssertTextGoneAsync(Marker);

        S.Advance(TimeSpan.FromDays(6));
        var daily = (await S.Store.Database.SnapshotAsync(7, Ct))!;
        Assert.True(File.Exists(copy), "kept while it is 7 days old or less");
        Assert.False(Holds(daily, Marker));

        S.Advance(TimeSpan.FromDays(2));
        await S.Store.Database.SnapshotAsync(7, Ct);
        Assert.False(File.Exists(copy));
    }

    // The moderate snapshot is reused only while nothing was written since and its file is still
    // there; one the rotation or its expiry deleted is never named as the one before.
    [Fact]
    public async Task TheModerateSnapshot_IsReusedWhileNothingWasWritten_ButNeverOnceItsFileIsGone()
    {
        await RateAsync(S.Viewer, _matrix, 4, Marker);
        var moderation = new RatingsModeration(S.Store.Store, S.ItemAccess, S.UserManager.Object, S.Library.Object, S.ArchivedLines, S.Store.Clock, S.Store.Logger);

        var first = (await moderation.SnapshotAsync(Ct))!;
        S.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(first, await moderation.SnapshotAsync(Ct));

        File.Delete(Path.Combine(PreOp, first));
        var second = (await moderation.SnapshotAsync(Ct))!;

        Assert.NotEqual(first, second);
        Assert.True(File.Exists(Path.Combine(PreOp, second)));
    }

    [Fact]
    public async Task TheRealRun_WhileTheDataIsReadOnly_Is503_AndTheDryRunStillAnswers()
    {
        var row = StoreFixture.Movie(S.Viewer.Id, _matrix.Id, 7, Marker) with { Identity = Identity(_matrix) };
        await S.Store.Store.UpsertAsync(row, Ct);
        await S.Store.Database.WriteAsync((c, t) => { StoreFixture.Execute(c, t, "PRAGMA user_version = 99;"); return 0; }, Ct);
        S.Store.Reopen();
        await _fx.StartAsync(S.NewService());

        var token = (await _fx.JsonAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}")).GetProperty("PreviewToken").GetString()!;
        Assert.Equal(SqliteMessages.NewerVersion, await _fx.ProblemAsync(S.Admin, HttpMethod.Delete, $"{Reviews}/{row.Id:N}?dryRun=false&ifPreview={token}", 503));
        Assert.Equal(Marker, S.Store.Store.Get(row.Id)!.ReviewText);
        Assert.False(Directory.Exists(PreOp));
        Assert.Equal(1, (await _fx.JsonAsync(S.Admin, HttpMethod.Get, Reviews)).GetProperty("TotalRecordCount").GetInt32());
    }

    [Fact]
    public async Task TheLog_PagesNewestFirst_FiltersByAction_AndNamesWhatTheCallerMaySee()
    {
        await _fx.StartAsync();
        var inMovies = await RateAsync(S.Viewer, _matrix, 4, "One.");
        var inMovies2 = await RateAsync(S.Viewer, _elsewhere, 3, "Two.");
        await RemoveAsync(S.Admin, inMovies.Id, textOnly: true);
        S.Advance(TimeSpan.FromSeconds(1));
        await RemoveAsync(ApiFixture.ApiKey, inMovies2.Id, textOnly: false);
        await S.Store.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = Now, Action = RatingValues.ModerationAction.UserDeleted, TargetUserId = Guid.NewGuid(), Details = "{\"Ratings\":0}" }, Ct);

        var all = await _fx.JsonAsync(S.Admin, HttpMethod.Get, Log);
        Assert.Equal(3, all.GetProperty("TotalRecordCount").GetInt32());
        var rows = all.GetProperty("Items").EnumerateArray().ToList();
        Assert.Equal(["UserDeleted", "RemoveEntry", "RemoveText"], rows.Select(r => r.GetProperty("Action").GetString()));
        Assert.False(rows[1].TryGetProperty("ActorUserId", out _));
        Assert.Equal(("admin", "viewer", "The Matrix"), (rows[2].GetProperty("ActorName").GetString(), rows[2].GetProperty("TargetName").GetString(), rows[2].GetProperty("ItemName").GetString()));
        Assert.Equal("Ronin", rows[1].GetProperty("ItemName").GetString());

        // A restricted admin gets no row about an item they can't open (TheLog_ForASignedInAdmin_*).
        var restricted = await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, Log);
        Assert.Equal(2, restricted.GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(["UserDeleted", "RemoveText"], restricted.GetProperty("Items").EnumerateArray().Select(r => r.GetProperty("Action").GetString()));
        Assert.Equal("The Matrix", restricted.GetProperty("Items")[1].GetProperty("ItemName").GetString());

        var removals = await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Get, $"{Log}?action=removetext");
        Assert.Equal(1, removals.GetProperty("TotalRecordCount").GetInt32());
        var second = await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Get, $"{Log}?startIndex=1&limit=1");
        Assert.Equal((3, "RemoveEntry"), (second.GetProperty("TotalRecordCount").GetInt32(), Assert.Single(second.GetProperty("Items").EnumerateArray()).GetProperty("Action").GetString()));
        Assert.Equal(RatingsModeration.ActionMessage, await _fx.ProblemAsync(S.Admin, HttpMethod.Get, $"{Log}?action=Rename", 400));
        Assert.Equal(400, await StatusAsync(S.Admin, HttpMethod.Get, $"{Log}?limit=500"));

        // No row holds a word of a review or a name.
        foreach (var row in S.Store.Store.GetModerationLog(0, 10))
        {
            Assert.Null(row.TextSnapshot);
            Assert.DoesNotContain("viewer", (row.Details ?? string.Empty) + row.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The log a signed-in admin gets has no row about an item they can't open, neither its id
    // nor a Details id (a Relink's FromItemId), and its total counts only their rows; a row about no item stays, and one
    // about an item the library no longer has goes only to an admin with every library. An API key gets every row.
    [Fact]
    public async Task TheLog_ForASignedInAdmin_LeavesOutEveryRowAboutAnItemTheyCantOpen_FromThePageAndTheTotal()
    {
        await _fx.StartAsync();
        var gone = Guid.NewGuid();
        async Task AppendAsync(string action, Guid? item, string? details = null)
        {
            S.Advance(TimeSpan.FromSeconds(1));
            await S.Store.Store.AppendModerationLogAsync(new ModerationLogRecord { AtUtc = Now, Action = action, RatingId = item is null ? null : Guid.NewGuid(), ItemId = item, TargetUserId = S.Viewer.Id, Details = details }, Ct);
        }

        await AppendAsync(RatingValues.ModerationAction.RemoveText, _elsewhere.Id);
        await AppendAsync(RatingValues.ModerationAction.Relink, _heat.Id, $"{{\"FromItemId\":\"{_elsewhere.Id:N}\"}}");
        await AppendAsync(RatingValues.ModerationAction.Relink, _heat.Id, $"{{\"FromItemId\":\"{_matrix.Id:N}\"}}");
        await AppendAsync(RatingValues.ModerationAction.Detach, gone, "{\"HeldSinceUtc\":\"2026-10-01T00:00:00.000Z\",\"MassDetach\":false}");
        await AppendAsync(RatingValues.ModerationAction.UserDeleted, null, "{\"Ratings\":0}");
        await AppendAsync(RatingValues.ModerationAction.Relink, _heat.Id, "{not json");

        // Movies only, not every library: the relink between two titles they open and the user's row; nothing of Ronin or
        // of the gone title, in the page, the total or the text.
        using (var response = await _fx.SendAsync(RestrictedAdmin, HttpMethod.Get, Log))
        {
            var text = await response.Content.ReadAsStringAsync(Ct);
            using var json = JsonDocument.Parse(text);
            Assert.Equal(2, json.RootElement.GetProperty("TotalRecordCount").GetInt32());
            Assert.Equal(["UserDeleted", "Relink"], json.RootElement.GetProperty("Items").EnumerateArray().Select(r => r.GetProperty("Action").GetString()));
            foreach (var hidden in new[] { _elsewhere.Id, gone })
            {
                Assert.DoesNotContain(hidden.ToString("N"), text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(hidden.ToString("D"), text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("Ronin", text, StringComparison.Ordinal);
        }

        var second = await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, $"{Log}?startIndex=1&limit=1");
        Assert.Equal((2, "Relink"), (second.GetProperty("TotalRecordCount").GetInt32(), Assert.Single(second.GetProperty("Items").EnumerateArray()).GetProperty("Action").GetString()));
        Assert.Equal(1, (await _fx.JsonAsync(RestrictedAdmin, HttpMethod.Get, $"{Log}?action=relink")).GetProperty("TotalRecordCount").GetInt32());
        Assert.Equal(0, (await _fx.JsonAsync(new ApiFixture.AsCaller("admin", Guid.NewGuid()), HttpMethod.Get, Log)).GetProperty("TotalRecordCount").GetInt32());

        // Every library: every row, the gone title's with no name; the API key the same.
        var all = await _fx.JsonAsync(S.Admin, HttpMethod.Get, Log);
        Assert.Equal(6, all.GetProperty("TotalRecordCount").GetInt32());
        var detach = all.GetProperty("Items").EnumerateArray().Single(r => r.GetProperty("Action").GetString() == "Detach");
        Assert.False(detach.TryGetProperty("ItemName", out _));
        Assert.Equal(6, (await _fx.JsonAsync(ApiFixture.ApiKey, HttpMethod.Get, Log)).GetProperty("TotalRecordCount").GetInt32());

        // Both libraries, but not every library: the gone title's row and the unreadable one go; the rest stay.
        S.Admin.SetPermission(PermissionKind.EnableAllFolders, false);
        var both = await _fx.JsonAsync(S.Admin, HttpMethod.Get, Log);
        Assert.Equal(["UserDeleted", "Relink", "Relink", "RemoveText"], both.GetProperty("Items").EnumerateArray().Select(r => r.GetProperty("Action").GetString()));
    }

    // ------------------------------------------------------------------------------------------------ a member's own

    [Fact]
    public async Task AMembersOwnDelete_OfARatingArchivedOnceAndBack_TakesItsArchiveLinesToo_AndNeverRewritesForOneNeverArchived()
    {
        await _fx.StartAsync();
        var archived = await RateAsync(S.Viewer, _matrix, 4, Marker);
        var never = await RateAsync(S.Viewer, _heat, 3, "Never archived.");
        ArchivedAndBack(archived);
        KeptArchived();
        var before = File.GetLastWriteTimeUtc(S.Archive.FilePath);
        var bytes = await File.ReadAllBytesAsync(S.Archive.FilePath, Ct);

        Assert.Equal(204, await StatusAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Items/{_heat.Id:N}/Mine"));
        Assert.Null(S.Store.Store.Get(never.Id));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(S.Archive.FilePath, Ct));
        Assert.Equal(before, File.GetLastWriteTimeUtc(S.Archive.FilePath));

        Assert.Equal(204, await StatusAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Items/{_matrix.Id:N}/Mine"));
        Assert.Equal(RatingValues.DeletedBy.User, S.Store.Store.GetTombstone(archived.Id)!.DeletedBy);
        Assert.False(Holds(S.Archive.FilePath, Marker));
        Assert.True(Holds(S.Archive.FilePath, "Another member keeps an archived line."));
        Assert.Empty(ArchivedLinePurges.PendingOf(S.Store.Store.GetMeta(ArchivedLinePurges.Key)));
        Assert.False(Directory.Exists(PreOp));
    }

    [Theory]
    [InlineData("own row")]
    [InlineData("clear review")]
    [InlineData("clear stars")]
    [InlineData("clear the only review")]
    public async Task AMembersOtherRemovals_OfARatingArchivedOnceAndBack(string how)
    {
        await _fx.StartAsync();
        RatingRecord row;
        if (how == "clear the only review")
        {
            // A review without stars: clearing it deletes the rating, which takes its lines too.
            await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, Marker, false, (DateTime?)null, Ct);
            row = S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!;
            Assert.Null(row.Stars2);
        }
        else
        {
            row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        }

        ArchivedAndBack(row);
        var path = how switch
        {
            "own row" => $"/UserRatings/Mine/{row.Id:N}",
            "clear review" or "clear the only review" => $"/UserRatings/Items/{_matrix.Id:N}/Mine/Review",
            _ => $"/UserRatings/Items/{_matrix.Id:N}/Mine/Rating"
        };

        using var response = await _fx.SendAsync(S.Viewer, HttpMethod.Delete, path);
        Assert.True(response.IsSuccessStatusCode, how);

        // The review gone takes its lines; stars cleared from a review that stays take nothing.
        Assert.Equal(how != "clear stars", !Holds(S.Archive.FilePath, Marker));
        Assert.Equal(how == "clear stars", S.Store.Store.Get(row.Id)?.ReviewText == Marker);
        Assert.Equal(how is "own row" or "clear the only review", S.Store.Store.Get(row.Id) is null);
    }

    [Fact]
    public async Task AMembersDelete_WhoseArchiveRewriteFails_IsPending_AndTheNextRunTakesTheLines()
    {
        await _fx.StartAsync();
        var row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        ArchivedAndBack(row);
        var blocker = Directory.CreateDirectory(S.Archive.FilePath + ".tmp");

        Assert.Equal(204, await StatusAsync(S.Viewer, HttpMethod.Delete, $"/UserRatings/Items/{_matrix.Id:N}/Mine"));
        Assert.Null(S.Store.Store.Get(row.Id));
        Assert.True(Holds(S.Archive.FilePath, Marker));
        Assert.Equal([row.Id], ArchivedLinePurges.PendingOf(S.Store.Store.GetMeta(ArchivedLinePurges.Key)));

        blocker.Delete();
        var run = await S.ArchivedLines.RunAsync(Ct);

        Assert.Equal((1, 1, 0), ((int)run["Pending"]!, (int)run["Done"]!, (int)run["Failed"]!));
        Assert.False(Holds(S.Archive.FilePath, Marker));
        Assert.Empty(ArchivedLinePurges.PendingOf(S.Store.Store.GetMeta(ArchivedLinePurges.Key)));
    }

    // A member's delete, review clear or review edit of a review never archived leaves no byte
    // of the old words in the database or its -wal once the checkpoint it asked for is done, as a moderator's removal does.
    [Theory]
    [InlineData("delete")]
    [InlineData("own row")]
    [InlineData("clear review")]
    [InlineData("clear the only review")]
    [InlineData("edit")]
    public async Task AMembersDeleteClearOrEdit_OfAReviewNeverArchived_LeavesNoByteInTheDatabaseOrItsWal(string how)
    {
        await _fx.StartAsync();
        for (var i = 0; i < 20; i++)
        {
            await UpsertAsync(StoreFixture.Movie(S.Viewer2.Id, Guid.NewGuid(), 6, "Synthetic padding review " + i + ".", at: Now));
        }

        RatingRecord row;
        if (how == "clear the only review")
        {
            await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, Marker, false, (DateTime?)null, Ct);
            row = S.Store.Store.GetAttached(S.Viewer.Id, _matrix.Id)!;
        }
        else
        {
            row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        }

        Assert.True(await S.Store.Store.WaitForCheckpointAsync(Ct));
        Assert.True(Holds(S.Store.File + "-wal", Marker), "the -wal holds the review before");

        if (how == "edit")
        {
            await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, "Other words entirely.", false, (DateTime?)null, Ct);
            Assert.Equal("Other words entirely.", S.Store.Store.Get(row.Id)!.ReviewText);
        }
        else
        {
            var path = how switch
            {
                "delete" => $"/UserRatings/Items/{_matrix.Id:N}/Mine",
                "own row" => $"/UserRatings/Mine/{row.Id:N}",
                _ => $"/UserRatings/Items/{_matrix.Id:N}/Mine/Review"
            };
            using var response = await _fx.SendAsync(S.Viewer, HttpMethod.Delete, path);
            Assert.True(response.IsSuccessStatusCode, how);
            Assert.Null(S.Store.Store.Get(row.Id)?.ReviewText);
        }

        await AssertTextGoneAsync(Marker);
        Assert.Empty(ArchivedLinePurges.PendingOf(S.Store.Store.GetMeta(ArchivedLinePurges.Key)));
    }

    // Other words in place of a review of a rating archived once and back take the
    // archive's earlier lines of it, as a removed review's do; the same words again, or only the spoiler flag, take nothing.
    [Fact]
    public async Task AMembersReviewEdit_OfARatingArchivedOnceAndBack_TakesItsArchiveLines_AndTheSameWordsTakeNothing()
    {
        await _fx.StartAsync();
        S.Configuration.AllowSpoilerFlag = true;
        var row = await RateAsync(S.Viewer, _matrix, 4, Marker);
        ArchivedAndBack(row);
        KeptArchived();
        var bytes = await File.ReadAllBytesAsync(S.Archive.FilePath, Ct);

        await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, Marker, true, (DateTime?)null, Ct);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(S.Archive.FilePath, Ct));
        Assert.Empty(ArchivedLinePurges.PendingOf(S.Store.Store.GetMeta(ArchivedLinePurges.Key)));

        await S.Service.SetReviewAsync(S.Viewer, _matrix.Id, "Other words entirely.", false, (DateTime?)null, Ct);

        Assert.Equal("Other words entirely.", S.Store.Store.Get(row.Id)!.ReviewText);
        Assert.False(Holds(S.Archive.FilePath, Marker));
        Assert.True(Holds(S.Archive.FilePath, "Another member keeps an archived line."));
        Assert.Empty(ArchivedLinePurges.PendingOf(S.Store.Store.GetMeta(ArchivedLinePurges.Key)));
        await AssertTextGoneAsync(Marker);
        Assert.False(Directory.Exists(PreOp));
    }

    // ------------------------------------------------------------------------------------------------ helpers

    private string PreOp => Path.Combine(S.Store.Snapshots, "pre-op");

    private DateTime Now => S.Store.Clock.GetUtcNow().UtcDateTime;

    // Jellyfin writes ids without hyphens, which JsonElement.GetGuid doesn't read.
    private static Guid[] Ids(JsonElement page) => [.. page.GetProperty("Items").EnumerateArray().Select(e => Guid.Parse(e.GetProperty("RatingId").GetString()!))];

    private static JsonElement Entry(JsonElement page, Guid id) => page.GetProperty("Items").EnumerateArray().Single(e => Guid.Parse(e.GetProperty("RatingId").GetString()!) == id);

    private static bool Holds(string path, string text)
    {
        var bytes = StoreFixture.ReadOpenFile(path);
        var needle = Encoding.UTF8.GetBytes(text);
        return bytes.AsSpan().IndexOf(needle) >= 0;
    }

    private Kit.Library.IdentitySnapshot Identity(Movie movie) => RatingsLibraryConsumer.Normalize(Kit.Library.ItemIdentity.Capture(movie, S.Library.Object));

    private async Task<RatingRecord> RateAsync(User user, Movie movie, decimal stars, string? text = null)
    {
        await S.Service.SetStarsAsync(user, movie.Id, stars, Ct);
        if (text is not null)
        {
            await S.Service.SetReviewAsync(user, movie.Id, text, false, (DateTime?)null, Ct);
        }

        return S.Store.Store.GetAttached(user.Id, movie.Id)!;
    }

    private async Task<RatingRecord> UpsertAsync(RatingRecord row)
    {
        await S.Store.Store.UpsertAsync(row, Ct);
        return S.Store.Store.Get(row.Id)!;
    }

    private async Task<int> StatusAsync(object? caller, HttpMethod method, string path)
    {
        using var response = await _fx.SendAsync(caller, method, path);
        return (int)response.StatusCode;
    }

    private async Task RemoveAsync(object caller, Guid ratingId, bool textOnly)
    {
        var token = (await _fx.JsonAsync(caller, HttpMethod.Delete, $"{Reviews}/{ratingId:N}?textOnly={textOnly}")).GetProperty("PreviewToken").GetString();
        await _fx.JsonAsync(caller, HttpMethod.Delete, $"{Reviews}/{ratingId:N}?textOnly={textOnly}&dryRun=false&ifPreview={token}");
    }

    // The rating as the archive keeps it after an archive and a reattach: its archived line, now history.
    private void ArchivedAndBack(RatingRecord row)
    {
        S.Archive.Append([ArchivedRating.ToArchive(row with { DetachedUtc = Now.AddDays(-100), DetachReason = RatingValues.DetachReason.Removed }, userKnown: true)]);
        S.Archive.MarkReattached([row.Id.ToString("D")]);
        Assert.DoesNotContain(S.Archive.Read().Records, r => r.Id == row.Id.ToString("D"));
    }

    // Another member's rating still in the archive (a record, which a rewrite keeps).
    private void KeptArchived()
    {
        var kept = StoreFixture.Movie(S.Viewer2.Id, Guid.NewGuid(), 7, "Another member keeps an archived line.", at: Now.AddDays(-200)) with { DetachedUtc = Now.AddDays(-100), DetachReason = RatingValues.DetachReason.Removed };
        S.Archive.Append([ArchivedRating.ToArchive(kept, userKnown: true)]);
        Assert.Single(S.Archive.Read().Records);
    }

    private void AssertUnchanged(RatingRecord row)
    {
        var now = S.Store.Store.Get(row.Id)!;
        Assert.Equal((row.Stars2, row.ReviewText, row.UpdatedUtc), (now.Stars2, now.ReviewText, now.UpdatedUtc));
        Assert.Null(S.Store.Store.GetTombstone(row.Id));
        Assert.DoesNotContain(S.Store.Store.GetModerationLog(0, 50), l => l.RatingId == row.Id);
        Assert.False(Directory.Exists(PreOp));
    }

    /// <summary>
    /// The store, forwarded, with a hook run once before the next pre-operation snapshot: a write that lands between a
    /// removal's first token check and its unit of work.
    /// </summary>
    internal class RacingStore : DispatchProxy
    {
        private IRatingsStore _inner = null!;

        public Func<Task>? BeforeSnapshot { get; set; }

        public static IRatingsStore Wrap(IRatingsStore inner)
        {
            var proxy = Create<IRatingsStore, RacingStore>();
            ((RacingStore)(object)proxy)._inner = inner;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IRatingsStore.PreOpSnapshotAsync) && BeforeSnapshot is { } before)
            {
                BeforeSnapshot = null;
                before().GetAwaiter().GetResult();
            }

            try
            {
                return targetMethod.Invoke(_inner, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    // The removed text is in neither the database file nor its -wal, once the checkpoint the removal asked for is done (it
    // runs in the background).
    private async Task AssertTextGoneAsync(string text)
    {
        Assert.True(await S.Store.Store.WaitForCheckpointAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct), "the checkpoint completed");
        Assert.False(Holds(S.Store.File, text));
        var wal = S.Store.File + "-wal";
        Assert.True(!File.Exists(wal) || new FileInfo(wal).Length == 0, "the -wal was checkpointed");
        Assert.True(!File.Exists(wal) || !Holds(wal, text));
    }
}
