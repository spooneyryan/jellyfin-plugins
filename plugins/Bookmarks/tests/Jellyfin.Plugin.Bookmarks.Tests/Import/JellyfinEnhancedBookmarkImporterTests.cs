using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests.Import;

/// <summary>
/// The Jellyfin Enhanced bookmark import against synthetic JE data: the
/// 81-style fixture (<c>kit/testdata/je-data/import-81</c>: three user folders, 81 bookmarks, 72 named, matches by item id,
/// by TMDb id and by an episode's series id and numbers, one of two copies, videos not in the library, duplicates, JE's
/// cross-version copy, malformed entries and a user the server doesn't have) and small files for the single cases: the dry
/// run, the apply, the ledger (a second run is a no-op; a JE entry changed later is rewritten only while ours is unchanged),
/// the clamp, unknown formats and unreadable files, the 404 and 503 answers, the owners' visibility, the checklist's gate
/// and the log.
/// </summary>
public sealed class JellyfinEnhancedBookmarkImporterTests : IDisposable
{
    private readonly JeImportFixture _fx = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task TheEightyOneFixture_DryRun_ReportsEveryOutcome_PerUser_AndWritesNothing()
    {
        _fx.BuildLibrary();
        _fx.CopyFixtureFiles();
        await AddViewersBankJobAsync();
        var before = await File.ReadAllBytesAsync(_fx.StoreFile, Ct);

        var report = await _fx.Importer.ImportAsync(dryRun: true, null, JeImportFixture.AdminId, Ct);

        Assert.True(report.DryRun);
        Assert.Equal("je-bookmark/1", report.Format);
        Assert.Null(report.AppliedUtc);
        Assert.Null(report.Snapshot);
        Assert.Empty(report.Checklist);
        Assert.False(report.ChecklistReady);
        Assert.Equal(["00000000000000000000000000000081", "00000000000000000000000000000082", "00000000000000000000000000000083"], report.Users.Select(u => u.JeUserId));

        var admin = report.Users[0];
        Assert.Equal((JeImportFixture.AdminId, "admin"), (admin.UserId!.Value, admin.UserName));
        Assert.Equal(
            (70, 64, 57, 3, 1, 3, 0, 2, 0, 4, 1),
            (admin.Found, admin.Import, admin.MatchedById, admin.MatchedByProviderId, admin.Ambiguous, admin.Detached, admin.Updated, admin.Duplicates, admin.AlreadyImported, admin.Skipped, admin.Clamped));
        Assert.Empty(admin.Errors);
        var viewer = report.Users[1];
        Assert.Equal((9, 8, 7, 0, 0, 1, 1, 0), (viewer.Found, viewer.Import, viewer.MatchedById, viewer.MatchedByProviderId, viewer.Ambiguous, viewer.Detached, viewer.Duplicates, viewer.Skipped));
        var nobody = report.Users[2];
        Assert.Null(nobody.UserId);
        Assert.Equal((2, 0, 2), (nobody.Found, nobody.Import, nobody.Skipped));
        Assert.Equal("No user on this server has the id 00000000000000000000000000000083; its 2 Jellyfin Enhanced bookmark(s) are skipped.", Assert.Single(nobody.Errors));

        // Every user's counts add up (the report's contract).
        Assert.All(report.Users, u =>
        {
            Assert.Equal(u.Found, u.Import + u.Updated + u.Duplicates + u.AlreadyImported + u.Skipped);
            Assert.Equal(u.Import, u.MatchedById + u.MatchedByProviderId + u.Ambiguous + u.Detached);
        });

        // 81 rows, 50 shown, the unusual outcomes first: the six skipped, the three duplicates, the four not in the library,
        // the ambiguous one, the one moved off the end, then the three matched by TMDb or TVDb.
        Assert.Equal(50, report.Samples.Count);
        Assert.Equal(31, report.MoreEntries);
        Assert.Equal(
            Enumerable.Repeat(BookmarkJeImportOutcome.Skipped, 6)
                .Concat(Enumerable.Repeat(BookmarkJeImportOutcome.Duplicate, 3))
                .Concat(Enumerable.Repeat(BookmarkJeImportOutcome.Detached, 4))
                .Append(BookmarkJeImportOutcome.Ambiguous)
                .Append(BookmarkJeImportOutcome.MatchedById)
                .Concat(Enumerable.Repeat(BookmarkJeImportOutcome.MatchedByProviderId, 3)),
            report.Samples.Take(18).Select(s => s.Outcome));
        Assert.Contains(report.Samples, s => s.Reason.StartsWith("Malformed: ", StringComparison.Ordinal));
        Assert.Contains(report.Samples, s => s.Reason.Contains("SyncedFrom", StringComparison.Ordinal));
        Assert.Contains(report.Samples, s => s.Reason == "Malformed: its ItemId isn't a Jellyfin item id.");
        Assert.Contains(report.Samples, s => s.Outcome == BookmarkJeImportOutcome.Duplicate && s.Label == "Bank job" && s.Reason.Contains("already has", StringComparison.Ordinal));
        Assert.Contains(report.Samples, s => s.Outcome == BookmarkJeImportOutcome.MatchedByProviderId && s.Reason == "Jellyfin Enhanced's item id is gone; matched by TMDb 900001.");
        Assert.Contains(report.Samples, s => s.Outcome == BookmarkJeImportOutcome.MatchedByProviderId && s.Reason == "Jellyfin Enhanced's item id is gone; matched by the series' TMDb 1396 or TVDb 900401, S01E02.");
        Assert.Contains(report.Samples, s => s.Outcome == BookmarkJeImportOutcome.Ambiguous && s.Reason.StartsWith("2 videos have TMDb 900201", StringComparison.Ordinal));
        Assert.Contains(report.Samples, s => s.Label == "Past the end (JE kept it)" && s.StartTicks == TimeSpan.FromSeconds(179).Ticks && s.Reason.EndsWith("It was past the end of the video (4:10), so it was moved to a second before the end.", StringComparison.Ordinal));

        // Nothing written: the file, the ledger and the runs as they were.
        Assert.Equal(before, await File.ReadAllBytesAsync(_fx.StoreFile, Ct));
        Assert.Empty(_fx.Store.ImportLedger);
        Assert.Empty(_fx.Store.ImportRuns);
        Assert.False(Directory.Exists(Path.Combine(_fx.SnapshotsFolder, "pre-op")));
    }

