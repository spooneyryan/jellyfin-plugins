using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.UserRatings.Api;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// The fixes of 0.3.19.0: the import's 100 MB limit, the export file's lifetime and the tmp/ sweep, the ledger
/// never bringing back a deleted rating, the archive in an import and a restore, a restore keeping deletions, the rows
/// checked one by one, and the rest.
/// </summary>
public sealed partial class RatingsDataTests
{
    [Fact]
    public void TheImportRoute_ReadsUpTo100MB_NotKestrels30MB()
    {
        // The in-process server doesn't enforce [RequestSizeLimit]; the attribute is checked here and the limit itself in the
        // sandbox (size-edge.mjs: about 30 MB, 99 MiB and exactly 100 MiB pass, with a Content-Length or chunked; 100 MiB + 1
        // is a 413 TooLarge). Since 0.3.20 the route allows 1 MiB more than the import: Kestrel counts a chunked body's
        // framing toward its limit, and an exactly 100 MiB export sent chunked was a 413 (found in 0.3.19's verification).
        var import = typeof(UserRatingsAdminController).GetMethod(nameof(UserRatingsAdminController.PostUserRatingsImport))!;
        var attribute = Assert.IsType<RequestSizeLimitAttribute>(Assert.Single(import.GetCustomAttributes(typeof(RequestSizeLimitAttribute), false)));
        var limit = ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)attribute).MaxRequestBodySize;
        Assert.Equal(UserRatingsAdminController.ImportRequestLimitBytes, limit);
        Assert.Equal(101L * 1024 * 1024, limit);
        Assert.Equal(100L * 1024 * 1024, ExportEnvelope.MaxImportBytes);
        Assert.Single(import.GetCustomAttributes(typeof(RawBodyAttribute), false));
    }

    [Fact]
    public async Task AnUndeclaredBody_OfExactly100MiB_IsTaken_AndOneByteMoreIsA413_TheBytesCountedExactly()
    {
        // The route's limit allows Kestrel's chunk framing; the import counts the body itself.
        var taken = await _data.StartImportAsync(null, new FilledStream(ExportEnvelope.MaxImportBytes), null, "Merge", true, "Id", null, Ct);
        await Finished();
        var job = _jobs.Get(Guid.Parse(taken.Id));
        Assert.Equal(AdminJob.Failed, job.State); // read whole, then not an export
        Assert.Empty(TmpFiles());

        var over = await Assert.ThrowsAsync<PluginException>(() => _data.StartImportAsync(null, new FilledStream(ExportEnvelope.MaxImportBytes + 1), null, "Merge", true, "Id", null, Ct));
        Assert.Equal((PluginErrorKind.PayloadTooLarge, RatingsData.TooLargeCode), (over.Kind, over.Code));
        Assert.Empty(TmpFiles());

        var declared = await _data.StartImportAsync(null, new FilledStream(ExportEnvelope.MaxImportBytes), ExportEnvelope.MaxImportBytes, "Merge", true, "Id", null, Ct);
        await Finished();
        Assert.Equal(AdminJob.Failed, _jobs.Get(Guid.Parse(declared.Id)).State);
        Assert.Empty(TmpFiles());
    }

    [Fact]
    public async Task AnImportOver100MB_IsA413WithItsCode_HoweverItIsFound_AndLeavesNoFileInTmp()
    {
        // The declared length, and Kestrel stopping the read at the route's limit (its BadHttpRequestException).
        var declared = await Assert.ThrowsAsync<PluginException>(() => _data.StartImportAsync(null, Stream.Null, ExportEnvelope.MaxImportBytes + 1, "Merge", true, "Id", null, Ct));
        Assert.Equal((PluginErrorKind.PayloadTooLarge, RatingsData.TooLargeCode), (declared.Kind, declared.Code));

        using var kestrel = new ThrowingStream(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));
        var stopped = await Assert.ThrowsAsync<PluginException>(() => _data.StartImportAsync(null, kestrel, null, "Merge", true, "Id", null, Ct));
        Assert.Equal((PluginErrorKind.PayloadTooLarge, RatingsData.TooLargeCode, RatingsData.TooLargeMessage), (stopped.Kind, stopped.Code, stopped.Message));
        Assert.Empty(TmpFiles());

        // Another bad request is not a 413.
        using var other = new ThrowingStream(new BadHttpRequestException("Unexpected end of request content.", StatusCodes.Status400BadRequest));
        await Assert.ThrowsAsync<BadHttpRequestException>(() => _data.StartImportAsync(null, other, null, "Merge", true, "Id", null, Ct));
        Assert.Empty(TmpFiles());
    }

    [Fact]
    public async Task TheExportJobsFile_GoesAnHourAfterItsJob_OrOnDelete_SoADeletedReviewDoesntStayInIt()
    {
        // Inverted: the file held a review its member deleted until ten more jobs had finished.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Words its member deletes."), Ct);
        var job = _data.StartExport(_s.Admin.Id);
        await Finished();
        var id = Guid.Parse(job.Id);
        var path = _data.ExportFile(_s.Admin.Id, id).Path;
        var finished = _jobs.Get(id);
        Assert.Equal((true, finished.FinishedUtc + AdminJobRunner.FileLifetime), (finished.HasFile, finished.FileExpiresUtc));
        await Store.DeleteAsync(row.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddMinutes(1), Ct);

        _s.Advance(AdminJobRunner.FileLifetime - TimeSpan.FromMinutes(1));
        Assert.True(_jobs.List().Single(j => j.Id == job.Id).HasFile);
        Assert.True(File.Exists(path));
        _s.Advance(TimeSpan.FromMinutes(1));
        var expired = _jobs.List().Single(j => j.Id == job.Id);
        Assert.Equal((AdminJob.Succeeded, false, (DateTime?)null), (expired.State, expired.HasFile, expired.FileExpiresUtc));
        Assert.False(File.Exists(path));
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<PluginException>(() => _data.ExportFile(_s.Admin.Id, id)).Kind);

        // DELETE Jobs/{id} on a finished export deletes its file at once; again, there's nothing left to delete: 409.
        var second = _data.StartExport(_s.Admin.Id);
        await Finished();
        var secondId = Guid.Parse(second.Id);
        var secondPath = _data.ExportFile(null, secondId).Path;
        _data.CancelJob(_s.Admin.Id, secondId);
        Assert.False(File.Exists(secondPath));
        Assert.False(_jobs.Get(secondId).HasFile);
        Assert.Equal(AdminJobRunner.FinishedCode, Assert.Throws<PluginException>(() => _data.CancelJob(null, secondId)).Code);
        Assert.Equal(AdminJobRunner.FinishedCode, Assert.Throws<PluginException>(() => _data.CancelJob(null, id)).Code);
    }

    [Fact]
    public async Task AnExportThatFails_LeavesNoFile()
    {
        var tmp = Path.Combine(Store.DataDirectory, "tmp");
        Directory.CreateDirectory(tmp);
        var path = Path.Combine(tmp, "userratings-export-failed.json");
        _jobs.Start("Export", dryRun: true, async (job, _) =>
        {
            job.File = path;
            await File.WriteAllTextAsync(path, "{\"half\":", Ct);
            throw new IOException("The disk is full.");
        });
        await Finished();
        Assert.Equal(AdminJob.Failed, _jobs.List().Single().State);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task BothExportRoutes_NameTheFileAlike_FromItsExportedUtc()
    {
        // GET Export gave userratings-export-export-<time>.json, the job's file a name from its mtime.
        var job = _data.StartExport(_s.Admin.Id);
        await Finished();
        var id = Guid.Parse(job.Id);
        var (path, name) = _data.ExportFile(_s.Admin.Id, id);
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        var exported = ExportEnvelope.Parse(await File.ReadAllBytesAsync(path, Ct)).ExportedUtc;
        Assert.Equal("userratings-export-" + exported.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json", _data.ExportFile(_s.Admin.Id, id).Name);
        Assert.Equal(name, _data.ExportFile(_s.Admin.Id, id).Name);
        Assert.Equal(name, ((IDictionary<string, object?>)_jobs.Get(id).Report!)["FileName"]);

        var api = new UserRatingsAdminApi(_consumer, static () => throw new InvalidOperationException(), static () => throw new InvalidOperationException(), data: () => _data);
        var direct = Assert.IsType<FileContentResult>(api.Export(_s.Admin.Id, new DefaultHttpContext()));
        var directUtc = ExportEnvelope.Parse(direct.FileContents).ExportedUtc;
        Assert.Equal("userratings-export-" + directUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json", direct.FileDownloadName);
    }

    [Fact]
    public async Task TheTmpSweep_DeletesWhatAKillLeft_AtTheStartAndDaily_NotAHeldUploadOrAnExportInItsHour()
    {
        var tmp = Path.Combine(Store.DataDirectory, "tmp");
        Directory.CreateDirectory(tmp);
        var leftExport = Path.Combine(tmp, "userratings-export-" + Guid.NewGuid().ToString("N") + ".json");
        var leftUpload = Path.Combine(tmp, "userratings-import-" + Guid.NewGuid().ToString("N") + ".json");
        var other = Path.Combine(tmp, "someone-elses.json");
        foreach (var file in new[] { leftExport, leftUpload, other })
        {
            await File.WriteAllTextAsync(file, "{\"Reviews\":\"every one\"}", Ct);
        }

        var export = _data.StartExport(_s.Admin.Id);
        await Finished();
        var exportPath = _data.ExportFile(null, Guid.Parse(export.Id)).Path;
        var upload = Path.Combine(tmp, "userratings-import-" + Guid.NewGuid().ToString("N") + ".json");
        var hold = _jobs.HoldUpload(upload);
        await File.WriteAllTextAsync(upload, "{", Ct);

        Assert.Equal(2, _data.SweepTemp()["Deleted"]);
        Assert.Equal((false, false, true, true, true), (File.Exists(leftExport), File.Exists(leftUpload), File.Exists(exportPath), File.Exists(upload), File.Exists(other)));
        hold.Dispose();
        Assert.Equal(1, _data.SweepTemp()["Deleted"]);
        Assert.False(File.Exists(upload));

        // A kill and a start: no job survives it, so the start's sweep deletes the export's file (the runner never got to).
        using var restarted = new AdminJobRunner(_s.Store.Clock, _s.Store.Logger);
        var data = new RatingsData(Store, _envelope, restarted, _s.UserManager.Object, _s.Library.Object, () => throw new InvalidOperationException(), () => _detached, () => _consumer, null, new ArchivedLinePurges(Store, _s.Store.Logger, () => _detached.ArchiveOf(_consumer)), _s.Settings.Read, _s.Store.Clock, _s.Store.Logger);
        await new DataTempSweep(() => data, _s.Store.Logger).StartAsync(Ct);
        Assert.False(File.Exists(exportPath));
        Assert.True(File.Exists(other));

        // A sweep that can't be created never stops the start.
        await new DataTempSweep(() => throw new InvalidOperationException("No store."), _s.Store.Logger).StartAsync(Ct);
    }

    [Fact]
    public async Task AnUpload_IsHeldWhileItsJobRuns_AndDeletedWhenItEnds()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "A fine film."), Ct);
        var file = await ExportFileAsync();
        await using (var body = File.OpenRead(file))
        {
            await _data.StartImportAsync(null, body, body.Length, "Merge", true, "Id", null, Ct);
        }

        Assert.Single(TmpFiles(), f => Path.GetFileName(f).StartsWith("userratings-import-", StringComparison.Ordinal));
        Assert.Equal(0, _data.SweepTemp()["Deleted"]);
        await Finished();
        Assert.DoesNotContain(TmpFiles(), f => Path.GetFileName(f).StartsWith("userratings-import-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RatingValues.DeletedBy.Moderator, "Merge", "SkippedDeleted")]
    [InlineData(RatingValues.DeletedBy.User, "Replace", "SkippedDeleted")]
    [InlineData(RatingValues.DeletedBy.UserDeleted, "Merge", "SkippedDeleted")]
    [InlineData(RatingValues.DeletedBy.Purge, "Merge", "PreviouslyPurged")]
    [InlineData(RatingValues.DeletedBy.Purge, "Replace", "PreviouslyPurged")]
    public async Task ARatingTheLedgerMapsToOneDeletedHere_NeverComesBack_HoweverNewTheFilesCopy(string deletedBy, string mode, string expected)
    {
        // Inverted: another server's X, newer, merged into the local Y (the ledger records X -> Y); Y is
        // deleted here; the same file again used to add X, the deleted words with it, since only X's own id was checked.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var local = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Local words.", StoreFixture.T0), Ct);
        var (other, otherId) = await AnotherServersCopyAsync(row =>
        {
            row["ReviewText"] = "Words from the other server.";
            row["UpdatedUtc"] = StoreFixture.T0.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
            row["ReviewUpdatedUtc"] = StoreFixture.T0.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
        });

        var first = await ImportAsync(other, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(first, "Updated"));
        Assert.Equal("Words from the other server.", Store.Get(local.Id)!.ReviewText);

        await Store.DeleteAsync(local.Id, deletedBy, StoreFixture.T0.AddDays(2), Ct);
        var dry = await ImportAsync(other, mode, dryRun: true);
        Assert.Equal(1, Outcome(dry, expected));
        var again = await ImportAsync(other, mode, dryRun: false);
        Assert.Equal((1, 0), (Outcome(again, expected), Outcome(again, "Added")));
        Assert.Null(Store.Get(otherId));
        Assert.Null(Store.Get(local.Id));
        Assert.Empty(Store.GetAttachedForItem(movie.Id));
        Assert.Equal(deletedBy, Store.GetTombstone(local.Id)!.DeletedBy);
    }

    [Fact]
    public async Task APurgedRatingOwnId_IsPreviouslyPurged_AndALiveLedgerTarget_IsStillUpdated()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var heat = _s.AddMovie(_s.Movies, "Heat", "949");
        var purged = await Store.UpsertAsync(Rating(_s.Viewer.Id, heat.Id, "Purged here."), Ct);
        var local = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Local words.", StoreFixture.T0), Ct);
        var file = await ExportFileAsync();
        await Store.DeleteAsync(purged.Id, RatingValues.DeletedBy.Purge, StoreFixture.T0.AddDays(1), Ct);
        var report = await ImportAsync(WithoutTombstones(file), "Merge", dryRun: false);
        Assert.Equal((1, 0, 1), (Outcome(report, "PreviouslyPurged"), Outcome(report, "SkippedDeleted"), Outcome(report, "Unchanged")));
        Assert.Null(Store.Get(purged.Id));

        // Without a tombstone the ledger's target is the row a newer copy updates, as before.
        var (other, otherId) = await AnotherServersCopyAsync(row => row["UpdatedUtc"] = StoreFixture.T0.AddDays(1).ToString("O", CultureInfo.InvariantCulture), onlyItem: movie.Id);
        Assert.Equal(1, Outcome(await ImportAsync(other, "Merge", dryRun: false), "Updated"));
        Assert.Equal(1, Outcome(await ImportAsync(other, "Merge", dryRun: false), "Unchanged"));
        Assert.Null(Store.Get(otherId));
        Assert.NotNull(Store.Get(local.Id));
    }

    [Fact]
    public async Task RestoringTheImportsSnapshot_PutsBackTheArchiveTheImportFound()
    {
        // Inverted: the archived ratings an import added stayed after its snapshot was restored.
        var archivedId = await ArchivedRatingAsync("Archived words.");
        var file = await ExportFileAsync();
        var archive = _detached.ArchiveOf(_consumer);
        File.Delete(archive.FilePath);

        var report = await ImportAsync(file, "Merge", dryRun: false);
        Assert.Equal(1, Archive(report).Imported);
        Assert.Equal(archivedId.ToString("D"), Assert.Single(archive.Read().Records).Id);
        var copy = Assert.Single(SnapshotRotation.ListPreOp(_detached.SnapshotDirectory, archive.SnapshotName, ".jsonl"), c => c.Operation == SnapshotOperations.Import);

        var restored = await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);
        Assert.Equal(copy.Name, restored["ArchiveRestoredFrom"]);
        Assert.Empty(archive.Read().Records);
        Assert.DoesNotContain("Archived words.", await File.ReadAllTextAsync(archive.FilePath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArchiveThatCantBeWrittenAfterTheCommit_IsReported_TheRatingsStayImported_WithTheirSnapshot()
    {
        // The job said "nothing of it was written" and lost the snapshot's name.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await ArchivedRatingAsync("Archived words.");
        var live = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Live words."), Ct);
        var file = await ExportFileAsync();
        var archive = _detached.ArchiveOf(_consumer);
        File.Delete(archive.FilePath);
        archive.Append([ArchivedRating.ToArchive(Rating(_s.Viewer.Id, Guid.NewGuid(), "Only here.") with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }, userKnown: true)]);
        await Store.WriteAsync(w => w.Delete(live.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddDays(3)), Ct);
        var dry = await ImportAsync(WithoutTombstones(file), "Merge", dryRun: true);

        // Read and copied, not written: the archive held open for reading only.
        IDictionary<string, object?> report;
        using (new FileStream(archive.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            report = await ImportAsync(WithoutTombstones(file), "Merge", dryRun: false, ifPlan: (string)dry["PlanToken"]!);
        }

        Assert.NotNull(report["ArchiveError"]);
        Assert.Equal(0, Archive(report).Imported);
        Assert.Contains(report["Notes"] as IEnumerable<string> ?? [], n => n.Contains("The ratings were imported", StringComparison.Ordinal) && n.Contains((string)report["SafetySnapshot"]!, StringComparison.Ordinal));
        Assert.Equal(1, Outcome(report, "SkippedDeleted"));
        Assert.Single(archive.Read().Records);

        // The runner, for a failure after a commit: the changes stay, and the snapshot undoes them; no cancel then.
        var gate = new TaskCompletionSource();
        var committed = new TaskCompletionSource();
        var job = _jobs.Start("Import", false, async (job, _) =>
        {
            job.MarkCommitted("userratings-import-20261101-120000.db");
            committed.SetResult();
            await gate.Task.ConfigureAwait(false);
            throw new IOException("The disk is full.");
        });
        await committed.Task;
        Assert.Equal(AdminJobRunner.WritingCode, Assert.Throws<PluginException>(() => _jobs.Cancel(Guid.Parse(job.Id))).Code);
        gate.SetResult();
        await Finished();
        var failed = _jobs.Get(Guid.Parse(job.Id));
        Assert.Equal(AdminJob.Failed, failed.State);
        Assert.Contains("were written", failed.Error, StringComparison.Ordinal);
        Assert.Contains("userratings-import-20261101-120000.db", failed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing of it was written", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherServersFormerMember_IsImported_ButOneWhoseUserWasDeletedHere_IsNot_RatingsAndArchiveAlike()
    {
        // A former member of another server is imported (a design note once said otherwise); the guard
        // that keeps out a former member whose user this server deleted (and doesn't know as a former member) is tested.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Template."), Ct);
        await ArchivedRatingAsync("Archived template.");
        var file = await ExportFileAsync();
        var deletedHere = Guid.NewGuid();
        _deleted.Add(deletedHere);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var template = json["Data"]!["Ratings"]!.AsArray().Single()!.AsObject();
        var archived = json["Archive"]!.AsArray().Single()!.AsObject();
        var ratings = new JsonArray();
        var archive = new JsonArray();
        var formerRows = new JsonArray();
        var ids = new Dictionary<string, (Guid Rating, Guid Archived, Guid Former)>();
        foreach (var (name, original) in new[] { ("elsewhere", Guid.NewGuid()), ("deleted here", deletedHere) })
        {
            var former = Guid.NewGuid();
            var row = (JsonObject)template.DeepClone();
            row["Id"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            row["UserId"] = former.ToString("D", CultureInfo.InvariantCulture);
            row["ReviewText"] = "A former member " + name + ".";
            ratings.Add(row);
            var line = (JsonObject)archived.DeepClone();
            line["Id"] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            line["UserId"] = former.ToString("D", CultureInfo.InvariantCulture);
            archive.Add(line);
            formerRows.Add(new JsonObject
            {
                ["Id"] = former.ToString("D", CultureInfo.InvariantCulture),
                ["OriginalUserId"] = original.ToString("D", CultureInfo.InvariantCulture),
                ["DisplayName"] = "Former member " + name,
                ["DeletedUtc"] = StoreFixture.T0.ToString("O", CultureInfo.InvariantCulture)
            });
            ids[name] = (Guid.Parse((string)row["Id"]!), Guid.Parse((string)line["Id"]!), former);
        }

        json["Data"]!["Ratings"] = ratings;
        json["Data"]!["FormerUsers"] = formerRows;
        json["Data"]!["Tombstones"] = new JsonArray();
        json["Archive"] = archive;
        var crafted = Path.Combine(_s.Store.Directory.Path, "former-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(crafted, json.ToJsonString(), Ct);
        File.Delete(_detached.ArchiveOf(_consumer).FilePath);

        var report = await ImportAsync(crafted, "Merge", dryRun: false);
        Assert.Equal((1, 1), (Outcome(report, "Added"), Outcome(report, "SkippedDeletedUser")));
        Assert.Contains(Store.GetAttachedForItem(movie.Id), r => r.UserId == ids["elsewhere"].Former && r.ReviewText == "A former member elsewhere.");
        Assert.Null(Store.Get(ids["deleted here"].Rating));
        Assert.True(Store.GetFormerUsers().ContainsKey(ids["elsewhere"].Former));
        Assert.False(Store.GetFormerUsers().ContainsKey(ids["deleted here"].Former));
        Assert.Equal(new[] { ids["elsewhere"].Archived.ToString("D") }, _detached.ArchiveOf(_consumer).Read().Records.Select(r => r.Id));
    }

    [Fact]
    public async Task AnArchivedRatingDeletedHere_IsNotImported_NorOneLiveHere()
    {
        // The archive's tombstone check (a rating deleted here since the export) had no test.
        var archivedId = await ArchivedRatingAsync("Archived, then deleted here.");
        var file = await ExportFileAsync();
        var archive = _detached.ArchiveOf(_consumer);
        File.Delete(archive.FilePath);
        await Store.WriteAsync(w => { w.AddTombstone(archivedId, _s.Viewer.Id, Guid.NewGuid(), RatingValues.DeletedBy.Purge, StoreFixture.T0.AddDays(5)); return 0; }, Ct);

        var report = await ImportAsync(WithoutTombstones(file), "Merge", dryRun: false);
        Assert.Equal((1, 0), (Archive(report).Records, Archive(report).Imported));
        Assert.StartsWith("1 archived ratings deleted here", Archive(report).Note, StringComparison.Ordinal);
        Assert.Empty(archive.Read().Records);
    }

    [Fact]
    public async Task Replace_MakesTheArchiveTheFiles_WithoutItsDeletedOnes_AndItsSnapshotBringsTheOldOneBack()
    {
        // Replace kept this server's archive and appended the file's lines.
        var fromFile = await ArchivedRatingAsync("In the file's archive.");
        var deletedHere = await ArchivedRatingAsync("In the file's archive, deleted here since.");
        var file = await ExportFileAsync();
        var archive = _detached.ArchiveOf(_consumer);
        File.Delete(archive.FilePath);
        var onlyHere = ArchivedRating.ToArchive(Rating(_s.Viewer.Id, Guid.NewGuid(), "Only in this server's archive.") with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }, userKnown: true);
        archive.Append([onlyHere]);
        await Store.WriteAsync(w => { w.AddTombstone(deletedHere, _s.Viewer.Id, Guid.NewGuid(), RatingValues.DeletedBy.User, StoreFixture.T0.AddDays(5)); return 0; }, Ct);

        var dry = await ImportAsync(WithoutTombstones(file), "Replace", dryRun: true);
        Assert.Equal(1, Archive(dry).Imported);
        Assert.Contains("Replace makes the archive the file's", Archive(dry).Note, StringComparison.Ordinal);
        Assert.Equal(new[] { onlyHere.Id }, archive.Read().Records.Select(r => r.Id));

        var report = await ImportAsync(WithoutTombstones(file), "Replace", dryRun: false);
        Assert.Equal(new[] { fromFile.ToString("D") }, archive.Read().Records.Select(r => r.Id));
        Assert.DoesNotContain("Only in this server's archive.", await File.ReadAllTextAsync(archive.FilePath, Ct), StringComparison.Ordinal);
        Assert.NotNull(Store.GetTombstone(deletedHere));

        await _data.RestoreAsync(_s.Admin.Id, (string)report["SafetySnapshot"]!, Ct);
        Assert.Equal(new[] { onlyHere.Id }, archive.Read().Records.Select(r => r.Id));
    }

    [Fact]
    public async Task AnUpdatedRow_TakesTheReviewItReplacesOutOfTheArchivesEarlierLines()
    {
        // A member's own edit does this (since moderation came in); an import's Updated row didn't.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Exported, newer.", StoreFixture.T0.AddDays(1)), Ct);
        var file = await ExportFileAsync();
        var older = row with { ReviewText = "Old words, once archived.", UpdatedUtc = StoreFixture.T0 };
        await Store.WriteAsync(w => { w.Upsert(older); return 0; }, Ct);
        var archive = _detached.ArchiveOf(_consumer);
        archive.Append([ArchivedRating.ToArchive(older with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }, userKnown: true)]);
        archive.MarkReattached([row.Id.ToString("D")]);
        Assert.Contains("Old words, once archived.", await File.ReadAllTextAsync(archive.FilePath, Ct), StringComparison.Ordinal);

        var report = await ImportAsync(file, "Merge", dryRun: false);
        Assert.Equal(1, Outcome(report, "Updated"));
        Assert.Equal("Exported, newer.", Store.Get(row.Id)!.ReviewText);
        Assert.DoesNotContain("Old words, once archived.", await File.ReadAllTextAsync(archive.FilePath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEnvelopeTombstone_DeletesALiveRatingOnlyWhenItIsNewer()
    {
        // The KeptNewer branch had no test: a tombstone older than the live row would have deleted it.
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var b = _s.AddMovie(_s.Movies, "B", "2");
        var kept = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Edited after the file's deletion.", StoreFixture.T0.AddDays(2)), Ct);
        var gone = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Deleted in the file since.", StoreFixture.T0), Ct);
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        json["Data"]!["Ratings"] = new JsonArray();
        json["Data"]!["Tombstones"] = new JsonArray(
            Tombstone(kept, StoreFixture.T0.AddDays(1)),
            Tombstone(gone, StoreFixture.T0.AddDays(1)));
        var crafted = Path.Combine(_s.Store.Directory.Path, "tombstones-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(crafted, json.ToJsonString(), Ct);

        var report = await ImportAsync(crafted, "Merge", dryRun: false);
        Assert.Equal((1, 1), (Outcome(report, "TombstoneKeptNewer"), Outcome(report, "TombstoneDeleted")));
        Assert.Equal("Edited after the file's deletion.", Store.Get(kept.Id)!.ReviewText);
        Assert.Null(Store.Get(gone.Id));
        Assert.Null(Store.GetTombstone(kept.Id));

        static JsonObject Tombstone(RatingRecord r, DateTime at) => new()
        {
            ["RatingId"] = r.Id.ToString("D", CultureInfo.InvariantCulture),
            ["UserId"] = r.UserId.ToString("D", CultureInfo.InvariantCulture),
            ["ItemId"] = r.ItemId.ToString("D", CultureInfo.InvariantCulture),
            ["DeletedUtc"] = at.ToString("O", CultureInfo.InvariantCulture),
            ["DeletedBy"] = RatingValues.DeletedBy.User
        };
    }

    [Fact]
    public async Task ARestore_UndoesEverythingSince_ExceptDeletions_WhichStayDeleted()
    {
        // Inverted: a review its member deleted after the snapshot came back, live.
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var b = _s.AddMovie(_s.Movies, "B", "2");
        var c = _s.AddMovie(_s.Movies, "C", "3");
        var d = _s.AddMovie(_s.Movies, "D", "4");
        var deleted = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Deleted by its member after the snapshot."), Ct);
        var removed = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Removed by a moderator after the snapshot."), Ct);
        var edited = await Store.UpsertAsync(Rating(_s.Viewer.Id, c.Id, "As it was."), Ct);
        var anonymized = await Store.UpsertAsync(Rating(_s.Viewer2.Id, d.Id, "Of a member deleted since."), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Import, Ct))!;

        await Store.DeleteAsync(deleted.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddMinutes(5), Ct);
        await Store.WriteAsync(w => w.ClearReview(removed.Id, StoreFixture.T0.AddMinutes(5)), Ct);
        await Store.WriteAsync(w => { w.Upsert(edited with { ReviewText = "Edited since.", UpdatedUtc = StoreFixture.T0.AddMinutes(5) }); return 0; }, Ct);
        var former = Guid.NewGuid();
        await Store.WriteAsync(w => { w.AddFormerUser(former, _s.Viewer2.Id, "Former member", StoreFixture.T0.AddMinutes(5)); w.Upsert(anonymized with { UserId = former }); return 0; }, Ct);
        var archive = _detached.ArchiveOf(_consumer);
        archive.Append([ArchivedRating.ToArchive(deleted with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }, userKnown: true)]);
        archive.MarkReattached([deleted.Id.ToString("D")]);

        var result = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);
        Assert.Null(Store.Get(deleted.Id));
        Assert.Equal(RatingValues.DeletedBy.User, Store.GetTombstone(deleted.Id)!.DeletedBy);
        Assert.Null(Store.Get(removed.Id)!.ReviewText);
        Assert.Equal(8, Store.Get(removed.Id)!.Stars2);
        Assert.Equal("As it was.", Store.Get(edited.Id)!.ReviewText);
        Assert.Equal(former, Store.Get(anonymized.Id)!.UserId);
        Assert.True(Store.GetFormerUsers().ContainsKey(former));
        Assert.Equal((1, 1, 1, (string?)null), (result["DeletionsKept"], result["ReviewRemovalsKept"], result["FormerMembersKept"], (string?)result["DeletionsError"]));
        Assert.DoesNotContain("Deleted by its member after the snapshot.", await File.ReadAllTextAsync(archive.FilePath, Ct), StringComparison.Ordinal);

        // The restore's own safety snapshot is the way back to the ratings as they were before it.
        Assert.NotNull(result["SafetySnapshot"]);
    }

    [Fact]
    public async Task ARestore_HoldsTheRunnerForItsWholeRun_AndWaitsForNoJob()
    {
        // The busy check was at the start only, so an export or an import could start during a restore.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Before."), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.Import, Ct))!;

        using var release = new ManualResetEventSlim();
        var inWrite = new TaskCompletionSource();
        var blocker = Task.Run(() => Store.WriteAsync(w => { inWrite.SetResult(); release.Wait(Ct); return 0; }, Ct));
        await inWrite.Task;
        var restore = Task.Run(() => _data.RestoreAsync(_s.Admin.Id, snapshot, Ct));
        await WaitUntil(() => { try { _jobs.ThrowIfBusy(); return false; } catch (PluginException) { return true; } });
        Assert.Equal(AdminJobRunner.BusyCode, Assert.Throws<PluginException>(() => _data.StartExport(_s.Admin.Id)).Code);
        Assert.Equal(AdminJobRunner.BusyCode, (await Assert.ThrowsAsync<PluginException>(() => _data.StartImportAsync(null, Stream.Null, 0, "Merge", true, "Id", null, Ct))).Code);
        Assert.False(restore.IsCompleted);
        release.Set();
        await blocker;
        await restore;
        _data.StartExport(_s.Admin.Id);
        await Finished();

        // And a restore while a job runs is refused.
        var gate = new TaskCompletionSource();
        _jobs.Start("Import", true, async (_, ct) => { await gate.Task.WaitAsync(ct).ConfigureAwait(false); return null; });
        Assert.Equal(AdminJobRunner.BusyCode, (await Assert.ThrowsAsync<PluginException>(() => _data.RestoreAsync(_s.Admin.Id, snapshot, Ct))).Code);
        gate.SetResult();
        await Finished();
    }

    [Fact]
    public async Task ARestore_PutsBackTheArchiveCopyOfItsOperationAndTime_NotAnOlderOne_ByAnySpellingOfTheName()
    {
        // The copy's time window, and the name looked up trimmed, ignoring case.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Before."), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.UserDeleted, Ct))!;
        var info = Store.ListSnapshots().Single(s => s.Name == snapshot);
        var archive = _detached.ArchiveOf(_consumer);
        Directory.CreateDirectory(Path.GetDirectoryName(archive.FilePath)!);
        await File.WriteAllTextAsync(archive.FilePath, "today\n", Ct);
        var older = SnapshotRotation.NewPreOpPath(_detached.SnapshotDirectory, archive.SnapshotName, SnapshotOperations.UserDeleted, ".jsonl", info.Utc.AddHours(-1));
        await File.WriteAllTextAsync(older, "an earlier deletion's\n", Ct);
        var matching = SnapshotRotation.NewPreOpPath(_detached.SnapshotDirectory, archive.SnapshotName, SnapshotOperations.UserDeleted, ".jsonl", info.Utc.AddSeconds(2));
        await File.WriteAllTextAsync(matching, "then\n", Ct);

        var result = await _data.RestoreAsync(_s.Admin.Id, "  " + snapshot.ToUpperInvariant() + " ", Ct);
        Assert.Equal((Path.GetFileName(matching), (bool?)true), (result["ArchiveRestoredFrom"], (bool?)result["ArchiveRestored"]));
        Assert.Equal("then\n", await File.ReadAllTextAsync(archive.FilePath, Ct));
    }

    [Fact]
    public async Task AnArchiveCopyThatCantBePutBack_IsInTheAnswer_AndTheArchiveIsAsItWas()
    {
        // The swap was outside the archive's lock, and its failure only logged.
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Before."), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.UserDeleted, Ct))!;
        var info = Store.ListSnapshots().Single(s => s.Name == snapshot);
        var archive = _detached.ArchiveOf(_consumer);
        Directory.CreateDirectory(Path.GetDirectoryName(archive.FilePath)!);
        await File.WriteAllTextAsync(archive.FilePath, "today\n", Ct);
        await File.WriteAllTextAsync(SnapshotRotation.NewPreOpPath(_detached.SnapshotDirectory, archive.SnapshotName, SnapshotOperations.UserDeleted, ".jsonl", info.Utc.AddSeconds(2)), "then\n", Ct);
        await Store.WriteAsync(w => { w.Upsert(row with { ReviewText = "Edited since.", UpdatedUtc = StoreFixture.T0.AddMinutes(1) }); return 0; }, Ct);

        IDictionary<string, object?> result;
        using (new FileStream(archive.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            result = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);
        }

        Assert.Equal("Before.", Store.Get(row.Id)!.ReviewText);
        Assert.Equal(((bool?)false, (string?)null), ((bool?)result["ArchiveRestored"], (string?)result["ArchiveRestoredFrom"]));
        Assert.NotNull(result["ArchiveError"]);
        Assert.Equal("today\n", await File.ReadAllTextAsync(archive.FilePath, Ct));
    }

    [Theory]
    [InlineData("N")]
    [InlineData("B")]
    [InlineData("P")]
    [InlineData("UPPER")]
    public async Task EveryGuidOfARow_IsReadInAnyForm_SoATombstoneInNFormStillKeepsItsRatingOut(string form)
    {
        // Before 0.3.19 a Guid in "N" form made its row unusable, and a tombstone dropped that way let
        // its rating back in.
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var b = _s.AddMovie(_s.Movies, "B", "2");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "Live."), Ct);
        var deleted = await Store.UpsertAsync(Rating(_s.Viewer.Id, b.Id, "Deleted here."), Ct);
        await Store.DeleteAsync(deleted.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddMinutes(1), Ct);
        var former = Guid.NewGuid();
        await Store.WriteAsync(w => { w.AddFormerUser(former, Guid.NewGuid(), "Former member", StoreFixture.T0); w.Upsert(Rating(former, a.Id, "A former member's.")); return 0; }, Ct);
        await ArchivedRatingAsync("Archived.");
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        json["Data"] = Reform(json["Data"]!, form);
        json["Archive"] = Reform(json["Archive"]!, form);
        var reformed = Path.Combine(_s.Store.Directory.Path, "reformed-" + form + ".json");
        await File.WriteAllTextAsync(reformed, json.ToJsonString(), Ct);
        Assert.DoesNotContain('"' + deleted.Id.ToString("D") + '"', await File.ReadAllTextAsync(reformed, Ct), StringComparison.Ordinal);

        // The same decisions as the file as exported, with no row unusable.
        var asExported = await ImportAsync(file, "Replace", dryRun: true);
        var report = await ImportAsync(reformed, "Replace", dryRun: true);
        Assert.Equal(0, report["Unusable"]);
        Assert.Equal((IReadOnlyDictionary<string, int>)asExported["Outcomes"]!, (IReadOnlyDictionary<string, int>)report["Outcomes"]!);
        Assert.Equal(1, Outcome(report, "TombstoneKnown"));
        Assert.Equal((1, 1), (Archive(report).Records, Archive(report).Imported));
        Assert.Equal(asExported["PlanToken"] is null, report["PlanToken"] is null);

        static JsonNode Reform(JsonNode node, string form)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(p => p.Key).ToList())
                    {
                        o[key] = o[key] is { } child ? Reform(child.DeepClone(), form) : null;
                    }

                    return o;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        array[i] = array[i] is { } child ? Reform(child.DeepClone(), form) : null;
                    }

                    return array;
                case JsonValue value when value.TryGetValue<string>(out var text) && Guid.TryParseExact(text, "D", out var id):
                    return JsonValue.Create(form == "UPPER" ? id.ToString("D").ToUpperInvariant() : id.ToString(form, CultureInfo.InvariantCulture))!;
                default:
                    return node;
            }
        }
    }

    private static ArchiveImportResult Archive(IDictionary<string, object?> report)
    {
        var archive = (IDictionary<string, object?>)report["Archive"]!;
        return new ArchiveImportResult((int)archive["Records"]!, (int)archive["Imported"]!, (string?)archive["Note"]);
    }

    // A detached rating moved to the archive (retention 1 day): its id.
    private async Task<Guid> ArchivedRatingAsync(string text)
    {
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, Guid.NewGuid(), text) with { DetachedUtc = _s.Store.Clock.GetUtcNow().UtcDateTime, DetachReason = RatingValues.DetachReason.Missing }, Ct);
        _s.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, (await _detached.ArchiveDueAsync(_consumer, Ct)).Archived);
        Assert.Null(Store.Get(row.Id));
        return row.Id;
    }

    // The export with its one rating (on onlyItem, if given) under a new id, as another server's copy, and without tombstones.
    private async Task<(string File, Guid Id)> AnotherServersCopyAsync(Action<JsonObject> change, Guid? onlyItem = null)
    {
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        var ratings = json["Data"]!["Ratings"]!.AsArray();
        var row = ratings.Single(r => onlyItem is null || Guid.Parse((string)r!["Identity"]!["ItemId"]!) == onlyItem)!.AsObject();
        ratings.Clear();
        ratings.Add(row);
        var id = Guid.NewGuid();
        row["Id"] = id.ToString("D", CultureInfo.InvariantCulture);
        change(row);
        json["Data"]!["Tombstones"] = new JsonArray();

        // Another server's export carries its own id (this server's own is read as such).
        json["ServerId"] = "server-2";
        var other = Path.Combine(_s.Store.Directory.Path, "other-server-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(other, json.ToJsonString(), Ct);
        return (other, id);
    }

    private string[] TmpFiles()
    {
        var tmp = Path.Combine(Store.DataDirectory, "tmp");
        return Directory.Exists(tmp) ? Directory.GetFiles(tmp) : [];
    }

    // A request body of so many bytes, with no length to read ahead.
    private sealed class FilledStream(long length) : Stream
    {
        private long _left = length;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = (int)Math.Min(buffer.Length, _left);
            buffer.Span[..n].Fill((byte)'x');
            _left -= n;
            return ValueTask.FromResult(n);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A request body that gives some bytes, then fails as Kestrel does.
    private sealed class ThrowingStream(Exception error) : Stream
    {
        private bool _gave;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_gave)
            {
                _gave = true;
                buffer.Span[0] = (byte)'{';
                return ValueTask.FromResult(1);
            }

            return ValueTask.FromException<int>(error);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
