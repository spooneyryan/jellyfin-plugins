using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// The Data tab's server side: the export and the envelope import as jobs, the snapshots and
/// Restore (the kit's logical restore, or the kit's corruption restore when the database is damaged, with the archive's matching
/// copy put back), and the detached list with Relink and Purge.
/// </summary>
/// <remarks>
/// Every route here covers every library's ratings, so a signed-in admin needs access to every library (an API key has it):
/// the export, the import, the jobs (a report, an export's file, a cancel), the snapshots and Restore answer 403 otherwise; the
/// detached list is empty and Relink and Purge answer 404, as
/// moderation treats a detached row. A Purge leaves what a deletion leaves: a <c>Purge</c> tombstone,
/// the row's pages zeroed (<c>secure_delete</c>), the checkpoint asked for, the archive's lines of the rating taken out.
/// </remarks>
internal sealed class RatingsData
{
    /// <summary>The 403's code for an admin without every library: the export, the import, the jobs (their reports and an
    /// export's file), the snapshots and Restore.</summary>
    public const string AllLibrariesCode = "AllLibrariesNeeded";

    /// <summary>How an administrator gets every library, the end of every 403 <see cref="AllLibrariesCode"/> (the Data tab's
    /// note says the same): an administrator whose libraries are ticked one by one has none of these tools.</summary>
    public const string AllLibrariesFix = "To use it: Dashboard → Users → your account → Access, tick \"Enable access to all libraries\", then Save, then reload this tab. If an Access Roles role manages this account's libraries, the role must give all libraries.";

    /// <summary>The 413's code for an import body over 100 MB, however it is found: the declared length, the bytes read, or
    /// Kestrel stopping the read at the route's <c>[RequestSizeLimit]</c>.</summary>
    public const string TooLargeCode = "TooLarge";

    /// <summary>The 413's words.</summary>
    public const string TooLargeMessage = "An export can be at most 100 MB.";

    /// <summary>The daily maintenance's step that deletes the data jobs' files left in <c>tmp/</c>.</summary>
    public const string TempStep = "DataTemp";

    /// <summary>
    /// The stem of an export's download name: the kit's <see cref="ExportEnvelope.ToFileResult"/> adds <c>-export-</c> and
    /// the time, so <c>GET Admin/Export</c> and the job's file are both <c>userratings-export-yyyyMMdd-HHmmss.json</c> (not
    /// <c>userratings-export-export-…</c>, nor a name from the file's time).
    /// </summary>
    public const string ExportFileStem = "userratings";

    /// <summary>The 404's words for the detached routes.</summary>
    public const string DetachedNotFoundMessage = "No detached rating has that id, or it isn't one you can change.";

    /// <summary>The detached list's page size.</summary>
    public const int DetachedPageSize = 50;

    /// <summary>The candidates listed per rating.</summary>
    public const int MaxCandidates = 5;

    private const string DetachedArchiveExtension = ".jsonl";

    private readonly IRatingsStore _store;
    private readonly RatingsEnvelope _envelope;
    private readonly AdminJobRunner _jobs;
    private readonly IUserManager _users;
    private readonly ILibraryManager _library;
    private readonly Func<IdentityResolver> _resolver;
    private readonly Func<DetachedRecords> _detached;
    private readonly Func<RatingsLibraryConsumer> _owner;
    private readonly Func<AttachedRecordVerifier>? _verifier;
    private readonly ArchivedLinePurges _archivedLines;
    private readonly Func<RatingsSettings> _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly JellyfinEnhancedImport? _jellyfinEnhanced;
    private readonly ItemAccess? _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsData"/> class.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <param name="envelope">The export and import.</param>
    /// <param name="jobs">The job runner.</param>
    /// <param name="users">The users.</param>
    /// <param name="library">The library.</param>
    /// <param name="resolver">The kit's resolver.</param>
    /// <param name="detached">The kit's detached records.</param>
    /// <param name="owner">The ratings' record owner.</param>
    /// <param name="verifier">The verify sweep, queued after a restore.</param>
    /// <param name="archivedLines">The archive's lines by rating id.</param>
    /// <param name="settings">The settings, read at each use.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="jellyfinEnhanced">The Jellyfin Enhanced importer; null where it isn't needed (tests).</param>
    /// <param name="access">Which titles each member can open, for the JE export's <c>leaveOutRestricted</c> (0.3.24); null
    /// where it isn't needed (tests).</param>
    public RatingsData(
        IRatingsStore store,
        RatingsEnvelope envelope,
        AdminJobRunner jobs,
        IUserManager users,
        ILibraryManager library,
        Func<IdentityResolver> resolver,
        Func<DetachedRecords> detached,
        Func<RatingsLibraryConsumer> owner,
        Func<AttachedRecordVerifier>? verifier,
        ArchivedLinePurges archivedLines,
        Func<RatingsSettings> settings,
        TimeProvider clock,
        ILogger logger,
        JellyfinEnhancedImport? jellyfinEnhanced = null,
        ItemAccess? access = null)
    {
        _store = store;
        _envelope = envelope;
        _jobs = jobs;
        _users = users;
        _library = library;
        _resolver = resolver;
        _detached = detached;
        _owner = owner;
        _verifier = verifier;
        _archivedLines = archivedLines;
        _settings = settings;
        _clock = clock;
        _logger = logger;
        _jellyfinEnhanced = jellyfinEnhanced;
        _access = access;
    }

