using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// The Jellyfin Enhanced review import, from synthetic <c>reviews.json</c> files in JE's own format in
/// a temporary plugin configurations folder: the dry run and its report (never review text), the apply and its
/// idempotent re-run, delete-then-reimport, <c>Copies = First</c> and <c>All</c>, the <c>UserMap</c>, times ahead of the
/// clock, conflicts, duplicates, purged reviews, hidden and disabled authors, spoilers, JE's mirror, Replace's JE ledger
/// and the restore of the import's snapshot.
/// </summary>
public sealed partial class RatingsDataTests
{
    private static readonly Guid UnknownJeUser = Guid.Parse("00000000000000000000000000000009");

    private JellyfinEnhancedImport? _jeImport;

    private JellyfinEnhancedImport Je => _jeImport ??= NewJe();

    private string JeFolder => _s.Store.Directory.Combine("configurations");

    private string JeReviews => Path.Combine(JeFolder, JellyfinEnhancedFiles.ConfigurationName, JellyfinEnhancedFiles.ReviewsFile);

    private DateTime Now => _s.Store.Clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task AJeDryRun_ReportsEveryEntry_WithTheFieldsAndSettings_WritesNothing_AndNeverHoldsReviewText()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        _s.AddMovie(_s.Movies, "Fight Club", "550");
        WriteJeSettings(("ShowUserReviews", "true"), ("HideReviewsFromHiddenUsers", "true"), ("ReviewTranslationApiKey", "a-key-never-shown"));
        var at = Now.AddDays(-3);
        WriteJe(
            Entry(_s.Viewer.Id, "603", 4.5, "Secret words one.", at),
            Entry(_s.Viewer.Id, "550", 3.3, string.Empty, at),
            Entry(_s.Viewer2.Id, "603", null, "Secret words two.", at),
            Entry(_s.Viewer2.Id, "999999", 2, "Secret words three.", at),
            Entry(_s.Viewer2.Id, "550", null, "   ", at),
            Entry(UnknownJeUser, "603", 5, "Secret words four.", at));
        var before = Store.ReadTables().Ratings.Count;

        var report = await JeAsync();

        Assert.True((bool)report["DryRun"]!);
        Assert.Equal((6, 3, 0), (N(report, "Total"), N(report, "WouldCreate"), N(report, "Created")));
        Assert.Equal((3, 1, 1, 1, 1, 1), (N(report, "Matched"), N(report, "Unmatched"), N(report, "StoredDetached"), N(report, "UnknownUser"), N(report, "Empty"), N(report, "Adjusted")));
        Assert.Equal(["UserId", "TmdbId", "MediaType", "Content", "Rating", "CreatedAt", "UpdatedAt"], (IReadOnlyList<string>)report["SourceFields"]!);
        var settings = (IDictionary<string, object?>)report["JeSettings"]!;
        Assert.Equal((true, true, false, true, false), ((bool)settings["ShowUserReviews"]!, (bool)settings["HideReviewsFromHiddenUsers"]!, (bool)settings["MirrorReviewRatingsToJellyfin"]!, (bool)settings["ReviewTranslationApiKeySet"]!, (bool)settings["ReviewTranslationUrlSet"]!));
        Assert.Equal(JellyfinEnhancedImport.MirrorOffNote, report["MirrorNote"]);
        Assert.Equal(JellyfinEnhancedImport.SpoilerNote, report["SpoilerNote"]);
        var unknown = Assert.Single((IEnumerable<IDictionary<string, object?>>)report["UnknownUserIds"]!);
        Assert.Equal((UnknownJeUser.ToString("N"), 1), ((string)unknown["JeUserId"]!, (int)unknown["Reviews"]!));
        Assert.Contains((IEnumerable<IDictionary<string, object?>>)report["Samples"]!, s => (string?)s["Item"] == "The Matrix (1999)" && (double?)s["Stars"] == 4.5 && (string?)s["User"] == _s.Viewer.Username);
        Assert.Matches("^[0-9a-f]{32}$", (string)report["PlanToken"]!);