    [Fact]
    public async Task Apply_WritesTheBookmarks_WithNamesPositionsAndOrigin_TheLedger_AndARunPerUser_InOneWrite()
    {
        _fx.BuildLibrary();
        _fx.CopyFixtureFiles();
        var native = await AddViewersBankJobAsync();
        var writtenBefore = await File.ReadAllBytesAsync(_fx.StoreFile, Ct);

        var report = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);

        Assert.False(report.DryRun);
        Assert.Equal(JeImportFixture.Now.UtcDateTime, report.AppliedUtc);
        Assert.Equal((64, 8), (report.Users[0].Import, report.Users[1].Import));
        var imported = _fx.Store.GetAll().Where(b => b.Origin == BookmarkOrigin.JellyfinEnhanced).ToList();
        Assert.Equal(72, imported.Count);
        Assert.Equal(64, imported.Count(b => b.UserId == JeImportFixture.AdminId));
        Assert.Single(_fx.Store.GetAll(), b => b.Origin == BookmarkOrigin.Native && b.Id == native.Id);
        Assert.All(imported, b =>
        {
            Assert.Null(b.EndTicks);
            Assert.Null(b.MediaSourceId);
            Assert.Null(b.AudioStreamIndex);
            Assert.Null(b.Clip);
            Assert.Null(b.DeletedUtc);
        });

        // One write, after a snapshot of the file as it was.
        Assert.Equal(writtenBefore, await File.ReadAllBytesAsync(_fx.StoreFile + ".bak", Ct));
        var snapshot = Assert.Single(Directory.GetFiles(Path.Combine(_fx.SnapshotsFolder, "pre-op"), "bookmarks-import-*"));
        Assert.Equal(Path.GetFileName(snapshot), report.Snapshot);

        // Names and positions kept; the label normalised; a long one cut, whole in the note.
        var byName = imported.Where(b => b.Name is not null).GroupBy(b => b.Name!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(179).Ticks, byName["Past the end (JE kept it)"].StartTicks);
        Assert.Equal("Line one line two and a tab", byName["Line one line two and a tab"].Name);
        Assert.Equal("<b>Tears</b> in rain", byName["<b>Tears</b> in rain"].Name);
        Assert.Equal("Am\u00e9lie\u2019s caf\u00e9 sc\u00e8ne \ud83c\udfac", byName["Am\u00e9lie\u2019s caf\u00e9 sc\u00e8ne \ud83c\udfac"].Name);
        Assert.Equal(TimeSpan.FromSeconds(33.333).Ticks, byName["Am\u00e9lie\u2019s caf\u00e9 sc\u00e8ne \ud83c\udfac"].StartTicks);
        var longOne = imported.Single(b => b.Note is not null);
        Assert.Equal(100, longOne.Name!.Length);
        Assert.StartsWith("A label much longer than the hundred characters", longOne.Name, StringComparison.Ordinal);
        Assert.EndsWith("cut at 100", longOne.Note, StringComparison.Ordinal);
        Assert.Equal(9, imported.Count(b => b.Name is null)); // the blank and missing labels

        // Matched by TMDb, by the series' TVDb id and numbers, and one of two copies: attached, identities captured.
        Assert.All(imported.Where(b => b.Name is "Tagged by TMDb" or "Tagged again"), b =>
        {
            Assert.Equal(_fx.Item("tagged").Id, b.ItemId);
            Assert.Equal("900001", b.Identity.ProviderIds["Tmdb"]);
            Assert.Equal("Tagged Movie (2012)", b.ItemName);
            Assert.Null(b.DetachedUtc);
            Assert.Equal(JeImportFixture.Now.UtcDateTime, b.AttachedUtc);
        });
        var coldOpen = byName["Cold open"];
        Assert.Equal(_fx.Item("s01e02").Id, coldOpen.ItemId);
        Assert.Equal(("Episode", 1, 2), (coldOpen.Identity.Kind, coldOpen.Identity.Season!.Value, coldOpen.Identity.Episode!.Value));
        Assert.Equal("Test Series S01E02 - Episode 2", coldOpen.ItemName);
        Assert.Contains(byName["Which remake?"].ItemId, new[] { _fx.Item("sameid1999").Id, _fx.Item("sameid2005").Id });

