using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Background service that extracts queued clips with ffmpeg, a configurable number at a time.
/// Job state lives on the bookmark, so queued and interrupted jobs survive a server restart.
/// </summary>
/// <remarks>
/// <para>While the bookmark data is read-only (a file from a newer Bookmarks, or an upgrade that failed)
/// the worker stays idle for the whole process, behind <see cref="BackgroundWriteGate"/>: no startup clean-up, no resumed
/// job, no worker, and a job is never claimed, since no status change could be saved.</para>
/// <para>Clip-only tombstones are never worked on, and every write checks for one under the store's lock, because
/// a bookmark can be deleted with its clip kept at any moment of a job: no job is queued, resumed or claimed for a
/// tombstone; a late outcome (failed, cancelled, interrupted) leaves it as it is, so the normalised <c>Completed</c> of its
/// kept file stays; and a job that finishes anyway has its new file deleted while the tombstone keeps the old one. The one
/// exception is a job whose output has the very path the tombstone keeps (possible only when that file was gone when the
/// job was planned): that file is then the clip, and the tombstone records it.</para>
/// <para>Clip folders (since 1.2.0): a job never creates or writes into a recorded clip folder that
/// holds clips recorded in Bookmarks while that folder isn't there (an unplugged drive: the folder made again would look like
/// one whose clips are all gone, and the missing-clip sweep would mark them), nor into one that is there without its marker
/// file (<see cref="ClipRootMarker"/>, naming the token recorded for it) unless its clips are there, none of them gone, which
/// proves it and writes the marker; the job fails with <see cref="ClipFolderAwayMessage"/>,
/// <see cref="ClipFolderUnrecognisedMessage"/> or <see cref="ClipFolderUnprovenMessage"/> before anything is written, each
/// naming the remedy (the folder back; an admin's confirmation on the Diagnostics tab, after which the clips gone can be
/// forgotten, or another clip folder), and can be made again then. The default clip folder in the plugin's data folder, deleted, is made again with
/// its marker and its clips are marked missing (<see cref="ClipFileOps.ConfirmFolderAsync"/>), so clips aren't refused
/// server-wide. A clip folder that holds no recorded clip (a new one) is created with its marker file; the job's folder is
/// looked at once more after its <c>.partial</c> is reserved.</para>
/// <para>Clip files: a clip folder is recorded in <c>ClipRoots</c> before the worker writes anything in
/// it; a remake always gets a fresh file name (the planner), so it never has to replace a file that may be in use; the
/// finished clip is moved into place without overwriting anything; and every file it deletes (a remake's previous file, a
/// discarded output, a temporary <c>.partial</c>) goes through <see cref="ClipFileOps"/>, which lists a previous or
/// discarded file still in use in <c>PendingFileDeletes</c> for <see cref="PartialFileJanitor"/> at the next start. A job's
/// <c>.partial</c> that a crash left behind is the janitor's too, at the next start before any job resumes.</para>
/// <para>Free space: a job keeps the estimate of its request (<c>ClipInfo.EstimatedBytes</c>), made again
/// when an interrupted job is queued again at startup; <see cref="ClipStorageGuard"/> checks the space once more when the job
/// starts, before its clip folder is recorded or anything is written (a refusal fails the job with the user's wording),
/// watches it while ffmpeg writes (stopping the job, which then fails with "Stopped: the clip drive is almost full."), and
/// words a failure on a full disk "The clip drive is full.", probing before the job's <c>.partial</c> is deleted.</para>
/// <para>Its log lines at Information and above carry ids, modes, durations and sizes only: a clip file's name holds the
/// bookmark's name or note, so its path is logged at Debug, and a failure's message names it as
/// <see cref="ClipFiles.LogPlaceholder"/>.</para>
/// </remarks>
internal sealed class ClipExtractionService : KitBackgroundService, IClipExtractionService
{
    /// <summary>
    /// The error of a job whose clip folder holds clips recorded in Bookmarks but isn't there, with the remedy
    /// for a folder gone for good.
    /// </summary>
    public const string ClipFolderAwayMessage = "The clip folder isn't available right now (an unplugged drive, or a folder that was moved), so no clip was made. Make the clip again once it is back. If it is gone for good, your server admin can confirm that on the Bookmarks settings page's Diagnostics tab, or choose another clip folder.";

    /// <summary>
    /// The error of a job whose clip folder holds clips recorded in Bookmarks and is there, but has neither its marker file nor
    /// any of those clips: not recognised as the folder they were made in. It names the remedy for a folder whose clips
    /// are gone for good.
    /// </summary>
    public const string ClipFolderUnrecognisedMessage = "The clip folder doesn't look like the one its clips were made in (a drive or share that isn't mounted?), so no clip was made. Make the clip again once it is back. If its clips are gone for good, your server admin can confirm the folder on the Bookmarks settings page's Diagnostics tab, or choose another clip folder.";