        // Nothing written, and no review text, translation key or anything of the file's values but names in the report.
        Assert.Equal(before, Store.ReadTables().Ratings.Count);
        Assert.Null(Store.ReadTables().ImportLedger.FirstOrDefault(l => l.Source == JellyfinEnhancedImport.LedgerSource));
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("Secret words", json, StringComparison.Ordinal);
        Assert.DoesNotContain("a-key-never-shown", json, StringComparison.Ordinal);
        Assert.Null(Store.GetAttached(_s.Viewer.Id, matrix.Id));
    }

    [Fact]
    public async Task TheApply_ImportsAsTheDryRunSaid_ThenARerunIsIdempotent()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var at = Now.AddDays(-3);
        WriteJe(Entry(_s.Viewer.Id, "603", 4.5, "Still holds up.", at), Entry(_s.Viewer2.Id, "603", null, "Text only.", at), Entry(_s.Viewer2.Id, "777", 2, string.Empty, at));

        var applied = await JeAsync(dryRun: false);

        Assert.Equal((3, 2, 1), (N(applied, "Created") + N(applied, "StoredDetached"), N(applied, "Created"), N(applied, "StoredDetached")));
        Assert.NotNull(applied["SafetySnapshot"]);
        var mine = Store.GetAttached(_s.Viewer.Id, matrix.Id)!;
        Assert.Equal((9, "Still holds up.", false, RatingValues.Origin.JellyfinEnhanced, at), (mine.Stars2, mine.ReviewText, mine.IsSpoiler, mine.Origin, mine.UpdatedUtc));
        Assert.Equal((int?)null, Store.GetAttached(_s.Viewer2.Id, matrix.Id)!.Stars2);
        var detached = Assert.Single(Store.ReadTables().Ratings, r => r.DetachedUtc is not null);
        Assert.Equal((RatingValues.DetachReason.Unmatched, "777", 4), (detached.DetachReason, detached.Identity.ProviderIds["Tmdb"], detached.Stars2));
        Assert.Equal(3, Store.ReadTables().ImportLedger.Count(l => l.Source == JellyfinEnhancedImport.LedgerSource));
        var log = Store.ReadTables().ModerationLog.Single(l => l.Action == RatingValues.ModerationAction.ImportJe);
        Assert.Contains("\"Created\":3", log.Details!, StringComparison.Ordinal);
        Assert.DoesNotContain("holds up", log.Details!, StringComparison.Ordinal);

        _s.Advance(TimeSpan.FromMinutes(1));
        var again = await JeAsync(dryRun: false);
        Assert.Equal((0, 0, 3), (N(again, "Created"), N(again, "Updated"), N(again, "AlreadyImported")));
        Assert.Equal(3, Store.ReadTables().Ratings.Count);
    }

    [Fact]
    public async Task DeleteThenReimport_IsSkippedDeleted_HoweverNewJesCopy()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        WriteJe(Entry(_s.Viewer.Id, "603", 4, "The first words.", Now.AddDays(-3)));
        await JeAsync(dryRun: false);
        await _s.Service.DeleteMineAsync(_s.Viewer, matrix.Id, Ct);

        _s.Advance(TimeSpan.FromHours(1));
        WriteJe(Entry(_s.Viewer.Id, "603", 5, "Edited in JE after the delete.", Now));
        var report = await JeAsync(dryRun: false);

        Assert.Equal((1, 0, 0), (N(report, "SkippedDeleted"), N(report, "Created"), N(report, "Updated")));
        Assert.Null(Store.GetAttached(_s.Viewer.Id, matrix.Id));
    }

    [Fact]
    public async Task ACopyInTwoLibraries_First_PicksTheOneItsMemberCanSee_AndAll_RatesEach_ThenEachCopysDeleteHolds()
    {
        // Viewer2 can't open Movies 2; its copy is the older one.
        var hidden = _s.AddMovie(_s.Movies2, "The Matrix", "603");
        hidden.DateCreated = Now.AddYears(-2);
        var seen = _s.AddMovie(_s.Movies, "The Matrix", "603");
        seen.DateCreated = Now.AddYears(-1);
        var at = Now.AddDays(-3);
        WriteJe(Entry(_s.Viewer2.Id, "603", 4, "On every copy in JE.", at), Entry(_s.Viewer.Id, "603", 3, string.Empty, at));

        var first = await JeAsync(dryRun: false);
        Assert.Equal((2, 2), (N(first, "MatchedMultiple"), N(first, "Created")));
        Assert.Contains("show the \"Also rated on another copy\" hint", (string)first["CopiesNote"]!, StringComparison.Ordinal);
        Assert.NotNull(Store.GetAttached(_s.Viewer2.Id, seen.Id));
        Assert.Null(Store.GetAttached(_s.Viewer2.Id, hidden.Id));
        Assert.NotNull(Store.GetAttached(_s.Viewer.Id, hidden.Id));

        // All: the copies the first run didn't rate get their own rating, each in the entry's ledger.
        _s.Advance(TimeSpan.FromMinutes(1));
        var all = await JeAsync(dryRun: false, new UserRatingsJeImportRequest { Copies = "All" });
        Assert.Equal((2, 2), (N(all, "Created"), N(all, "AlreadyImported")));
        Assert.Contains("each copy gets its own rating", (string)all["CopiesNote"]!, StringComparison.Ordinal);
        Assert.NotNull(Store.GetAttached(_s.Viewer2.Id, hidden.Id));
        var key = Entry(_s.Viewer2.Id, "603", 4, string.Empty, Now).Key;
        Assert.Equal(2, Store.ReadTables().ImportLedger.Single(l => l.Source == JellyfinEnhancedImport.LedgerSource && l.SourceKey == key).RatingIds.Count);

        // One copy's rating deleted by its member, then a newer JE copy: that copy stays deleted, the other is updated.
        await _s.Service.DeleteMineAsync(_s.Viewer2, seen.Id, Ct);
        _s.Advance(TimeSpan.FromHours(1));
        WriteJe(Entry(_s.Viewer2.Id, "603", 5, "Newer in JE.", Now.AddMinutes(-1)), Entry(_s.Viewer.Id, "603", 3, string.Empty, at));
        var after = await JeAsync(dryRun: false, new UserRatingsJeImportRequest { Copies = "All" });
        Assert.Equal((1, 1, 0), (N(after, "SkippedDeleted"), N(after, "Updated"), N(after, "Created")));
        Assert.Null(Store.GetAttached(_s.Viewer2.Id, seen.Id));
        Assert.Equal("Newer in JE.", Store.GetAttached(_s.Viewer2.Id, hidden.Id)!.ReviewText);
    }

    [Fact]
    public async Task AnUnknownUser_IsReportedAndSkipped_AndTheUserMapImportsItUnderTheMappedUser()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        WriteJe(Entry(UnknownJeUser, "603", 4, "A re-created user's words.", Now.AddDays(-3)));

        var skipped = await JeAsync(dryRun: false);
        Assert.Equal((1, 0), (N(skipped, "UnknownUser"), N(skipped, "Created")));

        var map = new UserRatingsJeImportRequest { UserMap = new Dictionary<string, string> { [UnknownJeUser.ToString("D")] = _s.Viewer2.Id.ToString("N") } };
        var mapped = await JeAsync(dryRun: false, map);
        Assert.Equal((0, 1), (N(mapped, "UnknownUser"), N(mapped, "Created")));
        Assert.Equal("A re-created user's words.", Store.GetAttached(_s.Viewer2.Id, matrix.Id)!.ReviewText);

        var bad = Assert.Throws<PluginException>(() => JeImportRequest.Parse(true, new UserRatingsJeImportRequest { UserMap = new Dictionary<string, string> { ["not-a-user"] = _s.Viewer.Id.ToString() } }));
        Assert.Equal(PluginErrorKind.Invalid, bad.Kind);
        Assert.Equal(PluginErrorKind.Invalid, Assert.Throws<PluginException>(() => JeImportRequest.Parse(true, new UserRatingsJeImportRequest { Copies = "Some" })).Kind);
    }

    [Fact]
    public async Task TimesAheadOfTheClock_AreWrittenAsTheImportsTime_Reported_AndTheSameFileAgainIsAlreadyImported()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var ahead = Now.AddDays(5);
        WriteJe(Entry(_s.Viewer.Id, "603", 4, "Dated ahead (JE's local time).", ahead));

        var report = await JeAsync(dryRun: false);
        Assert.Equal(1, N(report, "Clamped"));
        Assert.Contains("ahead of this server's clock", Assert.Single((IEnumerable<string>)report["Notes"]!), StringComparison.Ordinal);
        var row = Store.GetAttached(_s.Viewer.Id, matrix.Id)!;
        Assert.True(row.UpdatedUtc <= Now && row.CreatedUtc <= row.UpdatedUtc);
        Assert.Equal(ahead, Store.ReadTables().ImportLedger.Single(l => l.Source == JellyfinEnhancedImport.LedgerSource).SourceUpdatedUtc);

        _s.Advance(TimeSpan.FromMinutes(1));
        var again = await JeAsync(dryRun: false);
        Assert.Equal((1, 0), (N(again, "AlreadyImported"), N(again, "Updated")));

        // The member's delete is never older than the row, so a restore of the import's snapshot keeps it deleted... and a
        // JE re-import skips it.
        await _s.Service.DeleteMineAsync(_s.Viewer, matrix.Id, Ct);
        Assert.Equal(1, N(await JeAsync(), "SkippedDeleted"));
    }

    [Fact]
    public async Task AJeEditOfAnImportedReview_Updates_UnlessItsMemberEditedItSince()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        WriteJe(Entry(_s.Viewer.Id, "603", 4, "First words.", Now.AddDays(-3)));
        await JeAsync(dryRun: false);

        WriteJe(Entry(_s.Viewer.Id, "603", 5, "Edited in JE.", Now.AddDays(-2)));
        var updated = await JeAsync(dryRun: false);
        Assert.Equal(1, N(updated, "Updated"));
        Assert.Equal((10, "Edited in JE."), (Store.GetAttached(_s.Viewer.Id, matrix.Id)!.Stars2, Store.GetAttached(_s.Viewer.Id, matrix.Id)!.ReviewText));

        _s.Advance(TimeSpan.FromMinutes(1));
        await _s.Service.SetReviewAsync(_s.Viewer, matrix.Id, "The member's own edit.", false, (DateTime?)null, Ct);
        WriteJe(Entry(_s.Viewer.Id, "603", 1, "Edited in JE again.", Now.AddDays(-1)));
        var newer = await JeAsync(dryRun: false);
        Assert.Equal((1, 0), (N(newer, "SkippedNewer"), N(newer, "Updated")));
        Assert.Equal("The member's own edit.", Store.GetAttached(_s.Viewer.Id, matrix.Id)!.ReviewText);
    }

    [Fact]
    public async Task AMembersOwnRating_IsAConflict_TheNewerWins_AndOnlyAWriteRecordsALedgerRow()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var club = _s.AddMovie(_s.Movies, "Fight Club", "550");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, matrix.Id, "Rated here, newer.", Now.AddDays(-1)), Ct);
        await Store.UpsertAsync(Rating(_s.Viewer.Id, club.Id, "Rated here, older.", Now.AddDays(-5)), Ct);
        WriteJe(Entry(_s.Viewer.Id, "603", 2, "JE's older words.", Now.AddDays(-3)), Entry(_s.Viewer.Id, "550", 2, "JE's newer words.", Now.AddDays(-3)));

        var report = await JeAsync(dryRun: false);

        Assert.Equal((2, 1, 1), (N(report, "Conflict"), N(report, "ConflictKeptLocal"), N(report, "ConflictTookJe")));
        Assert.Equal("Rated here, newer.", Store.GetAttached(_s.Viewer.Id, matrix.Id)!.ReviewText);
        Assert.Equal("JE's newer words.", Store.GetAttached(_s.Viewer.Id, club.Id)!.ReviewText);
        var ledger = Store.ReadTables().ImportLedger.Where(l => l.Source == JellyfinEnhancedImport.LedgerSource).ToList();
        Assert.Equal([Store.GetAttached(_s.Viewer.Id, club.Id)!.Id], Assert.Single(ledger).RatingIds);
    }

    [Fact]
    public async Task TwoKeysOnOneMemberAndItem_TheNewerWins_AsDuplicateInSource()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var older = Entry(_s.Viewer.Id, "603", 2, "Older.", Now.AddDays(-4));
        var newer = Entry(_s.Viewer.Id, "603", 5, "Newer, under an odd key.", Now.AddDays(-2)) with { Key = _s.Viewer.Id.ToString("N") + ":movie:0603" };

        WriteJe(older, newer);
        var report = await JeAsync(dryRun: false);

        Assert.Equal((1, 1, 1), (N(report, "DuplicateInSource"), N(report, "Created"), N(report, "KeyMismatch")));
        Assert.Equal("Newer, under an odd key.", Store.GetAttached(_s.Viewer.Id, matrix.Id)!.ReviewText);
    }

    [Fact]
    public async Task APurgedReview_IsPreviouslyPurged_AndMadeAgainOnlyWithRecreatePurged()
    {
        WriteJe(Entry(_s.Viewer.Id, "424242", 3, "Its movie isn't here.", Now.AddDays(-3)));
        await JeAsync(dryRun: false);
        var detached = Assert.Single(Store.ReadTables().Ratings, r => r.DetachedUtc is not null);
        await _data.PurgeAsync(_s.Admin.Id, detached.Id, Ct);

        var purged = await JeAsync(dryRun: false);
        Assert.Equal((1, 0), (N(purged, "PreviouslyPurged"), N(purged, "StoredDetached")));
        Assert.DoesNotContain(Store.ReadTables().Ratings, r => r.DetachedUtc is not null);

        var again = await JeAsync(dryRun: false, new UserRatingsJeImportRequest { RecreatePurged = true });
        Assert.Equal((0, 1), (N(again, "PreviouslyPurged"), N(again, "StoredDetached")));

        // StoreUnmatched off: such an entry is only counted (the row stored before stays).
        var off = await JeAsync(dryRun: true, new UserRatingsJeImportRequest { StoreUnmatched = false });
        Assert.Equal((0, 1), (N(off, "StoredDetached"), N(off, "Unmatched")));
        Assert.Single(Store.ReadTables().Ratings, r => r.DetachedUtc is not null);
    }

    [Fact]
    public async Task HiddenAndDisabledAuthors_AreImportedAndCounted_SpoilersAreNotFlagged_AndJesMirrorNeverTouchesUserData()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        _s.Viewer2.SetPermission(PermissionKind.IsDisabled, true);
        _s.Viewer.SetPermission(PermissionKind.IsHidden, true);
        WriteJeSettings(("ShowUserReviews", "true"), ("MirrorReviewRatingsToJellyfin", "true"));
        WriteJe(Entry(_s.Viewer.Id, "603", 4, "Spoiler: the spoon isn't there.", Now.AddDays(-3)), Entry(_s.Viewer2.Id, "603", 3, "A disabled member's review.", Now.AddDays(-3)));

        var report = await JeAsync(dryRun: false);

        Assert.Equal((2, 1, 1), (N(report, "Created"), N(report, "HiddenUserReviews"), N(report, "DisabledUserReviews")));
        Assert.False(Store.GetAttached(_s.Viewer.Id, matrix.Id)!.IsSpoiler);
        Assert.Equal(JellyfinEnhancedImport.MirrorOnNote, report["MirrorNote"]);
        Assert.True((bool)((IDictionary<string, object?>)report["JeSettings"]!)["MirrorReviewRatingsToJellyfin"]!);
        _s.UserData.Verify(u => u.SaveUserData(It.IsAny<User>(), It.IsAny<BaseItem>(), It.IsAny<MediaBrowser.Controller.Entities.UserItemData>(), It.IsAny<MediaBrowser.Model.Entities.UserDataSaveReason>(), It.IsAny<CancellationToken>()), Times.Never);
        _s.UserData.Verify(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>()), Times.Never);
    }

    [Fact]
    public async Task AMembersDeleteOnTheItem_WithoutALedgerRow_KeepsAnOlderJeReviewOut_ButNotANewerOne()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await _s.Service.SetStarsAsync(_s.Viewer, matrix.Id, 3m, Ct);
        await _s.Service.DeleteMineAsync(_s.Viewer, matrix.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));

        WriteJe(Entry(_s.Viewer.Id, "603", 4, "Written before the delete.", Now.AddDays(-3)));
        Assert.Equal((1, 0), (N(await JeAsync(), "SkippedDeleted"), N(await JeAsync(), "WouldCreate")));

        WriteJe(Entry(_s.Viewer.Id, "603", 4, "Written after the delete.", Now.AddSeconds(-1)));
        Assert.Equal((0, 1), (N(await JeAsync(), "SkippedDeleted"), N(await JeAsync(), "WouldCreate")));
    }

    [Fact]
    public async Task AReplace_KeepsTheJeLedgerOfADeletedRating_SoAJeReimportNeverBringsItBack()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var club = _s.AddMovie(_s.Movies, "Fight Club", "550");
        await Store.UpsertAsync(Rating(_s.Viewer2.Id, club.Id, "Another member's row.", Now.AddDays(-4)), Ct);
        WriteJe(Entry(_s.Viewer.Id, "603", 4, "Imported, then deleted.", Now.AddDays(-3)));
        await JeAsync(dryRun: false);
        await _s.Service.DeleteMineAsync(_s.Viewer, matrix.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));

        // Another server's export without any JE ledger: the wipe keeps the JE row of the deleted rating.
        var (other, _) = await AnotherServersCopyAsync(_ => { }, club.Id);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(other, Ct))!.AsObject();
        json["Data"]!["ImportLedger"] = new JsonArray();
        json["Data"]!["ImportLedgerRows"] = new JsonArray();
        await File.WriteAllTextAsync(other, json.ToJsonString(), Ct);
        await ImportAsync(other, "Replace", dryRun: false);
        Assert.Contains(Store.ReadTables().ImportLedger, l => l.Source == JellyfinEnhancedImport.LedgerSource);

        _s.Advance(TimeSpan.FromHours(1));
        WriteJe(Entry(_s.Viewer.Id, "603", 5, "Newer in JE.", Now.AddMinutes(-1)));
        var report = await JeAsync(dryRun: false);
        Assert.Equal((1, 0), (N(report, "SkippedDeleted"), N(report, "Created")));
        Assert.Null(Store.GetAttached(_s.Viewer.Id, matrix.Id));

        // This server's own export, Replace: the file's JE ledger joins the kept one; still skipped.
        _s.Advance(TimeSpan.FromMinutes(1));
        await ImportAsync(await ExportFileAsync(), "Replace", dryRun: false);
        Assert.Equal(1, N(await JeAsync(), "SkippedDeleted"));
    }

    [Fact]
    public async Task AReplaceOfTheServersOwnExport_JoinsTheFilesJeLedgerToTheKeptOne_SoTheLiveCopyStillFollowsJe()
    {
        // Copies = All on two copies, one copy's rating deleted by its member: after a Replace of this server's own export the
        // entry's ledger holds both (the kept row of the deleted one, the file's of the live one), so a newer JE copy skips
        // the deleted copy and updates the live one.
        var one = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var two = _s.AddMovie(_s.Movies2, "The Matrix", "603");
        var all = new UserRatingsJeImportRequest { Copies = "All" };
        WriteJe(Entry(_s.Viewer.Id, "603", 4, "On both copies.", Now.AddDays(-3)));
        await JeAsync(dryRun: false, all);
        await _s.Service.DeleteMineAsync(_s.Viewer, one.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        await ImportAsync(await ExportFileAsync(), "Replace", dryRun: false);
        Assert.Equal(2, Store.ReadTables().ImportLedger.Single(l => l.Source == JellyfinEnhancedImport.LedgerSource).RatingIds.Count);

        _s.Advance(TimeSpan.FromHours(1));
        WriteJe(Entry(_s.Viewer.Id, "603", 5, "Newer in JE.", Now.AddMinutes(-1)));
        var report = await JeAsync(dryRun: false, all);
        Assert.Equal((1, 1, 0, 0), (N(report, "SkippedDeleted"), N(report, "Updated"), N(report, "Created"), N(report, "Conflict")));
        Assert.Null(Store.GetAttached(_s.Viewer.Id, one.Id));
        Assert.Equal("Newer in JE.", Store.GetAttached(_s.Viewer.Id, two.Id)!.ReviewText);
    }

    [Fact]
    public async Task RestoringTheImportsSnapshot_UndoesIt()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        WriteJe(Entry(_s.Viewer.Id, "603", 4, "Imported.", Now.AddDays(-3)));
        var report = await JeAsync(dryRun: false);
        Assert.NotNull(Store.GetAttached(_s.Viewer.Id, matrix.Id));

        _s.Advance(TimeSpan.FromMinutes(1));
        await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);

        Assert.Null(Store.GetAttached(_s.Viewer.Id, matrix.Id));
        Assert.DoesNotContain(Store.ReadTables().ImportLedger, l => l.Source == JellyfinEnhancedImport.LedgerSource);
        Assert.Equal(1, N(await JeAsync(), "WouldCreate"));
    }

    [Fact]
    public async Task ASeasonAndAnEpisode_AreFoundUnderTheSeriesByNumber()
    {
        var series = _s.Library.AddSeries("Breaking Bad", null, ("Tmdb", "1396"));
        var season = _s.Library.AddSeason(series, 1);
        var episode = _s.Library.AddEpisode(series, season, 1);
        foreach (var item in new BaseItem[] { series, season, episode })
        {
            _s.Library.SetTopParent(item, _s.Movies.PhysicalFolderIds[0]);
        }

        WriteJe(
            Entry(_s.Viewer.Id, "1396", 4, "A strong pilot.", Now.AddDays(-3), "tv", ":s1:e1"),
            Entry(_s.Viewer.Id, "1396", 4.5, string.Empty, Now.AddDays(-3), "tv", ":s1"),
            Entry(_s.Viewer.Id, "1396", 5, string.Empty, Now.AddDays(-3), "tv"),
            Entry(_s.Viewer.Id, "1396", 3, string.Empty, Now.AddDays(-3), "tv", ":s2:e9"));

        var report = await JeAsync(dryRun: false);

        Assert.Equal((3, 1), (N(report, "Created"), N(report, "StoredDetached")));
        Assert.Equal(8, Store.GetAttached(_s.Viewer.Id, episode.Id)!.Stars2);
        Assert.Equal(9, Store.GetAttached(_s.Viewer.Id, season.Id)!.Stars2);
        Assert.Equal(10, Store.GetAttached(_s.Viewer.Id, series.Id)!.Stars2);
        var missing = Assert.Single(Store.ReadTables().Ratings, r => r.DetachedUtc is not null);
        Assert.Equal(("Episode", 2, 9), (missing.Identity.Kind, missing.Identity.Season, missing.Identity.Episode));
        Assert.Equal("1396", missing.Identity.SeriesProviderIds!["Tmdb"]);
        Assert.Equal("TMDb 1396", missing.Identity.Name);
        Assert.Equal(0, N(report, "KeyMismatch"));
        Assert.Equal(4, Store.ReadTables().ImportLedger.Count(l => l.Source == JellyfinEnhancedImport.LedgerSource));
    }

    // The shape before JE 12.14 (or another tool's): the series' id alone in TmdbId, the season and episode in the key only.
    [Fact]
    public async Task ASeasonAndAnEpisode_WithTheSeriesIdAloneInTheBody_AreFoundTheSameWay()
    {
        var series = _s.Library.AddSeries("Breaking Bad", null, ("Tmdb", "1396"));
        var season = _s.Library.AddSeason(series, 1);
        var episode = _s.Library.AddEpisode(series, season, 1);
        foreach (var item in new BaseItem[] { series, season, episode })
        {
            _s.Library.SetTopParent(item, _s.Movies.PhysicalFolderIds[0]);
        }

        WriteJe(
            Entry(_s.Viewer.Id, "1396", 4, "A strong pilot.", Now.AddDays(-3), "tv", ":s1:e1") with { Tmdb = "1396" },
            Entry(_s.Viewer.Id, "1396", 4.5, string.Empty, Now.AddDays(-3), "tv", ":s1") with { Tmdb = "1396" });

        var report = await JeAsync(dryRun: false);

        Assert.Equal((2, 0, 0), (N(report, "Created"), N(report, "StoredDetached"), N(report, "KeyMismatch")));
        Assert.Equal(8, Store.GetAttached(_s.Viewer.Id, episode.Id)!.Stars2);
        Assert.Equal(9, Store.GetAttached(_s.Viewer.Id, season.Id)!.Stars2);
    }

    // A body that really disagrees with its key is still a KeyMismatch: another series' id, or another episode's key. The
    // body's series id wins; the season and episode are the key's.
    [Fact]
    public async Task ABodyThatDisagreesWithItsTvKey_IsAKeyMismatch_TheBodysSeriesIdWinning()
    {
        var series = _s.Library.AddSeries("Breaking Bad", null, ("Tmdb", "1396"));
        var season = _s.Library.AddSeason(series, 1);
        var episode = _s.Library.AddEpisode(series, season, 2);
        foreach (var item in new BaseItem[] { series, season, episode })
        {
            _s.Library.SetTopParent(item, _s.Movies.PhysicalFolderIds[0]);
        }

        WriteJe(
            Entry(_s.Viewer.Id, "1", 4, "Another series' id in the key.", Now.AddDays(-3), "tv", ":s1:e2") with { Tmdb = "1396:s1:e2" },
            Entry(_s.Viewer2.Id, "1396", 3, "Another episode's key in the body.", Now.AddDays(-3), "tv", ":s1:e2") with { Tmdb = "1396:s1:e1" });

        var report = await JeAsync(dryRun: false);

        Assert.Equal((2, 2), (N(report, "KeyMismatch"), N(report, "Created")));
        Assert.Equal(8, Store.GetAttached(_s.Viewer.Id, episode.Id)!.Stars2);
        Assert.Equal(6, Store.GetAttached(_s.Viewer2.Id, episode.Id)!.Stars2);
    }

    // The reattach: a season's and an episode's reviews of a series not identified yet are stored detached with the series'
    // TMDb id (never the whole key), so they reattach once the series gets it; the same file again is AlreadyImported.
    [Fact]
    public async Task ASeasonAndAnEpisodeStoredDetached_ReattachOnceTheSeriesGetsItsTmdbId()
    {
        var series = _s.Library.AddSeries("Breaking Bad", null, ("Tvdb", "81189"));
        var season = _s.Library.AddSeason(series, 1);
        var episode = _s.Library.AddEpisode(series, season, 2);
        foreach (var item in new BaseItem[] { series, season, episode })
        {
            _s.Library.SetTopParent(item, _s.Movies.PhysicalFolderIds[0]);
        }

        WriteJe(
            Entry(_s.Viewer.Id, "1396", 3.5, "The second episode.", Now.AddDays(-3), "tv", ":s1:e2"),
            Entry(_s.Viewer.Id, "1396", 4, string.Empty, Now.AddDays(-3), "tv", ":s1"));
        var report = await JeAsync(dryRun: false);
        Assert.Equal((0, 2), (N(report, "Created"), N(report, "StoredDetached")));
        var stored = Store.ReadTables().Ratings.Where(r => r.DetachedUtc is not null).OrderBy(r => r.Identity.Kind, StringComparer.Ordinal).ToList();
        Assert.Equal(["Episode", "Season"], stored.Select(r => r.Identity.Kind));
        Assert.All(stored, r => Assert.Equal("1396", r.Identity.SeriesProviderIds!["Tmdb"]));
        Assert.Equal([(1, (int?)2), (1, null)], stored.Select(r => (r.Identity.Season!.Value, r.Identity.Episode)));

        // Identified: the series has the TMDb id now, and a refresh updates it, its season and its episode.
        series.ProviderIds["Tmdb"] = "1396";
        await _consumer.HandleAsync(new LibraryChangeBatch([], [UpdatedChange(series), UpdatedChange(season), UpdatedChange(episode)], [], [], [], fullPass: false, duringPostScan: false, afterFullPass: false), Ct);

        Assert.Equal(7, Store.GetAttached(_s.Viewer.Id, episode.Id)?.Stars2);
        Assert.Equal(8, Store.GetAttached(_s.Viewer.Id, season.Id)?.Stars2);
        Assert.DoesNotContain(Store.ReadTables().Ratings, r => r.DetachedUtc is not null);
        var again = await JeAsync();
        Assert.Equal(2, N(again, "AlreadyImported"));
    }

    // An entry stored detached (no copy had its TMDb id), then two copies get the
    // id (the review stays detached: two strong candidates), then Copies = All. Before, that added a rating on both copies
    // beside the detached one, three for one JE review. A detached row of the entry's ledger covers every copy now:
    // nothing is added until the admin attaches it from the Detached list, and the next Copies = All run adds the other copy.
    [Fact]
    public async Task CopiesAll_AfterADetachedRow_AddsNoDuplicates_AndAddsTheOtherCopyOnceTheAdminAttachesIt()
    {
        var a = _s.AddMovie(_s.Movies, "Big Buck Test");
        var b = _s.AddMovie(_s.Movies, "Big Buck Test");
        WriteJe(Entry(_s.Viewer.Id, "900888", 1, "On both copies?", Now.AddDays(-3)));
        Assert.Equal(1, N(await JeAsync(dryRun: false), "StoredDetached"));
        var stored = Assert.Single(Store.ReadTables().Ratings);

        foreach (var copy in new[] { a, b })
        {
            copy.ProviderIds["Tmdb"] = "900888";
        }

        await _consumer.HandleAsync(new LibraryChangeBatch([], [UpdatedChange(a), UpdatedChange(b)], [], [], [], fullPass: false, duringPostScan: false, afterFullPass: false), Ct);
        Assert.NotNull(Store.Get(stored.Id)!.DetachedUtc);

        _s.Advance(TimeSpan.FromMinutes(1));
        var all = new UserRatingsJeImportRequest { Copies = "All" };
        var dry = await JeAsync(dryRun: true, all);
        Assert.Equal((0, 1, 1), (N(dry, "WouldCreate"), N(dry, "AlreadyImported"), N(dry, "MatchedMultiple")));
        var run = await JeAsync(dryRun: false, all);
        Assert.Equal((0, 1), (N(run, "Created"), N(run, "AlreadyImported")));
        Assert.Equal(stored.Id, Assert.Single(Store.ReadTables().Ratings).Id);

        // The admin attaches it to one copy (the Detached list's Relink); Copies = All then rates the other.
        await _data.RelinkAsync(_s.Admin.Id, stored.Id, a.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        var after = await JeAsync(dryRun: false, all);
        Assert.Equal((1, 1), (N(after, "Created"), N(after, "AlreadyImported")));
        Assert.Equal(stored.Id, Store.GetAttached(_s.Viewer.Id, a.Id)!.Id);
        Assert.Equal((2, "On both copies?"), (Store.GetAttached(_s.Viewer.Id, b.Id)!.Stars2, Store.GetAttached(_s.Viewer.Id, b.Id)!.ReviewText));
        Assert.Equal(2, Store.ReadTables().Ratings.Count);
        var rerun = await JeAsync(dryRun: true, all);
        Assert.Equal((0, 2), (N(rerun, "WouldCreate"), N(rerun, "AlreadyImported")));
    }

    // JE's rating 0 is no rating (JE saves `rating || null` and shows no stars for it), so it
    // maps to none, never half a star; a rating above 0 that rounds to nothing is half a star, adjusted.
    [Theory]
    [InlineData(0.0, null, false)]
    [InlineData(0.2, 1, true)]
    [InlineData(0.5, 1, false)]
    [InlineData(-1.0, 1, true)]
    [InlineData(3.3, 7, true)]
    [InlineData(5.0, 10, false)]
    [InlineData(6.0, 10, true)]
    public void JesRating_AsHalfStars_ZeroBeingNone(double rating, int? stars2, bool adjusted)
    {
        Assert.Equal((stars2, adjusted), (JellyfinEnhancedImport.Stars2(rating, out var a), a));
    }

    [Fact]
    public async Task ARatingOfZero_WithText_IsAReviewWithoutStars_AndWithoutText_IsEmpty()
    {
        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        WriteJe(Entry(_s.Viewer.Id, "603", 0, "Words, no stars.", Now.AddDays(-3)), Entry(_s.Viewer2.Id, "603", 0, string.Empty, Now.AddDays(-3)));

        var report = await JeAsync(dryRun: false);

        Assert.Equal((1, 1, 0), (N(report, "Created"), N(report, "Empty"), N(report, "Adjusted")));
        Assert.Equal(((int?)null, "Words, no stars."), (Store.GetAttached(_s.Viewer.Id, matrix.Id)!.Stars2, Store.GetAttached(_s.Viewer.Id, matrix.Id)!.ReviewText));
        Assert.Null(Store.GetAttached(_s.Viewer2.Id, matrix.Id));
    }

    [Fact]
    public async Task TheRoute_Answers404WithoutJe_400ForAnotherFormatOrNoPlan_AndAChangedFileFailsTheRealRun()
    {
        _s.AddMovie(_s.Movies, "The Matrix", "603");
        var data = JeData();
        Assert.Equal(JellyfinEnhancedImport.NotFoundCode, Assert.Throws<PluginException>(() => data.StartJellyfinEnhancedImport(_s.Admin.Id, null, true, null)).Code);

        Directory.CreateDirectory(Path.GetDirectoryName(JeReviews)!);
        await File.WriteAllTextAsync(JeReviews, "{\"Bookmarks\":{}}", Ct);
        Assert.Equal(JellyfinEnhancedImport.FormatCode, Assert.Throws<PluginException>(() => data.StartJellyfinEnhancedImport(_s.Admin.Id, null, true, null)).Code);

        WriteJe(Entry(_s.Viewer.Id, "603", 4, "Words.", Now.AddDays(-3)));
        Assert.Equal(PluginErrorKind.Invalid, Assert.Throws<PluginException>(() => data.StartJellyfinEnhancedImport(_s.Admin.Id, null, false, null)).Kind);
        var limited = Assert.Throws<PluginException>(() => data.StartJellyfinEnhancedImport(_s.Viewer.Id, null, true, null));
        Assert.Equal(RatingsData.AllLibrariesCode, limited.Code);

        var dry = data.StartJellyfinEnhancedImport(_s.Admin.Id, null, true, null);
        await Finished();
        var plan = (string)((IDictionary<string, object?>)_jobs.Get(Guid.Parse(dry.Id)).Report!)["PlanToken"]!;
        Assert.Equal(JellyfinEnhancedImport.JobKind, dry.Kind);

        WriteJe(Entry(_s.Viewer.Id, "603", 5, "Changed since the dry run.", Now.AddDays(-2)));
        var real = data.StartJellyfinEnhancedImport(_s.Admin.Id, null, false, plan);
        await Finished();
        Assert.Equal(AdminJob.Failed, _jobs.Get(Guid.Parse(real.Id)).State);
        Assert.Empty(Store.ReadTables().Ratings);

        // The plan is compared before the import snapshot, so the refused run (409 Changed)
        // leaves no snapshot and no copy of the archive.
        var refused = await Assert.ThrowsAsync<PluginException>(() => JeAsync(dryRun: false, plan: plan));
        Assert.Equal((PluginErrorKind.Conflict, RatingsEnvelope.ChangedCode), (refused.Kind, refused.Code));
        Assert.DoesNotContain(Store.ListSnapshots(), s => s.Operation == SnapshotOperations.Import);
        Assert.DoesNotContain(SnapshotRotation.ListPreOp(_detached.SnapshotDirectory, _detached.ArchiveOf(_consumer).SnapshotName, ".jsonl"), c => c.Operation == SnapshotOperations.Import);
        Assert.Empty(Store.ReadTables().Ratings);

        // The right plan writes, after its snapshot.
        var applied = await JeAsync(dryRun: false);
        Assert.Equal(1, N(applied, "Created"));
        Assert.Equal(SnapshotOperations.Import, Assert.Single(Store.ListSnapshots(), s => s.Name == (string)applied["SafetySnapshot"]!).Operation);
    }

    private static int N(IDictionary<string, object?> report, string key) => (int)report[key]!;

    // An entry as JE 12.14 writes it (posted through JE's own API in the je sandbox): the key
    // "<userIdN>:<type>:<tmdbKey>", and the same whole TMDb key in TmdbId ("1396:s1:e1" for an episode). `with { Tmdb = … }`
    // gives an older or another tool's shape, the series' id alone.
    private static JeEntry Entry(Guid user, string tmdb, double? rating, string content, DateTime at, string type = "movie", string suffix = "")
        => new(user.ToString("N", CultureInfo.InvariantCulture) + ":" + type + ":" + tmdb + suffix, user, tmdb + suffix, type, rating, content, at);

    private void WriteJe(params JeEntry[] entries)
    {
        var reviews = new JsonObject();
        foreach (var e in entries)
        {
            reviews[e.Key] = new JsonObject
            {
                ["UserId"] = e.User.ToString("N", CultureInfo.InvariantCulture),
                ["TmdbId"] = e.Tmdb,
                ["MediaType"] = e.Type,
                ["Content"] = e.Content,
                ["Rating"] = e.Rating,
                ["CreatedAt"] = e.At.AddDays(-1).ToString("O", CultureInfo.InvariantCulture),
                ["UpdatedAt"] = e.At.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(JeReviews)!);
        File.WriteAllText(JeReviews, new JsonObject { ["Reviews"] = reviews }.ToJsonString());
    }

    private void WriteJeSettings(params (string Name, string Value)[] settings)
    {
        Directory.CreateDirectory(JeFolder);
        var root = new System.Xml.Linq.XElement("PluginConfiguration", settings.Select(s => new System.Xml.Linq.XElement(s.Name, s.Value)));
        root.Save(Path.Combine(JeFolder, JellyfinEnhancedFiles.SettingsFile));
    }

    // A real run takes the PlanToken of a dry run made just before it, unless it is given one.
    private async Task<IDictionary<string, object?>> JeAsync(bool dryRun = true, UserRatingsJeImportRequest? body = null, string? plan = null)
    {
        if (!dryRun && plan is null)
        {
            plan = (string)(await JeAsync(true, body))["PlanToken"]!;
        }

        var request = JeImportRequest.Parse(dryRun, body);
        var (file, settings) = Je.Read();
        using var cancel = new CancellationTokenSource();
        var job = new AdminJob(Guid.NewGuid(), JellyfinEnhancedImport.JobKind, dryRun, StoreFixture.T0, cancel);
        return await Je.ImportAsync(file, settings, request, plan, _s.Admin.Id, job, Ct);
    }

    private JellyfinEnhancedImport NewJe()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(JeFolder);
        var reader = new JellyfinEnhancedReader(paths.Object, null, new ServiceCollection().BuildServiceProvider(), NullLogger.Instance);
        var lines = new ArchivedLinePurges(_s.Store.Store, _s.Store.Logger, () => _detached.ArchiveOf(_consumer));
        return new JellyfinEnhancedImport(Store, _s.Library.Object, _s.UserManager.Object, () => reader, () => _detached, () => _consumer, lines, _s.Store.Clock, _s.Store.Logger);
    }

    private RatingsData JeData()
    {
        var journal = new MoveJournalReader(_s.Store.Directory.Combine("moves.jsonl"), _s.Store.Logger);
        var resolver = new IdentityResolver(_s.Library.Object, journal, _s.Store.Clock);
        var lines = new ArchivedLinePurges(_s.Store.Store, _s.Store.Logger, () => _detached.ArchiveOf(_consumer));
        return new RatingsData(_s.Store.Store, _envelope, _jobs, _s.UserManager.Object, _s.Library.Object, () => resolver, () => _detached, () => _consumer, null, lines, _s.Settings.Read, _s.Store.Clock, _s.Store.Logger, Je, _s.ItemAccess);
    }

    private static ItemChange UpdatedChange(BaseItem item)
        => new() { Kind = LibraryChangeKind.Updated, ItemId = item.Id, ItemKind = item.GetBaseItemKind(), IsFolder = item.IsFolder, Utc = DateTimeOffset.UtcNow };

    private sealed record JeEntry(string Key, Guid User, string Tmdb, string Type, double? Rating, string Content, DateTime At);
}
