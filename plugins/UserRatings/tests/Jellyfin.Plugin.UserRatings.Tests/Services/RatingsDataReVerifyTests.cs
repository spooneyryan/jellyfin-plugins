using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// Regressions fixed in 0.3.21.0: a deletion never older than its row, and an import's
/// times never ahead of this server's clock.
/// </summary>
public sealed partial class RatingsDataTests
{
    public static TheoryData<string> DeletionPaths => new() { "DeleteMine", "DeleteOwnRow", "Purge", "UserDeletedLive", "UserDeletedArchived" };

    // A row dated ahead of the clock (an import before 0.3.21, or a clock set back since its write), deleted on this server by
    // each path: the tombstone is no older than the row, so restoring a snapshot that holds the row keeps it deleted.
    [Theory]
    [MemberData(nameof(DeletionPaths))]
    public async Task ADeletionOfARowDatedAhead_IsNoOlderThanTheRow_SoAnEarlierRestoreKeepsItDeleted(string path)
    {
        var ahead = _s.Store.Clock.GetUtcNow().UtcDateTime.AddDays(10);
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var detached = path is "Purge" or "UserDeletedArchived";
        var row = await Store.UpsertAsync(
            Rating(_s.Viewer.Id, detached ? Guid.NewGuid() : m.Id, "Dated ahead.", ahead) with
            {
                DetachedUtc = detached ? StoreFixture.T0 : null,
                DetachReason = detached ? RatingValues.DetachReason.Missing : null
            },
            Ct);
        var s0 = await SnapshotNowAsync();
        if (path == "UserDeletedArchived")
        {
            // Archived after the snapshot, which holds it live.
            _s.Advance(TimeSpan.FromDays(2));
            Assert.Equal(1, (await _detached.ArchiveDueAsync(_consumer, Ct)).Archived);
            Assert.Null(Store.Get(row.Id));
        }

        switch (path)
        {
            case "DeleteMine":
                await _s.Service.DeleteMineAsync(_s.Viewer, m.Id, Ct);
                break;
            case "DeleteOwnRow":
                await _s.Service.DeleteOwnRowAsync(_s.Viewer, row.Id, Ct);
                break;
            case "Purge":
                await _data.PurgeAsync(_s.Admin.Id, row.Id, Ct);
                break;
            default:
                await DeletedUserOwner().HandleUserDeletedAsync(_s.Viewer.Id, DeletedUserPolicy.Delete, Ct);
                break;
        }

        var tombstone = Store.GetTombstone(row.Id)!;
        Assert.Equal(RatingValues.DeletionOrigin.Local, tombstone.Origin);
        Assert.True(tombstone.DeletedUtc > ahead, path + ": the tombstone is older than the row");
        _s.Advance(TimeSpan.FromMinutes(1));
        await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(row.Id));
        Assert.NotNull(Store.GetTombstone(row.Id));
        Assert.DoesNotContain(_detached.ArchiveOf(_consumer).Read().Records, a => Guid.TryParse(a.Id, out var g) && g == row.Id);
    }

    [Fact]
    public async Task AServerClockSetBack_AMembersDeleteStaysDeleted_RestoringASnapshotInBetween()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        await _s.Service.SetReviewAsync(_s.Viewer, m.Id, "Words written while the clock was ahead.", false, (DateTime?)null, Ct);
        var row = Store.GetAttached(_s.Viewer.Id, m.Id)!;
        var s0 = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;
        _s.Advance(TimeSpan.FromSeconds(1));

        // The clock is corrected back an hour (NTP), then the member deletes.
        _s.Store.Clock.Set(_s.Store.Clock.GetUtcNow() - TimeSpan.FromHours(1));
        await _s.Service.DeleteMineAsync(_s.Viewer, m.Id, Ct);
        Assert.True(Store.GetTombstone(row.Id)!.DeletedUtc > row.UpdatedUtc);
        _s.Store.Clock.Set(_s.Store.Clock.GetUtcNow() + TimeSpan.FromHours(2));
        await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(row.Id));
    }

    [Fact]
    public async Task AnImportedRowWithAFutureTime_IsWrittenAsTheImportsTime_AndStaysDeletedAfterItsMembersDelete()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var seed = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "seed", StoreFixture.T0), Ct);
        var ahead = _s.Store.Clock.GetUtcNow().UtcDateTime.AddHours(6);
        var (other, _) = await AnotherServersCopyAsync(row =>
        {
            var at = ahead.ToString("O", CultureInfo.InvariantCulture);
            row["ReviewText"] = "Words from a server whose clock runs ahead.";
            row["CreatedUtc"] = at;
            row["UpdatedUtc"] = at;
            row["ReviewUpdatedUtc"] = at;
        });
        var report = await ImportAsync(other, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "Updated"));
        Assert.Equal(1, Outcome(report, "Clamped"));
        Assert.Contains(((IEnumerable<string>)report["Notes"]!), n => n.Contains("ahead of this server's clock", StringComparison.Ordinal));
        var imported = Store.Get(seed.Id)!;
        Assert.Equal("Words from a server whose clock runs ahead.", imported.ReviewText);
        var now = _s.Store.Clock.GetUtcNow().UtcDateTime;
        Assert.True(imported.UpdatedUtc <= now && imported.ReviewUpdatedUtc <= now && imported.CreatedUtc <= now);

        // The same file again is the same import: nothing changes, the row keeps the time that import wrote.
        _s.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, Outcome(await ImportAsync(other, "Merge", dryRun: false), "Unchanged"));
        Assert.Equal(imported.UpdatedUtc, Store.Get(seed.Id)!.UpdatedUtc);

        // A member's own edit since is the newer, though the file's time is still ahead.
        _s.Advance(TimeSpan.FromMinutes(1));
        await _s.Service.SetReviewAsync(_s.Viewer, m.Id, "The member's own words, after the import.", false, (DateTime?)null, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, Outcome(await ImportAsync(other, "Merge", dryRun: false), "KeptNewer"));
        Assert.Equal("The member's own words, after the import.", Store.Get(seed.Id)!.ReviewText);

        // Deleted by its member, it stays deleted through a restore in between.
        var s0 = await SnapshotNowAsync();
        await _s.Service.DeleteMineAsync(_s.Viewer, m.Id, Ct);
        await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(seed.Id));
    }

    [Fact]
    public async Task AnImportedTombstoneDatedAhead_IsWrittenAsTheImportsTime()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "seed", StoreFixture.T0), Ct);
        var (other, _) = await AnotherServersCopyAsync(_ => { });
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(other, Ct))!.AsObject();
        var gone = Guid.NewGuid();
        json["Data"]!["Tombstones"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["RatingId"] = gone.ToString("D"),
            ["UserId"] = _s.Viewer.Id.ToString("D"),
            ["ItemId"] = m.Id.ToString("D"),
            ["DeletedUtc"] = _s.Store.Clock.GetUtcNow().UtcDateTime.AddDays(3).ToString("O", CultureInfo.InvariantCulture),
            ["DeletedBy"] = RatingValues.DeletedBy.User
        });
        await File.WriteAllTextAsync(other, json.ToJsonString(), Ct);
        var report = await ImportAsync(other, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "Clamped"));
        Assert.True(Store.GetTombstone(gone)!.DeletedUtc <= _s.Store.Clock.GetUtcNow().UtcDateTime);
    }

    // The member cleared a review, then was deleted (Anonymize): the restore removed the review again under the member's
    // own id and never re-keyed the rating to the former member.
    [Fact]
    public async Task AnAnonymizedMember_WhoClearedAReviewFirst_StaysAFormerMember_ThroughAnEarlierRestore()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Words its member cleared before being deleted."), Ct);
        var s0 = await SnapshotNowAsync();
        await _s.Service.ClearReviewAsync(_s.Viewer, m.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        await DeletedUserOwner().HandleUserDeletedAsync(_s.Viewer.Id, DeletedUserPolicy.Anonymize, Ct);
        var former = Store.Get(row.Id)!.UserId;
        Assert.NotEqual(_s.Viewer.Id, former);
        _s.Advance(TimeSpan.FromMinutes(1));
        var r = await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        var back = Store.Get(row.Id)!;
        Assert.Null(back.ReviewText);
        Assert.Equal(former, back.UserId);
        Assert.Null(Store.GetAttached(_s.Viewer.Id, m.Id));
        Assert.Equal((1, 1), ((int)r["FormerMembersKept"]!, (int)r["ReviewRemovalsKept"]!));
    }

    // A Replace whose file holds an old StartEmpty row (from before the snapshot) made a later restore say that deletions
    // may be back. A Start empty made after the snapshot still says so.
    [Fact]
    public async Task AReplacesFileWithAnOldStartEmptyRow_DoesntMakeALaterRestoreSayDeletionsMayBeBack()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        await Store.WriteAsync(w => w.AppendModerationLog(new ModerationLogRecord { AtUtc = _s.Store.Clock.GetUtcNow().UtcDateTime, ActorUserId = _s.Admin.Id, Action = RatingValues.ModerationAction.StartEmpty }), Ct);
        _s.Advance(TimeSpan.FromDays(1));
        await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Words.", _s.Store.Clock.GetUtcNow().UtcDateTime), Ct);
        var s0 = await SnapshotNowAsync();
        await ImportAsync(await ExportFileAsync(), "Replace", dryRun: false);
        _s.Advance(TimeSpan.FromMinutes(1));
        var r = await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(r["DeletionsError"]);

        // The control: a Start empty's row written after the snapshot is still read.
        await Store.WriteAsync(w => w.AppendModerationLog(new ModerationLogRecord { AtUtc = _s.Store.Clock.GetUtcNow().UtcDateTime, ActorUserId = _s.Admin.Id, Action = RatingValues.ModerationAction.StartEmpty }), Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        var again = await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Contains("started empty", (string)again["DeletionsError"]!, StringComparison.Ordinal);
    }

    // A moderator's Remove text, then a Replace of this server's own export (its log before the Replace is this server's),
    // then a restore of a snapshot from before the removal: the text stays removed.
    [Fact]
    public async Task AModeratorsRemoveText_ThroughAReplaceOfTheServersOwnExport_StaysRemoved_RestoringAnEarlierSnapshot()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Words a moderator removed."), Ct);
        var s0 = await SnapshotNowAsync();
        await ModeratorRemovesAsync(row.Id, textOnly: true);
        Assert.Null(Store.Get(row.Id)!.ReviewText);
        _s.Advance(TimeSpan.FromMinutes(1));
        var report = await ImportAsync(await ExportFileAsync(), "Replace", dryRun: false);
        Assert.True((bool)report["OwnServer"]!);
        Assert.Contains("\"OwnServer\":true", Store.ReadTables().ModerationLog.Last(l => l.Action == RatingValues.ModerationAction.ImportEnvelope).Details!, StringComparison.Ordinal);
        _s.Advance(TimeSpan.FromMinutes(1));
        var r = await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(row.Id)!.ReviewText);
        Assert.Equal(1, r["ReviewRemovalsKept"]);
    }

    // The log alone: a moderator's Remove text, the member's new review, then a Replace of this server's own export, then a
    // restore of a snapshot from before the removal: the removed words don't come back. The clear check can't tell (the
    // member's row has a review again); only the log's RemoveText row, which the Replace brought back from the file before
    // its own row, says so, and the restore reads it only while an own-server Replace isn't taken as the last Replace.
    [Fact]
    public async Task AModeratedReview_RewrittenByItsMember_ThroughAReplaceOfTheServersOwnExport_StaysRemoved_RestoringAnEarlierSnapshot()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "The words a moderator removed."), Ct);
        var s0 = await SnapshotNowAsync();
        await ModeratorRemovesAsync(row.Id, textOnly: true);
        _s.Advance(TimeSpan.FromMinutes(1));
        await _s.Service.SetReviewAsync(_s.Viewer, m.Id, "The member's new words.", false, (DateTime?)null, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        var report = await ImportAsync(await ExportFileAsync(), "Replace", dryRun: false);
        Assert.True((bool)report["OwnServer"]!);
        _s.Advance(TimeSpan.FromMinutes(1));
        var r = await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.NotEqual("The words a moderator removed.", Store.Get(row.Id)?.ReviewText);
        Assert.Equal(1, r["ReviewRemovalsKept"]);
    }

    // A member's clear, then a Merge of this server's own export that changes nothing, then a restore of a snapshot from
    // before the clear: the review stays cleared (the import's ledger rows are this server's, not an import's write).
    [Fact]
    public async Task AMembersClear_ThroughANoOpMergeOfTheServersOwnExport_StaysCleared_RestoringAnEarlierSnapshot()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Words its member cleared."), Ct);
        var s0 = await SnapshotNowAsync();
        await _s.Service.ClearReviewAsync(_s.Viewer, m.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        var report = await ImportAsync(await ExportFileAsync(), "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "Unchanged"));
        _s.Advance(TimeSpan.FromMinutes(1));
        await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(row.Id)!.ReviewText);
    }

    // The same two with another server's export: its log before a Replace is the file's, and its ledger an import's.
    [Fact]
    public async Task AnotherServersReplace_StillTakesItsLogAsTheFiles_AndItsLedgerAsTheImports()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Words.", StoreFixture.T0), Ct);
        var (other, _) = await AnotherServersCopyAsync(_ => { });
        var report = await ImportAsync(other, "Merge", dryRun: false);
        Assert.False((bool)report["OwnServer"]!);
        Assert.Contains("\"OwnServer\":false", Store.ReadTables().ModerationLog.Last(l => l.Action == RatingValues.ModerationAction.ImportEnvelope).Details!, StringComparison.Ordinal);
        Assert.NotNull(Store.Get(row.Id));
        Assert.True(_envelope.IsOwnServer(" SERVER-1 "));
        Assert.False(_envelope.IsOwnServer(null));
        Assert.False(_envelope.IsOwnServer("server-2"));
    }

    // A moderator removed a review, its member wrote new words, then the log's retention deleted the removal's row while
    // a snapshot from before the removal was still kept: restoring it brought the moderated words back. The removal rows now
    // stay while a kept snapshot is older than them; the other rows go at the cutoff as before.
    [Fact]
    public async Task AModeratedReview_RewrittenByItsMember_StaysRemoved_AfterTheLogsRetention_RestoringAnOlderSnapshot()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "The words a moderator removed."), Ct);
        await Store.WriteAsync(w => w.AppendModerationLog(new ModerationLogRecord { AtUtc = _s.Store.Clock.GetUtcNow().UtcDateTime, Action = RatingValues.ModerationAction.Archive }), Ct);
        var s0 = await SnapshotNowAsync();
        await ModeratorRemovesAsync(row.Id, textOnly: true);
        _s.Advance(TimeSpan.FromMinutes(1));
        await _s.Service.SetReviewAsync(_s.Viewer, m.Id, "The member's new words.", false, (DateTime?)null, Ct);

        // ModerationLogDays = 1, three days on.
        _s.Advance(TimeSpan.FromDays(3));
        var retention = new ModerationLogRetention(Store, () => 1, _s.Store.Clock, _s.Store.Logger);
        var run = await retention.RunAsync(Ct);
        Assert.True(run!.ContainsKey("RemovalsCutoffUtc"));
        var log = Store.ReadTables().ModerationLog;
        Assert.Contains(log, l => l.Action == RatingValues.ModerationAction.RemoveText && l.RatingId == row.Id);
        Assert.DoesNotContain(log, l => l.Action == RatingValues.ModerationAction.Archive);

        await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.NotEqual("The words a moderator removed.", Store.Get(row.Id)?.ReviewText);
    }

    // A member clearing the last of a rating (its review, with no stars) deletes it; the
    // tombstone is this server's own, so a restore of an earlier snapshot keeps it deleted.
    [Fact]
    public async Task AMemberClearingTheLastOfAReviewOnlyRating_IsALocalDeletion_KeptByAnEarlierRestore()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "A review with no stars.") with { Stars2 = null }, Ct);
        var s0 = await SnapshotNowAsync();
        await _s.Service.ClearReviewAsync(_s.Viewer, m.Id, Ct);
        Assert.Equal(RatingValues.DeletionOrigin.Local, Store.GetTombstone(row.Id)!.Origin);
        var r = await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(row.Id));
        Assert.Equal((1, 0), ((int)r["DeletionsKept"]!, (int)r["ImportDeletionsUndone"]!));
    }

    // The deletions a restore applies again are this server's own, so a second restore of the same snapshot
    // keeps them too: one the snapshot holds (deleted again) and one made and deleted since (its tombstone added again).
    [Fact]
    public async Task TheDeletionsARestoreAppliedAgain_StayLocal_SoASecondRestoreKeepsThemToo()
    {
        var seed = _s.AddMovie(_s.Movies, "Seed", "9");
        var held = await Store.UpsertAsync(Rating(_s.Viewer2.Id, seed.Id, "In the snapshot."), Ct);
        var s0 = await SnapshotNowAsync();
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var made = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Made after the snapshot."), Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        await _s.Service.DeleteMineAsync(_s.Viewer, m.Id, Ct);
        await _s.Service.DeleteMineAsync(_s.Viewer2, seed.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));

        await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Equal(RatingValues.DeletionOrigin.Local, Store.GetTombstone(made.Id)!.Origin);
        Assert.Equal(RatingValues.DeletionOrigin.Local, Store.GetTombstone(held.Id)!.Origin);
        _s.Advance(TimeSpan.FromMinutes(1));
        var again = await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(made.Id));
        Assert.Null(Store.Get(held.Id));
        Assert.NotNull(Store.GetTombstone(made.Id));
        Assert.Equal((2, 0), ((int)again["DeletionsKept"]!, (int)again["ImportDeletionsUndone"]!));
    }

    // A newer copy dated ahead of the clock, without the review: the import writes it at its own time (clamped), and the
    // restore of its snapshot takes that time as the import's (ImportTimes.Written), so the review comes back with the undo.
    [Fact]
    public async Task AReviewAnImportDatedAheadClearedSince_ComesBackWithTheRestoreOfTheImportsSnapshot()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var local = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Words the import's newer copy hasn't.", StoreFixture.T0), Ct);
        var (other, _) = await AnotherServersCopyAsync(row =>
        {
            row["Id"] = local.Id.ToString("D", CultureInfo.InvariantCulture);
            row["ReviewText"] = null;
            row["ReviewUpdatedUtc"] = null;
            row["UpdatedUtc"] = _s.Store.Clock.GetUtcNow().UtcDateTime.AddHours(5).ToString("O", CultureInfo.InvariantCulture);
        });
        var report = await ImportAsync(other, "Merge", dryRun: false);
        Assert.Equal((1, 1), (Outcome(report, "Updated"), Outcome(report, "Clamped")));
        Assert.Null(Store.Get(local.Id)!.ReviewText);

        var restored = await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);
        Assert.Equal(0, restored["ReviewRemovalsKept"]);
        Assert.Equal("Words the import's newer copy hasn't.", Store.Get(local.Id)!.ReviewText);
    }

    // Beyond this server's own export: another server's copy of the same rating, as the member left it (a clear), merged
    // as Unchanged, never claims the rating's time as the import's write; the clear stays through an earlier restore.
    [Fact]
    public async Task AMembersClear_ThroughANoOpMergeOfAnotherServersCopy_StaysCleared_RestoringAnEarlierSnapshot()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Words its member cleared."), Ct);
        var s0 = await SnapshotNowAsync();
        await _s.Service.ClearReviewAsync(_s.Viewer, m.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        var (other, _) = await AnotherServersCopyAsync(r => r["Id"] = row.Id.ToString("D", CultureInfo.InvariantCulture));
        var report = await ImportAsync(other, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "Unchanged"));
        Assert.False((bool)report["OwnServer"]!);
        _s.Advance(TimeSpan.FromMinutes(1));
        await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(row.Id)!.ReviewText);
    }

    // A Replace of this server's own export taken after a member's clear: the import's rows are this server's,
    // so the clear stays through a restore of a snapshot from before it.
    [Fact]
    public async Task AMembersClear_ThroughAReplaceOfTheServersOwnExport_StaysCleared_RestoringAnEarlierSnapshot()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Words its member cleared."), Ct);
        var s0 = await SnapshotNowAsync();
        await _s.Service.ClearReviewAsync(_s.Viewer, m.Id, Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        var report = await ImportAsync(await ExportFileAsync(), "Replace", dryRun: false);
        Assert.True((bool)report["OwnServer"]!);
        _s.Advance(TimeSpan.FromMinutes(1));
        await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(Store.Get(row.Id)!.ReviewText);
    }

    // With another server's export: its StartEmpty row, dated after the snapshot, is the other server's (before the
    // Replace), so a restore doesn't say this server's deletions may be back.
    [Fact]
    public async Task AnotherServersReplace_WithAStartEmptyRowDatedAfterTheSnapshot_DoesntSayDeletionsMayBeBack()
    {
        var m = _s.AddMovie(_s.Movies, "A", "1");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, m.Id, "Words.", StoreFixture.T0), Ct);
        var s0 = await SnapshotNowAsync();
        var (other, _) = await AnotherServersCopyAsync(_ => { });
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(other, Ct))!.AsObject();
        json["Data"]!["ModerationLog"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["Id"] = 1,
            ["AtUtc"] = _s.Store.Clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["Action"] = RatingValues.ModerationAction.StartEmpty
        });
        await File.WriteAllTextAsync(other, json.ToJsonString(), Ct);
        _s.Advance(TimeSpan.FromMinutes(1));
        await ImportAsync(other, "Replace", dryRun: false);
        Assert.Contains(Store.ReadTables().ModerationLog, l => l.Action == RatingValues.ModerationAction.StartEmpty);
        _s.Advance(TimeSpan.FromMinutes(1));
        var r = await _data.RestoreAsync(_s.Admin.Id, s0, Ct);
        Assert.Null(r["DeletionsError"]);
    }

    private RatingsUserDataOwner DeletedUserOwner() => new(_s.Store.Store, _s.Store.Logger, _s.Store.Clock, () => _consumer);

    // A pre-operation snapshot now, then a minute on.
    private async Task<string> SnapshotNowAsync()
    {
        var name = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Purge, Ct))!;
        _s.Advance(TimeSpan.FromMinutes(1));
        return name;
    }
}
