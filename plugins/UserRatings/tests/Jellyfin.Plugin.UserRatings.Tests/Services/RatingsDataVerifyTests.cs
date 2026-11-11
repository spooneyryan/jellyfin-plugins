using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// The fixes of the 0.3.19 verification (0.3.20.0): the ledger keeps pointing at a deleted rating through Replace
/// and through a restore, a restore undoes what an import did and keeps only this server's own deletions, a moderated
/// review never comes back, the export's file really goes, and the rest.
/// </summary>
public sealed partial class RatingsDataTests
{
    // ----------------------------------------------------------------------------------- the ledger through Replace

    [Theory]
    [InlineData("Replace", "Replace")]
    [InlineData("Replace", "Merge")]
    public async Task AfterAReplaceSkippedADeletedRating_TheNextImportOfTheSameFile_SkipsItToo(string first, string second)
    {
        // Replace wiped the ledger and a skipped row wrote no ledger row, so the link X -> Y was gone after one Replace,
        // and the next import of the same file added X with the words a moderator had deleted.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var local = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Local words.", StoreFixture.T0), Ct);
        var (other, otherId) = await AnotherServersCopyAsync(row =>
        {
            row["ReviewText"] = "Words from the other server.";
            row["UpdatedUtc"] = StoreFixture.T0.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
            row["ReviewUpdatedUtc"] = StoreFixture.T0.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        });
        Assert.Equal(1, Outcome(await ImportAsync(other, "Merge", dryRun: false), "Updated"));
        await Store.DeleteAsync(local.Id, RatingValues.DeletedBy.Moderator, StoreFixture.T0.AddDays(2), Ct);

        var once = await ImportAsync(other, first, dryRun: false);
        Assert.Equal(1, Outcome(once, "SkippedDeleted"));
        var twice = await ImportAsync(other, second, dryRun: false);

        Assert.Equal((1, 0), (Outcome(twice, "SkippedDeleted"), Outcome(twice, "Added")));
        Assert.Null(Store.Get(otherId));
        Assert.Null(Store.Get(local.Id));
        var link = Assert.Single(Store.ReadTables().ImportLedger, l => l.Source == RatingsEnvelope.LedgerSource);
        Assert.Equal((otherId.ToString("N", CultureInfo.InvariantCulture), local.Id), (link.SourceKey, Assert.Single(link.RatingIds)));
    }

    [Fact]
    public async Task Replace_KeepsTheEnvelopeLedgersRowsThatPointAtADeletedRating_AndWipesTheRest()
    {
        // The wipe itself: a link to a deleted rating stays even when this file doesn't hold that rating (another file
        // may), a link to a live rating goes with the ratings Replace wipes.
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var b = _s.AddMovie(_s.Movies, "B", "2");
        var deleted = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Deleted here."), Ct);
        var live = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Live here."), Ct);
        var keyDeleted = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var keyLive = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await Store.WriteAsync(
            w =>
            {
                w.RecordImport(new ImportLedgerRecord(RatingsEnvelope.LedgerSource, keyDeleted, StoreFixture.T0, StoreFixture.T0, [deleted.Id]));
                w.RecordImport(new ImportLedgerRecord(RatingsEnvelope.LedgerSource, keyLive, StoreFixture.T0, StoreFixture.T0, [live.Id]));
                return 0;
            },
            Ct);
        await Store.DeleteAsync(deleted.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddMinutes(1), Ct);
        var file = WithoutTombstones(await ExportFileAsync());

        var report = await ImportAsync(file, "Replace", dryRun: false);

        Assert.Equal(1, Outcome(report, "Added"));
        var ledger = Store.ReadTables().ImportLedger.Where(l => l.Source == RatingsEnvelope.LedgerSource).ToDictionary(l => l.SourceKey);
        Assert.Equal([deleted.Id], ledger[keyDeleted].RatingIds);
        Assert.False(ledger.ContainsKey(keyLive));
    }

    // -------------------------------------------------------------------------- a restore undoes what an import did

