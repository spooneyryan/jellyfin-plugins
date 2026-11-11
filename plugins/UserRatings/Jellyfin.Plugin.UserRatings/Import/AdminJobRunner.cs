using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// One data job at a time: the envelope export and import, dry run or not. A job runs in the
/// background from the request that starts it (202 at once), reports its phase and progress, and can be cancelled until it
/// enters its write phase (<see cref="AdminJob.EnterWritePhase"/>): from then on Cancel is refused with 409 until it commits,
/// so a cancel never leaves a half-applied import. The state is in memory: the running job and the last
/// <see cref="KeepFinished"/> finished ones; a restart cancels a running job (its write is one transaction, so nothing of it
/// stays). An export's file holds every review, so it is kept for its download only: it is
/// deleted <see cref="FileLifetime"/> after its job succeeded (checked every minute and at every read of the jobs), on
/// <c>DELETE Jobs/{id}</c> (<see cref="Cancel"/>), when a failed or cancelled job ends, when its job leaves the list, and when
/// the runner stops; <see cref="SweepTemp"/> deletes what a kill left in <c>tmp/</c>, at the next start and in the daily
/// maintenance. The daily maintenance holds the runner too (<see cref="HoldWhenIdleAsync"/>, its gate since the
/// 0.3.19 verification): a restore or an import writes the database and then the archive, and the maintenance's Archive
/// step mustn't run between the two.
/// </summary>
internal sealed class AdminJobRunner : IDisposable
{
    /// <summary>The finished jobs kept.</summary>
    public const int KeepFinished = 10;

    /// <summary>The 409's code when a job is running.</summary>
    public const string BusyCode = "JobRunning";

    /// <summary>The 409's code when Cancel comes in the write phase.</summary>
    public const string WritingCode = "JobWriting";

    /// <summary>The 409's code when Cancel comes after the job finished.</summary>
    public const string FinishedCode = "JobFinished";

    /// <summary>The 404's words.</summary>
    public const string NotFoundMessage = "No data job has that id. The last 10 finished jobs are kept, and none across a restart.";

    /// <summary>The names of the files a data job writes in <c>tmp/</c>: an export's, and an import's upload.</summary>
    public static readonly IReadOnlyList<string> TempPatterns = ["userratings-export-*.json", "userratings-import-*.json"];

    /// <summary>How long a succeeded export's file waits for its download.</summary>
    public static readonly TimeSpan FileLifetime = TimeSpan.FromHours(1);

    /// <summary>How long the daily maintenance waits for a restore, Start empty or a data job before it skips its run.</summary>
    public static readonly TimeSpan MaintenanceWait = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly LinkedList<AdminJob> _finished = new();

    // Jobs that left the list of the last 10 while their file's delete was pending: the per-minute expiry tries again.
    private readonly List<AdminJob> _pendingFiles = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly HashSet<string> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly ITimer _expiry;
    private AdminJob? _running;
    private string? _held;
    private TaskCompletionSource? _idle;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminJobRunner"/> class.
    /// </summary>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger.</param>
    public AdminJobRunner(TimeProvider clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _clock = clock;
        _logger = logger;
        _expiry = clock.CreateTimer(static state => ((AdminJobRunner)state!).ExpireFilesQuietly(), this, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Gets the running job's task, for tests and the runner's own stop; null when none runs.
    /// </summary>
    public Task? Current
    {
        get
        {
            lock (_lock)
            {
                return _running?.Task;
            }
        }
    }

    /// <summary>
    /// Starts a job.
    /// </summary>
    /// <param name="kind">The kind (<c>Export</c>, <c>Import</c>).</param>
    /// <param name="dryRun">Whether it writes nothing.</param>
    /// <param name="work">The work: its report, given the job (progress, the write phase, a file) and the cancellation.</param>
    /// <returns>The job as it starts.</returns>
    /// <exception cref="PluginException">409 <see cref="BusyCode"/> while another job runs.</exception>
    public UserRatingsJobDto Start(string kind, bool dryRun, Func<AdminJob, CancellationToken, Task<object?>> work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(work);
        AdminJob job;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfBusyLocked();

            job = new AdminJob(Guid.NewGuid(), kind, dryRun, _clock.GetUtcNow().UtcDateTime, CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token));
            _running = job;
            job.Task = Task.Run(() => RunAsync(job, work));
        }

        return job.ToDto();
    }