    /// <summary>
    /// The error of a job whose clip folder holds clips recorded in Bookmarks and is there without its marker file, with some
    /// of those clips but not all: not proven to be the folder they were made in. Since 1.2.0 the My Clips page offers no
    /// Forget in such a folder (its clips read as their folder offline), so the remedy is the admin's confirmation, after
    /// which the gone clips can be forgotten.
    /// </summary>
    public const string ClipFolderUnprovenMessage = "The clip folder isn't recognised as the one its clips were made in (its marker file is missing and some of its clips are gone), so no clip was made. Your server admin can confirm the folder on the Bookmarks settings page's Diagnostics tab (the clips that are gone can be forgotten on the My Clips page then), or choose another clip folder.";

    private const int MaxWorkers = 8;

    private readonly IBookmarkStore _store;
    private readonly BackgroundWriteGate _gate;
    private readonly IClipPlanner _planner;
    private readonly IFfmpegRunner _runner;
    private readonly ClipFileOps _files;
    private readonly PartialFileJanitor _janitor;
    private readonly ClipStorageGuard _guard;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly ILogger<ClipExtractionService> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, RunningClip> _running = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipExtractionService"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="gate">Keeps the worker idle while the bookmark data is read-only.</param>
    /// <param name="planner">Resolves bookmarks into clip jobs.</param>
    /// <param name="runner">Runs ffmpeg.</param>
    /// <param name="files">The clip file operations.</param>
    /// <param name="janitor">The clean-up at startup.</param>
    /// <param name="guard">The free-space guard.</param>
    /// <param name="configurationProvider">The plugin configuration.</param>
    /// <param name="applicationHost">The server host, for the kit's startup gate.</param>
    /// <param name="logger">The logger.</param>
    public ClipExtractionService(
        IBookmarkStore store,
        BackgroundWriteGate gate,
        IClipPlanner planner,
        IFfmpegRunner runner,
        ClipFileOps files,
        PartialFileJanitor janitor,
        ClipStorageGuard guard,
        IPluginConfigurationProvider configurationProvider,
        IServerApplicationHost applicationHost,
        ILogger<ClipExtractionService> logger)
        : base(applicationHost, logger)
    {
        _store = store;
        _gate = gate;
        _planner = planner;
        _runner = runner;
        _files = files;
        _janitor = janitor;
        _guard = guard;
        _configurationProvider = configurationProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets the pause between two tries of the clip file renames that wait (<see cref="ClipFileOps.RenameRetryInterval"/>, a
    /// minute; a test's shorter).
    /// </summary>
    internal TimeSpan RenameRetryInterval { get; init; } = ClipFileOps.RenameRetryInterval;

    /// <inheritdoc />
    public async Task<Bookmark?> EnqueueAsync(Guid bookmarkId, Guid ownerId, BookmarkClipMode mode, long? estimatedBytes, CancellationToken cancellationToken)
    {
        var queued = false;
        var bookmark = await _store.UpdateAsync(
            bookmarkId,
            b =>
            {
                queued = false;

                // A tombstone gets no job (a remake is 404), nor a record that changed owner since the caller
                // looked it up; one already queued or running is left alone.
                if (!BookmarkRecordRules.IsLiveBookmarkOf(b, ownerId) || b.Clip is { Status: BookmarkClipStatus.Queued or BookmarkClipStatus.Running })
                {
                    return b;
                }

                queued = true;
                return b with
                {
                    Clip = new ClipInfo
                    {
                        Status = BookmarkClipStatus.Queued,
                        Mode = mode,
                        StartTicks = b.StartTicks,
                        EndTicks = b.EndTicks ?? b.StartTicks,
                        RequestedAt = DateTime.UtcNow,

                        // The free-space guard's estimate, which later requests subtract while this job
                        // waits or runs, also after a restart.
                        EstimatedBytes = estimatedBytes,

                        // The existing clip stays available until the new one has been written.
                        FilePath = b.Clip?.FilePath,
                        FileSize = b.Clip?.FileSize,
                        FileStartTicks = b.Clip?.FileStartTicks,
                        FileEndTicks = b.Clip?.FileEndTicks
                    }
                };
            },
            cancellationToken).ConfigureAwait(false);

        if (queued)
        {
            _queue.Writer.TryWrite(bookmarkId);
            _logger.LogInformation("Queued {Mode} clip for bookmark {BookmarkId}", mode, bookmarkId);
        }

        return bookmark is not null && BookmarkRecordRules.IsLiveBookmarkOf(bookmark, ownerId) ? bookmark : null;
    }

    /// <inheritdoc />
    public async Task CancelAsync(Guid bookmarkId, CancellationToken cancellationToken)
    {
        // A queued job is cancelled by changing its status; the worker skips it when dequeued. A tombstone's clip is its
        // kept file, never a job.
        await _store.UpdateAsync(
            bookmarkId,
            b => BookmarkRecordRules.IsLive(b) && b.Clip is { Status: BookmarkClipStatus.Queued }
                ? b with { Clip = b.Clip with { Status = BookmarkClipStatus.Cancelled, CompletedAt = DateTime.UtcNow } }
                : b,
            cancellationToken).ConfigureAwait(false);

        StopRunning(bookmarkId);
    }

    /// <inheritdoc />
    public void StopRunning(Guid bookmarkId)
    {
        // A running job is registered before it is claimed (see ProcessAsync), so this can't miss it.
        if (_running.TryGetValue(bookmarkId, out var running))
        {
            running.Cancel();
        }
    }

    /// <inheritdoc />
    public double? GetProgress(Guid bookmarkId)
        => _running.TryGetValue(bookmarkId, out var running) ? running.Progress : null;

    /// <summary>
    /// Replaces full server paths in a failure message with file names, so users don't see the server's layout.
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <param name="plan">The job's plan, if it got that far.</param>
    /// <returns>The redacted message.</returns>
    internal static string RedactPaths(string message, ClipPlan? plan)
    {
        if (plan is null)
        {
            return message;
        }

        // The temporary path starts with the output path, so it has to be replaced first.
        foreach (var path in new[] { plan.TempPath, plan.OutputPath, plan.Command.InputPath })
        {
            message = message.Replace(path, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        }

        var directory = Path.GetDirectoryName(plan.OutputPath);
        return string.IsNullOrEmpty(directory)
            ? message
            : message.Replace(directory, "the clip folder", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The kit's base runs it once the startup gate is open: background services start before Jellyfin
    /// has located ffmpeg and probed its features, so jobs resumed any earlier would fail. Nothing it throws stops the
    /// server.
    /// </remarks>
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The gate: while the data is read-only no job could record its state, so nothing below
            // runs: no startup clean-up, no resumed job and no worker. A newer Bookmarks' file or an upgrade that failed
            // stays so until the server restarts; data that is missing or unreadable, until an admin recovers it on the
            // settings page (Bookmarks 1.2), and then all of it starts, as at a start, so clips asked for after that are made.
            if (!_gate.MayWrite(BackgroundWriter.ClipWorker))
            {
                await _gate.WaitUntilOpenAsync(stoppingToken).ConfigureAwait(false);
                _logger.LogInformation("The bookmark data can be written again, so the clip worker starts");
            }

            // The clean-up at startup, behind its own BackgroundWriter, while no job runs: the stale .partial files
            // of jobs an earlier run didn't finish, so a resumed job gets its own name again rather than " (2)",
            // then the old clip files still waiting to be deleted.
            await _janitor.RunAtStartupAsync(stoppingToken).ConfigureAwait(false);
            await RequeueInterruptedJobsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // The store file may have been unreadable (locked): the workers still start, for jobs queued once it can be read.
            _logger.LogError(ex, "Could not restore clip jobs from before the last shutdown");
        }

        var workerCount = Math.Clamp(_configurationProvider.GetConfiguration().MaxConcurrentClips, 1, MaxWorkers);
        var workers = Enumerable.Range(0, workerCount).Select(_ => RunWorkerAsync(stoppingToken)).ToList();

        // The clip file renames that wait (a clip folder away, data that couldn't be written) are tried again while any
        // waits (after a minute, then less often while nothing changes), not only at that clip's next action, the daily
        // maintenance or a restart.
        workers.Add(_files.RetryWaitingRenamesAsync(RenameRetryInterval, stoppingToken));
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RequeueInterruptedJobsAsync(CancellationToken stoppingToken)
    {
        // Tombstones are skipped: their clip is a kept file, and a job state on one (written by another version,
        // or by hand) is left as it is.
        var pending = _store.GetAll()
            .Where(b => BookmarkRecordRules.IsLive(b) && b.Clip is { Status: BookmarkClipStatus.Queued or BookmarkClipStatus.Running })
            .OrderBy(b => b.Clip!.RequestedAt)
            .ToList();

        var resumed = 0;
        foreach (var bookmark in pending)
        {
            // The estimate is made again: the source or the settings may have changed while the server was
            // down, and a job 1.0 queued has none.
            var estimate = Reestimate(bookmark, bookmark.Clip!);
            var current = await _store.UpdateAsync(
                bookmark.Id,
                b => BookmarkRecordRules.IsLive(b) && b.Clip is { Status: BookmarkClipStatus.Running or BookmarkClipStatus.Queued } clip
                    && (clip.Status == BookmarkClipStatus.Running || (estimate.Known && clip.EstimatedBytes != estimate.Bytes))
                    ? b with
                    {
                        Clip = clip with
                        {
                            Status = BookmarkClipStatus.Queued,
                            EstimatedBytes = estimate.Known ? estimate.Bytes : clip.EstimatedBytes
                        }
                    }
                    : b,
                stoppingToken).ConfigureAwait(false);
            if (current is not null && BookmarkRecordRules.IsLive(current) && current.Clip is { Status: BookmarkClipStatus.Queued })
            {
                _queue.Writer.TryWrite(bookmark.Id);
                resumed++;
            }
        }

        if (resumed > 0)
        {
            _logger.LogInformation("Resuming {Count} clip jobs from before the last shutdown", resumed);
        }
    }

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var bookmarkId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(bookmarkId, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Keep the worker alive; with a single worker an escaped exception would also stop the server.
                    _logger.LogError(ex, "Unexpected error in clip worker for bookmark {BookmarkId}", bookmarkId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task ProcessAsync(Guid bookmarkId, CancellationToken stoppingToken)
    {
        // Never claim a job the store can't record (a store that couldn't be read at startup and loaded read-only later).
        if (!_gate.MayWrite(BackgroundWriter.ClipWorker))
        {
            return;
        }

        using var job = new RunningClip(stoppingToken);

        // Register before claiming so a concurrent CancelAsync always finds the job. If another worker
        // holds the registration, it re-posts the id when it finishes if a new request arrived meanwhile.
        if (!_running.TryAdd(bookmarkId, job))
        {
            return;
        }

        DateTime? claimedRequest = null;
        try
        {
            var bookmark = await _store.UpdateAsync(
                bookmarkId,
                b =>
                {
                    claimedRequest = null;

                    // Only a live bookmark's queued job is claimed; a tombstone never runs one, even if a job
                    // state was left on it.
                    if (BookmarkRecordRules.IsTombstone(b) || b.Clip is not { Status: BookmarkClipStatus.Queued } clip)
                    {
                        return b;
                    }

                    claimedRequest = clip.RequestedAt;
                    return b with { Clip = clip with { Status = BookmarkClipStatus.Running, Error = null } };
                },
                stoppingToken).ConfigureAwait(false);

            if (claimedRequest is not null && bookmark?.Clip is { } clip)
            {
                await RunJobAsync(bookmark, clip, job, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down before the job was claimed; it stays queued.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing clip for bookmark {BookmarkId}", bookmarkId);
            if (claimedRequest is not null)
            {
                await TrySetOutcomeAsync(bookmarkId, BookmarkClipStatus.Failed, "Unexpected error: " + ex.Message).ConfigureAwait(false);
            }
        }
        finally
        {
            _running.TryRemove(bookmarkId, out _);

            // A new request can arrive after this job finished but before it was unregistered; another
            // worker would have dropped it, so queue it again.
            if (claimedRequest is DateTime handled
                && !stoppingToken.IsCancellationRequested
                && _store.Get(bookmarkId) is { } current
                && BookmarkRecordRules.IsLive(current)
                && current.Clip is { Status: BookmarkClipStatus.Queued } latest
                && latest.RequestedAt != handled)
            {
                _queue.Writer.TryWrite(bookmarkId);
            }
        }
    }

    private async Task RunJobAsync(Bookmark bookmark, ClipInfo clip, RunningClip job, CancellationToken stoppingToken)
    {
        // The job's plan, and its own .partial once reserved: only that one is ever deleted as the job's temporary file, never
        // a name it planned but didn't reserve (another job may have reserved it since).
        var reserved = new ReservedOutput();
        try
        {
            // Clip the range captured when the job was queued, even if the bookmark was edited since.
            await PlanAndReserveAsync(bookmark with { StartTicks = clip.StartTicks, EndTicks = clip.EndTicks }, clip.Mode, reserved, job.Token).ConfigureAwait(false);
            var plan = reserved.Plan!;

            // The file name holds the bookmark's name or note: its path is logged at Debug only (ClipFiles).
            _logger.LogInformation("Extracting {Mode} clip {Duration} long for bookmark {BookmarkId}", clip.Mode, plan.Duration, bookmark.Id);
            _logger.LogDebug("Clip for bookmark {BookmarkId} goes to {Path}", bookmark.Id, plan.OutputPath);

            var arguments = FfmpegClipCommandBuilder.Build(plan.Command);

            // The free-space watchdog while ffmpeg writes; it stops the job when the drive is almost full.
            using (var watch = CancellationTokenSource.CreateLinkedTokenSource(job.Token))
            {
                var watchdog = _guard.WatchAsync(plan.OutputFolder, job, bookmark.Id, watch.Token);
                try
                {
                    await _runner.RunAsync(plan.EncoderPath, arguments, plan.Duration, job, job.Token).ConfigureAwait(false);
                }
                finally
                {
                    await watch.CancelAsync().ConfigureAwait(false);
                    await watchdog.ConfigureAwait(false);
                }
            }

            // Into place, never over another file: the name was free when it was planned, and its .partial kept it
            // from other jobs since. A remake that keeps its file's name (since 1.2.1) replaces its own file instead, while
            // the record still names it; when it can't (the file in use), the clip takes a fresh name after all.
            var outputPath = plan.OutputPath;
            ClipFileResult moved;
            if (plan.ReplacesOwnFile && StillOwns(bookmark.Id, plan.OutputPath))
            {
                moved = await _files.ReplaceAsync(plan.TempPath, plan.OutputPath, bookmark.Id, CancellationToken.None).ConfigureAwait(false);
                if (moved.Outcome is ClipFileOutcome.InUse or ClipFileOutcome.Failed)
                {
                    _logger.LogInformation("The remade clip of bookmark {BookmarkId} couldn't replace its file ({Outcome}); it takes a fresh name", bookmark.Id, moved.Outcome);
                    (outputPath, moved) = await MoveUnderFreshNameAsync(plan, bookmark.Id).ConfigureAwait(false);
                }
            }
            else if (plan.ReplacesOwnFile)
            {
                (outputPath, moved) = await MoveUnderFreshNameAsync(plan, bookmark.Id).ConfigureAwait(false);
            }
            else
            {
                moved = await _files.MoveAsync(plan.TempPath, plan.OutputPath, bookmark.Id, CancellationToken.None).ConfigureAwait(false);
            }

            if (moved.Outcome != ClipFileOutcome.Done)
            {
                // A full disk keeps its HResult, for the disk-full wording.
                throw ClipStorageGuard.IsDiskFull(moved.HResult)
                    ? new ClipExtractionException(DescribeFailedMove(moved), new IOException(moved.Error, moved.HResult!.Value))
                    : new ClipExtractionException(DescribeFailedMove(moved));
            }

            await CompleteAsync(bookmark.Id, clip, outputPath).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await _files.DeleteTemporaryAsync(reserved.TempPath, bookmark.Id).ConfigureAwait(false);
            await TrySetOutcomeAsync(bookmark.Id, BookmarkClipStatus.Queued, null).ConfigureAwait(false);
            _logger.LogInformation("Clip for bookmark {BookmarkId} interrupted by shutdown; it will resume on next start", bookmark.Id);
        }
        catch (OperationCanceledException) when (job.StopReason == ClipStopReason.LowDiskSpace)
        {
            // The watchdog stopped it: a failure, not a cancellation, and its .partial goes.
            await _files.DeleteTemporaryAsync(reserved.TempPath, bookmark.Id).ConfigureAwait(false);
            await TrySetOutcomeAsync(bookmark.Id, BookmarkClipStatus.Failed, ClipStorageGuard.AlmostFullMessage).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _files.DeleteTemporaryAsync(reserved.TempPath, bookmark.Id).ConfigureAwait(false);
            await TrySetOutcomeAsync(bookmark.Id, BookmarkClipStatus.Cancelled, null).ConfigureAwait(false);
            _logger.LogInformation("Clip for bookmark {BookmarkId} cancelled", bookmark.Id);
        }
        catch (Exception ex)
        {
            // A full disk is worded as such, probed before the .partial is deleted and gives its space back.
            // The guard's own refusal when the job was about to start is the job's error as it is.
            var plan = reserved.Plan;
            var diskFull = ex is ClipExtractionException { FromStorageGuard: true }
                ? null
                : await _guard.DescribeFailureAsync(ex, plan?.OutputFolder, reserved.TempPath is not null, bookmark.Id).ConfigureAwait(false);
            await _files.DeleteTemporaryAsync(reserved.TempPath, bookmark.Id).ConfigureAwait(false);
            var expected = diskFull is not null || ex is ClipExtractionException or IOException or UnauthorizedAccessException;
            var message = diskFull ?? RedactPaths(ex.Message, plan);
            await TrySetOutcomeAsync(bookmark.Id, BookmarkClipStatus.Failed, expected ? message : "Unexpected error: " + message).ConfigureAwait(false);
            if (expected)
            {
                // ffmpeg's error output and an I/O exception name the clip file, whose name holds the bookmark's name or
                // note: the Warning says <clip file> instead, and the whole message is at Debug.
                _logger.LogWarning("Clip for bookmark {BookmarkId} failed: {Error}", bookmark.Id, ClipFiles.RedactForLog(ex.Message, plan?.OutputPath, plan?.TempPath));
                _logger.LogDebug(ex, "Clip for bookmark {BookmarkId} failed", bookmark.Id);
            }
            else
            {
                _logger.LogError(ex, "Clip for bookmark {BookmarkId} failed unexpectedly", bookmark.Id);
            }
        }
    }

    // What a job's error says when its finished clip couldn't be moved into place: no path (a clip file's name holds the
    // bookmark's name or note), and RedactPaths runs on it too.
    private static string DescribeFailedMove(ClipFileResult moved) => moved.Outcome switch
    {
        ClipFileOutcome.InUse => "The finished clip couldn't be put in place: its file was in use. Make the clip again.",
        ClipFileOutcome.Missing => "The finished clip couldn't be put in place: its temporary file is gone. Make the clip again.",
        ClipFileOutcome.Refused => "The finished clip couldn't be put in place: its folder isn't one of the clip folders.",
        _ => "The finished clip couldn't be put in place: " + (moved.Error ?? "the move failed.") + " Make the clip again."
    };

    // Whether the live record still names its clip's own file, which its remake may replace (ClipPlan.ReplacesOwnFile): not
    // when the bookmark was deleted with its clip kept (a tombstone keeps that file), its clip deleted, or its file renamed
    // while the job ran.
    private bool StillOwns(Guid bookmarkId, string path)
        => _store.Get(bookmarkId) is { Clip.FilePath: { Length: > 0 } recorded } current
            && BookmarkRecordRules.IsLive(current)
            && ClipFileGuard.TryGetFullPath(recorded, out var full)
            && ClipPlanner.PathsEqual(full, path)
            && _files.ReusableOwnPath(bookmarkId, path, whileWriting: true) is not null;

    // A remake that couldn't replace its own file: a fresh name in its folder, chosen and moved into under the name gate, so
    // no other job or rename takes it meanwhile. The record's path stays taken, so it is never the one chosen.
    private async Task<(string Path, ClipFileResult Moved)> MoveUnderFreshNameAsync(ClipPlan plan, Guid bookmarkId)
    {
        await _files.NameGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            string fresh;
            try
            {
                fresh = ClipFileNaming.MakeUnique(Path.GetDirectoryName(plan.OutputPath)!, Path.GetFileName(plan.OutputPath), _files.NameTaken(retryOnce: true));
            }
            catch (ClipFolderUnreachableException ex)
            {
                throw new ClipExtractionException(ClipFileOps.FolderUnreachableClipMessage, ex) { FolderUnreachable = true };
            }

            _logger.LogDebug("The remade clip of bookmark {BookmarkId} goes to {Path}", bookmarkId, fresh);
            return (fresh, await _files.MoveAsync(plan.TempPath, fresh, bookmarkId, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            _files.NameGate.Release();
        }
    }

    private static void ReserveOutput(ClipPlan plan)
    {
        var directory = Path.GetDirectoryName(plan.OutputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Create(plan.TempPath).Dispose();
    }

    // Plans the job, checks the recorded clip folders it would write in and the free space again (before anything is
    // written), records its clip folder (before anything is written there, so
    // ClipFileGuard accepts every file of the job), gives it its marker file, and reserves the output name with its .partial
    // file, one job at a time. `reserved` gets the plan as soon as there is one, and the .partial's path once the file is
    // created.
    private async Task PlanAndReserveAsync(Bookmark bookmark, BookmarkClipMode mode, ReservedOutput reserved, CancellationToken cancellationToken)
    {
        // One job at a time plans its output, records its clip folder and reserves the name (its .partial file), so two jobs
        // never pick the same name; and never while a clip file is being renamed, which chooses its new name
        // under the same gate (ClipFileOps.NameGate, a semaphore, since recording the folder writes the store).
        await _files.NameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var plan = _planner.Plan(bookmark, mode);
            reserved.Plan = plan;
            await CheckRecordedClipFoldersAsync(plan, bookmark.Id, cancellationToken).ConfigureAwait(false);

            var space = await _guard.CheckAtClaimAsync(plan.OutputFolder, plan.EstimatedBytes, bookmark.Id, cancellationToken).ConfigureAwait(false);
            if (space.Refused)
            {
                // Failed, not queued again, with the wording a user gets (the owner reads it, whoever that is); the numbers
                // are in the guard's Warning.
                throw new ClipExtractionException(space.UserMessage) { FromStorageGuard = true };
            }

            try
            {
                await _store.AddClipRootAsync(plan.Root, CancellationToken.None).ConfigureAwait(false);
            }
            catch (PluginException ex)
            {
                // 409 at 50 recorded folders, 400 for a folder that isn't a path, 503 when the store can't be written.
                throw new ClipExtractionException(ex.Message, ex);
            }

            var marked = await EnsureRootMarkerAsync(plan, bookmark.Id, cancellationToken).ConfigureAwait(false);

            // Create the temporary file now so jobs planned after this one pick a different name.
            ReserveOutput(plan);
            reserved.TempPath = plan.TempPath;

            // The clip folder once more, now that the job's .partial is in it: one unplugged since the look above, which the
            // reservation made again, has no marker file, and the job fails rather than write its clip there.
            if (marked && ClipRootPaths.TryNormalize(plan.Root, out var root) && _files.CheckRoot(root) != ClipRootState.Marked)
            {
                _logger.LogDebug("The clip folder {Folder} of bookmark {BookmarkId}'s clip went away while its job started", root, bookmark.Id);
                throw new ClipExtractionException(ClipFolderAwayMessage);
            }
        }
        finally
        {
            _files.NameGate.Release();
        }
    }

    // The recorded clip folders a job would write in (its own clip folder and any recorded one above it) that hold
    // clips recorded in Bookmarks: each must be there, and be those clips' own folder,
    // proven by its marker file naming the token recorded for it, or by its clips, every one of them that isn't being made
    // there (one there and none gone), which then gives it the marker (the backfill). Otherwise the job fails before anything
    // is written: creating the folder, or writing a clip into a folder that isn't theirs, would make it look like a folder
    // whose clips are gone (an unplugged drive whose folder the job made again; an empty mount point; another drive under
    // the same letter; a folder holding a clip 1.1 wrote there while the right one was away), and the missing-clip sweep
    // would mark them. A marker written before markers carried a token gets its token (adopted). The default clip folder
    // deleted is made again with its marker, its clips marked missing, as the sweep does. A folder without recorded clips
    // (a new one) passes, and is given its marker by EnsureRootMarkerAsync.
    private async Task CheckRecordedClipFoldersAsync(ClipPlan plan, Guid bookmarkId, CancellationToken cancellationToken)
    {
        if (!ClipRootPaths.TryNormalize(plan.OutputFolder, out var output))
        {
            return;
        }

        List<string>? clipFiles = null;
        foreach (var root in RecordedRootsHolding(output))
        {
            clipFiles ??= RecordedClipFiles();
            var held = clipFiles.Where(path => path.Length > root.Length && ClipRootPaths.IsUnder(path, root)).ToList();
            if (held.Count == 0)
            {
                continue;
            }

            switch (_files.CheckRoot(root))
            {
                case ClipRootState.Marked:
                    continue;
                case ClipRootState.Adoptable:
                    // Proven as it was before the token; a token that can't be recorded is a Warning (ClipFileOps).
                    await _files.WriteRootMarkerAsync(root, cancellationToken).ConfigureAwait(false);
                    continue;
                case ClipRootState.Absent when _files.DeletedDefaultFolder() is { } deleted && ClipRootPaths.Comparer.Equals(deleted, root):
                    await RecreateDefaultFolderAsync(root, bookmarkId, cancellationToken).ConfigureAwait(false);
                    continue;
                case ClipRootState.Absent:
                    _logger.LogDebug("The clip of bookmark {BookmarkId} wasn't made: its clip folder {Folder}, which holds {Clips} clips, isn't there", bookmarkId, root, held.Count);
                    throw new ClipExtractionException(ClipFolderAwayMessage);
                default:
                    var clips = _files.CountClips(root);
                    if (clips.Present > 0 && clips.Gone == 0)
                    {
                        // Its clips are there, none gone: their folder, and its marker says so from now on.
                        await _files.WriteRootMarkerAsync(root, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    _logger.LogDebug(
                        "The clip of bookmark {BookmarkId} wasn't made: its clip folder {Folder} has no marker file, and of its {Clips} clips {Present} are there and {Gone} gone",
                        bookmarkId,
                        root,
                        clips.Clips,
                        clips.Present,
                        clips.Gone);
                    throw new ClipExtractionException(clips.Present > 0 ? ClipFolderUnprovenMessage : ClipFolderUnrecognisedMessage);
            }
        }
    }

    // The job's own clip folder carries its marker file from its first clip on: created with the folder when
    // it is new, written into a folder there without one that holds no recorded clip. One that holds clips was proven or
    // refused by CheckRecordedClipFoldersAsync; one gone since then is refused now if it holds clips (an unplugged drive),
    // made again with its clips marked missing if it is the deleted default folder, and made otherwise. Returns whether the
    // folder is marked now: a marker that can't be written is a Warning (ClipFileOps), and the clip is made all the same,
    // since no clip in that folder could be taken for gone.
    private async Task<bool> EnsureRootMarkerAsync(ClipPlan plan, Guid bookmarkId, CancellationToken cancellationToken)
    {
        if (!ClipRootPaths.TryNormalize(plan.Root, out var root))
        {
            return false;
        }

        switch (_files.CheckRoot(root))
        {
            case ClipRootState.Marked:
                return true;
            case ClipRootState.Absent when HoldsRecordedClips(root) && _files.DeletedDefaultFolder() is { } deleted && ClipRootPaths.Comparer.Equals(deleted, root):
                await RecreateDefaultFolderAsync(root, bookmarkId, cancellationToken).ConfigureAwait(false);
                return _files.CheckRoot(root) == ClipRootState.Marked;
            case ClipRootState.Absent when HoldsRecordedClips(root):
                _logger.LogDebug("The clip of bookmark {BookmarkId} wasn't made: its clip folder {Folder} went away", bookmarkId, root);
                throw new ClipExtractionException(ClipFolderAwayMessage);
            case ClipRootState.Absent:
                Directory.CreateDirectory(root);
                break;
            case ClipRootState.Unmarked when HoldsRecordedClips(root):
                // Its clips proved it above, but its marker couldn't be written (a Warning then): not asked again.
                return false;
        }

        return await _files.WriteRootMarkerAsync(root, cancellationToken).ConfigureAwait(false) != ClipRootMarking.Failed;
    }

    // The default clip folder, deleted while clips are recorded in it: made again with its marker
    // file, its clips marked missing (ClipFileOps.ConfirmFolderAsync), so the job writes there and the sweep judges it. One that
    // can't be made fails the job as a folder away.
    private async Task RecreateDefaultFolderAsync(string root, Guid bookmarkId, CancellationToken cancellationToken)
    {
        var confirmation = await _files.ConfirmFolderAsync(root, dryRun: false, cancellationToken).ConfigureAwait(false);
        if (confirmation.Failure is not null)
        {
            _logger.LogDebug("The clip of bookmark {BookmarkId} wasn't made: the default clip folder {Folder} is gone and couldn't be made again", bookmarkId, root);
            throw new ClipExtractionException(ClipFolderAwayMessage);
        }

        _logger.LogInformation(
            "The default clip folder was gone (deleted with its {Clips} clips): made again with its marker file for the clip of bookmark {BookmarkId}, {Marked} clips marked missing",
            confirmation.Counts.Clips,
            bookmarkId,
            confirmation.Newly);
    }

    // The recorded clip folders (normalised, each once) that are the folder or lie above it.
    private List<string> RecordedRootsHolding(string folder)
    {
        var roots = new List<string>();
        foreach (var candidate in _store.ClipRoots)
        {
            if (ClipRootPaths.TryNormalize(candidate, out var root) && ClipRootPaths.IsUnder(folder, root) && !roots.Contains(root, ClipRootPaths.Comparer))
            {
                roots.Add(root);
            }
        }

        return roots;
    }

    // The full paths of every record's clip file, live bookmarks' and tombstones' (a clip being made keeps its earlier file).
    private List<string> RecordedClipFiles()
        => _store.GetAll()
            .Select(b => b.Clip?.FilePath)
            .Select(path => ClipFileGuard.TryGetFullPath(path, out var full) ? full : null)
            .OfType<string>()
            .ToList();

    private bool HoldsRecordedClips(string root)
        => RecordedClipFiles().Any(path => path.Length > root.Length && ClipRootPaths.IsUnder(path, root));

    // A queued job's estimate made again from a fresh plan, when it can be planned; a job that can't be
    // planned keeps its estimate (and fails with the planner's reason when it runs); whatever the planner throws here, the
    // other jobs still resume.
    private (bool Known, long? Bytes) Reestimate(Bookmark bookmark, ClipInfo clip)
    {
        try
        {
            return (true, _planner.Plan(bookmark with { StartTicks = clip.StartTicks, EndTicks = clip.EndTicks }, clip.Mode).EstimatedBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not estimate the size of the clip of bookmark {BookmarkId} again", bookmark.Id);
            return (false, null);
        }
    }

    private async Task CompleteAsync(Guid bookmarkId, ClipInfo clip, string outputPath)
    {
        var size = new FileInfo(outputPath).Length;
        var now = DateTime.UtcNow;
        var completed = false;
        var keptByTombstone = false;
        string? replaced = null;
        await _store.UpdateAsync(
            bookmarkId,
            b =>
            {
                completed = false;
                keptByTombstone = false;
                replaced = null;
                // The new file is there: a "missing since" of the file it replaces (the missing-clip sweep's) goes.
                var written = b.Clip is null ? null : b.Clip with
                {
                    Status = BookmarkClipStatus.Completed,
                    CompletedAt = now,
                    Error = null,
                    FilePath = outputPath,
                    FileSize = size,
                    FileStartTicks = clip.StartTicks,
                    FileEndTicks = clip.EndTicks,
                    MissingSinceUtc = null
                };

                if (BookmarkRecordRules.IsTombstone(b))
                {
                    // The bookmark was deleted with its clip kept while ffmpeg ran. The tombstone keeps its file,
                    // unless this job's output has that very path (the kept file was gone when the job was planned, so its
                    // name was free): the file on disk is then this clip, and the tombstone records it, still in its
                    // file-only state.
                    if (written is null || !ClipPlanner.PathsEqual(b.Clip!.FilePath, outputPath))
                    {
                        return b;
                    }

                    keptByTombstone = true;
                    return b with { Clip = BookmarkRecordRules.FileOnly(written) };
                }

                if (written is null)
                {
                    return b;
                }

                completed = true;
                replaced = b.Clip!.FilePath;
                return b with { Clip = written };
            },
            CancellationToken.None).ConfigureAwait(false);

        // At Information the id and the size only: the file name holds the bookmark's name or note, so the path is logged at
        // Debug (the completion line's path too, since 1.2.0).
        if (keptByTombstone)
        {
            _logger.LogInformation(
                "Clip for bookmark {BookmarkId} replaced the clip file its deleted bookmark keeps ({Size} bytes); the kept clip is this one now",
                bookmarkId,
                size);
            _logger.LogDebug("Clip for bookmark {BookmarkId} written to {Path}, the file its deleted bookmark keeps", bookmarkId, outputPath);
            return;
        }

        if (!completed)
        {
            // The bookmark or its clip was deleted while ffmpeg ran, or the bookmark was deleted with an earlier clip kept
            // (a tombstone, which keeps that one); don't leave an orphaned file behind (later, if something opened it).
            await _files.DeleteOrQueueAsync(outputPath, bookmarkId, BackgroundWriter.ClipWorker, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Clip for bookmark {BookmarkId} was discarded: the bookmark or its clip was deleted while it was made", bookmarkId);
            return;
        }

        // A remake wrote a fresh file: the record points at it now, and the previous file goes, or, while it is in
        // use (watched, downloaded), waits in PendingFileDeletes.
        if (replaced is not null && !ClipPlanner.PathsEqual(replaced, outputPath))
        {
            await _files.DeleteOrQueueAsync(replaced, bookmarkId, BackgroundWriter.ClipWorker, CancellationToken.None).ConfigureAwait(false);
        }

        _logger.LogInformation("Clip for bookmark {BookmarkId} written ({Size} bytes)", bookmarkId, size);
        _logger.LogDebug("Clip for bookmark {BookmarkId} written to {Path}", bookmarkId, outputPath);
    }

    private async Task TrySetOutcomeAsync(Guid bookmarkId, BookmarkClipStatus status, string? error)
    {
        try
        {
            // A tombstone keeps the normalised Completed of its file: a late Failed, Cancelled or Queued (a
            // shutdown) of the job that ran when its bookmark was deleted changes nothing.
            await _store.UpdateAsync(
                bookmarkId,
                b => b.Clip is null || BookmarkRecordRules.IsTombstone(b)
                    ? b
                    : b with
                    {
                        Clip = b.Clip with
                        {
                            Status = status,
                            Error = error,
                            CompletedAt = status == BookmarkClipStatus.Queued ? null : DateTime.UtcNow
                        }
                    },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PluginException or IOException or UnauthorizedAccessException)
        {
            // The kit store reports a failed write (and a read-only store) as a 503 PluginException.
            _logger.LogError(ex, "Could not record clip status {Status} for bookmark {BookmarkId}", status, bookmarkId);
        }
    }

    // What a job has planned and reserved so far, for its clean-up: the plan, and the path of its own .partial once created.
    private sealed class ReservedOutput
    {
        public ClipPlan? Plan { get; set; }

        public string? TempPath { get; set; }
    }
}
