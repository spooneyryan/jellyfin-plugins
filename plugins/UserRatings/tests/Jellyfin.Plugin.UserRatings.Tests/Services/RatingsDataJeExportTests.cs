using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// The ratings as Jellyfin Enhanced 12.14's <c>reviews.json</c>: JE's shape and keys
/// (the whole TMDb key in <c>TmdbId</c>, as JE 12.14 writes it), what is left out and why, the round trip through the JE
/// import, and who may export.
/// </summary>
public sealed partial class RatingsDataTests
{
    private static readonly Regex JeTime = new(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}Z$", RegexOptions.CultureInvariant);

    [Fact]
    public async Task TheJeExport_IsJe1214sShape_MoviesSeriesSeasonsAndEpisodes_KeyedAsJeKeysThem()
    {
        var series = _s.Library.AddSeries("Breaking Bad", null, ("Tmdb", "1396"));
        var season = _s.Library.AddSeason(series, 1);
        var episode = _s.Library.AddEpisode(series, season, 2);
        foreach (var item in new BaseItem[] { series, season, episode })
        {
            _s.Library.SetTopParent(item, _s.Movies.PhysicalFolderIds[0]);
        }

        var at = StoreFixture.T0.AddDays(-2).AddMilliseconds(789);
        WriteJe(
            Entry(_s.Viewer.Id, "1396", 5, string.Empty, at, "tv"),
            Entry(_s.Viewer.Id, "1396", null, "A slow start.", at, "tv", ":s1"),
            Entry(_s.Viewer.Id, "1396", 4, "The best one.", at, "tv", ":s1:e2"));
        Assert.Equal(3, N(await JeAsync(dryRun: false), "Created"));
        var movie = _s.AddMovie(_s.Movies, "Fight Club", "550");
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer2.Id, movie.Id, 7, "Rules one and two.", "550", at), Ct);

        var export = JeData().ExportJellyfinEnhanced(_s.Admin.Id);

        using var json = JsonDocument.Parse(export.Bytes);
        var reviews = json.RootElement.GetProperty("Reviews");
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        var v = _s.Viewer.Id.ToString("N"); var w = _s.Viewer2.Id.ToString("N");
        Assert.Equal(new[] { $"{v}:tv:1396", $"{v}:tv:1396:s1", $"{v}:tv:1396:s1:e2", $"{w}:movie:550" }.Order(StringComparer.Ordinal), reviews.EnumerateObject().Select(p => p.Name));
        foreach (var entry in reviews.EnumerateObject())
        {
            // JE 12.14's fields, in its order; the key's whole TMDb part in TmdbId; its times.
            Assert.Equal(["UserId", "TmdbId", "MediaType", "Content", "Rating", "CreatedAt", "UpdatedAt"], entry.Value.EnumerateObject().Select(p => p.Name));
            var parts = entry.Name.Split(':', 3);
            Assert.Equal(parts[0], entry.Value.GetProperty("UserId").GetString());
            Assert.Equal(parts[1], entry.Value.GetProperty("MediaType").GetString());
            Assert.Equal(parts[2], entry.Value.GetProperty("TmdbId").GetString());
            Assert.Matches(JeTime, entry.Value.GetProperty("CreatedAt").GetString()!);
            Assert.Matches(JeTime, entry.Value.GetProperty("UpdatedAt").GetString()!);
        }

        // Stars as JE's rating, null for a review without stars; "" for stars without a review.
        Assert.Equal((5.0, string.Empty), (reviews.GetProperty($"{v}:tv:1396").GetProperty("Rating").GetDouble(), reviews.GetProperty($"{v}:tv:1396").GetProperty("Content").GetString()));
        Assert.Equal(JsonValueKind.Null, reviews.GetProperty($"{v}:tv:1396:s1").GetProperty("Rating").ValueKind);
        Assert.Equal("A slow start.", reviews.GetProperty($"{v}:tv:1396:s1").GetProperty("Content").GetString());
        Assert.Equal(4.0, reviews.GetProperty($"{v}:tv:1396:s1:e2").GetProperty("Rating").GetDouble());
        Assert.Equal((3.5, "Rules one and two."), (reviews.GetProperty($"{w}:movie:550").GetProperty("Rating").GetDouble(), reviews.GetProperty($"{w}:movie:550").GetProperty("Content").GetString()));
        Assert.Equal("2026-10-17T03:20:00.7890000Z", reviews.GetProperty($"{w}:movie:550").GetProperty("UpdatedAt").GetString());
        Assert.Equal((4, 0, 0), (export.Reviews, export.SkippedTotal, export.MergedCopies));
    }

    [Fact]
    public async Task TheJeExport_LeavesOutWhatJeCantShow_EachCountedByItsReason_AndTakesATitlesIdsAsTheyAreNow()
    {
        var plain = _s.AddMovie(_s.Movies, "Home Video");
        var later = _s.AddMovie(_s.Movies, "Identified Later", "601");
        var a = _s.AddMovie(_s.Movies, "Heat", "949");
        var b = _s.AddMovie(_s.Movies2, "Heat", "949");
        var older = StoreFixture.T0.AddDays(-2);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, plain.Id, 6, null, tmdb: string.Empty), Ct);
        // The row's own identity has no TMDb id, the item has one now: the item's counts.
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, later.Id, 6, null, tmdb: string.Empty), Ct);
        await Store.UpsertAsync(StoreFixture.Track(_s.Viewer.Id, Guid.NewGuid(), Guid.NewGuid()), Ct);
        await Store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid(), 6, "A former member's.", "603"), Ct);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, Guid.NewGuid(), 6, null, "604") with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }, Ct);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer2.Id, a.Id, 4, "The first copy.", "949", older), Ct);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer2.Id, b.Id, 9, "The second copy, rated later.", "949"), Ct);
        // A JE review stored detached (JE's own) stays in: imported back it is AlreadyImported.
        WriteJe(Entry(_s.Viewer.Id, "777", 3, "A film this library lacks.", StoreFixture.T0.AddDays(-1)));
        Assert.Equal(1, N(await JeAsync(dryRun: false), "StoredDetached"));

        // Every title (the Movies 2 copy is one viewer2 can't open, which the default leaves out: below).
        var export = JeData().ExportJellyfinEnhanced(_s.Admin.Id, leaveOutRestricted: false);

        Assert.Equal(new Dictionary<string, int>
        {
            [JellyfinEnhancedExportWriter.Detached] = 1,
            [JellyfinEnhancedExportWriter.FormerMember] = 1,
            [JellyfinEnhancedExportWriter.NoTmdbId] = 1,
            [JellyfinEnhancedExportWriter.NotInJe] = 1,
        }, export.Skipped);
        Assert.Equal((3, 4, 1), (export.Reviews, export.SkippedTotal, export.MergedCopies));
        var reviews = JsonDocument.Parse(export.Bytes).RootElement.GetProperty("Reviews");
        Assert.Equal(9 / 2.0, reviews.GetProperty(_s.Viewer2.Id.ToString("N") + ":movie:949").GetProperty("Rating").GetDouble());
        Assert.True(reviews.TryGetProperty(_s.Viewer.Id.ToString("N") + ":movie:601", out _));
        Assert.True(reviews.TryGetProperty(_s.Viewer.Id.ToString("N") + ":movie:777", out _));

        // By default the copy in Movies 2 is left out (RestrictedLibrary), so nothing is merged and the first copy's is written.
        var byDefault = JeData().ExportJellyfinEnhanced(_s.Admin.Id);
        Assert.Equal(1, byDefault.Skipped[JellyfinEnhancedExportWriter.RestrictedLibrary]);
        Assert.Equal((3, 5, 0), (byDefault.Reviews, byDefault.SkippedTotal, byDefault.MergedCopies));
        Assert.Equal(4 / 2.0, JsonDocument.Parse(byDefault.Bytes).RootElement.GetProperty("Reviews").GetProperty(_s.Viewer2.Id.ToString("N") + ":movie:949").GetProperty("Rating").GetDouble());
    }

    [Fact]
    public async Task TheJeExport_ImportedBack_IsAlreadyImported_OrChangesNothing()
    {
        var series = _s.Library.AddSeries("Breaking Bad", null, ("Tmdb", "1396"));
        var season = _s.Library.AddSeason(series, 1);
        var episode = _s.Library.AddEpisode(series, season, 1);
        foreach (var item in new BaseItem[] { series, season, episode })
        {
            _s.Library.SetTopParent(item, _s.Movies.PhysicalFolderIds[0]);
        }

        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var heat = _s.AddMovie(_s.Movies, "Heat", "949");
        WriteJe(
            Entry(_s.Viewer.Id, "603", 4.5, "Still holds up.", Now.AddDays(-3)),
            Entry(_s.Viewer.Id, "1396", 4, "A strong pilot.", Now.AddDays(-3), "tv", ":s1:e1"),
            Entry(_s.Viewer.Id, "1396", null, "Season one.", Now.AddDays(-3), "tv", ":s1"),
            Entry(_s.Viewer.Id, "1396", 3, string.Empty, Now.AddDays(-3), "tv", ":s2:e9"),
            Entry(_s.Viewer2.Id, "603", 2.3, string.Empty, Now.AddDays(-3)));
        var first = await JeAsync(dryRun: false);
        Assert.Equal((4, 1), (N(first, "Created"), N(first, "StoredDetached")));
        // Ratings made here, not by JE: one a member then edits on an imported title, one on another title.
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer2.Id, heat.Id, 8, "Made here.", "949", Now.AddDays(-1)), Ct);
        var edited = Store.GetAttached(_s.Viewer.Id, matrix.Id)!;
        await Store.UpsertAsync(edited with { Stars2 = 6, UpdatedUtc = Now.AddHours(-1) }, Ct);
        var before = Store.ReadTables();

        var export = JeData().ExportJellyfinEnhanced(_s.Admin.Id);
        await File.WriteAllBytesAsync(JeReviews, export.Bytes, Ct);
        var dry = await JeAsync();
        var real = await JeAsync(dryRun: false);

        Assert.Equal(6, export.Reviews);
        Assert.Equal((6, 0, 0, 0), (N(dry, "Total"), N(dry, "WouldCreate"), N(dry, "WouldUpdate"), N(dry, "StoredDetached")));
        Assert.Equal(0, N(dry, "KeyMismatch") + N(dry, "Invalid") + N(dry, "UnknownUser"));
        // The four JE gave and the detached one: AlreadyImported (the edited one SkippedNewer); Heat: the member's own, kept.
        Assert.Equal(4, N(dry, "AlreadyImported"));
        Assert.Equal(1, N(dry, "SkippedNewer"));
        Assert.Equal(1, N(dry, "ConflictKeptLocal"));
        Assert.Equal((0, 0), (N(real, "Created"), N(real, "Updated")));
        var after = Store.ReadTables();
        Assert.Equal(before.Ratings.OrderBy(r => r.Id).Select(r => (r.Id, r.Stars2, r.ReviewText, r.UpdatedUtc)), after.Ratings.OrderBy(r => r.Id).Select(r => (r.Id, r.Stars2, r.ReviewText, r.UpdatedUtc)));
    }

    [Fact]
    public async Task TheJeExport_IsForAdminsWithEveryLibrary_AndWritesNothingOnTheServer()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, movie.Id, 8, "Fine.", "603"), Ct);
        var restricted = ServicesFixture.NewUser("restricted");
        restricted.SetPermission(PermissionKind.IsAdministrator, true);
        restricted.SetPermission(PermissionKind.EnableAllFolders, false);
        _s.Users.Add(restricted);
        var root = _s.Store.Directory.Path;
        string[] Files() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.Contains("userratings.db", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        var files = Files();
        var generation = Store.Generation;

        Assert.Equal(RatingsData.AllLibrariesCode, Assert.Throws<PluginException>(() => JeData().ExportJellyfinEnhanced(restricted.Id)).Code);
        var byKey = JeData().ExportJellyfinEnhanced(null);
        var byAdmin = JeData().ExportJellyfinEnhanced(_s.Admin.Id);

        Assert.Equal(byKey.Bytes, byAdmin.Bytes);
        Assert.Equal(1, byAdmin.Reviews);
        Assert.Equal(files, Files());
        Assert.Equal(generation, Store.Generation);
        Assert.False(File.Exists(JeReviews));
    }

    [Fact]
    public async Task TheJeExportRoute_SendsReviewsJson_WithItsCountsInItsHeaders_NoStore_NeverAnonymous()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var plain = _s.AddMovie(_s.Movies, "Home Video");
        var plain2 = _s.AddMovie(_s.Movies, "Another Home Video");
        var a = _s.AddMovie(_s.Movies, "Heat", "949");
        var b = _s.AddMovie(_s.Movies2, "Heat", "949");
        var longer = _s.AddMovie(_s.Movies, "Long Review", "550");
        var limit = _s.AddMovie(_s.Movies, "At The Limit", "551");
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, movie.Id, 8, "Fine.", "603"), Ct);
        // Two left out for the same reason: X-UserRatings-Skipped is their total, not the number of reasons.
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, plain.Id, 6, null, tmdb: string.Empty), Ct);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, plain2.Id, 6, null, tmdb: string.Empty), Ct);
        await Store.UpsertAsync(StoreFixture.Track(_s.Viewer.Id, Guid.NewGuid(), Guid.NewGuid()), Ct);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer2.Id, a.Id, 4, null, "949", StoreFixture.T0.AddDays(-2)), Ct);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer2.Id, b.Id, 9, null, "949"), Ct);
        // JE saves at most 2,000 characters: one over it is written and counted, one at it isn't counted (L-c).
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, longer.Id, 8, new string('x', JellyfinEnhancedExportWriter.JeMaxReviewLength + 1), "550"), Ct);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, limit.Id, 8, new string('y', JellyfinEnhancedExportWriter.JeMaxReviewLength), "551"), Ct);
        var data = JeData();
        var api = new UserRatingsAdminApi(_consumer, static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(), data: () => data);
        string[] Headers(HttpContext c) =>
        [
            c.Response.Headers[UserRatingsAdminApi.ExportedHeader].ToString(), c.Response.Headers[UserRatingsAdminApi.SkippedHeader].ToString(),
            c.Response.Headers[UserRatingsAdminApi.SkippedDetailHeader].ToString(), c.Response.Headers[UserRatingsAdminApi.MergedCopiesHeader].ToString(),
            c.Response.Headers[UserRatingsAdminApi.OverJeLimitHeader].ToString(),
        ];

        // The default: the copy in Movies 2, which viewer2 can't open, is left out (RestrictedLibrary).
        var context = new DefaultHttpContext();
        var file = Assert.IsType<FileContentResult>(api.ExportJellyfinEnhanced(_s.Admin.Id, context));
        Assert.Equal(("reviews.json", "application/json"), (file.FileDownloadName, file.ContentType));
        Assert.Equal(data.ExportJellyfinEnhanced(_s.Admin.Id).Bytes, file.FileContents);
        Assert.Equal(["4", "4", "NoTmdbId=2,NotInJe=1,RestrictedLibrary=1", "0", "1"], Headers(context));

        // Every title (leaveOutRestricted=false): the two copies merged into one key.
        var all = new DefaultHttpContext();
        var allFile = Assert.IsType<FileContentResult>(api.ExportJellyfinEnhanced(_s.Admin.Id, all, leaveOutRestricted: false));
        Assert.Equal(data.ExportJellyfinEnhanced(_s.Admin.Id, leaveOutRestricted: false).Bytes, allFile.FileContents);
        Assert.Equal(["4", "3", "NoTmdbId=2,NotInJe=1", "1", "1"], Headers(all));
        Assert.Equal(
            ["X-UserRatings-Exported", "X-UserRatings-Skipped", "X-UserRatings-Skipped-Detail", "X-UserRatings-Merged-Copies", "X-UserRatings-Over-Je-Limit"],
            new[] { UserRatingsAdminApi.ExportedHeader, UserRatingsAdminApi.SkippedHeader, UserRatingsAdminApi.SkippedDetailHeader, UserRatingsAdminApi.MergedCopiesHeader, UserRatingsAdminApi.OverJeLimitHeader });

        // The route: a GET of Admin/Export/JellyfinEnhanced with the query's leaveOutRestricted, no-store, under the
        // controller's elevation (never anonymous).
        var action = typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.GetUserRatingsJellyfinEnhancedExport))!;
        Assert.Equal("Export/JellyfinEnhanced", Assert.Single(action.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>()).Template);
        var parameter = Assert.Single(action.GetParameters());
        Assert.Equal(("leaveOutRestricted", typeof(bool?)), (parameter.Name, parameter.ParameterType));
        Assert.Single(parameter.GetCustomAttributes(typeof(FromQueryAttribute), false));
        Assert.Single(action.GetCustomAttributes(typeof(NoStoreAttribute), false));
        Assert.Empty(action.GetCustomAttributes(typeof(AllowAnonymousAttribute), true));
    }

    [Fact]
    public async Task TheJeExport_TakesTheArchive_JesArchivedRatingsWithTheirStoredIdentity_TheOthersCountedArchived()
    {
        // Before 0.3.24 the export read only the database's rows, so an archived rating was in no count.
        WriteJe(
            Entry(_s.Viewer.Id, "777", 3, "A film this library lacks.", StoreFixture.T0.AddDays(-1)),
            Entry(_s.Viewer.Id, "1399", 4, "An episode of a show this library lacks.", StoreFixture.T0.AddDays(-1), "tv", ":s1:e1"));
        Assert.Equal(2, N(await JeAsync(dryRun: false), "StoredDetached"));
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer2.Id, Guid.NewGuid(), 6, "Made here, its film gone.", "778") with { DetachedUtc = _s.Store.Clock.GetUtcNow().UtcDateTime, DetachReason = RatingValues.DetachReason.Missing }, Ct);
        _s.Advance(TimeSpan.FromDays(2));
        Assert.Equal(3, (await _detached.ArchiveDueAsync(_consumer, Ct)).Archived);
        Assert.Empty(Store.ReadTables().Ratings);

        var export = JeData().ExportJellyfinEnhanced(_s.Admin.Id);

        var v = _s.Viewer.Id.ToString("N");
        var reviews = JsonDocument.Parse(export.Bytes).RootElement.GetProperty("Reviews");
        Assert.Equal(new[] { $"{v}:movie:777", $"{v}:tv:1399:s1:e1" }, reviews.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("1399:s1:e1", "An episode of a show this library lacks.", 4.0), (reviews.GetProperty($"{v}:tv:1399:s1:e1").GetProperty("TmdbId").GetString(), reviews.GetProperty($"{v}:tv:1399:s1:e1").GetProperty("Content").GetString(), reviews.GetProperty($"{v}:tv:1399:s1:e1").GetProperty("Rating").GetDouble()));
        Assert.Equal(new Dictionary<string, int> { [JellyfinEnhancedExportWriter.Archived] = 1 }, export.Skipped);
        Assert.Equal((2, 1, 0), (export.Reviews, export.SkippedTotal, export.MergedCopies));

        // Imported back, JE's two are already imported (in the archive), and nothing is made again.
        await File.WriteAllBytesAsync(JeReviews, export.Bytes, Ct);
        var dry = await JeAsync();
        Assert.Equal((2, 0, 0), (N(dry, "AlreadyImportedArchived"), N(dry, "WouldCreate"), N(dry, "StoredDetached")));
    }

    [Fact]
    public async Task TheJeExport_LeavesOutTitlesNotEveryMemberCanSee_ByDefault_CountedRestrictedLibrary_AndIncludesThemWhenAsked()
    {
        // JE 12.14 serves every review by TMDb id to any signed-in user, so a review of a title in
        // a library some member can't open would reach them.
        _s.Movies.PhysicalLocationsList = [@"C:\media\Movies"];
        _s.Movies2.PhysicalLocationsList = [@"C:\media\Movies 2"];
        var open = _s.AddMovie(_s.Movies, "Heat", "949");
        var restricted = _s.AddMovie(_s.Movies2, "Alien", "348");
        var noTmdb = _s.AddMovie(_s.Movies2, "A Home Video");
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, open.Id, 8, "In a library everyone has.", "949"), Ct);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, restricted.Id, 7, "In Movies 2.", "348"), Ct);
        // Another reason first: JE couldn't show it anyway, so RestrictedLibrary counts only what the choice leaves out.
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, noTmdb.Id, 6, null, tmdb: string.Empty), Ct);
        // JE's own ratings, detached: one with no path (JE's review of a title this server never had), one whose stored path
        // is in Movies, one in Movies 2, one in no library any more.
        WriteJe(Entry(_s.Viewer2.Id, "777", 3, "A film this library lacks.", StoreFixture.T0.AddDays(-1)));
        Assert.Equal(1, N(await JeAsync(dryRun: false), "StoredDetached"));
        RatingRecord JeDetached(string tmdb, string path) => StoreFixture.Movie(_s.Viewer2.Id, Guid.NewGuid(), 6, "JE's, " + tmdb, tmdb) with
        {
            Origin = RatingValues.Origin.JellyfinEnhanced,
            Identity = StoreFixture.Movie(_s.Viewer2.Id, Guid.NewGuid(), tmdb: tmdb).Identity with { Path = path },
            DetachedUtc = StoreFixture.T0,
            DetachReason = RatingValues.DetachReason.Missing
        };
        await Store.UpsertAsync(JeDetached("801", @"C:\media\Movies\Gone (2001)\Gone (2001).mkv"), Ct);
        await Store.UpsertAsync(JeDetached("802", @"c:/MEDIA/Movies 2/Gone Too (2002)/Gone Too (2002).mkv"), Ct);
        await Store.UpsertAsync(JeDetached("803", @"D:\elsewhere\Gone Away (2003)\Gone Away (2003).mkv"), Ct);
        // A folder whose name only starts as a library's does isn't in it: the folder's name is matched whole.
        await Store.UpsertAsync(JeDetached("804", @"C:\media\Moviesque\Not In Movies (2004)\Not In Movies (2004).mkv"), Ct);

        var byDefault = JeData().ExportJellyfinEnhanced(_s.Admin.Id);
        var all = JeData().ExportJellyfinEnhanced(_s.Admin.Id, leaveOutRestricted: false);

        static string[] Keys(JellyfinEnhancedExport e) => JsonDocument.Parse(e.Bytes).RootElement.GetProperty("Reviews").EnumerateObject().Select(p => p.Name.Split(':', 2)[1]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["movie:777", "movie:801", "movie:949"], Keys(byDefault));
        Assert.Equal(new Dictionary<string, int> { [JellyfinEnhancedExportWriter.NoTmdbId] = 1, [JellyfinEnhancedExportWriter.RestrictedLibrary] = 4 }, byDefault.Skipped);
        Assert.Equal(["movie:348", "movie:777", "movie:801", "movie:802", "movie:803", "movie:804", "movie:949"], Keys(all));
        Assert.Equal(new Dictionary<string, int> { [JellyfinEnhancedExportWriter.NoTmdbId] = 1 }, all.Skipped);

        // Every member, also one who can't sign in now: a disabled user who can't open Heat leaves it out too.
        var disabled = ServicesFixture.NewUser("disabled");
        disabled.SetPermission(PermissionKind.IsDisabled, true);
        _s.Users.Add(disabled);
        _s.Library.Hide(disabled, open);
        Assert.DoesNotContain("movie:949", Keys(JeData().ExportJellyfinEnhanced(_s.Admin.Id)));
    }

    [Fact]
    public async Task TheJeExport_WritesWhenARatingWasMade_NotWhenItChanged_AndKeepsSpecialsAsSeasonZero()
    {
        var series = _s.Library.AddSeries("Doctor Who", null, ("Tmdb", "57243"));
        var specials = _s.Library.AddSeason(series, 0);
        var special = _s.Library.AddEpisode(series, specials, 1);
        foreach (var item in new BaseItem[] { series, specials, special })
        {
            _s.Library.SetTopParent(item, _s.Movies.PhysicalFolderIds[0]);
        }

        var movie = _s.AddMovie(_s.Movies, "Fight Club", "550");
        var made = StoreFixture.T0.AddDays(-9).AddMilliseconds(123);
        var changed = StoreFixture.T0.AddDays(-1).AddMilliseconds(456);
        await Store.UpsertAsync(StoreFixture.Movie(_s.Viewer.Id, movie.Id, 7, "Rules one and two.", "550", changed) with { CreatedUtc = made }, Ct);
        WriteJe(
            Entry(_s.Viewer.Id, "57243", null, "The specials.", made, "tv", ":s0"),
            Entry(_s.Viewer.Id, "57243", 4, "A Christmas special.", made, "tv", ":s0:e1"));
        Assert.Equal(2, N(await JeAsync(dryRun: false), "Created"));

        var export = JeData().ExportJellyfinEnhanced(_s.Admin.Id);

        var v = _s.Viewer.Id.ToString("N");
        var reviews = JsonDocument.Parse(export.Bytes).RootElement.GetProperty("Reviews");
        // CreatedAt is when it was made, UpdatedAt when it last changed.
        Assert.Equal(("2026-10-10T03:20:00.1230000Z", "2026-10-18T03:20:00.4560000Z"), (reviews.GetProperty($"{v}:movie:550").GetProperty("CreatedAt").GetString(), reviews.GetProperty($"{v}:movie:550").GetProperty("UpdatedAt").GetString()));
        // Season 0, the specials, as JE keys it: s0.
        Assert.Equal(("57243:s0", "The specials."), (reviews.GetProperty($"{v}:tv:57243:s0").GetProperty("TmdbId").GetString(), reviews.GetProperty($"{v}:tv:57243:s0").GetProperty("Content").GetString()));
        Assert.Equal(4.0, reviews.GetProperty($"{v}:tv:57243:s0:e1").GetProperty("Rating").GetDouble());
        Assert.Equal((3, 0), (export.Reviews, export.SkippedTotal));
    }
}
