using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// The job runner, the envelope export and import (Merge, Replace, the dry run's PlanToken, tombstones and deleted
/// users never brought back), Restore with the archive's copy, and the detached list with Relink and Purge.
/// </summary>
public sealed partial class RatingsDataTests : IDisposable
{
    private readonly ServicesFixture _s = new();
    private readonly HashSet<Guid> _deleted = [];
    private readonly RatingsLibraryConsumer _consumer;
    private readonly DetachedRecords _detached;
    private readonly AdminJobRunner _jobs;
    private readonly RatingsEnvelope _envelope;
    private readonly RatingsData _data;

    public RatingsDataTests()
    {
        // The data the tests write is dated from T0 on; the clock is ahead of it, as a server's is (an import writes no
        // time ahead of the clock since 0.3.21).
        _s.Advance(TimeSpan.FromDays(30));
        _s.Admin.SetPermission(PermissionKind.EnableAllFolders, true);
        var journal = new MoveJournalReader(_s.Store.Directory.Combine("moves.jsonl"), _s.Store.Logger);
        var resolver = new IdentityResolver(_s.Library.Object, journal, _s.Store.Clock);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(new Version(10, 11, 5));
        RatingsLibraryConsumer? consumer = null;
        _detached = new DetachedRecords(
            _s.Store.Directory.Combine("archive"),
            _s.Store.Snapshots,
            _s.Library.Object,
            id => _s.UserManager.Object.GetUserById(id),
            resolver,
            journal,
            () => [consumer!],
            _s.Store.Logger,
            _s.Store.Clock);
        _consumer = consumer = new RatingsLibraryConsumer(host.Object, _s.Store.Store, _s.Library.Object, _s.UserManager.Object, journal, static () => throw new InvalidOperationException("No verifier here."), () => _detached, null, _s.Store.Logger, _s.Store.Clock, static () => 1);
        _s.UserManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => _s.Users.FirstOrDefault(u => u.Id == id));
        var lines = new ArchivedLinePurges(_s.Store.Store, _s.Store.Logger, () => _detached.ArchiveOf(_consumer));
        _jobs = new AdminJobRunner(_s.Store.Clock, _s.Store.Logger);
        _envelope = new RatingsEnvelope(_s.Store.Store, _s.Library.Object, _s.UserManager.Object, () => resolver, () => _detached, () => _consumer, () => KnownUsers.Empty, () => _deleted, lines, "server-1", _s.Store.Clock, _s.Store.Logger);
        _data = new RatingsData(_s.Store.Store, _envelope, _jobs, _s.UserManager.Object, _s.Library.Object, () => resolver, () => _detached, () => _consumer, null, lines, _s.Settings.Read, _s.Store.Clock, _s.Store.Logger);
    }

    private static CancellationToken Ct => ServicesFixture.Ct;

    private IRatingsStore Store => _s.Store.Store;

    public void Dispose()
    {
        _jobs.Dispose();
        _consumer.Dispose();
        _s.Dispose();
    }

    [Fact]
    public async Task TheRunner_RunsOneJobAtATime_ReportsProgress_AndKeepsTheLastTen()
    {
        var gate = new TaskCompletionSource();
        var first = _jobs.Start("Import", dryRun: true, async (job, ct) =>
        {
            job.Progress("Resolve", 3, 10);
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return "done";
        });
        var busy = Assert.Throws<PluginException>(() => _jobs.Start("Export", true, (_, _) => Task.FromResult<object?>(null)));
        Assert.Equal(PluginErrorKind.Conflict, busy.Kind);
        Assert.Equal(AdminJobRunner.BusyCode, busy.Code);
        await WaitUntil(() => _jobs.Get(Guid.Parse(first.Id)).Processed == 3);
        Assert.Equal(("Running", "Resolve", 10), (_jobs.Get(Guid.Parse(first.Id)).State, _jobs.Get(Guid.Parse(first.Id)).Phase, _jobs.Get(Guid.Parse(first.Id)).Total));
        gate.SetResult();
        await Finished();
        var finished = _jobs.Get(Guid.Parse(first.Id));
        Assert.Equal((AdminJob.Succeeded, "done"), (finished.State, finished.Report));
        Assert.Equal(AdminJobRunner.FinishedCode, Assert.Throws<PluginException>(() => _jobs.Cancel(Guid.Parse(first.Id))).Code);

        for (var i = 0; i < AdminJobRunner.KeepFinished; i++)
        {
            _jobs.Start("Export", true, (_, _) => Task.FromResult<object?>(i));
            await Finished();
        }

        Assert.Equal(AdminJobRunner.KeepFinished, _jobs.List().Count);
        Assert.Equal(PluginErrorKind.NotFound, Assert.Throws<PluginException>(() => _jobs.Get(Guid.Parse(first.Id))).Kind);
    }

    [Fact]
    public async Task ACancel_StopsAJobBeforeItsWritePhase_AndIsRefusedInIt_SoAnImportIsNeverHalfApplied()
    {
        var resolving = new TaskCompletionSource();
        var job = _jobs.Start("Import", false, async (job, ct) =>
        {
            resolving.SetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            return null;
        });
        await resolving.Task;
        _jobs.Cancel(Guid.Parse(job.Id));
        await Finished();
        Assert.Equal(AdminJob.Cancelled, _jobs.Get(Guid.Parse(job.Id)).State);

        var writing = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var second = _jobs.Start("Import", false, async (job, ct) =>
        {
            job.EnterWritePhase(1);
            writing.SetResult();
            await release.Task.ConfigureAwait(false);
            return "committed";
        });
        await writing.Task;
        Assert.Equal(AdminJobRunner.WritingCode, Assert.Throws<PluginException>(() => _jobs.Cancel(Guid.Parse(second.Id))).Code);
        Assert.True(_jobs.Get(Guid.Parse(second.Id)).Writing);
        release.SetResult();
        await Finished();
        Assert.Equal((AdminJob.Succeeded, "committed"), (_jobs.Get(Guid.Parse(second.Id)).State, _jobs.Get(Guid.Parse(second.Id)).Report));
    }

    [Fact]
    public async Task TheExportJobsFile_IsWhatGetExportAnswers_InTheEnvelopesShape()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "A fine film."), Ct);
        var file = await ExportFileAsync();

        // The same bytes as GET Export (ToUtf8Bytes): the users' ids in the "N" format, and Deleted only when true. Over a
        // second of the system's clock later: both take the plugin's clock's time (before 0.3.22 the
        // export took the system's, so the test failed whenever a second ticked over between the two).
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        Assert.Equal(_data.Export(_s.Admin.Id).ToUtf8Bytes(), await File.ReadAllBytesAsync(file, Ct));
        var users = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!["Users"]!.AsArray();
        Assert.NotEmpty(users);
        Assert.All(users, u =>
        {
            Assert.Matches("^[0-9a-f]{32}$", (string)u!["Id"]!);
            Assert.False(u!.AsObject().ContainsKey("Deleted"));
        });
    }

    [Fact]
    public async Task AnExportImportedWithReplace_BringsEveryTableBack()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var other = _s.AddMovie(_s.Movies, "Heat", "949");
        var kept = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "A fine film."), Ct);
        var gone = await Store.UpsertAsync(Rating(_s.Viewer2.Id, other.Id, "Long."), Ct);
        await Store.DeleteAsync(gone.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddHours(1), Ct);
        var file = await ExportFileAsync();

        await Store.WriteAsync(w => w.Delete(kept.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddHours(2)), Ct);
        Assert.Null(Store.Get(kept.Id));

        // The kept rating was deleted after the export: Replace keeps that tombstone, so it stays deleted.
        var report = await ImportAsync(file, "Replace", dryRun: false);
        Assert.Null(Store.Get(kept.Id));
        Assert.Equal(1, Outcome(report, "SkippedDeleted"));
        Assert.NotNull(Store.GetTombstone(gone.Id));
        Assert.NotNull(report["SafetySnapshot"]);
    }

    [Fact]
    public async Task ATombstonedRating_NeverComesBack_ThroughMergeOrReplace()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Deleted words."), Ct);
        var file = await ExportFileAsync();
        await Store.DeleteAsync(row.Id, RatingValues.DeletedBy.Moderator, StoreFixture.T0.AddDays(1), Ct);

        foreach (var mode in new[] { "Merge", "Replace" })
        {
            var report = await ImportAsync(file, mode, dryRun: false);
            Assert.Null(Store.Get(row.Id));
            Assert.Equal(1, Outcome(report, "SkippedDeleted"));
        }
    }

    [Fact]
    public async Task ADeletedUsersRatings_AreNeverRecreated_ByIdOrByName()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var other = _s.AddMovie(_s.Movies, "Heat", "949");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Mine."), Ct);
        var orphan = await Store.UpsertAsync(Rating(_s.Viewer.Id, other.Id, "Kept as an orphan."), Ct);
        var file = await ExportFileAsync();

        // The user is deleted: one rating with Delete's tombstone, the other kept as an orphan; a new user takes the name.
        await Store.WriteAsync(w => w.Delete(row.Id, RatingValues.DeletedBy.UserDeleted, StoreFixture.T0.AddDays(1)), Ct);
        _s.Users.Remove(_s.Viewer);
        _deleted.Add(_s.Viewer.Id);
        var namesake = ServicesFixture.NewUser(_s.Viewer.Username);
        _s.Users.Add(namesake);
        var bare = WithoutTombstones(file);
        foreach (var by in new[] { "Id", "Name" })
        {
            var report = await ImportAsync(bare, "Replace", dryRun: false, mapUsersBy: by);
            Assert.Null(Store.Get(row.Id));
            Assert.Null(Store.Get(orphan.Id));
            Assert.Empty(Store.GetAttachedForItem(movie.Id));
            Assert.Empty(Store.GetAttachedForUser(namesake.Id));
            Assert.Equal((1, 1), (Outcome(report, "SkippedDeleted"), Outcome(report, "SkippedUnknownUser")));
        }
    }

    [Fact]
    public async Task Merge_KeepsTheNewerAndTheLocalOnly_Replace_TakesTheExportsAsTheyAre()
    {
        var a = _s.AddMovie(_s.Movies, "A", "1");
        var b = _s.AddMovie(_s.Movies, "B", "2");
        var c = _s.AddMovie(_s.Movies, "C", "3");
        var rowA = await Store.UpsertAsync(Rating(_s.Viewer.Id, a.Id, "A, as exported.", StoreFixture.T0), Ct);
        var rowC = await Store.UpsertAsync(Rating(_s.Viewer.Id, c.Id, "C, only in the export.", StoreFixture.T0), Ct);
        var file = await ExportFileAsync();
        await Store.WriteAsync(w => { w.Upsert(rowA with { ReviewText = "A, newer here.", UpdatedUtc = StoreFixture.T0.AddDays(1) }); return 0; }, Ct);
        await Store.WriteAsync(w => w.Delete(rowC.Id, RatingValues.DeletedBy.User, StoreFixture.T0.AddDays(1)), Ct);
        await Store.WriteAsync(w => { w.Upsert(Rating(_s.Viewer.Id, b.Id, "B, only here.", StoreFixture.T0.AddDays(1))); return 0; }, Ct);

        // The deleted C stays deleted (its tombstone); a C rated again under a new id would be its own row.
        var merge = await ImportAsync(file, "Merge", dryRun: false);
        Assert.Equal("A, newer here.", Store.Get(rowA.Id)!.ReviewText);
        Assert.Equal(1, Outcome(merge, "KeptNewer"));
        Assert.Single(Store.GetAttachedForItem(b.Id));

        var replace = await ImportAsync(file, "Replace", dryRun: false);
        Assert.Equal("A, as exported.", Store.Get(rowA.Id)!.ReviewText);
        Assert.Empty(Store.GetAttachedForItem(b.Id));
        Assert.Equal(1, Outcome(replace, "Added"));
    }

    [Fact]
    public async Task TheRealImport_NeedsTheDryRunsPlan_AndWritesNothingWhenItChanged()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Exported.", StoreFixture.T0.AddDays(1)), Ct);
        var file = await ExportFileAsync();
        await Store.WriteAsync(w => { w.Upsert(row with { ReviewText = "Older here.", UpdatedUtc = StoreFixture.T0 }); return 0; }, Ct);

        var dry = await ImportAsync(file, "Merge", dryRun: true);
        var plan = (string)dry["PlanToken"]!;
        Assert.Matches("^[0-9a-f]{32}$", plan);
        Assert.Equal(1, Outcome(dry, "Updated"));
        Assert.Equal("Older here.", Store.Get(row.Id)!.ReviewText);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => _data.StartImportAsync(null, Stream.Null, 0, "Merge", false, "Id", null, Ct))).Kind);

        // The member edits after the dry run: the row's decision changes (Updated, now KeptNewer), so the real run writes nothing.
        await Store.WriteAsync(w => { w.Upsert(row with { ReviewText = "Changed since.", UpdatedUtc = StoreFixture.T0.AddDays(3) }); return 0; }, Ct);
        var refused = await Assert.ThrowsAsync<PluginException>(() => ImportAsync(file, "Merge", dryRun: false, ifPlan: plan));
        Assert.Equal((PluginErrorKind.Conflict, RatingsEnvelope.ChangedCode), (refused.Kind, refused.Code));
        Assert.Equal("Changed since.", Store.Get(row.Id)!.ReviewText);

        // The plan is compared before the import snapshot, so a refused run leaves none, and
        // no copy of the archive either (before, the assertion here could never fail: a snapshot's name is never empty).
        Assert.DoesNotContain(Store.ListSnapshots(), s => s.Operation == SnapshotOperations.Import);
        Assert.DoesNotContain(SnapshotRotation.ListPreOp(_detached.SnapshotDirectory, _detached.ArchiveOf(_consumer).SnapshotName, ".jsonl"), c => c.Operation == SnapshotOperations.Import);

        var again = await ImportAsync(file, "Merge", dryRun: true);
        Assert.NotEqual(plan, again["PlanToken"]);
        var applied = await ImportAsync(file, "Merge", dryRun: false, ifPlan: (string)again["PlanToken"]!);
        Assert.Equal("Changed since.", Store.Get(row.Id)!.ReviewText);
        Assert.Contains(Store.ListSnapshots(), s => s.Name == (string)applied["SafetySnapshot"]!);
    }

    [Fact]
    public async Task AnExportOfANewerSchema_IsRefusedWith409_AndAnotherPluginsWith400()
    {
        _s.AddMovie(_s.Movies, "The Matrix", "603");
        var file = await ExportFileAsync();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!.AsObject();
        json["SchemaVersion"] = RatingsEnvelope.DataSchemaVersion + 1;
        var newer = Path.Combine(_s.Store.Directory.Path, "newer.json");
        await File.WriteAllTextAsync(newer, json.ToJsonString(), Ct);
        Assert.Equal(PluginErrorKind.Conflict, (await Assert.ThrowsAsync<PluginException>(() => ImportAsync(newer, "Merge", dryRun: true))).Kind);

        json["SchemaVersion"] = RatingsEnvelope.DataSchemaVersion;
        json["PluginId"] = Guid.NewGuid().ToString();
        var foreign = Path.Combine(_s.Store.Directory.Path, "foreign.json");
        await File.WriteAllTextAsync(foreign, json.ToJsonString(), Ct);
        Assert.Equal(PluginErrorKind.Invalid, (await Assert.ThrowsAsync<PluginException>(() => ImportAsync(foreign, "Merge", dryRun: true))).Kind);
    }

    [Fact]
    public async Task ARestoreOfAPreOperationSnapshot_PutsTheArchivesCopyBack_AndKeepsTodaysArchiveAsTheRestoresCopy()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var row = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Before."), Ct);
        var snapshot = Path.GetFileName(await Store.PreOpSnapshotAsync(SnapshotOperations.UserDeleted, Ct))!;
        var info = Store.ListSnapshots().Single(s => s.Name == snapshot);
        var archive = _detached.ArchiveOf(_consumer);
        Directory.CreateDirectory(Path.GetDirectoryName(archive.FilePath)!);
        await File.WriteAllTextAsync(archive.FilePath, "today\n", Ct);
        var copy = SnapshotRotation.NewPreOpPath(_detached.SnapshotDirectory, archive.SnapshotName, SnapshotOperations.UserDeleted, ".jsonl", info.Utc.AddSeconds(2));
        await File.WriteAllTextAsync(copy, "then\n", Ct);
        await Store.WriteAsync(w => { w.Upsert(row with { ReviewText = "Edited since.", UpdatedUtc = StoreFixture.T0.AddMinutes(1) }); return 0; }, Ct);

        // An edit since is undone (a deletion since would stay: RatingsDataReviewTests).
        var result = await _data.RestoreAsync(_s.Admin.Id, snapshot, Ct);
        Assert.Equal("Before.", Store.Get(row.Id)!.ReviewText);
        Assert.Equal("then\n", await File.ReadAllTextAsync(archive.FilePath, Ct));
        Assert.Equal(Path.GetFileName(copy), result["ArchiveRestoredFrom"]);
        Assert.Contains(SnapshotRotation.ListPreOp(_detached.SnapshotDirectory, archive.SnapshotName, ".jsonl"), c => c.Operation == SnapshotOperations.Restore);
        Assert.Contains(Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.RestoreSnapshot);
    }

    [Fact]
    public async Task TheDataRoutes_AreForAdminsWithEveryLibrary_TheDetachedOnesAnsweringAsIfNothingWereThere()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var detached = await Store.UpsertAsync(Rating(_s.Viewer.Id, movie.Id, "Detached.") with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }, Ct);
        var restricted = ServicesFixture.NewUser("restricted");
        restricted.SetPermission(PermissionKind.IsAdministrator, true);
        restricted.SetPermission(PermissionKind.EnableAllFolders, false);
        _s.Users.Add(restricted);

        Assert.Equal(0, _data.Detached(restricted.Id, null, 0, null).TotalRecordCount);
        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<PluginException>(() => _data.PurgeAsync(restricted.Id, detached.Id, Ct))).Kind);
        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<PluginException>(() => _data.RelinkAsync(restricted.Id, detached.Id, movie.Id, Ct))).Kind);
        var refused = Assert.Throws<PluginException>(() => _data.Export(restricted.Id));
        Assert.Equal(RatingsData.AllLibrariesCode, refused.Code);
        Assert.Equal(PluginErrorKind.Forbidden, refused.Kind);

        // 1.0.1 (found on a server in use): the refusal says how to get every library, not only why.
        Assert.Equal(
            "An export covers every library's ratings, so it is for administrators with access to every library. To use it: Dashboard → Users → your account → Access, tick \"Enable access to all libraries\", then Save, then reload this tab. If an Access Roles role manages this account's libraries, the role must give all libraries.",
            refused.Message);
        var restoreRefused = await Assert.ThrowsAsync<PluginException>(() => _data.RestoreAsync(restricted.Id, "x.db", Ct));
        Assert.Equal(RatingsData.AllLibrariesCode, restoreRefused.Code);
        Assert.StartsWith("A restore covers every library's ratings", restoreRefused.Message, StringComparison.Ordinal);
        Assert.EndsWith(RatingsData.AllLibrariesFix, restoreRefused.Message, StringComparison.Ordinal);
        Assert.Equal(RatingsData.AllLibrariesCode, Assert.Throws<PluginException>(() => _data.StartExport(restricted.Id)).Code);
        Assert.Equal(RatingsData.AllLibrariesCode, (await Assert.ThrowsAsync<PluginException>(() => _data.StartImportAsync(restricted.Id, Stream.Null, 0, "Merge", true, "Id", null, Ct))).Code);

        // An export another admin started: its report, its file (every library's reviews), a cancel and the snapshots are
        // theirs too; the Data tab takes the snapshots' 403 as the sign to hide export, import and restore.
        var export = _data.StartExport(_s.Admin.Id);
        await Finished();
        var exportId = Guid.Parse(export.Id);
        Assert.Equal(RatingsData.AllLibrariesCode, Assert.Throws<PluginException>(() => _data.Jobs(restricted.Id)).Code);
        Assert.Equal(RatingsData.AllLibrariesCode, Assert.Throws<PluginException>(() => _data.Job(restricted.Id, exportId)).Code);
        Assert.Equal(RatingsData.AllLibrariesCode, Assert.Throws<PluginException>(() => _data.ExportFile(restricted.Id, exportId)).Code);
        Assert.Equal(RatingsData.AllLibrariesCode, Assert.Throws<PluginException>(() => _data.CancelJob(restricted.Id, exportId)).Code);
        Assert.Equal(RatingsData.AllLibrariesCode, Assert.Throws<PluginException>(() => _data.Snapshots(restricted.Id)).Code);
        Assert.Contains(_data.Jobs(_s.Admin.Id), j => j.Id == export.Id);
        Assert.Equal(AdminJob.Succeeded, _data.Job(null, exportId).State);
        Assert.True(File.Exists(_data.ExportFile(null, exportId).Path));
        Assert.True(_data.Snapshots(_s.Admin.Id).ContainsKey("Integrity"));
        Assert.Equal(1, _data.Detached(null, null, 0, null).TotalRecordCount);
        Assert.Equal(1, _data.Detached(_s.Admin.Id, RatingValues.DetachReason.Missing, 0, null).TotalRecordCount);
        Assert.Equal(0, _data.Detached(_s.Admin.Id, RatingValues.DetachReason.Unmatched, 0, null).TotalRecordCount);
    }

    [Fact]
    public async Task Relink_AttachesTheRating_UnlessTheMemberRatesTheItem_AndPurge_LeavesATombstoneAndASnapshot()
    {
        var movie = _s.AddMovie(_s.Movies, "The Matrix", "603");
        var other = _s.AddMovie(_s.Movies, "Heat", "949");
        var first = await Store.UpsertAsync(Rating(_s.Viewer.Id, Guid.NewGuid(), "Gone item.") with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }, Ct);
        var second = await Store.UpsertAsync(Rating(_s.Viewer.Id, Guid.NewGuid(), "Gone too.") with { DetachedUtc = StoreFixture.T0, DetachReason = RatingValues.DetachReason.Missing }, Ct);
        await Store.UpsertAsync(Rating(_s.Viewer.Id, other.Id, "Already here."), Ct);

        Assert.Contains(_data.Detached(null, null, 0, null).Items.Single(d => d.Id == first.Id).Candidates, c => c.ItemId == movie.Id);
        await _data.RelinkAsync(null, first.Id, movie.Id, Ct);
        var relinked = Store.Get(first.Id)!;
        Assert.Equal((movie.Id, (DateTime?)null), (relinked.ItemId, relinked.DetachedUtc));
        Assert.Contains(Store.GetModerationLog(0, 10), l => l.Action == RatingValues.ModerationAction.Relink && l.RatingId == first.Id);

        var conflict = await Assert.ThrowsAsync<PluginException>(() => _data.RelinkAsync(null, second.Id, other.Id, Ct));
        Assert.Equal((PluginErrorKind.Conflict, "AlreadyRated"), (conflict.Kind, conflict.Code));
        Assert.NotNull(Store.Get(second.Id)!.DetachedUtc);

        var snapshot = await _data.PurgeAsync(null, second.Id, Ct);
        Assert.Null(Store.Get(second.Id));
        Assert.Equal(RatingValues.DeletedBy.Purge, Store.GetTombstone(second.Id)!.DeletedBy);
        Assert.StartsWith("userratings-purge-", snapshot, StringComparison.Ordinal);
        Assert.Equal(PluginErrorKind.NotFound, (await Assert.ThrowsAsync<PluginException>(() => _data.PurgeAsync(null, relinked.Id, Ct))).Kind);
    }

    private static RatingRecord Rating(Guid user, Guid item, string text, DateTime? at = null)
        => StoreFixture.Movie(user, item, 8, text, at: at);

    private static int Outcome(IDictionary<string, object?> report, string name)
        => report["Outcomes"] is IReadOnlyDictionary<string, int> outcomes ? outcomes.GetValueOrDefault(name) : 0;

    private async Task Finished()
    {
        try
        {
            await (_jobs.Current ?? Task.CompletedTask);
        }
        catch (OperationCanceledException)
        {
            // A cancelled job's task; the runner recorded it.
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }

    private string WithoutTombstones(string file)
    {
        var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        json["Data"]!["Tombstones"] = new JsonArray();
        var bare = Path.Combine(_s.Store.Directory.Path, "bare-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(bare, json.ToJsonString());
        return bare;
    }

    // The export job's own file, as the Data tab downloads it, copied out so a later job can't drop it.
    private async Task<string> ExportFileAsync()
    {
        var job = _data.StartExport(_s.Admin.Id);
        await Finished();
        Assert.Equal(AdminJob.Succeeded, _jobs.Get(Guid.Parse(job.Id)).State);
        var file = Path.Combine(_s.Store.Directory.Path, "export-" + Guid.NewGuid().ToString("N") + ".json");
        File.Copy(_data.ExportFile(_s.Admin.Id, Guid.Parse(job.Id)).Path, file);
        return file;
    }

    private Task<IDictionary<string, object?>> ImportAsync(string file, string mode, bool dryRun, string mapUsersBy = "Id", string? ifPlan = null)
        => ImportCoreAsync(file, mode, dryRun, mapUsersBy, ifPlan);

    private async Task<IDictionary<string, object?>> ImportCoreAsync(string file, string mode, bool dryRun, string mapUsersBy, string? ifPlan)
    {
        var request = ImportRequest.Parse(mode, dryRun, mapUsersBy);
        if (!dryRun && ifPlan is null)
        {
            var dry = await ImportCoreAsync(file, mode, true, mapUsersBy, null);
            ifPlan = (string)dry["PlanToken"]!;
        }

        using var cancel = new CancellationTokenSource();
        var job = new AdminJob(Guid.NewGuid(), "Import", dryRun, StoreFixture.T0, cancel);
        return await _envelope.ImportAsync(file, request, ifPlan, _s.Admin.Id, job, Ct);
    }
}