        // Not in library: detached with what JE knew, its own item id kept, no runtime (the import relink policy).
        var gone = byName["Gone film"];
        Assert.Equal(Guid.ParseExact("b14000000000000000000000000000f1", "N"), gone.ItemId);
        Assert.Equal(JeImportFixture.Now.UtcDateTime, gone.DetachedUtc);
        Assert.Null(gone.AttachedUtc);
        Assert.Equal(("Movie", "A Film Not Here (1999)", "999001"), (gone.Identity.Kind, gone.Identity.Name, gone.Identity.ProviderIds["Tmdb"]));
        Assert.Null(gone.Identity.RunTimeTicks);
        Assert.Equal("A Film Not Here (1999)", gone.ItemName);
        Assert.Equal(TimeSpan.FromSeconds(3600.5).Ticks, gone.StartTicks);
        var missingEpisode = byName["Missing episode"];
        Assert.Equal(("Episode", 3, 4), (missingEpisode.Identity.Kind, missingEpisode.Identity.Season!.Value, missingEpisode.Identity.Episode!.Value));
        Assert.Equal("999003", missingEpisode.Identity.SeriesProviderIds!["Tvdb"]);
        Assert.True(BookmarkRecordRules.HasLibraryItem(gone));

        // JE's own times.
        var first = imported.Single(b => b.Name == "Bank job" && b.UserId == JeImportFixture.AdminId);
        Assert.Equal(new DateTime(2026, 3, 2, 11, 38, 8, 143, DateTimeKind.Utc), first.CreatedAt);
        Assert.Equal(first.CreatedAt, first.UpdatedAt);

        // The ledger: every imported entry and every duplicate, with the time JE and we gave it; a run per user file, the
        // unknown user's included (its file was read and every entry skipped).
        Assert.Equal(72 + 3, _fx.Store.ImportLedger.Count);
        Assert.Equal(3, _fx.Store.ImportLedger.Values.Count(l => l.Outcome == "Duplicate" && l.BookmarkId == Guid.Empty));
        Assert.All(_fx.Store.ImportLedger.Values.Where(l => l.Outcome == "Imported"), l => Assert.Equal(_fx.Store.Get(l.BookmarkId)!.UpdatedAt, l.RecordUpdatedAt));
        Assert.Equal(["je:00000000000000000000000000000081", "je:00000000000000000000000000000082", "je:00000000000000000000000000000083"], _fx.Store.ImportRuns.Keys);
        Assert.Equal((70, 64, 3, 2, 4), (_fx.Store.ImportRuns["je:00000000000000000000000000000081"].Found, _fx.Store.ImportRuns["je:00000000000000000000000000000081"].Imported, _fx.Store.ImportRuns["je:00000000000000000000000000000081"].Detached, _fx.Store.ImportRuns["je:00000000000000000000000000000081"].Duplicates, _fx.Store.ImportRuns["je:00000000000000000000000000000081"].Skipped));
        Assert.Equal("admin", _fx.Store.KnownUsers.NameOf(JeImportFixture.AdminId));
        Assert.Equal("viewer", _fx.Store.KnownUsers.NameOf(JeImportFixture.ViewerId));

        // The report carries the switch-off checklist now (every JE user is covered), the shortcut steps last.
        Assert.True(report.ChecklistReady);
        // JE's four boxes in the order its Pages tab lets them be unticked (12.11 and 12.12 alike), and the key the way its
        // Keyboard tab sets it: this reader has no plugin manager, so JE's version is unknown
        // and the checklist takes the newest page's words, 12.12's grid of keys (the kit's JeSwitchOffTests have 12.11's).
        Assert.Equal(["BookmarksAutoCreateCustomTab", "BookmarksUseCustomTabs", "BookmarksUsePluginPages", "BookmarksEnabled", "CustomTab", "PluginPagesLink", "ShortcutAdmin", "ShortcutUsers"], report.Checklist.Select(i => i.Key));
        Assert.Contains("Keyboard tab, click the key (B) at the left end of the Bookmark Current Time row, press another key combination, such as Shift+X, then Save Settings", report.Checklist[6].Text, StringComparison.Ordinal);
        Assert.Contains("never Delete or the ⊘ button beside the key (Disable for every user), never a blank key (Backspace puts B back)", report.Checklist[6].Text, StringComparison.Ordinal);