    [Fact]
    public async Task RestoringAnImportsSnapshot_UndoesTheDeletionsTheFilesTombstonesMade()
    {
        // The import's own snapshot kept the deletion its file's tombstone made, though it is the way to undo the import.
        var b = _s.AddMovie(_s.Movies, "B", "2");
        var gone = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Deleted by the file's tombstone.", StoreFixture.T0), Ct);
        var crafted = await WithTombstoneAsync(gone, StoreFixture.T0.AddDays(1));

        var report = await ImportAsync(crafted, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "TombstoneDeleted"));
        Assert.StartsWith(RatingValues.DeletionOrigin.ImportPrefix, Store.GetTombstone(gone.Id)!.Origin, StringComparison.Ordinal);
        var restored = await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);

        Assert.Equal((0, 1), (restored["DeletionsKept"], restored["ImportDeletionsUndone"]));
        Assert.Equal("Deleted by the file's tombstone.", Store.Get(gone.Id)!.ReviewText);
        Assert.Null(Store.GetTombstone(gone.Id));
        var log = Store.ReadTables().ModerationLog.Last(l => l.Action == RatingValues.ModerationAction.RestoreSnapshot);
        Assert.Contains("\"KeptOnly\":\"Local\"", log.Details, StringComparison.Ordinal);
        Assert.Contains("\"ImportDeletionsUndone\":1", log.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoringAReplacesSnapshot_BringsBackARatingTheFilesOlderTombstoneNamed()
    {
        // The tombstone a Replace added (the rating was wiped first) deleted, at its own snapshot's restore, the live
        // rating that snapshot holds, newer than the tombstone.
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var kept = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Edited here after the file's deletion.", StoreFixture.T0.AddDays(2)), Ct);
        var crafted = await WithTombstoneAsync(kept, StoreFixture.T0.AddDays(1));
        Assert.Equal(1, Outcome(await ImportAsync(crafted, "Merge", dryRun: true), "TombstoneKeptNewer"));

        var report = await ImportAsync(crafted, "Replace", dryRun: false);
        Assert.Equal(1, Outcome(report, "TombstoneAdded"));
        var restored = await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);

        Assert.Equal((0, 1), (restored["DeletionsKept"], restored["ImportDeletionsUndone"]));
        Assert.Equal("Edited here after the file's deletion.", Store.Get(kept.Id)!.ReviewText);
        Assert.Null(Store.GetTombstone(kept.Id));
    }

    [Fact]
    public async Task RestoringAnEarlierSnapshot_UndoesAnImportsDeletions_AndKeepsThisServersOwn()
    {
        // The rule: restoring any snapshot from before the import undoes it, its deletions with it; a member's delete made
        // here after the snapshot, before or after the import, stays deleted.
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var b = _s.AddMovie(_s.Movies, "B", "2");
        var c = _s.AddMovie(_s.Movies, "C", "3");
        var byFile = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Deleted by the file.", StoreFixture.T0), Ct);
        var byMember = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Deleted by its member, before the import.", StoreFixture.T0), Ct);
        var byMemberLater = await Store.UpsertAsync(Rating(_s.Viewer.Id, c.Id, "Deleted by its member, after the import.", StoreFixture.T0), Ct);
        var crafted = await WithTombstoneAsync(byFile, StoreFixture.T0.AddDays(1));
        var earlier = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;
        _s.Advance(TimeSpan.FromMinutes(1));
        await Store.DeleteAsync(byMember.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddDays(1), Ct);
        Assert.Equal(1, Outcome(await ImportAsync(crafted, "Merge", dryRun: false), "TombstoneDeleted"));
        await Store.DeleteAsync(byMemberLater.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddDays(2), Ct);

        var restored = await _data.RestoreAsync(_s.Admin.Id, earlier, Ct);

        Assert.Equal((2, 1), (restored["DeletionsKept"], restored["ImportDeletionsUndone"]));
        Assert.NotNull(Store.Get(byFile.Id));
        Assert.Null(Store.Get(byMember.Id));
        Assert.Null(Store.Get(byMemberLater.Id));
        Assert.Equal(RatingValues.DeletionOrigin.Local, Store.GetTombstone(byMember.Id)!.Origin);
    }

    [Fact]
    public async Task RestoringAnImportsSnapshot_UndoesTheFilesFormerMembers()
    {
        // The file's former members were kept as FormerMembersKept, as if deleted here since.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Words."), Ct);
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var formerId = Guid.NewGuid();
        json["Data"]!["FormerUsers"] = new JsonArray(new JsonObject
        {
            ["Id"] = formerId.ToString("D", CultureInfo.InvariantCulture),
            ["OriginalUserId"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            ["DisplayName"] = "Another server's former member",
            ["DeletedUtc"] = StoreFixture.T0.ToString("O", CultureInfo.InvariantCulture)
        });
        var crafted = Path.Combine(_s.Store.Directory.Path, "former-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(crafted, json.ToJsonString(), Ct);
        var report = await ImportAsync(crafted, "Merge", dryRun: false);
        Assert.StartsWith(RatingValues.DeletionOrigin.ImportPrefix, Store.ReadTables().FormerUsers.Single(f => f.Id == formerId).Origin, StringComparison.Ordinal);

        var restored = await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);

        Assert.Equal((0, 1), (restored["FormerMembersKept"], restored["ImportFormerMembersUndone"]));
        Assert.False(Store.GetFormerUsers().ContainsKey(formerId));
    }

    [Fact]
    public async Task TheExportCarriesNoOrigin_AndAnImportsTombstonesCarryItsJob()
    {
        // The origin says which job of this server made a deletion: it means nothing on another server, so it isn't exported.
        var b = _s.AddMovie(_s.Movies, "B", "2");
        var gone = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Deleted by the file's tombstone.", StoreFixture.T0), Ct);
        var crafted = await WithTombstoneAsync(gone, StoreFixture.T0.AddDays(1));
        var report = await ImportAsync(crafted, "Merge", dryRun: false);
        var origin = Store.GetTombstone(gone.Id)!.Origin;
        var envelopeRow = Store.ReadTables().ModerationLog.Last(l => l.Action == RatingValues.ModerationAction.ImportEnvelope);
        Assert.Contains("\"Origin\":\"" + origin + "\"", envelopeRow.Details, StringComparison.Ordinal);
        Assert.Contains("\"DeletedByTombstones\":1", envelopeRow.Details, StringComparison.Ordinal);
        Assert.NotNull(report["SafetySnapshot"]);

        var export = JsonNode.Parse(await File.ReadAllTextAsync(await ExportFileAsync(), Ct))!["Data"]!;

        var tombstone = Assert.Single(export["Tombstones"]!.AsArray())!.AsObject();
        Assert.Equal(gone.Id, Guid.Parse((string)tombstone["RatingId"]!));
        Assert.False(tombstone.ContainsKey("Origin"));
        Assert.Equal(["DeletedBy", "DeletedUtc", "ItemId", "RatingId", "UserId"], tombstone.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    // -------------------------------------------------------------------------------- a restore keeps the ledger's links

    [Fact]
    public async Task RestoringAnImportsSnapshot_KeepsTheLedgersLinkToARatingDeletedSince_SoTheFileDoesntBringItBack()
    {
        // The restore kept the moderator's deletion of Y but dropped the ledger row X -> Y the import wrote, so the same
        // file imported again added X with the deleted words.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var local = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Local words.", StoreFixture.T0), Ct);
        var (other, otherId) = await AnotherServersCopyAsync(row =>
        {
            row["ReviewText"] = "Words from the other server.";
            row["UpdatedUtc"] = StoreFixture.T0.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
            row["ReviewUpdatedUtc"] = StoreFixture.T0.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        });
        var imported = await ImportAsync(other, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(imported, "Updated"));
        await Store.DeleteAsync(local.Id, RatingValues.DeletedBy.Moderator, StoreFixture.T0.AddDays(2), Ct);

        var restored = await _data.RestoreAsync(_s.Admin.Id, (string)imported["SafetySnapshot"]!, Ct);
        Assert.Equal(1, restored["DeletionsKept"]);
        Assert.Null(Store.Get(local.Id));

        var again = await ImportAsync(other, "Merge", dryRun: false);
        Assert.Equal((1, 0), (Outcome(again, "SkippedDeleted"), Outcome(again, "Added")));
        Assert.Null(Store.Get(otherId));
        var log = Store.ReadTables().ModerationLog.Last(l => l.Action == RatingValues.ModerationAction.RestoreSnapshot);
        Assert.Contains("\"LedgerLinksKept\":1", log.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARatingMadeAndDeletedAfterTheSnapshot_KeepsItsTombstone_SoAnExportTakenInBetweenCantBringItBack()
    {
        // The tombstone of a rating the restored snapshot never held is kept too.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Made after the snapshot, then deleted.", StoreFixture.T0.AddMinutes(1)), Ct);
        var between = await ExportFileAsync();
        await Store.DeleteAsync(row.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddMinutes(2), Ct);

        var restored = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);

        Assert.Equal(1, restored["DeletionsKept"]);
        Assert.Equal(RatingValues.DeletedBy.User, Store.GetTombstone(row.Id)!.DeletedBy);
        var report = await ImportAsync(between, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "SkippedDeleted"));
        Assert.Null(Store.Get(row.Id));
    }

    // ----------------------------------------------------------------------------------- a moderated review stays gone

    [Fact]
    public async Task AReviewAModeratorRemovedSince_StaysRemoved_EvenWhenItsMemberWroteANewOneAfter()
    {
        // The rule held only while the row had no review just before the restore; the removed words came back, live.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Removed by a moderator after the snapshot."), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;
        _s.Advance(TimeSpan.FromMinutes(5));
        await ModeratorRemovesAsync(row.Id, textOnly: true);
        _s.Advance(TimeSpan.FromMinutes(1));
        await Store.WriteAsync(w => { w.Upsert(w.Get(row.Id)! with { ReviewText = "Written after the removal.", UpdatedUtc = _s.Store.Clock.GetUtcNow().UtcDateTime, ReviewUpdatedUtc = _s.Store.Clock.GetUtcNow().UtcDateTime }); return 0; }, Ct);

        var result = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);

        var back = Store.Get(row.Id)!;
        Assert.Null(back.ReviewText);
        Assert.Equal(8, back.Stars2);
        Assert.Equal(1, result["ReviewRemovalsKept"]);
        Assert.DoesNotContain("Removed by a moderator after the snapshot.", Store.ReadTables().Ratings.Select(r => r.ReviewText ?? string.Empty));
    }

    [Fact]
    public async Task ARatingAModeratorRemovedSince_StaysDeleted_AsItsTombstoneSays()
    {
        // RemoveEntry, and Remove text on a rating with only its review (which deletes it): the tombstone keeps it deleted.
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var b = _s.AddMovie(_s.Movies, "B", "2");
        var entry = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Its whole entry removed."), Ct);
        var textOnly = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Only a review, its text removed.") with { Stars2 = null }, Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;
        _s.Advance(TimeSpan.FromMinutes(5));
        await ModeratorRemovesAsync(entry.Id, textOnly: false);
        await ModeratorRemovesAsync(textOnly.Id, textOnly: true);

        var result = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);

        Assert.Null(Store.Get(entry.Id));
        Assert.Null(Store.Get(textOnly.Id));
        Assert.Equal((RatingValues.DeletedBy.Moderator, RatingValues.DeletedBy.Moderator), (Store.GetTombstone(entry.Id)!.DeletedBy, Store.GetTombstone(textOnly.Id)!.DeletedBy));
        Assert.Equal(2, result["DeletionsKept"]);
    }

    [Fact]
    public async Task AReviewAnImportClearedSince_ComesBackWithTheRestoreOfTheImportsSnapshot()
    {
        // The member's rule (no log row: the review is gone just before the restore) leaves out a rating an import set
        // after the snapshot: restoring the import's snapshot undoes the import, the review it cleared too.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var local = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Words the import's newer copy hasn't.", StoreFixture.T0), Ct);
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var row = json["Data"]!["Ratings"]!.AsArray().Single()!.AsObject();
        row["ReviewText"] = null;
        row["ReviewUpdatedUtc"] = null;
        row["UpdatedUtc"] = StoreFixture.T0.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        json["ServerId"] = "server-2"; // another server's file (this server's own ledger is its own)
        var newer = Path.Combine(_s.Store.Directory.Path, "newer-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(newer, json.ToJsonString(), Ct);
        var report = await ImportAsync(newer, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "Updated"));
        Assert.Null(Store.Get(local.Id)!.ReviewText);

        var restored = await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);

        Assert.Equal(0, restored["ReviewRemovalsKept"]);
        Assert.Equal("Words the import's newer copy hasn't.", Store.Get(local.Id)!.ReviewText);
    }

    [Fact]
    public async Task AStartEmptySinceTheSnapshot_SaysTheDeletionsBeforeItMayBeBack()
    {
        // A Start empty after the snapshot quarantined the deletions made between the two; the answer says so, and
        // the deletions after it are kept.
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var after = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Deleted after the start empty."), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;
        _s.Advance(TimeSpan.FromMinutes(5));
        await Store.WriteAsync(w => w.AppendModerationLog(new ModerationLogRecord { AtUtc = _s.Store.Clock.GetUtcNow().UtcDateTime, ActorUserId = _s.Admin.Id, Action = RatingValues.ModerationAction.StartEmpty }), Ct);
        await Store.DeleteAsync(after.Id, RatingValues.DeletedBy.User, _s.Store.Clock.GetUtcNow().UtcDateTime, Ct);

        var restored = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);

        Assert.Contains("started empty", (string)restored["DeletionsError"]!, StringComparison.Ordinal);
        Assert.Equal(1, restored["DeletionsKept"]);
        Assert.Null(Store.Get(after.Id));
    }

    [Fact]
    public async Task ATombstoneOlderThanTheRestoredRow_IsNotAppliedOverIt()
    {
        // The rule's guard: a deletion is never applied again over a row the restored snapshot holds with a newer time (a
        // clock set back, or a snapshot from before an earlier restore).
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Newer than the deletion.", StoreFixture.T0.AddDays(2)), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;
        await Store.WriteAsync(w => { w.AddTombstone(row.Id, row.UserId, row.ItemId, RatingValues.DeletedBy.User, StoreFixture.T0.AddDays(1)); return 0; }, Ct);

        var restored = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);

        Assert.Equal(0, restored["DeletionsKept"]);
        Assert.Equal("Newer than the deletion.", Store.Get(row.Id)!.ReviewText);
        Assert.Null(Store.GetTombstone(row.Id));
    }

    [Fact]
    public async Task ARemovalRowTheRestoredSnapshotHolds_IsNotTakenForOneMadeAfterIt()
    {
        // Only the log rows the restored snapshot doesn't hold (by id, time, action and rating) were written after it.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Kept: its removal row is the snapshot's own.", StoreFixture.T0), Ct);
        await Store.WriteAsync(w => w.AppendModerationLog(new ModerationLogRecord { AtUtc = StoreFixture.T0.AddMinutes(1), ActorUserId = _s.Admin.Id, Action = RatingValues.ModerationAction.RemoveText, RatingId = row.Id }), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;

        var restored = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);

        Assert.Equal(0, restored["ReviewRemovalsKept"]);
        Assert.Equal("Kept: its removal row is the snapshot's own.", Store.Get(row.Id)!.ReviewText);
    }

    [Fact]
    public async Task TheRemovalRowsAReplaceBroughtFromItsFile_AreNotThisServersOwn()
    {
        // After a Replace the log before its ImportEnvelope row is the file's: a removal another server logged is undone with
        // the Replace, not made here again.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Kept: the removal is the file's.", StoreFixture.T0), Ct);
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        json["Data"]!["ModerationLog"]!.AsArray().Add(new JsonObject
        {
            ["Id"] = 1,
            ["AtUtc"] = StoreFixture.T0.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture),
            ["Action"] = RatingValues.ModerationAction.RemoveText,
            ["RatingId"] = row.Id.ToString("D", CultureInfo.InvariantCulture)
        });
        json["ServerId"] = "server-2"; // another server's file (this server's own log is its own)
        var crafted = Path.Combine(_s.Store.Directory.Path, "logged-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(crafted, json.ToJsonString(), Ct);
        var report = await ImportAsync(crafted, "Replace", dryRun: false);
        Assert.Contains(Store.ReadTables().ModerationLog, l => l.Action == RatingValues.ModerationAction.RemoveText && l.RatingId == row.Id);

        var restored = await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);

        Assert.Equal(0, restored["ReviewRemovalsKept"]);
        Assert.Equal("Kept: the removal is the file's.", Store.Get(row.Id)!.ReviewText);
    }

    // Windows: the download route opens the export's file with delete sharing, so DELETE Jobs/{id} deletes it at
    // once, and the download finishes from its open handle.
    [Fact]
    public async Task ADownloadInProgress_DoesntStopTheDeleteOfItsFile_AndStillReadsItToTheEnd()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Words a member deletes."), Ct);
        var job = _data.StartExport(_s.Admin.Id);
        await Finished();
        var id = Guid.Parse(job.Id);
        var path = _data.ExportFile(_s.Admin.Id, id).Path;
        var bytes = await File.ReadAllBytesAsync(path, Ct);
        var controller = Controller();

        var download = Assert.IsType<FileStreamResult>(controller.GetUserRatingsJobFile(id));
        await using (download.FileStream)
        {
            Assert.IsType<NoContentResult>(controller.DeleteUserRatingsJob(id));
            Assert.False(File.Exists(path));
            Assert.False(_jobs.Get(id).HasFile);
            using var rest = new MemoryStream();
            await download.FileStream.CopyToAsync(rest, Ct);
            Assert.Equal(bytes, rest.ToArray());
        }

        Assert.Equal(AdminJobRunner.FinishedCode, Assert.Throws<PluginException>(() => controller.DeleteUserRatingsJob(id)).Code);
        Assert.Equal(0, (int)_data.SweepTemp()["Deleted"]!);
    }

    // A handle without delete sharing (PhysicalFile's, before 0.3.20) keeps the file. The delete is never reported done
    // (202, not 204), the file is offered no more, and the per-minute expiry deletes it once the handle closes: not the
    // daily tmp/ sweep. The same for the hour's expiry.
    [Fact]
    public async Task AFileHeldWithoutDeleteSharing_IsWithdrawnAt202_AndTheNextMinuteDeletesIt_OnceClosed()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Words a member deletes."), Ct);
        var job = _data.StartExport(_s.Admin.Id);
        await Finished();
        var id = Guid.Parse(job.Id);
        var path = _data.ExportFile(_s.Admin.Id, id).Path;
        var second = _data.StartExport(_s.Admin.Id);
        await Finished();
        var secondId = Guid.Parse(second.Id);
        var secondPath = _data.ExportFile(_s.Admin.Id, secondId).Path;
        var controller = Controller();

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var accepted = Assert.IsType<AcceptedResult>(controller.DeleteUserRatingsJob(id));
            Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
            var body = Assert.IsAssignableFrom<IDictionary<string, object?>>(accepted.Value);
            Assert.Equal((false, UserRatingsAdminController.FileDeletePendingCode), ((bool)body["Deleted"]!, (string)body["Code"]!));
            Assert.False(_data.CancelJob(_s.Admin.Id, id));
            Assert.True(File.Exists(path));
            Assert.False(_jobs.Get(id).HasFile);
            Assert.Null(_jobs.Get(id).FileExpiresUtc);
            Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<PluginException>(() => controller.GetUserRatingsJobFile(id)).Kind);

            _s.Advance(AdminJobRunner.FileLifetime + TimeSpan.FromMinutes(1));
            Assert.Equal(0, _jobs.ExpireFiles());
            Assert.True(File.Exists(secondPath));
            Assert.False(_jobs.Get(secondId).HasFile);
            Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<PluginException>(() => _data.ExportFile(_s.Admin.Id, secondId)).Kind);
        }

        _s.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, _jobs.ExpireFiles());
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(secondPath));
        Assert.Equal(0, _jobs.ExpireFiles());
        Assert.Equal(0, (int)_data.SweepTemp()["Deleted"]!);
        Assert.Equal(AdminJobRunner.FinishedCode, Assert.Throws<PluginException>(() => controller.DeleteUserRatingsJob(id)).Code);
    }

    // A job that leaves the list of the last 10 while its file is held keeps its delete pending: the per-minute expiry
    // still deletes the file once the handle closes.
    [Fact]
    public async Task AJobThatLeavesTheListWhileItsFileIsHeld_StillHasItsFileDeleted_OnceClosed()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Words."), Ct);
        var job = _data.StartExport(_s.Admin.Id);
        await Finished();
        var path = _data.ExportFile(_s.Admin.Id, Guid.Parse(job.Id)).Path;

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            for (var i = 0; i < AdminJobRunner.KeepFinished; i++)
            {
                _jobs.Start("Export", true, (_, _) => Task.FromResult<object?>(null));
                await Finished();
            }

            Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<PluginException>(() => _jobs.Get(Guid.Parse(job.Id))).Kind);
            Assert.True(File.Exists(path));
            Assert.Equal(0, _jobs.ExpireFiles());
        }

        Assert.Equal(1, _jobs.ExpireFiles());
        Assert.False(File.Exists(path));
        Assert.Equal(0, _jobs.ExpireFiles());
    }

    // The concurrency finding: the daily maintenance's gate waits for a data job, then holds the runner, so its Archive step
    // never runs between an import's commit and its archive rewrite, or a restore's database and its archive; while it
    // holds, a job and a restore are refused.
    [Fact]
    public async Task TheMaintenancesHold_WaitsForARunningJob_ThenRefusesJobsAndRestores_UntilReleased()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Start("Import", false, async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return null;
        });
        var waiting = _jobs.HoldWhenIdleAsync("daily maintenance", AdminJobRunner.MaintenanceWait, Ct);
        await Task.Delay(100, Ct);
        Assert.False(waiting.IsCompleted);

        release.SetResult();
        using (Assert.IsAssignableFrom<IDisposable>(await waiting.WaitAsync(TimeSpan.FromSeconds(30), Ct)))
        {
            var busy = Assert.Throws<PluginException>(() => _jobs.Start("Export", true, (_, _) => Task.FromResult<object?>(null)));
            Assert.Equal(AdminJobRunner.BusyCode, busy.Code);
            Assert.Contains("daily maintenance is running", busy.Message, StringComparison.Ordinal);
            var restore = await Assert.ThrowsAsync<PluginException>(() => _data.RestoreAsync(_s.Admin.Id, "userratings-20260101.db", Ct));
            Assert.Equal(AdminJobRunner.BusyCode, restore.Code);
            Assert.Equal(AdminJobRunner.BusyCode, Assert.Throws<PluginException>(() => _data.StartExport(_s.Admin.Id)).Code);
        }

        _data.StartExport(_s.Admin.Id);
        await Finished();
    }

    [Fact]
    public async Task TheMaintenancesHold_WaitsForARestore_AndGivesUpAfterTenMinutesOnTheClock_SoItsRunIsSkipped()
    {
        // A runner on a clock whose timers fire only when it moves.
        var clock = new ManualTimerProvider(new DateTimeOffset(StoreFixture.T0));
        using var runner = new AdminJobRunner(clock, _s.Store.Logger);
        using (runner.Hold("restore"))
        {
            var waiting = runner.HoldWhenIdleAsync("daily maintenance", AdminJobRunner.MaintenanceWait, Ct);
            clock.Advance(AdminJobRunner.MaintenanceWait - TimeSpan.FromSeconds(1));
            await Task.Delay(100, Ct);
            Assert.False(waiting.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(30), Ct));
            Assert.Equal(AdminJobRunner.BusyCode, Assert.Throws<PluginException>(() => runner.ThrowIfBusy()).Code);
        }

        // Idle: at once, and the restore's own hold is refused while the maintenance holds.
        using var hold = await runner.HoldWhenIdleAsync("daily maintenance", AdminJobRunner.MaintenanceWait, Ct);
        Assert.NotNull(hold);
        Assert.Contains("daily maintenance", Assert.Throws<PluginException>(() => runner.Hold("restore")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMaintenancesWait_EndsWhenTheRunnerStops()
    {
        using (_jobs.Hold("restore"))
        {
            var waiting = _jobs.HoldWhenIdleAsync("daily maintenance", AdminJobRunner.MaintenanceWait, Ct);
            _jobs.Dispose();
            Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        }
    }

    // A negative control no test caught: a purge made since the snapshot stays a purge
    // through the restore, so an older export's copy of it is PreviouslyPurged, not just skipped.
    [Fact]
    public async Task APurgeSinceTheSnapshot_StaysAPurgeThroughTheRestore_SoAnOlderExportsCopyIsPreviouslyPurged()
    {
        var movie = _s.AddMovie(_s.Movies, "Heat", "949");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Purged here after the snapshot."), Ct);
        var file = WithoutTombstones(await ExportFileAsync());
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Import, Ct))!;
        _s.Advance(TimeSpan.FromMinutes(1));
        await Store.DeleteAsync(row.Id, RatingValues.DeletedBy.Purge, StoreFixture.T0.AddMinutes(1), Ct);

        var restored = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);
        Assert.Equal(1, restored["DeletionsKept"]);
        Assert.Null(Store.Get(row.Id));
        Assert.Equal(RatingValues.DeletedBy.Purge, Store.GetTombstone(row.Id)!.DeletedBy);

        var report = await ImportAsync(file, "Merge", dryRun: false);
        Assert.Equal((1, 0), (Outcome(report, "PreviouslyPurged"), Outcome(report, "SkippedDeleted")));
        Assert.Null(Store.Get(row.Id));
    }

    // The restore asks for a checkpoint, so words written since the snapshot, and words the restore deletes again,
    // leave the database file and its -wal (secure_delete), not only the rows.
    [Fact]
    public async Task ARestore_AsksForACheckpoint_SoTheWordsItTakesAwayLeaveTheDatabaseFile()
    {
        const string Since = "Zz words written after the snapshot, gone with the restore.";
        const string Deleted = "Zz words the member deleted after the snapshot, deleted again.";
        for (var i = 0; i < 20; i++)
        {
            await Store.UpsertAsync(Rating(_s.Viewer2.Id, Guid.NewGuid(), "Synthetic padding review " + i + "."), Ct);
        }

        var matrix = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var deleted = await Store.UpsertAsync(Rating(_s.Viewer.Id, matrix.Id, Deleted), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Import, Ct))!;
        _s.Advance(TimeSpan.FromMinutes(1));
        var heat = _s.AddMovie(_s.Movies, "Heat", "949");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, heat.Id, Since, StoreFixture.T0.AddMinutes(1)), Ct);
        await Store.DeleteAsync(deleted.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddMinutes(1), Ct);
        Assert.True(await Store.RequestCheckpoint());
        Assert.True(Holds(_s.Store.File, Since), "the words written since are in the database file before the restore");

        var restored = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);
        Assert.Equal(1, restored["DeletionsKept"]);
        Assert.True(await Store.WaitForCheckpointAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct), "the restore's checkpoint completed");
        Assert.False(Holds(_s.Store.File, Since));
        Assert.False(Holds(_s.Store.File, Deleted));
        var wal = _s.Store.File + "-wal";
        Assert.True(!File.Exists(wal) || !Holds(wal, Since));
        Assert.True(!File.Exists(wal) || !Holds(wal, Deleted));
    }

    // The archive part's decisions are in the PlanToken: an archived rating deleted here between the dry run and the
    // real run changes the plan (409 Changed), and nothing is written.
    [Fact]
    public async Task AnArchivedRatingDeletedHereBetweenTheDryRunAndTheRealRun_IsA409Changed()
    {
        var archivedId = await ArchivedRatingAsync("Archived, deleted between the runs.");
        var file = WithoutTombstones(await ExportFileAsync());
        var archive = _detached.ArchiveOf(_consumer);
        File.Delete(archive.FilePath);
        var dry = await ImportAsync(file, "Merge", dryRun: true);
        Assert.Equal((1, 1), (Archive(dry).Records, Archive(dry).Imported));

        await Store.WriteAsync(w => { w.AddTombstone(archivedId, _s.Viewer.Id, Guid.NewGuid(), RatingValues.DeletedBy.Purge, StoreFixture.T0.AddDays(5)); return 0; }, Ct);
        var changed = await Assert.ThrowsAsync<PluginException>(() => ImportAsync(file, "Merge", dryRun: false, ifPlan: (string)dry["PlanToken"]!));
        Assert.Equal(RatingsEnvelope.ChangedCode, changed.Code);
        Assert.Empty(archive.Read().Records);
    }

    // The tmp/ sweep leaves the running job's file (an export finishing), and takes what nothing holds.
    [Fact]
    public async Task TheTmpSweep_LeavesTheRunningJobsFile_AndTakesWhatNothingHolds()
    {
        var tmp = Path.Combine(Store.DataDirectory, "tmp");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "userratings-export-" + Guid.NewGuid().ToString("N") + ".json");
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _jobs.Start("Export", false, async (job, ct) =>
        {
            job.File = path;
            await File.WriteAllTextAsync(path, "{}", ct);
            written.SetResult();
            await release.Task.WaitAsync(ct);
            return null;
        });
        await written.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var leftover = Path.Combine(tmp, "userratings-export-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(leftover, "{}", Ct);

        Assert.Equal(1, (int)_data.SweepTemp()["Deleted"]!);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(leftover));
        release.SetResult();
        await Finished();
        Assert.True(File.Exists(path));
    }

    // A former member's name from a file drops the bidi controls and U+FEFF and keeps its joiners; the review's
    // own text keeps them as written.
    [Fact]
    public async Task AnImportedFormerMembersName_DropsTheBidiControls_KeepsItsJoiners_AndAReviewKeepsItsOwn()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Words."), Ct);
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var formerId = Guid.NewGuid();
        json["Data"]!["FormerUsers"] = new JsonArray(new JsonObject
        {
            ["Id"] = formerId.ToString("D", CultureInfo.InvariantCulture),
            ["OriginalUserId"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            ["DisplayName"] = "\uFEFF\u202Eecila\u202C \U0001F469\u200D\U0001F4BB",
            ["DeletedUtc"] = StoreFixture.T0.ToString("O", CultureInfo.InvariantCulture)
        });
        var row = json["Data"]!["Ratings"]!.AsArray().Single()!.AsObject();
        row["Id"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        row["UserId"] = formerId.ToString("D", CultureInfo.InvariantCulture);
        row["ReviewText"] = "\u05E9\u05DC\u05D5\u05DD \u2067abc\u2069 \u202Eopen";
        var crafted = Path.Combine(_s.Store.Directory.Path, "names-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(crafted, json.ToJsonString(), Ct);

        var report = await ImportAsync(crafted, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "Added"));
        Assert.Equal("ecila \U0001F469\u200D\U0001F4BB", Store.GetFormerUsers()[formerId]);
        Assert.Equal("\u05E9\u05DC\u05D5\u05DD \u2067abc\u2069 \u202Eopen", Store.Get(Guid.Parse((string)row["Id"]!))!.ReviewText);
    }

    // The admin controller over this test's data service, as an API key calls it.
    private UserRatingsAdminController Controller()
    {
        var api = new Mock<IUserRatingsAdminApi>();
        api.Setup(a => a.GetExportFile(It.IsAny<Guid?>(), It.IsAny<Guid>())).Returns((Guid? admin, Guid jobId) => _data.ExportFile(admin, jobId));
        api.Setup(a => a.CancelJob(It.IsAny<Guid?>(), It.IsAny<Guid>())).Returns((Guid? admin, Guid jobId) => _data.CancelJob(admin, jobId));
        return new UserRatingsAdminController(Mock.Of<IKitDiagnostics>(), api.Object) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
    }

    private static bool Holds(string path, string text)
        => StoreFixture.ReadOpenFile(path).AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(text)) >= 0;

    // A crafted file with no ratings and one tombstone of a rating here.
    private async Task<string> WithTombstoneAsync(RatingRecord rating, DateTime deletedUtc)
    {
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        json["Data"]!["Ratings"] = new JsonArray();
        json["Data"]!["Tombstones"] = new JsonArray(new JsonObject
        {
            ["RatingId"] = rating.Id.ToString("D", CultureInfo.InvariantCulture),
            ["UserId"] = rating.UserId.ToString("D", CultureInfo.InvariantCulture),
            ["ItemId"] = rating.ItemId.ToString("D", CultureInfo.InvariantCulture),
            ["DeletedUtc"] = deletedUtc.ToString("O", CultureInfo.InvariantCulture),
            ["DeletedBy"] = RatingValues.DeletedBy.User
        });
        var crafted = Path.Combine(_s.Store.Directory.Path, "tombstones-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(crafted, json.ToJsonString(), Ct);
        return crafted;
    }

    // A moderator's removal through the moderation service: its dry run, then the real run, which writes the log row.
    private async Task ModeratorRemovesAsync(Guid ratingId, bool textOnly)
    {
        var moderation = new RatingsModeration(_s.Store.Store, _s.ItemAccess, _s.UserManager.Object, _s.Library.Object, _s.ArchivedLines, _s.Store.Clock, _s.Store.Logger);
        var token = (await moderation.RemoveAsync(_s.Admin.Id, ratingId, textOnly, null, dryRun: null, null, Ct)).PreviewToken;
        await moderation.RemoveAsync(_s.Admin.Id, ratingId, textOnly, UserRatingsModerationReason.Spam, dryRun: false, token, Ct);
    }
}