    /// <summary>
    /// Starts the Jellyfin Enhanced review import as a data job: JE's <c>reviews.json</c> and
    /// settings are read first, so a missing or foreign file is this request's 404 or 400.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="body">Copies, StoreUnmatched, RecreatePurged and the UserMap; null for the defaults.</param>
    /// <param name="dryRun">Whether nothing is written (true by default).</param>
    /// <param name="ifPlan">The dry run's PlanToken; required for a real run.</param>
    /// <returns>The job.</returns>
    /// <exception cref="PluginException">400 a bad request, no ifPlan, or a file of another shape; 403 an admin without
    /// every library; 404 no JE reviews; 409 another job running.</exception>
    public UserRatingsJobDto StartJellyfinEnhancedImport(Guid? adminId, UserRatingsJeImportRequest? body, bool? dryRun, string? ifPlan)
    {
        RequireAllLibraries(adminId, "A Jellyfin Enhanced import");
        var request = JeImportRequest.Parse(dryRun, body);
        if (!request.DryRun && string.IsNullOrWhiteSpace(ifPlan))
        {
            throw PluginException.Invalid("The real import needs ifPlan: the PlanToken of its dry run.");
        }

        var importer = _jellyfinEnhanced ?? throw PluginException.Unavailable("The Jellyfin Enhanced import isn't available.");
        ThrowIfBusy();
        var (file, settings) = importer.Read();
        return _jobs.Start(JellyfinEnhancedImport.JobKind, request.DryRun, async (job, token)
            => await importer.ImportAsync(file, settings, request, ifPlan, adminId, job, token).ConfigureAwait(false));
    }

    /// <summary>
    /// Builds the export at once (<c>GET Admin/Export</c>).
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <returns>The envelope.</returns>
    public ExportEnvelope Export(Guid? adminId)
    {
        RequireAllLibraries(adminId, "An export");
        return _envelope.Export().Envelope;
    }

    /// <summary>
    /// Builds the ratings as Jellyfin Enhanced's <c>reviews.json</c> (<c>GET Admin/Export/JellyfinEnhanced</c>),
    /// in memory: nothing is written on the server, here or in JE's folder. Every rating in
    /// one read; an attached one's title as it is now (a series that got its TMDb id since the rating counts), a detached
    /// one's as the row has it, and an archived one's as it was archived (since 0.3.24: the archive is
    /// read as the envelope's export reads it, so it fails as a 503 while the archive can't be read).
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="leaveOutRestricted">Whether to leave out the ratings of titles that not every member can open (true by
    /// default; since 0.3.24): JE 12.14 serves every review by TMDb id to any signed-in user, with no library
    /// check, so a review of a restricted library's title would reach every member.</param>
    /// <returns>The file and its counts.</returns>
    /// <exception cref="PluginException">403 an admin without every library; 503 the ratings or the archive can't be read
    /// now.</exception>
    public JellyfinEnhancedExport ExportJellyfinEnhanced(Guid? adminId, bool leaveOutRestricted = true)
    {
        RequireAllLibraries(adminId, "An export");
        var users = KitUsers.GetAll(_users);
        var ids = users.Select(u => u.Id).ToHashSet();
        var rows = _store.GetForModeration(null, null, null, null, null, 0, null).Rows;
        var archived = ArchivedRatings();
        var restricted = leaveOutRestricted ? Restricted(users, rows) : null;
        return JellyfinEnhancedExportWriter.Write(rows, archived, IdentityNow, ids.Contains, restricted);
    }

    /// <summary>
    /// Starts the export as a job (<c>POST Admin/Export</c>): its file waits in <c>tmp/</c> for <c>GET Admin/Jobs/{id}/File</c>.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <returns>The job.</returns>
    public UserRatingsJobDto StartExport(Guid? adminId)
    {
        RequireAllLibraries(adminId, "An export");
        return _jobs.Start("Export", dryRun: true, async (job, cancellationToken) =>
        {
            job.Progress("Read", 0, 0);
            var (envelope, counts) = _envelope.Export();
            cancellationToken.ThrowIfCancellationRequested();
            var file = Path.Combine(TempDirectory(), "userratings-export-" + job.Id.ToString("N", CultureInfo.InvariantCulture) + ".json");
            job.File = file;
            var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None);
            await using (stream.ConfigureAwait(false))
            {
                // The kit's shape, as GET Export writes it (ToUtf8Bytes): the users' ids in the "N" format, Deleted only when true.
                await JsonSerializer.SerializeAsync(stream, envelope.ToJson(), JsonStoreFormat.Options, cancellationToken).ConfigureAwait(false);
            }

            counts["FileName"] = DownloadName(envelope.ExportedUtc);
            return counts;
        });
    }

    /// <summary>
    /// Gets a finished export job's file.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="jobId">The job.</param>
    /// <returns>The file and the name to save it under.</returns>
    public (string Path, string Name) ExportFile(Guid? adminId, Guid jobId)
    {
        RequireAllLibraries(adminId, "An export");
        var path = _jobs.FileOf(jobId);

        // The name the job reported, from the export's ExportedUtc (as GET Export's), not the file's time.
        var name = _jobs.Get(jobId).Report is IDictionary<string, object?> report && report.TryGetValue("FileName", out var reported) && reported is string text
            ? text
            : DownloadName(File.GetLastWriteTimeUtc(path));
        return (path, name);
    }