        // A restart reads the same: names, ledger and runs survive the file.
        _fx.Recreate();
        Assert.Equal(72, _fx.Store.GetAll().Count(b => b.Origin == BookmarkOrigin.JellyfinEnhanced));
        Assert.Equal(75, _fx.Store.ImportLedger.Count);
        Assert.Equal(3, _fx.Store.ImportRuns.Count);
    }

    [Fact]
    public async Task ASecondRun_IsANoOp_NothingWritten_EveryEntryAlreadyImportedOrStillSkipped()
    {
        _fx.BuildLibrary();
        _fx.CopyFixtureFiles();
        await AddViewersBankJobAsync();
        await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);
        var written = await File.ReadAllBytesAsync(_fx.StoreFile, Ct);
        _fx.Clock.Advance(TimeSpan.FromHours(1));

        var again = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);

        Assert.Equal((0, 66, 4), (again.Users[0].Import, again.Users[0].AlreadyImported, again.Users[0].Skipped));
        Assert.Equal((0, 9), (again.Users[1].Import, again.Users[1].AlreadyImported));
        Assert.Equal((0, 2), (again.Users[2].Import, again.Users[2].Skipped));
        Assert.Null(again.Snapshot);
        Assert.Equal(written, await File.ReadAllBytesAsync(_fx.StoreFile, Ct));
        Assert.Single(Directory.GetFiles(Path.Combine(_fx.SnapshotsFolder, "pre-op")));
        Assert.Contains(again.Samples, s => s.Outcome == BookmarkJeImportOutcome.AlreadyImported && s.Reason == "Imported on 2026-10-07 12:00 UTC.");

        // The viewer's rows (past the 50 samples above): the duplicate stays one.
        var viewer = await _fx.Importer.ImportAsync(dryRun: true, new BookmarkJeImportRequest { UserIds = [JeImportFixture.ViewerId] }, JeImportFixture.AdminId, Ct);
        Assert.Contains(viewer.Samples, s => s.Outcome == BookmarkJeImportOutcome.AlreadyImported && s.Label == "Bank job" && s.Reason == "Left out as a duplicate on 2026-10-07 12:00 UTC.");
        Assert.True(again.ChecklistReady);
    }

    [Fact]
    public async Task AJeEntryChangedLater_IsRewritten_OnlyWhileOursIsUnchanged_ADeletedOneStaysDeleted()
    {
        // The import's rule: a newer local edit wins.
        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(
            JeImportFixture.AdminId,
            ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "First")),
            ("Bm_2", JeImportFixture.Entry(movie.Id, 120, "Second")),
            ("Bm_3", JeImportFixture.Entry(movie.Id, 180, "Third")),
            ("Bm_4", JeImportFixture.Entry(movie.Id, 240, "Fourth")));
        await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);
        var ids = _fx.Store.ImportLedger.ToDictionary(l => l.Key[(l.Key.LastIndexOf(':') + 1)..], l => l.Value.BookmarkId, StringComparer.Ordinal);
        var firstCreated = _fx.Store.Get(ids["Bm_1"])!.CreatedAt;

        // Here: the second renamed by its owner, the third deleted. In JE: all four changed later; the fourth's time not.
        await _fx.Store.UpdateAsync(ids["Bm_2"], b => b with { Name = "Mine now", UpdatedAt = BookmarkRecordRules.NextUpdatedAt(b.UpdatedAt, DateTime.UtcNow) }, Ct);
        await _fx.Store.RemoveAsync(ids["Bm_3"], Ct);
        _fx.WriteBookmarks(
            JeImportFixture.AdminId,
            ("Bm_1", JeImportFixture.Entry(movie.Id, 65, "First, edited in JE", "2026-06-01T08:00:00.000Z")),
            ("Bm_2", JeImportFixture.Entry(movie.Id, 125, "Second, edited in JE", "2026-06-01T08:00:00.000Z")),
            ("Bm_3", JeImportFixture.Entry(movie.Id, 185, "Third, edited in JE", "2026-06-01T08:00:00.000Z")),
            ("Bm_4", JeImportFixture.Entry(movie.Id, 245, "Fourth, edited without a new time")));
        _fx.Clock.Advance(TimeSpan.FromDays(1));

        var dry = await _fx.Importer.ImportAsync(dryRun: true, null, JeImportFixture.AdminId, Ct);
        Assert.Equal((0, 1, 3), (dry.Users[0].Import, dry.Users[0].Updated, dry.Users[0].AlreadyImported));
        Assert.Equal("First", _fx.Store.Get(ids["Bm_1"])!.Name);

        var report = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);

        Assert.Equal((0, 1, 3), (report.Users[0].Import, report.Users[0].Updated, report.Users[0].AlreadyImported));
        var updated = _fx.Store.Get(ids["Bm_1"])!;
        Assert.Equal(("First, edited in JE", TimeSpan.FromSeconds(65).Ticks, firstCreated), (updated.Name, updated.StartTicks, updated.CreatedAt));
        Assert.Equal(new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), updated.UpdatedAt);
        Assert.Equal(updated.UpdatedAt, _fx.Store.ImportLedger["je:00000000000000000000000000000081:Bm_1"].RecordUpdatedAt);
        Assert.Equal("Mine now", _fx.Store.Get(ids["Bm_2"])!.Name);
        Assert.Null(_fx.Store.Get(ids["Bm_3"]));
        Assert.Equal("Fourth", _fx.Store.Get(ids["Bm_4"])!.Name);
        Assert.Contains(report.Samples, s => s.JeKey == "Bm_1" && s.Outcome == BookmarkJeImportOutcome.Updated);
        Assert.Contains(report.Samples, s => s.JeKey == "Bm_2" && s.Reason.EndsWith("the change made here wins.", StringComparison.Ordinal));
        Assert.Contains(report.Samples, s => s.JeKey == "Bm_3" && s.Reason.EndsWith("deleted here since: not brought back.", StringComparison.Ordinal));
        Assert.Equal(3, _fx.Store.GetAll().Count);
    }

    [Fact]
    public async Task Duplicates_OfOurOwnAndOfEachOther_AreLeftOut_AndLookedAtAgainOnlyWhenJeChangesThem()
    {
        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var ours = TestData.CreateBookmark(JeImportFixture.AdminId, 60, null, movie.Id) with { Name = "Bank job" };
        await _fx.Store.AddAsync(ours, Ct);
        _fx.WriteBookmarks(
            JeImportFixture.AdminId,
            ("Bm_1", JeImportFixture.Entry(movie.Id, 60.9, "Bank job")),         // ours, within a second
            ("Bm_2", JeImportFixture.Entry(movie.Id, 61.5, "Bank job")),         // more than a second away: imported
            ("Bm_3", JeImportFixture.Entry(movie.Id, 62.4, "Bank job")),         // within a second of Bm_2
            ("Bm_4", JeImportFixture.Entry(movie.Id, 62.4, "Another name")),     // another name: imported
            ("Bm_5", JeImportFixture.Entry(movie.Id, 300, null)),
            ("Bm_6", JeImportFixture.Entry(movie.Id, 300.5, "  ")));             // no name equals no name

        var report = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);

        Assert.Equal((3, 3), (report.Users[0].Import, report.Users[0].Duplicates));
        Assert.Equal(4, _fx.Store.GetAll().Count);
        Assert.Contains(report.Samples, s => s.JeKey == "Bm_1" && s.Reason.Contains("already has", StringComparison.Ordinal));
        Assert.Contains(report.Samples, s => s.JeKey == "Bm_3" && s.Reason.Contains("imported in this run", StringComparison.Ordinal));

        // A duplicate JE changed since (a new label, a new time) is looked at again, and now imported.
        _fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60.9, "Bank job, the reflection", "2026-07-01T00:00:00.000Z")));
        var again = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);
        Assert.Equal((1, 0), (again.Users[0].Import, again.Users[0].Duplicates));
        Assert.Contains(_fx.Store.GetAll(), b => b.Name == "Bank job, the reflection");
    }

    [Fact]
    public async Task UnreadableUnsupportedAndMalformedFiles_AreReported_NothingIsWrittenForThem_AndTheirUsersStayPending()
    {
        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "Fine")));
        _fx.WriteBookmarks(JeImportFixture.ViewerId, "{ \"Bookmarks\": { \"Bm_1\": { \"ItemId\": ");       // cut short (JE writing)
        var third = Guid.NewGuid();
        _fx.Users.Setup(m => m.GetUserById(third)).Returns(TestData.CreateUser("third"));
        _fx.WriteBookmarks(third, "{ \"Marks\": { } }");                                                 // another shape

        var report = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);

        Assert.Equal(1, report.Users.Single(u => u.UserId == JeImportFixture.AdminId).Import);
        var cut = report.Users.Single(u => u.JeUserId == JeImportFixture.ViewerId.ToString("N"));
        Assert.Equal(0, cut.Found);
        Assert.StartsWith("Couldn't read the Jellyfin Enhanced bookmarks of viewer (", Assert.Single(cut.Errors), StringComparison.Ordinal);
        Assert.EndsWith("Try again; nothing was imported for them.", cut.Errors[0], StringComparison.Ordinal);
        var other = report.Users.Single(u => u.JeUserId == third.ToString("N"));
        Assert.StartsWith("Unsupported Jellyfin Enhanced format", Assert.Single(other.Errors), StringComparison.Ordinal);
        Assert.Single(_fx.Store.GetAll());
        Assert.Equal(["je:" + JeImportFixture.AdminId.ToString("N")], _fx.Store.ImportRuns.Keys);

        // The two files keep the checklist closed: they were never imported.
        Assert.False(report.ChecklistReady);
        Assert.Empty(report.Checklist);
        var gate = _fx.Importer.Describe();
        Assert.False((bool)gate["ChecklistReady"]!);
        Assert.Equal(2, ((System.Collections.ICollection)gate["Pending"]!).Count);
        Assert.True(_fx.Importer.HasPendingImport());
    }

    [Fact]
    public async Task AFileWithBytesThatArentUtf8_IsReportedUnreadable_NotAnError_AndTheOthersAreImported()
    {
        // Bookmarks 1.2.1, through the kit's JeDataParser: a label whose bytes aren't UTF-8 made the import answer 500 in
        // 1.2.0 (the parser's GetString threw); the file is now Unreadable with the plain reason, its user stays pending.
        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "Fine")));
        var text = System.Text.Json.JsonSerializer.Serialize(new { Bookmarks = new Dictionary<string, object> { ["Bm_1"] = JeImportFixture.Entry(movie.Id, 90, "LABEL") } });
        var bytes = System.Text.Encoding.UTF8.GetBytes(text).ToList();
        var at = text.IndexOf("LABEL", StringComparison.Ordinal);
        bytes[at + 1] = 0xFF;
        bytes[at + 2] = 0xFE;
        _fx.WriteBookmarks(JeImportFixture.ViewerId, bytes.ToArray());

        var dry = await _fx.Importer.ImportAsync(dryRun: true, null, JeImportFixture.AdminId, Ct);
        var report = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);

        foreach (var run in new[] { dry, report })
        {
            var viewer = run.Users.Single(u => u.JeUserId == JeImportFixture.ViewerId.ToString("N"));
            Assert.Equal(0, viewer.Found);
            Assert.Equal(
                "Couldn't read the Jellyfin Enhanced bookmarks of viewer (Not valid text: the file has bytes that aren't UTF-8.). Try again; nothing was imported for them.",
                Assert.Single(viewer.Errors));
        }

        Assert.Equal(1, report.Users.Single(u => u.UserId == JeImportFixture.AdminId).Import);
        Assert.Equal(JeImportFixture.AdminId, Assert.Single(_fx.Store.GetAll()).UserId);
        Assert.True(_fx.Importer.HasPendingImport());
    }

    [Fact]
    public async Task NoJeData_Is404_TooManyOrEmptyUserIds_Are400_AndReadOnlyData_Refuses_TheImport_Not_TheDryRun()
    {
        var none = await Assert.ThrowsAsync<BookmarkException>(() => _fx.Importer.ImportAsync(true, null, JeImportFixture.AdminId, Ct));
        Assert.Equal(PluginErrorKind.NotFound, none.Kind);
        Assert.Equal(JellyfinEnhancedBookmarkImporter.NoDataMessage, none.Message);

        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "Fine")));
        var other = await Assert.ThrowsAsync<BookmarkException>(() => _fx.Importer.ImportAsync(true, new BookmarkJeImportRequest { UserIds = [JeImportFixture.ViewerId] }, JeImportFixture.AdminId, Ct));
        Assert.Equal(PluginErrorKind.NotFound, other.Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<BookmarkException>(() => _fx.Importer.ImportAsync(true, new BookmarkJeImportRequest { UserIds = [Guid.Empty] }, JeImportFixture.AdminId, Ct))).Kind);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<BookmarkException>(() => _fx.Importer.ImportAsync(true, new BookmarkJeImportRequest { UserIds = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToList() }, JeImportFixture.AdminId, Ct))).Kind);
        Assert.Equal(1, (await _fx.Importer.ImportAsync(true, new BookmarkJeImportRequest { UserIds = [JeImportFixture.AdminId] }, JeImportFixture.AdminId, Ct)).Users[0].Import);

        // A newer version's file: read-only. The dry run still reports; the import is the store's 503, nothing changed.
        _fx.Store.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(_fx.StoreFile)!);
        await File.WriteAllTextAsync(_fx.StoreFile, "{ \"Version\": 3, \"Bookmarks\": [] }", Ct);
        _fx.Recreate();
        Assert.True(_fx.Store.IsReadOnly);
        var dry = await _fx.Importer.ImportAsync(true, null, JeImportFixture.AdminId, Ct);
        Assert.True(dry.StoreReadOnly);
        var refused = await Assert.ThrowsAsync<PluginException>(() => _fx.Importer.ImportAsync(false, null, JeImportFixture.AdminId, Ct));
        Assert.Equal(PluginErrorKind.Unavailable, refused.Kind);
        Assert.Equal("{ \"Version\": 3, \"Bookmarks\": [] }", await File.ReadAllTextAsync(_fx.StoreFile, Ct));
    }

    [Fact]
    public async Task AJeDataFolderThatCantBeListed_Is503_AndKeepsTheChecklistClosed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The listing is denied with a Windows ACL.");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "Fine")));
        var folder = new DirectoryInfo(_fx.DataFolder);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var security = folder.GetAccessControl();
        security.AddAccessRule(deny);
        folder.SetAccessControl(security);
        try
        {
            var refused = await Assert.ThrowsAsync<PluginException>(() => _fx.Importer.ImportAsync(true, null, JeImportFixture.AdminId, Ct));
            Assert.Equal(PluginErrorKind.Unavailable, refused.Kind);
            var gate = _fx.Importer.Describe();
            Assert.False((bool)gate["ChecklistReady"]!);
            Assert.NotNull(gate["Problem"]);
            Assert.False(_fx.Importer.HasPendingImport());
        }
        finally
        {
            security.RemoveAccessRule(deny);
            folder.SetAccessControl(security);
        }
    }

    [Fact]
    public async Task TheGate_OpensOnlyOnceEveryJeUserWithBookmarks_HasAnAppliedImport_NotADryRun()
    {
        // No JE bookmarks at all: nothing to import first, the checklist may show.
        var empty = _fx.Importer.Describe();
        Assert.True((bool)empty["ChecklistReady"]!);
        Assert.Equal(0, empty["UsersWithBookmarks"]);
        Assert.False(_fx.Importer.HasPendingImport());

        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "Fine")));
        _fx.WriteBookmarks(JeImportFixture.ViewerId, "{ \"Bookmarks\": { } }");
        Assert.False((bool)_fx.Importer.Describe()["ChecklistReady"]!);
        Assert.True(_fx.Importer.HasPendingImport());

        await _fx.Importer.ImportAsync(dryRun: true, null, JeImportFixture.AdminId, Ct);
        Assert.False((bool)_fx.Importer.Describe()["ChecklistReady"]!);

        var adminOnly = await _fx.Importer.ImportAsync(dryRun: false, new BookmarkJeImportRequest { UserIds = [JeImportFixture.AdminId] }, JeImportFixture.AdminId, Ct);
        Assert.False(adminOnly.ChecklistReady);
        Assert.Empty(adminOnly.Checklist);
        var gate = _fx.Importer.Describe();
        Assert.False((bool)gate["ChecklistReady"]!);
        Assert.Single((System.Collections.IEnumerable)gate["Pending"]!);

        // The viewer's empty file covered too (nothing to import, but read): open.
        var all = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);
        Assert.True(all.ChecklistReady);
        Assert.Equal(8, all.Checklist.Count);
        Assert.True((bool)_fx.Importer.Describe()["ChecklistReady"]!);
        Assert.False(_fx.Importer.HasPendingImport());
        Assert.Equal(1, _fx.Importer.Describe()["ImportedBookmarks"]);

        // A JE user who gets bookmarks later closes it again until they are imported.
        _fx.WriteBookmarks(Guid.NewGuid(), ("Bm_9", JeImportFixture.Entry(movie.Id, 90, "Later")));
        Assert.False((bool)_fx.Importer.Describe()["ChecklistReady"]!);
    }

    [Fact]
    public async Task VideosTheOwnerCantOpen_AreNeverMatched_ByIdOrByTmdb()
    {
        // The kit's privacy rule: a user's record only names videos they can open; the import matches as the owner.
        var hidden = _fx.Library.AddMovie("Hidden", 2001, TimeSpan.FromMinutes(90), null, ("Tmdb", "700001"));
        var visibleCopy = _fx.Library.AddMovie("Shown", 2001, TimeSpan.FromMinutes(90), null, ("Tmdb", "700002"));
        var hiddenCopy = _fx.Library.AddMovie("Shown", 2001, TimeSpan.FromMinutes(90), @"C:\media\Movies 2\Shown.mkv", ("Tmdb", "700002"));
        _fx.Library.Hide(_fx.Viewer, hidden, hiddenCopy);
        _fx.WriteBookmarks(
            JeImportFixture.ViewerId,
            ("Bm_1", JeImportFixture.Entry(hidden.Id, 60, "On a hidden film", tmdb: "700001")),
            ("Bm_2", JeImportFixture.Entry(Guid.NewGuid(), 60, "By TMDb, one copy hidden", tmdb: "700002")));

        var report = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);

        var viewer = report.Users.Single();
        Assert.Equal((1, 1, 0), (viewer.Detached, viewer.MatchedByProviderId, viewer.Ambiguous));
        var onHidden = _fx.Store.GetAll().Single(b => b.Name == "On a hidden film");
        Assert.Equal((hidden.Id, true), (onHidden.ItemId, onHidden.DetachedUtc is not null));
        Assert.Equal("A film (2000)", onHidden.ItemName);
        Assert.Equal(visibleCopy.Id, _fx.Store.GetAll().Single(b => b.Name == "By TMDb, one copy hidden").ItemId);
    }

    [Fact]
    public async Task NamesAndLabels_NeverReachTheLog_OnlyCounts()
    {
        _fx.BuildLibrary();
        _fx.CopyFixtureFiles();

        await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);

        var information = _fx.Logger.Entries.Where(e => e.Level >= LogLevel.Information).ToList();
        var line = Assert.Single(information, e => e.Message.StartsWith("Jellyfin Enhanced bookmark import applied", StringComparison.Ordinal));
        Assert.Equal("Jellyfin Enhanced bookmark import applied for 3 user(s): 73 added (4 not in the library), 0 updated, 2 duplicates, 0 already imported, 6 skipped", line.Message);
        var labels = new[] { "Bank job", "Cold open", "Gone film", "Tears", "Viewer:", "Nobody here" };
        Assert.DoesNotContain(_fx.Logger.Entries, e => labels.Any(l => e.Message.Contains(l, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AUserWithTwoFolderSpellings_IsImportedOnce_AndWaitsOnceAtTheGate()
    {
        // JE 12.8+ renames other spellings to the N form at its start, but a D-form folder
        // survives when the N folder is already there. Read twice, the user's bookmarks were imported twice.
        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.ViewerId, ("Bm_1700000000000_twice", JeImportFixture.Entry(movie.Id, 60, "Twice")));
        var dashed = Path.Combine(_fx.DataFolder, JeImportFixture.ViewerId.ToString("D"));
        Directory.CreateDirectory(dashed);
        File.Copy(Path.Combine(_fx.DataFolder, JeImportFixture.ViewerId.ToString("N"), "bookmark.json"), Path.Combine(dashed, "bookmark.json"));

        var gate = _fx.Importer.Describe();
        Assert.Equal(1, gate["UsersWithBookmarks"]);
        Assert.Single((System.Collections.IEnumerable)gate["Pending"]!);

        var dry = await _fx.Importer.ImportAsync(dryRun: true, null, JeImportFixture.AdminId, Ct);
        var row = Assert.Single(dry.Users);
        Assert.Equal((1, 1, 0), (row.Found, row.Import, row.Duplicates));
        Assert.Single(dry.Samples);

        var applied = await _fx.Importer.ImportAsync(dryRun: false, null, JeImportFixture.AdminId, Ct);
        Assert.Equal(1, Assert.Single(applied.Users).Import);
        var bookmark = Assert.Single(_fx.Store.GetAll());
        Assert.Equal(bookmark.Id, Assert.Single(_fx.Store.ImportLedger.Values).BookmarkId);
        Assert.True(applied.ChecklistReady);
        Assert.False(_fx.Importer.HasPendingImport());
    }

    [Fact]
    public void ThePlan_TakesEachJeUsersFileOnce_EvenWhenGivenItTwice()
    {
        // The planner's own guard behind the reader's: a file planned twice would add each bookmark twice, since neither
        // the ledger nor the duplicate check sees what the run plans for the same user before.
        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "Once")), ("Bm_2", JeImportFixture.Entry(movie.Id, 90, null)));
        var source = new JeImportSource(JeImportFixture.AdminId, _fx.Admin, _fx.Reader.ReadBookmarks(JeImportFixture.AdminId));
        var document = _fx.Store.Documents.Current;
        var resolution = new JeEntryResolution(BookmarkJeImportOutcome.MatchedById, movie.Id, ItemIdentity.Capture(movie, _fx.Library.Object), "Heat (1995)", movie.RunTimeTicks, "On it.");

        Assert.Equal(2, JeImportPlanner.ToResolve(document, [source, source]).Count);
        var plan = JeImportPlanner.Plan(document, [source, source with { }], _ => resolution, JeImportFixture.Now.UtcDateTime, JeImportFixture.AdminId);

        Assert.Equal(2, Assert.Single(plan.Users).Import);
        Assert.Equal(2, plan.Added.Count);
        Assert.Equal(2, plan.Ledger.Count);
        Assert.Equal(plan.Added.Select(b => b.Id).Order(), plan.Ledger.Values.Select(l => l.BookmarkId).Order());
        Assert.Single(plan.Runs);
    }

    [Fact]
    public async Task TheRunEntry_NamesTheAdminWhoAppliedIt_AndNoOneForAnApiKey()
    {
        // Found in testing: AppliedBy was never set, so every run said 00000000-….
        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.AdminId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "By an admin")));
        _fx.WriteBookmarks(JeImportFixture.ViewerId, ("Bm_1", JeImportFixture.Entry(movie.Id, 60, "By an API key")));

        await _fx.Importer.ImportAsync(dryRun: false, new BookmarkJeImportRequest { UserIds = [JeImportFixture.AdminId] }, JeImportFixture.AdminId, Ct);
        await _fx.Importer.ImportAsync(dryRun: false, new BookmarkJeImportRequest { UserIds = [JeImportFixture.ViewerId] }, Guid.Empty, Ct);

        Assert.Equal(JeImportFixture.AdminId, _fx.Store.ImportRuns["je:00000000000000000000000000000081"].AppliedBy);
        Assert.Null(_fx.Store.ImportRuns["je:00000000000000000000000000000082"].AppliedBy);
        var file = await File.ReadAllTextAsync(_fx.StoreFile, Ct);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(file, "\"AppliedBy\""));
        Assert.DoesNotContain(Guid.Empty.ToString("D"), file, StringComparison.Ordinal);

        var runs = ((IEnumerable<IDictionary<string, object?>>)_fx.Importer.Describe()["Runs"]!).ToList();
        Assert.Equal(("00000000000000000000000000000081", "admin"), ((string?)runs[0]["AppliedBy"], (string?)runs[0]["AppliedByName"]));
        Assert.Equal((null, null), ((string?)runs[1]["AppliedBy"], (string?)runs[1]["AppliedByName"]));

        // A restart reads it back.
        _fx.Recreate();
        Assert.Equal(JeImportFixture.AdminId, _fx.Store.ImportRuns["je:00000000000000000000000000000081"].AppliedBy);
    }

    [Fact]
    public async Task ImportsRun_OneAtATime()
    {
        var movie = _fx.Library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        _fx.WriteBookmarks(JeImportFixture.AdminId, Enumerable.Range(0, 50).Select(i => ("Bm_" + i.ToString(CultureInfo.InvariantCulture), (object)JeImportFixture.Entry(movie.Id, 60 + (i * 5), "Name " + i.ToString(CultureInfo.InvariantCulture)))).ToArray());

        var reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => _fx.Importer.ImportAsync(false, null, JeImportFixture.AdminId, Ct), Ct)));

        Assert.Equal(50, reports.Sum(r => r.Users[0].Import));
        Assert.Equal(50, _fx.Store.GetAll().Count);
        Assert.Equal(50, _fx.Store.ImportLedger.Count);
    }

    // The viewer's own bookmark "Bank job" at 12.4 s on Chaptered Movie: the fixture's viewer has the same in JE at 12.9 s.
    private async Task<Bookmark> AddViewersBankJobAsync()
    {
        var item = _fx.Item("chaptered");
        var native = TestData.CreateBookmark(JeImportFixture.ViewerId, 0, null, item.Id) with
        {
            StartTicks = TimeSpan.FromSeconds(12.4).Ticks,
            Name = "Bank job",
            ItemName = "Chaptered Movie (2022)",
            Identity = ItemIdentity.Capture(item, _fx.Library.Object)
        };
        await _fx.Store.AddAsync(native, Ct);
        return native;
    }
}