    /// <summary>
    /// Holds the runner for work that isn't a job but mustn't run beside one (a restore, and
    /// Start empty): no job starts until the returned handle is disposed.
    /// </summary>
    /// <param name="kind">What holds it, for the 409's words.</param>
    /// <returns>The hold.</returns>
    /// <exception cref="PluginException">409 <see cref="BusyCode"/> while a job runs or another hold is held.</exception>
    public IDisposable Hold(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfBusyLocked();
            _held = kind;
        }

        return new Release(ReleaseHold);
    }

    /// <summary>
    /// Holds the runner as <see cref="Hold"/> does, once no job runs and nothing holds it, waiting for that up to
    /// <paramref name="timeout"/> on the runner's clock: the daily maintenance's gate, so its
    /// Archive step never runs between a restore's database and its archive, or between an import's commit and its archive
    /// rewrite. While held, a job, a restore and Start empty are refused with 409 <see cref="BusyCode"/>.
    /// </summary>
    /// <param name="kind">What holds it, for the 409's words.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The hold; null when the runner was still busy at the timeout, or has stopped.</returns>
    public async Task<IDisposable?> HoldWhenIdleAsync(string kind, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var deadline = _clock.GetUtcNow() + timeout;
        while (true)
        {
            Task idle;
            lock (_lock)
            {
                if (_disposed)
                {
                    return null;
                }

                if (_running is null && _held is null)
                {
                    _held = kind;
                    return new Release(ReleaseHold);
                }

                _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                idle = _idle.Task;
            }

            var left = deadline - _clock.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            try
            {
                await idle.WaitAsync(left, _clock, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Refuses while a job runs or a hold is held.
    /// </summary>
    /// <exception cref="PluginException">409 <see cref="BusyCode"/>.</exception>
    public void ThrowIfBusy()
    {
        lock (_lock)
        {
            ThrowIfBusyLocked();
        }
    }

    /// <summary>
    /// Gets a job.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <returns>The job.</returns>
    /// <exception cref="PluginException">404 unknown or no longer kept.</exception>
    public UserRatingsJobDto Get(Guid id)
    {
        ExpireFiles();
        return Find(id).ToDto();
    }

    /// <summary>
    /// Gets a finished export's file.
    /// </summary>
    /// <param name="id">The job's id.</param>
    /// <returns>The file's path.</returns>
    /// <exception cref="PluginException">404 unknown, not finished or without a file.</exception>
    public string FileOf(Guid id)
    {
        ExpireFiles();
        var job = Find(id);
        return job.State == AdminJob.Succeeded && job.DownloadFile is { } file && File.Exists(file)
            ? file
            : throw PluginException.NotFound("That job has no file to download: a finished export keeps one for an hour, until it is deleted or leaves the list of the last 10. Export again.");
    }

    /// <summary>
    /// Lists the running job and the finished ones kept, newest first.
    /// </summary>
    /// <returns>The jobs.</returns>
    public IReadOnlyList<UserRatingsJobDto> List()
    {
        ExpireFiles();
        lock (_lock)
        {
            var jobs = new List<AdminJob>();
            if (_running is { } running)
            {
                jobs.Add(running);
            }

            jobs.AddRange(_finished);
            return jobs.Select(j => j.ToDto()).ToList();
        }
    }

    /// <summary>
    /// Cancels a job before its write phase, or deletes a finished export's file.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <returns>True when it is done (the job asked to stop, or the file gone); false when the file is withdrawn but still
    /// open (a download in progress), so its delete waits for the per-minute expiry (found while verifying 0.3.19).</returns>
    /// <exception cref="PluginException">404 unknown; 409 <see cref="FinishedCode"/> finished without a file,
    /// <see cref="WritingCode"/> in its write phase.</exception>
    public bool Cancel(Guid id)
    {
        var job = Find(id);
        if (!job.TryCancel(out var writing))
        {
            if (!writing && job.State == AdminJob.Succeeded && job.DeleteFile(_logger) is var deletion && deletion != AdminJob.FileDeletion.None)
            {
                if (deletion == AdminJob.FileDeletion.Deleted)
                {
                    _logger.LogInformation("The export file of data job {JobId} was deleted on request", job.Id);
                    return true;
                }

                _logger.LogInformation("The export file of data job {JobId} was withdrawn on request; it is still open, so it is deleted within a minute of being closed", job.Id);
                return false;
            }

            throw writing
                ? new PluginException(PluginErrorKind.Conflict, "The job is writing now: its changes are one transaction, so it can't be cancelled until it commits. Nothing is left half done.") { Code = WritingCode }
                : new PluginException(PluginErrorKind.Conflict, "The job has already finished.") { Code = FinishedCode };
        }

        return true;
    }

    /// <summary>
    /// Holds an import's upload in <c>tmp/</c> against <see cref="SweepTemp"/> until the returned handle is disposed. Hold it
    /// before the file is created and release it after the file is deleted.
    /// </summary>
    /// <param name="path">The upload's path.</param>
    /// <returns>The hold.</returns>
    public IDisposable HoldUpload(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = Path.GetFullPath(path);
        lock (_lock)
        {
            _uploads.Add(full);
        }

        return new Release(() =>
        {
            lock (_lock)
            {
                _uploads.Remove(full);
            }
        });
    }

    /// <summary>
    /// Deletes the files of exports that succeeded more than <see cref="FileLifetime"/> ago, and tries again those withdrawn
    /// whose delete failed (a download held them open; found while verifying 0.3.19).
    /// </summary>
    /// <returns>The files deleted.</returns>
    public int ExpireFiles()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        List<AdminJob> due;
        lock (_lock)
        {
            if (_disposed)
            {
                return 0;
            }

            due = _finished.Where(j => (j.FileExpiresUtc is { } expires && expires <= now) || j.FileDeletePending).Concat(_pendingFiles).ToList();
        }

        var deleted = 0;
        foreach (var job in due)
        {
            var deletion = job.DeleteFile(_logger);
            if (deletion == AdminJob.FileDeletion.Deleted)
            {
                deleted++;
            }

            if (deletion != AdminJob.FileDeletion.Pending)
            {
                lock (_lock)
                {
                    _pendingFiles.Remove(job);
                }
            }
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} export file(s) kept for {Lifetime} for their download", deleted, FileLifetime);
        }

        return deleted;
    }

    /// <summary>
    /// Deletes the data jobs' files in <c>tmp/</c> (<see cref="TempPatterns"/>) that no job and no upload holds: what a kill
    /// left (an export's file, an upload cut short), and an expired export's file. At the start nothing is held, so every one
    /// goes.
    /// </summary>
    /// <param name="directory">The plugin's <c>tmp/</c>.</param>
    /// <returns>The files deleted, expired ones included.</returns>
    public int SweepTemp(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var deleted = ExpireFiles();
        if (!Directory.Exists(directory))
        {
            return deleted;
        }

        // The files first, then what is held: an upload is held before its file exists, so a file listed here that a job or an
        // upload owns is in the held set read after.
        var files = TempPatterns.SelectMany(pattern => Directory.EnumerateFiles(directory, pattern)).Select(Path.GetFullPath).ToList();
        HashSet<string> held;
        lock (_lock)
        {
            // The running job's file is held too: it is being written (Windows refuses the delete then; Linux would not).
            held = new HashSet<string>(_uploads, StringComparer.OrdinalIgnoreCase);
            foreach (var job in _running is { } running ? _finished.Append(running) : _finished)
            {
                if (job.DownloadFile is { } file)
                {
                    held.Add(Path.GetFullPath(file));
                }
            }
        }

        foreach (var file in files.Where(f => !held.Contains(f)))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "The data job file {File} left in tmp/ couldn't be deleted; the next sweep tries again", Path.GetFileName(file));
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<AdminJob> all;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SignalIdleLocked();
            all = [.. _finished];
            if (_running is { } running)
            {
                all.Add(running);
            }
        }

        _expiry.Dispose();
        _stopping.Cancel();
        foreach (var job in all)
        {
            job.DeleteFile(_logger);
        }

        _stopping.Dispose();
    }

    // A failure after the write committed: the changes stay, and the snapshot taken before them
    // undoes them, the deletions the job made too; deletions made on this server since stay deleted (the rule
    // since the 0.3.19 verification).
    private static string AfterCommit(AdminJob job, string reason)
        => "The job's changes were written, but a step after them failed: " + reason + " "
            + (job.SafetySnapshot is { } snapshot ? "Restore " + snapshot : "Restore the snapshot taken before them")
            + " to undo them, the deletions they made too; deletions made on this server since then stay deleted.";

    private void ThrowIfBusyLocked()
    {
        if (_running is { } other)
        {
            throw new PluginException(
                PluginErrorKind.Conflict,
                string.Create(CultureInfo.InvariantCulture, $"Another data job is running ({other.Kind}{(other.DryRun ? ", a dry run" : string.Empty)}, started {other.StartedUtc:u}). Wait for it to finish, or cancel it."))
            {
                Code = BusyCode
            };
        }

        if (_held is { } held)
        {
            throw new PluginException(PluginErrorKind.Conflict, "A " + held + " is running. Wait for it to finish.") { Code = BusyCode };
        }
    }

    private void ReleaseHold()
    {
        lock (_lock)
        {
            _held = null;
            SignalIdleLocked();
        }
    }

    // Wakes HoldWhenIdleAsync's waiters (their continuations run asynchronously, never under the lock).
    private void SignalIdleLocked()
    {
        var idle = _idle;
        _idle = null;
        idle?.TrySetResult();
    }

    private void ExpireFilesQuietly()
    {
        try
        {
            ExpireFiles();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The export files' expiry failed; the next minute tries again");
        }
    }

    private AdminJob Find(Guid id)
    {
        lock (_lock)
        {
            if (_running is { } running && running.Id == id)
            {
                return running;
            }

            return _finished.FirstOrDefault(j => j.Id == id) ?? throw PluginException.NotFound(NotFoundMessage);
        }
    }

    private async Task RunAsync(AdminJob job, Func<AdminJob, CancellationToken, Task<object?>> work)
    {
        job.MarkRunning();
        try
        {
            var report = await work(job, job.Token).ConfigureAwait(false);
            job.Finish(AdminJob.Succeeded, report, null, _clock.GetUtcNow().UtcDateTime);
        }
        catch (OperationCanceledException) when (job.Token.IsCancellationRequested)
        {
            job.Finish(AdminJob.Cancelled, null, null, _clock.GetUtcNow().UtcDateTime, message: "Cancelled. Nothing was written.");
        }
        catch (PluginException ex)
        {
            job.Finish(AdminJob.Failed, null, ex, _clock.GetUtcNow().UtcDateTime, message: job.Committed ? AfterCommit(job, ex.Message) : null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The {Kind} data job {JobId} failed", job.Kind, job.Id);
            job.Finish(AdminJob.Failed, null, null, _clock.GetUtcNow().UtcDateTime, message: job.Committed ? AfterCommit(job, "The server log has the details.") : "The job failed. The server log has the details; nothing of it was written.");
        }
        finally
        {
            // Only a succeeded export's file waits for its download.
            if (job.State != AdminJob.Succeeded)
            {
                job.DeleteFile(_logger);
            }

            List<AdminJob> evicted = [];
            lock (_lock)
            {
                if (ReferenceEquals(_running, job))
                {
                    _running = null;
                    SignalIdleLocked();
                }

                _finished.AddFirst(job);
                while (_finished.Count > KeepFinished)
                {
                    evicted.Add(_finished.Last!.Value);
                    _finished.RemoveLast();
                }
            }

            foreach (var old in evicted)
            {
                if (old.DeleteFile(_logger) == AdminJob.FileDeletion.Pending)
                {
                    lock (_lock)
                    {
                        _pendingFiles.Add(old);
                    }
                }
            }

            job.DisposeToken();
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                release();
            }
        }
    }
}