    /// <summary>
    /// Stores an import's body in <c>tmp/</c> and starts the import job (<c>POST Admin/Import</c>).
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="body">The request body.</param>
    /// <param name="length">The declared length, if any.</param>
    /// <param name="mode">Merge or Replace.</param>
    /// <param name="dryRun">Whether it writes nothing; true by default.</param>
    /// <param name="mapUsersBy">Id or Name.</param>
    /// <param name="ifPlan">The dry run's PlanToken, for a real run.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The job.</returns>
    public async Task<UserRatingsJobDto> StartImportAsync(Guid? adminId, Stream body, long? length, string? mode, bool? dryRun, string? mapUsersBy, string? ifPlan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        RequireAllLibraries(adminId, "An import");
        var request = ImportRequest.Parse(mode, dryRun, mapUsersBy);
        if (!request.DryRun && string.IsNullOrWhiteSpace(ifPlan))
        {
            throw PluginException.Invalid("The real import needs ifPlan: the PlanToken of its dry run.");
        }

        ThrowIfBusy();
        if (length > ExportEnvelope.MaxImportBytes)
        {
            throw TooLarge();
        }

        var file = Path.Combine(TempDirectory(), "userratings-import-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".json");
        var hold = _jobs.HoldUpload(file);
        try
        {
            var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await ReadBodyAsync(body, buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > ExportEnvelope.MaxImportBytes)
                    {
                        throw TooLarge();
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            return _jobs.Start("Import", request.DryRun, async (job, token) =>
            {
                try
                {
                    return await _envelope.ImportAsync(file, request, ifPlan, adminId, job, token).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(file);
                    hold.Dispose();
                }
            });
        }
        catch
        {
            TryDelete(file);
            hold.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deletes the data jobs' files in <c>tmp/</c> that nothing holds: an export's file past
    /// its hour, and what a kill left, an export's file or an upload cut short. Run at the start and as the daily
    /// maintenance's <see cref="TempStep"/>.
    /// </summary>
    /// <returns>The step's report: the files deleted.</returns>
    public IDictionary<string, object?> SweepTemp()
    {
        var deleted = _jobs.SweepTemp(Path.Combine(_store.DataDirectory, "tmp"));
        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} data job file(s) from tmp/", deleted);
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal) { ["Deleted"] = deleted };
    }

    /// <summary>Lists the jobs.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <returns>The running one and the last finished ones.</returns>
    public IReadOnlyList<UserRatingsJobDto> Jobs(Guid? adminId)
    {
        RequireAllLibraries(adminId, "A data job");
        return _jobs.List();
    }

    /// <summary>Gets a job.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="jobId">Its id.</param>
    /// <returns>The job.</returns>
    public UserRatingsJobDto Job(Guid? adminId, Guid jobId)
    {
        RequireAllLibraries(adminId, "A data job");
        return _jobs.Get(jobId);
    }

    /// <summary>Cancels a job before its write phase, or deletes a finished export's file.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="jobId">Its id.</param>
    /// <returns>False when the export's file is withdrawn but still open, so its delete waits (<see cref="AdminJobRunner.Cancel"/>).</returns>
    public bool CancelJob(Guid? adminId, Guid jobId)
    {
        RequireAllLibraries(adminId, "A data job");
        return _jobs.Cancel(jobId);
    }

    /// <summary>
    /// Lists the snapshots and the database's integrity, for the Data tab (<c>GET Admin/Snapshots</c>). The Data tab takes its
    /// 403 for an admin without every library as the sign to hide export, import and restore.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <returns>The snapshots, newest first, and the integrity.</returns>
    public IDictionary<string, object?> Snapshots(Guid? adminId)
    {
        RequireAllLibraries(adminId, "A snapshot");
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Snapshots"] = _store.ListSnapshots().Select(s => s.ToDictionary()).ToList(),
            ["Integrity"] = _store.DescribeIntegrity()
        };
    }

    /// <summary>
    /// Restores a snapshot (<c>POST Admin/Snapshots/Restore</c>), then puts back the archive's copy of a pre-operation
    /// snapshot's operation, queues the verify sweep and asks for the checkpoint.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="name">The snapshot's file name.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>What was restored.</returns>
    public async Task<IDictionary<string, object?>> RestoreAsync(Guid? adminId, string? name, CancellationToken cancellationToken)
    {
        RequireAllLibraries(adminId, "A restore");
        if (string.IsNullOrWhiteSpace(name))
        {
            throw PluginException.Invalid("Name the snapshot to restore.");
        }

        // The whole restore holds the runner: no export or import starts while it runs, and it
        // waits for none. The name is looked up as the kit finds it, trimmed and ignoring case.
        using var hold = _jobs.Hold("restore");
        var trimmed = name.Trim();
        var info = _store.ListSnapshots().FirstOrDefault(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        var result = await _store.RestoreSnapshotAsync(trimmed, cancellationToken).ConfigureAwait(false);
        string? archiveFrom = null;
        string? archiveError = null;
        bool? archiveRestored = null;
        if (info is { Kind: SnapshotKind.PreOp, Operation: { } operation })
        {
            try
            {
                archiveFrom = RestoreArchiveCopy(operation, info.Utc);
                archiveRestored = archiveFrom is not null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The archive is as it was (RestoreFrom writes beside it); the answer says so.
                _logger.LogError(ex, "Restore: {Snapshot} is back, but the archive's copy of its operation couldn't be put back", info.Name);
                archiveError = ex.Message;
                archiveRestored = false;
            }
        }

        // Everything since the snapshot is undone, except this server's own deletions (the rule
        // since 0.3.19): what its own safety snapshot, the ratings just before it, had deleted here stays deleted;
        // what an import did since, its deletions too, is undone.
        var kept = await KeepDeletionsAsync(result.SafetySnapshot, info?.Name ?? trimmed, info?.Utc ?? DateTime.MinValue).ConfigureAwait(false);

        // The restored pages replaced the live ones; the checkpoint takes the earlier ones out of the -wal too.
        RatingsCheckpoint.RequestAfter(_store, _logger, "the restore of " + (info?.Name ?? trimmed));
        _verifier?.Invoke().Queue(VerifySweepReason.DataRestored);
        try
        {
            await _store.WriteAsync(
                w => w.AppendModerationLog(new ModerationLogRecord
                {
                    AtUtc = _clock.GetUtcNow().UtcDateTime,
                    ActorUserId = adminId,
                    Action = RatingValues.ModerationAction.RestoreSnapshot,
                    Details = JsonSerializer.Serialize(new Dictionary<string, object?>
                    {
                        ["Snapshot"] = info?.Name ?? trimmed,
                        ["Archive"] = archiveFrom,
                        ["DeletionsKept"] = kept.Deletions,
                        ["ReviewRemovalsKept"] = kept.Reviews,
                        ["FormerMembersKept"] = kept.FormerMembers,
                        ["KeptOnly"] = RatingValues.DeletionOrigin.Local,
                        ["ImportDeletionsUndone"] = kept.ImportDeletionsUndone,
                        ["ImportFormerMembersUndone"] = kept.ImportFormerMembersUndone,
                        ["LedgerLinksKept"] = kept.LedgerLinks,
                        ["DeletionsError"] = kept.Error
                    })
                }),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PluginException ex)
        {
            _logger.LogWarning(ex, "Restore: {Snapshot} is back, but its moderation-log row couldn't be written", info?.Name ?? trimmed);
        }

        _logger.LogInformation(
            "Restore: {Snapshot} restored (safety snapshot {Safety}, archive copy {Archive}, this server's deletions kept {Deletions}, review removals kept {Reviews}, an import's deletions undone {ImportDeletions})",
            info?.Name ?? trimmed,
            result.SafetySnapshot ?? "none",
            archiveFrom ?? "none",
            kept.Deletions,
            kept.Reviews,
            kept.ImportDeletionsUndone);
        var answer = result.ToDictionary();
        answer["ArchiveRestoredFrom"] = archiveFrom;
        answer["ArchiveRestored"] = archiveRestored;
        answer["ArchiveError"] = archiveError;
        answer["DeletionsKept"] = kept.Deletions;
        answer["ReviewRemovalsKept"] = kept.Reviews;
        answer["FormerMembersKept"] = kept.FormerMembers;
        answer["ImportDeletionsUndone"] = kept.ImportDeletionsUndone;
        answer["ImportFormerMembersUndone"] = kept.ImportFormerMembersUndone;
        answer["DeletionsError"] = kept.Error;
        return answer;
    }

    /// <summary>
    /// Starts the ratings empty (<c>POST Admin/Data/StartEmpty</c>), as only an admin can choose,
    /// while the database is damaged: when no snapshot passes its check, or the admin would rather not go back to one. The
    /// damaged database is moved to a quarantine folder (kept), a new empty one takes its place, every snapshot is kept
    /// until one of them is restored, and the archive is left as it is. The runner is held as for a restore; a
    /// <c>StartEmpty</c> row is the new database's first moderation-log row.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The kit's recovery, with <c>StartedEmpty</c>.</returns>
    /// <exception cref="PluginException">403 an admin without every library; 409 a job is running, or the database isn't
    /// damaged (restore a snapshot instead); 503 as the kit's recovery.</exception>
    public async Task<IDictionary<string, object?>> StartEmptyAsync(Guid? adminId, CancellationToken cancellationToken)
    {
        RequireAllLibraries(adminId, "Starting empty");
        using var hold = _jobs.Hold("start empty");
        var reason = _store.StatusMessage;
        var recovery = await _store.StartEmptyAsync(cancellationToken).ConfigureAwait(false);
        var quarantine = recovery.QuarantineFolder is null ? null : Path.GetFileName(recovery.QuarantineFolder);
        _logger.LogWarning(
            "An admin ({AdminId}) started the ratings empty while the database was damaged ({Reason}); kept: {Quarantine} and every snapshot",
            adminId?.ToString("N", CultureInfo.InvariantCulture) ?? "an API key",
            reason ?? "no message",
            quarantine ?? "nothing moved aside");
        _verifier?.Invoke().Queue(VerifySweepReason.DataRestored);
        try
        {
            await _store.WriteAsync(
                w => w.AppendModerationLog(new ModerationLogRecord
                {
                    AtUtc = _clock.GetUtcNow().UtcDateTime,
                    ActorUserId = adminId,
                    Action = RatingValues.ModerationAction.StartEmpty,
                    Details = JsonSerializer.Serialize(new Dictionary<string, object?> { ["QuarantineFolder"] = quarantine })
                }),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (PluginException ex)
        {
            _logger.LogWarning(ex, "Start empty: the ratings are empty, but the moderation-log row couldn't be written");
        }

        var answer = recovery.ToDictionary();
        answer["StartedEmpty"] = true;
        answer["QuarantineFolder"] = quarantine;
        return answer;
    }

    /// <summary>
    /// Lists the detached ratings (<c>GET Admin/Detached</c>), with up to five candidates each.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="reason">Only this reason, or all.</param>
    /// <param name="startIndex">Rows to skip.</param>
    /// <param name="limit">Rows to return, at most 50.</param>
    /// <returns>The page.</returns>
    public QueryResult<UserRatingsDetachedDto> Detached(Guid? adminId, string? reason, int startIndex, int? limit)
    {
        if (reason is not null && !IsReason(reason))
        {
            throw PluginException.Invalid("reason must be Removed, Missing, Unmatched or Ambiguous.");
        }

        var start = Math.Max(0, startIndex);
        if (!HasAllLibraries(adminId))
        {
            return new QueryResult<UserRatingsDetachedDto>(start, 0, []);
        }

        var rows = _store.GetDetached().Where(r => reason is null || string.Equals(r.DetachReason, reason, StringComparison.Ordinal)).ToList();
        var page = rows.Skip(start).Take(Math.Clamp(limit ?? DetachedPageSize, 1, DetachedPageSize)).ToList();
        var former = _store.GetFormerUsers();
        return new QueryResult<UserRatingsDetachedDto>(start, rows.Count, page.Select(r => ToDto(r, former)).ToList());
    }

    /// <summary>
    /// Relinks a detached rating to an item (<c>POST Admin/Detached/{id}/Relink</c>).
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>A task.</returns>
    public async Task RelinkAsync(Guid? adminId, Guid ratingId, Guid? itemId, CancellationToken cancellationToken)
    {
        if (itemId is not { } targetId || targetId == Guid.Empty)
        {
            throw PluginException.Invalid("The body must be {\"ItemId\": \"…\"}.");
        }

        var row = DetachedRow(adminId, ratingId);
        if (_library.GetItemById(targetId) is not { } item || !ItemAccess.IsRateable(item, _settings()))
        {
            throw PluginException.Invalid("That item wasn't found, or it can't be rated.");
        }

        if (_users.GetUserById(row.UserId) is { } member && !IdentityResolver.VisibleTo(_library, member)(item))
        {
            throw PluginException.Invalid("The member can't open that item, so the rating can't be moved there.");
        }

        _store.ThrowIfReadOnly();
        var identity = ItemIdentity.Capture(item, _library);
        await _store.WriteAsync(
            w =>
            {
                if (w.Get(ratingId) is not { DetachedUtc: not null } current)
                {
                    throw PluginException.NotFound(DetachedNotFoundMessage);
                }

                if (w.GetAttached(current.UserId, item.Id) is not null)
                {
                    throw new PluginException(PluginErrorKind.Conflict, "The member already has a rating on that item. Nothing was changed.") { Code = "AlreadyRated" };
                }

                var now = _clock.GetUtcNow().UtcDateTime;
                w.Upsert(current with { Identity = identity, DetachedUtc = null, DetachReason = null, AttachedUtc = now });
                w.AppendModerationLog(new ModerationLogRecord
                {
                    AtUtc = now,
                    ActorUserId = adminId,
                    Action = RatingValues.ModerationAction.Relink,
                    RatingId = current.Id,
                    TargetUserId = current.UserId,
                    ItemId = item.Id,
                    Details = JsonSerializer.Serialize(new Dictionary<string, string> { ["FromItemId"] = current.ItemId.ToString("N", CultureInfo.InvariantCulture) })
                });
                return 0;
            },
            cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Detached: rating {RatingId} relinked from {FromItemId} to {ItemId} by {AdminId}", ratingId, row.ItemId, item.Id, adminId);
    }

    /// <summary>
    /// Purges a detached rating (<c>DELETE Admin/Detached/{id}</c>): a <c>purge</c> pre-operation snapshot, the row deleted
    /// with a <c>Purge</c> tombstone and a log row, then its archive lines and the checkpoint.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The safety snapshot's name.</returns>
    public async Task<string?> PurgeAsync(Guid? adminId, Guid ratingId, CancellationToken cancellationToken)
    {
        var row = DetachedRow(adminId, ratingId);
        _store.ThrowIfReadOnly();
        var view = _archivedLines.Look();
        var snapshot = await _store.PreOpSnapshotAsync(SnapshotOperations.Purge, cancellationToken).ConfigureAwait(false);
        var recorded = await _store.WriteAsync(
            w =>
            {
                if (w.Get(ratingId) is not { DetachedUtc: not null } current)
                {
                    throw PluginException.NotFound(DetachedNotFoundMessage);
                }

                // Never older than the row: a restore keeps the purge.
                var now = RatingsService.WriteTimeAfter(_clock.GetUtcNow().UtcDateTime, current.UpdatedUtc);
                w.Delete(current.Id, RatingValues.DeletedBy.Purge, now);
                w.AppendModerationLog(new ModerationLogRecord
                {
                    AtUtc = now,
                    ActorUserId = adminId,
                    Action = RatingValues.ModerationAction.Purge,
                    RatingId = current.Id,
                    TargetUserId = current.UserId,
                    ItemId = current.ItemId,
                    Details = string.Create(CultureInfo.InvariantCulture, $"{{\"Stars\":{(current.Stars2 is null ? 0 : 1)},\"Reviews\":{(current.ReviewText is null ? 0 : 1)}}}")
                });
                return ArchivedLinePurges.Record(w, view, [current.Id]);
            },
            cancellationToken).ConfigureAwait(false);
        await AfterDeletionAsync(recorded, "the purge of rating " + ratingId.ToString("N", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        _logger.LogInformation("Detached: rating {RatingId} of user {UserId} purged by {AdminId} (snapshot {Snapshot})", ratingId, row.UserId, adminId, snapshot is null ? "none" : Path.GetFileName(snapshot));
        return snapshot is null ? null : Path.GetFileName(snapshot);
    }

    /// <summary>
    /// After rows were deleted: the archive's lines of their ratings, then the checkpoint (a rewrite that fails stays pending).
    /// </summary>
    /// <param name="recorded">The archive purges recorded.</param>
    /// <param name="what">What deleted them, for the log.</param>
    /// <returns>A task.</returns>
    public async Task AfterDeletionAsync(int recorded, string what)
    {
        if (recorded > 0)
        {
            try
            {
                await _archivedLines.RunAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "The archive lines after {What} stay pending; the daily maintenance takes them out", what);
            }
        }

        RatingsCheckpoint.RequestAfter(_store, _logger, what);
    }

    /// <summary>
    /// Tells whether a caller has access to every library: an API key (null), or an admin with <c>EnableAllFolders</c>. The
    /// Data tab's routes and the unknown users' tools are theirs.
    /// </summary>
    /// <param name="adminId">The admin's user id, or null for an API key.</param>
    /// <returns>Whether.</returns>
    public bool HasAllLibraries(Guid? adminId)
        => adminId is not { } id || (_users.GetUserById(id) is { } admin && admin.HasPermission(PermissionKind.EnableAllFolders));

    private static bool IsReason(string reason)
        => reason is RatingValues.DetachReason.Removed or RatingValues.DetachReason.Missing or RatingValues.DetachReason.Unmatched or RatingValues.DetachReason.Ambiguous;

    private static string DownloadName(DateTime utc) => ExportFileStem + "-export-" + utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";

    private static PluginException TooLarge() => new(PluginErrorKind.PayloadTooLarge, TooLargeMessage) { Code = TooLargeCode };

    // Kestrel stops a body over the route's [RequestSizeLimit] with a 413 BadHttpRequestException while it is read: the same
    // 413 and code as a declared length over it, so the Data tab says why, not a network error.
    private static async ValueTask<int> ReadBodyAsync(Stream body, byte[] buffer, CancellationToken cancellationToken)
    {
        try
        {
            return await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw TooLarge();
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start's tmp/ to hold; nothing reads it.
        }
    }

    private string TempDirectory()
    {
        var directory = Path.Combine(_store.DataDirectory, "tmp");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private void ThrowIfBusy() => _jobs.ThrowIfBusy();

    // A rating's title as it is now when it is in the library (the JE export: provider ids change after a rating), else the
    // identity the row keeps.
    // The archive's ratings as the envelope's export takes them (RatingsEnvelope.Export): ExportArchive's records (none the
    // database holds, none whose purge is pending; a 503 while the archive can't be read), each with the identity it was
    // archived with.
    private List<RatingRecord> ArchivedRatings()
    {
        var owner = _owner();
        var detached = _detached();
        var exported = detached.ExportArchive(owner)
            .Select(e => e["Id"]?.GetValue<string>())
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return detached.ArchiveOf(owner).Read().Records
            .Where(r => exported.Contains(r.Id))
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => ArchivedRating.FromArchive(g.Last()))
            .OfType<RatingRecord>()
            .ToList();
    }

    // Which ratings are of a title some member can't open (the JE export's leaveOutRestricted; since
    // 0.3.24). Every user counts, disabled ones too (one can be enabled again while the file is in JE's folder). An attached
    // rating's item is checked for each user as the details page checks it (the kit's batch, then one by one: libraries,
    // parental limits, parents). A detached or archived one has no item to check: its stored path is, against the libraries
    // (inside one some user can't open, or inside none any more: left out); one with no path is a review JE's import kept
    // detached, of a title this server never had, which every member could read in JE already.
    private Func<RatingRecord, bool> Restricted(IReadOnlyList<User> users, IReadOnlyList<RatingRecord> rows)
    {
        var access = _access ?? throw PluginException.Unavailable("Which titles every member can open can't be told here.");
        var itemIds = rows.Where(r => r.DetachedUtc is null).Select(r => r.ItemId).Distinct().ToList();
        var everyone = new HashSet<Guid>(itemIds);
        var libraries = access.AllLibraries();
        var everyonesLibraries = new HashSet<Guid>(libraries.Select(l => l.Id));
        foreach (var user in users)
        {
            everyone.IntersectWith(access.VisibleIds(user, itemIds));
            everyonesLibraries.IntersectWith(access.VisibleLibraries(user).Select(l => l.Id));
        }

        return row =>
        {
            if (row.DetachedUtc is null)
            {
                return !everyone.Contains(row.ItemId);
            }

            if (string.IsNullOrWhiteSpace(row.Identity.Path))
            {
                return false;
            }

            var holders = libraries.Where(l => (l.PhysicalLocations ?? []).Any(location => IsUnder(row.Identity.Path, location))).ToList();
            return holders.Count == 0 || holders.Any(l => !everyonesLibraries.Contains(l.Id));
        };
    }

    // Whether a path is a folder's or under it, either separator, any case (Windows' paths).
    private static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        static string Normal(string value) => value.Replace('\\', '/').TrimEnd('/');
        var p = Normal(path);
        var f = Normal(folder);
        return f.Length > 0 && (string.Equals(p, f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));
    }

    private IdentitySnapshot IdentityNow(RatingRecord row)
        => row.DetachedUtc is null && _library.GetItemById(row.Identity.ItemId) is { } item ? ItemIdentity.Capture(item, _library) : row.Identity;

    private void RequireAllLibraries(Guid? adminId, string what)
    {
        if (!HasAllLibraries(adminId))
        {
            throw new PluginException(PluginErrorKind.Forbidden, AllLibrariesMessage(what)) { Code = AllLibrariesCode };
        }
    }

    /// <summary>The words of a 403 <see cref="AllLibrariesCode"/>: what is refused, why, and <see cref="AllLibrariesFix"/>.</summary>
    /// <param name="what">What is refused, as the sentence's subject ("An export").</param>
    /// <returns>The sentence.</returns>
    internal static string AllLibrariesMessage(string what)
        => what + " covers every library's ratings, so it is for administrators with access to every library. " + AllLibrariesFix;

    private RatingRecord DetachedRow(Guid? adminId, Guid ratingId)
        => HasAllLibraries(adminId) && _store.Get(ratingId) is { DetachedUtc: not null } row ? row : throw PluginException.NotFound(DetachedNotFoundMessage);

    private UserRatingsDetachedDto ToDto(RatingRecord row, IReadOnlyDictionary<Guid, string> former)
    {
        var member = _users.GetUserById(row.UserId);
        var name = member?.Username ?? (former.TryGetValue(row.UserId, out var formerName) ? formerName : "Deleted user");
        var candidates = new List<UserRatingsDetachedCandidateDto>();
        try
        {
            var result = _resolver().Resolve(row.Identity, RelinkPolicy.Default, isVisible: member is null ? null : IdentityResolver.VisibleTo(_library, member));
            var items = result.Candidates.Select(c => (c.Item, Strength: c.Strength.ToString())).ToList();
            if (result.Item is { } hit && items.All(c => c.Item.Id != hit.Id))
            {
                items.Insert(0, (hit, result.Strength.ToString()));
            }

            foreach (var (item, strength) in items.Take(MaxCandidates))
            {
                var identity = ItemIdentity.Capture(item, _library);
                candidates.Add(new UserRatingsDetachedCandidateDto
                {
                    ItemId = item.Id,
                    Name = item.Name ?? string.Empty,
                    Kind = identity.Kind,
                    Year = identity.Year,
                    SeriesName = identity.SeriesName,
                    Strength = strength,
                    MemberRatesIt = _store.GetAttached(row.UserId, item.Id) is not null
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Detached: the candidates of rating {RatingId} couldn't be found", row.Id);
        }

        return new UserRatingsDetachedDto
        {
            Id = row.Id,
            UserId = row.UserId,
            UserName = name,
            ItemId = row.ItemId,
            Kind = row.Identity.Kind,
            Name = row.Identity.Name,
            Year = row.Identity.Year,
            SeriesName = row.Identity.SeriesName,
            Season = row.Identity.Season,
            Episode = row.Identity.Episode,
            Stars = row.Stars2 is { } s ? s / 2.0 : null,
            HasReview = row.ReviewText is not null,
            DetachedUtc = row.DetachedUtc!.Value,
            DetachReason = row.DetachReason ?? RatingValues.DetachReason.Missing,
            Candidates = candidates
        };
    }

    private string? RestoreArchiveCopy(string operation, DateTime snapshotUtc)
    {
        var detached = _detached();
        var archive = detached.ArchiveOf(_owner());
        var copy = SnapshotRotation.ListPreOp(detached.SnapshotDirectory, archive.SnapshotName, DetachedArchiveExtension)
            .Where(c => string.Equals(c.Operation, operation, StringComparison.OrdinalIgnoreCase) && c.Utc >= snapshotUtc.AddSeconds(-1) && c.Utc <= snapshotUtc.AddMinutes(10))
            .OrderBy(c => c.Utc)
            .FirstOrDefault();
        if (copy is null)
        {
            return null;
        }

        // Under the archive's lock; the archive as it is now becomes the restore's own copy
        // (kept 7 days, as every pre-operation copy).
        archive.RestoreFrom(copy.Path, detached.SnapshotDirectory);
        return copy.Name;
    }

    // What a restore keeps of the state it replaced (the rule since
    // 0.3.19). The snapshot comes back whole; then only this server's own deletions made after it are applied again,
    // from the restore's own safety snapshot (the ratings just before it): a rating its member, a moderator, the deleted-user
    // policy or a Purge deleted is deleted again with its tombstone (never over a row the snapshot holds with a newer time),
    // and the ledger's links to it come back with it; a review a moderator removed is removed again, from the
    // RemoveText and RemoveEntry rows written after the snapshot, whatever the member wrote since, and one its member
    // removed too, unless an import set the rating since; a member deleted here and made a former member stays one. An
    // import's tombstones, deletions and former members are part of that import (their Origin): restoring its snapshot, or an
    // earlier one, undoes them with the rest of it. The archive's lines of what is deleted or removed again go too.
    // Without a safety snapshot (the damaged database's restore), or after a Start empty since the snapshot, the deletions
    // made in between can't be told: the answer says so.
    private async Task<KeptDeletions> KeepDeletionsAsync(string? safety, string restored, DateTime restoredUtc)
    {
        if (safety is null)
        {
            return KeptDeletions.Failed("The database was restored without a snapshot of it first (it was damaged), so deletions made since " + restored + " can't be told; they are back.");
        }

        try
        {
            var before = _store.ReadPreOpSnapshotTables(safety);
            var view = _archivedLines.Look();
            var kept = await _store.WriteAsync(w => KeepDeletions(w, before, restoredUtc, view), CancellationToken.None).ConfigureAwait(false);
            if (kept.Purges > 0)
            {
                try
                {
                    await _archivedLines.RunAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Restore: the archive lines of the ratings deleted since {Snapshot} stay pending; the daily maintenance takes them out", restored);
                }
            }

            if (kept.StartedEmptyUtc is { } emptied)
            {
                // The deletions made between the snapshot and the Start empty were in the database it quarantined.
                kept = kept with
                {
                    Error = string.Create(
                        CultureInfo.InvariantCulture,
                        $"The ratings were started empty at {emptied:yyyy-MM-dd HH:mm} UTC, after {restored} was taken, so deletions made between the two can't be told; they may be back. Deletions made since the start empty stay deleted.")
                };
            }

            return kept;
        }
        catch (PluginException ex)
        {
            _logger.LogError(ex, "Restore: {Snapshot} is back, but the deletions made since couldn't be applied again from {Safety}", restored, safety);
            return KeptDeletions.Failed("The deletions made since " + restored + " couldn't be applied again (" + ex.Message + "); restore " + safety + " to go back to the ratings as they were before the restore.");
        }
    }

    // Under the writer lock: the restored ratings are the live ones (w), the state the restore replaced is before.
    private static KeptDeletions KeepDeletions(IRatingsWriter w, RatingsTables before, DateTime restoredUtc, ArchivedLinePurges.ArchiveView view)
    {
        var ids = new List<Guid>();
        var beforeRatings = before.Ratings.ToDictionary(r => r.Id);

        // This server's own deletions since the snapshot; an import's are undone with the import.
        int deleted = 0, importUndone = 0;
        foreach (var t in before.Tombstones)
        {
            if (w.HasTombstone(t.RatingId))
            {
                continue;
            }

            if (!RatingValues.DeletionOrigin.IsLocal(t.Origin))
            {
                importUndone++;
                continue;
            }

            if (w.Get(t.RatingId) is { } row)
            {
                if (row.UpdatedUtc > t.DeletedUtc)
                {
                    // The snapshot holds the rating as it was after that deletion: it isn't deleted over it.
                    continue;
                }

                w.Delete(row.Id, t.DeletedBy, t.DeletedUtc);
            }
            else
            {
                // Also a rating made and deleted since the snapshot: its tombstone stays, so no file brings it back.
                w.AddTombstone(t.RatingId, t.UserId, t.ItemId, t.DeletedBy, t.DeletedUtc);
            }

            ids.Add(t.RatingId);
            deleted++;
        }

        // The ledger's links to a deleted rating: the next import of a file holding the rating it maps finds it deleted.
        var links = 0;
        foreach (var entry in before.ImportLedger)
        {
            if (!entry.RatingIds.Any(w.HasTombstone))
            {
                continue;
            }

            var now = w.GetImport(entry.Source, entry.SourceKey);
            if (now is null)
            {
                w.RecordImport(entry);
                links++;
            }
            else if (entry.RatingIds.Except(now.RatingIds).ToList() is { Count: > 0 } missing)
            {
                w.RecordImport(now with { RatingIds = [.. now.RatingIds, .. missing] });
                links++;
            }
        }

        // Former members made here since (the deleted-user policy); an import's go with it.
        var restoredFormer = w.GetFormerUsers();
        var localFormer = new HashSet<Guid>();
        int formerKept = 0, formerUndone = 0;
        foreach (var f in before.FormerUsers)
        {
            if (!RatingValues.DeletionOrigin.IsLocal(f.Origin))
            {
                formerUndone += restoredFormer.ContainsKey(f.Id) ? 0 : 1;
                continue;
            }

            localFormer.Add(f.Id);
            if (!restoredFormer.ContainsKey(f.Id))
            {
                w.AddFormerUserIfAbsent(f);
                formerKept++;
            }
        }

        // The moderation log rows written after the snapshot: those the restored log doesn't hold, by id, time, action and
        // rating (a restore rewinds the ids). After a Replace the log before it is the file's, so only the rows after the
        // last Replace are this server's own.
        var restoredLog = w.GetModerationLogKeys().Select(LogKey).ToHashSet();
        var since = before.ModerationLog.Where(l => !restoredLog.Contains(LogKey(l))).ToList();
        // A Replace of this server's own export (OwnServer, since 0.3.21) brings back this
        // server's own log, so its rows before it stay this server's.
        var lastReplace = before.ModerationLog
            .Where(l => l.Action == RatingValues.ModerationAction.ImportEnvelope && l.Details is { } d && d.Contains("\"Mode\":\"Replace\"", StringComparison.Ordinal) && !IsOwnServerImport(l))
            .Select(l => l.Id)
            .DefaultIfEmpty(0)
            .Max();
        // A Start empty made here after the snapshot: not one an imported log holds from before it (in 0.3.20,
        // a Replace's file with an old StartEmpty row made the restore say deletions may be back).
        DateTime? startedEmpty = since
            .Where(l => l.Action == RatingValues.ModerationAction.StartEmpty && l.AtUtc >= restoredUtc && l.Id > lastReplace)
            .Select(l => (DateTime?)l.AtUtc)
            .Max();

        // A review a moderator removed since is removed again, at the latest removal's time.
        var cleared = 0;
        var removedReviews = new HashSet<Guid>();
        var removals = since
            .Where(l => l.Id > lastReplace && l.RatingId is not null && (l.Action == RatingValues.ModerationAction.RemoveText || l.Action == RatingValues.ModerationAction.RemoveEntry))
            .GroupBy(l => l.RatingId!.Value);
        foreach (var removal in removals)
        {
            var at = removal.Max(l => l.AtUtc);
            removedReviews.Add(removal.Key);
            if (RemoveReviewAgain(w, removal.Key, beforeRatings.GetValueOrDefault(removal.Key), at))
            {
                ids.Add(removal.Key);
                cleared++;
            }
        }

        // A review its member removed since (no log row: the rating just before the restore has none), unless an import set
        // that rating after the snapshot (its ledger row, imported since, with the rating's time): the import is undone.
        // Not an import of this server's own export: its rows are this server's, and a member's change since stays theirs.
        var ownImports = before.ModerationLog.Where(IsOwnServerImport).Select(l => l.AtUtc).ToHashSet();
        var importedSince = before.ImportLedger
            .Where(e => e.ImportedUtc >= restoredUtc && !ownImports.Contains(e.ImportedUtc))
            .SelectMany(e => e.RatingIds.Select(id => (id, ImportTimes.Written(e))))
            .ToHashSet();
        foreach (var then in before.Ratings)
        {
            if (w.Get(then.Id) is not { } now)
            {
                continue;
            }

            // Its member was deleted since, and the rating kept as a former member's: first, whatever else applies (in
            // 0.3.20, a member who cleared a review, then was deleted, stayed under their own id).
            if (localFormer.Contains(then.UserId) && now.UserId != then.UserId)
            {
                w.Upsert(now with { UserId = then.UserId });
                now = w.Get(then.Id)!;
            }

            if (then.ReviewText is null && now.ReviewText is not null && !removedReviews.Contains(then.Id) && !importedSince.Contains((then.Id, then.UpdatedUtc)))
            {
                if (RemoveReviewAgain(w, then.Id, then, then.UpdatedUtc))
                {
                    ids.Add(then.Id);
                    cleared++;
                }
            }
        }

        var purges = ArchivedLinePurges.Record(w, view, ids);
        return new KeptDeletions(deleted, cleared, formerKept, importUndone, formerUndone, links, null, purges, startedEmpty);
    }

    // Removes the review of a restored rating again, as Remove text does: a rating that keeps its stars loses only the
    // review; one with only its review keeps the stars it had just before the restore, or is deleted when it had none
    // (Remove text deletes such a row). A review written after the removal (the snapshot's, by its time) stays.
    private static bool RemoveReviewAgain(IRatingsWriter w, Guid id, RatingRecord? before, DateTime at)
    {
        if (w.Get(id) is not { ReviewText: not null } now || (now.ReviewUpdatedUtc is { } wrote && wrote > at))
        {
            return false;
        }

        if (now.Stars2 is not null)
        {
            w.Upsert(now with { ReviewText = null, IsSpoiler = false, ReviewUpdatedUtc = null, EditCount = 0, UpdatedUtc = now.UpdatedUtc > at ? now.UpdatedUtc : at });
        }
        else if (before is { Stars2: not null })
        {
            w.Upsert(before with { ReviewText = null, IsSpoiler = false, ReviewUpdatedUtc = null, EditCount = 0 });
        }
        else
        {
            w.Delete(id, RatingValues.DeletedBy.Moderator, at);
        }

        return true;
    }

    // An envelope import's log row that says the file was this server's own export (its ledger rows have its time).
    private static bool IsOwnServerImport(ModerationLogRecord row)
        => row.Action == RatingValues.ModerationAction.ImportEnvelope && row.Details is { } d && d.Contains("\"OwnServer\":true", StringComparison.Ordinal);

    private static (long Id, DateTime AtUtc, string Action, Guid? RatingId) LogKey(ModerationLogRecord row) => (row.Id, row.AtUtc, row.Action, row.RatingId);

    // What a restore kept (null when it couldn't tell), what it undid of the imports since, and why it couldn't tell.
    private sealed record KeptDeletions(int? Deletions, int? Reviews, int? FormerMembers, int? ImportDeletionsUndone, int? ImportFormerMembersUndone, int? LedgerLinks, string? Error, int Purges = 0, DateTime? StartedEmptyUtc = null)
    {
        public static KeptDeletions Failed(string error) => new(null, null, null, null, null, null, error);
    }
}
