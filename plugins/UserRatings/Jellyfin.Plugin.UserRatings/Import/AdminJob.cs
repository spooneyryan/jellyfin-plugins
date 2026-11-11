using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// One job of the <see cref="AdminJobRunner"/>: its state, phase and progress, its cancellation until the write phase, and an
/// export's file.
/// </summary>
internal sealed class AdminJob
{
    /// <summary>The state before it runs.</summary>
    public const string Queued = "Queued";

    /// <summary>The state while it runs.</summary>
    public const string Running = "Running";

    /// <summary>The state of a job that finished with its report.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>The state of a job that failed.</summary>
    public const string Failed = "Failed";

    /// <summary>The state of a job cancelled before its write phase.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>The write phase's name.</summary>
    public const string WritePhase = "Write";

    private readonly object _lock = new();
    private readonly CancellationTokenSource _cancel;
    private string _state = Queued;
    private string? _phase;
    private bool _writing;
    private bool _committed;
    private string? _safetySnapshot;
    private int _processed;
    private int _total;
    private DateTime? _finishedUtc;
    private object? _report;
    private string? _error;
    private string? _errorCode;
    private int? _errorStatus;
    private string? _file;
    private bool _fileWithdrawn;

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminJob"/> class.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="dryRun">Whether it writes nothing.</param>
    /// <param name="startedUtc">When it started.</param>
    /// <param name="cancel">Its cancellation, linked to the runner's stop.</param>
    public AdminJob(Guid id, string kind, bool dryRun, DateTime startedUtc, CancellationTokenSource cancel)
    {
        Id = id;
        Kind = kind;
        DryRun = dryRun;
        StartedUtc = startedUtc;
        _cancel = cancel;
        Token = cancel.Token;
    }

    /// <summary>What <see cref="DeleteFile"/> did.</summary>
    public enum FileDeletion
    {
        /// <summary>The job had no file (any more).</summary>
        None,

        /// <summary>The file is gone.</summary>
        Deleted,

        /// <summary>The file couldn't be deleted (on Windows, a handle opened without delete sharing): it is no longer offered,
        /// and the runner's per-minute expiry tries again.</summary>
        Pending
    }

    /// <summary>Gets the id.</summary>
    public Guid Id { get; }

    /// <summary>Gets the kind.</summary>
    public string Kind { get; }

    /// <summary>Gets a value indicating whether it writes nothing.</summary>
    public bool DryRun { get; }

    /// <summary>Gets when it started.</summary>
    public DateTime StartedUtc { get; }

    /// <summary>Gets its cancellation.</summary>
    public CancellationToken Token { get; }

    /// <summary>Gets or sets its task (set once, by the runner).</summary>
    public Task? Task { get; set; }

    /// <summary>Gets the state.</summary>
    public string State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets the export's file while it is offered for download: null once it was withdrawn (deleted, or its delete
    /// pending).</summary>
    public string? DownloadFile
    {
        get
        {
            lock (_lock)
            {
                return _fileWithdrawn ? null : _file;
            }
        }
    }

    /// <summary>Gets a value indicating whether the file was withdrawn but couldn't be deleted yet: the per-minute expiry tries again (found while
    /// verifying 0.3.19).</summary>
    public bool FileDeletePending
    {
        get
        {
            lock (_lock)
            {
                return _fileWithdrawn && _file is not null;
            }
        }
    }

    /// <summary>Gets or sets an export's file (in the plugin's <c>tmp/</c>): deleted when the job fails or is cancelled, an
    /// hour after it succeeded (<see cref="AdminJobRunner.FileLifetime"/>), on <c>DELETE Jobs/{id}</c>, when it leaves the list,
    /// and when the runner stops; null once deleted, and only then (a delete that failed keeps it, withdrawn, for the next
    /// try).</summary>
    public string? File
    {
        get
        {
            lock (_lock)
            {
                return _file;
            }
        }

        set
        {
            lock (_lock)
            {
                _file = value;
            }
        }
    }

    /// <summary>Gets when it finished, or null.</summary>
    public DateTime? FinishedUtc
    {
        get
        {
            lock (_lock)
            {
                return _finishedUtc;
            }
        }
    }

    /// <summary>Gets a value indicating whether the job's write committed.</summary>
    public bool Committed
    {
        get
        {
            lock (_lock)
            {
                return _committed;
            }
        }
    }

    /// <summary>Gets the snapshot taken before the job's write, once it committed.</summary>
    public string? SafetySnapshot
    {
        get
        {
            lock (_lock)
            {
                return _safetySnapshot;
            }
        }
    }

    /// <summary>Gets when a succeeded export's file is deleted, or null when there is none.</summary>
    public DateTime? FileExpiresUtc
    {
        get
        {
            lock (_lock)
            {
                return ExpiresUtc();
            }
        }
    }

    /// <summary>
    /// Reports the phase and its progress.
    /// </summary>
    /// <param name="phase">The phase.</param>
    /// <param name="processed">Rows done.</param>
    /// <param name="total">Rows in all.</param>
    public void Progress(string phase, int processed, int total)
    {
        lock (_lock)
        {
            _phase = phase;
            _processed = processed;
            _total = total;
        }
    }

    /// <summary>
    /// Enters the write phase: a cancel asked for before it throws here, and from here on Cancel is refused until the job
    /// finishes. The work then writes with <see cref="CancellationToken.None"/>.
    /// </summary>
    /// <param name="total">The rows to write.</param>
    /// <exception cref="OperationCanceledException">Cancelled before.</exception>
    public void EnterWritePhase(int total)
    {
        lock (_lock)
        {
            Token.ThrowIfCancellationRequested();
            _writing = true;
            _phase = WritePhase;
            _processed = 0;
            _total = total;
        }
    }

    /// <summary>
    /// Leaves the write phase once the work committed or rolled back (an archive step after it can be cancelled again).
    /// </summary>
    public void LeaveWritePhase()
    {
        lock (_lock)
        {
            _writing = false;
        }
    }

    /// <summary>
    /// Records that the job's write committed: from here on it can't be cancelled, and a
    /// failure says the changes were written and which snapshot undoes them, never that nothing was.
    /// </summary>
    /// <param name="safetySnapshot">The snapshot taken before the write, or null.</param>
    public void MarkCommitted(string? safetySnapshot)
    {
        lock (_lock)
        {
            _committed = true;
            _safetySnapshot = safetySnapshot;
        }
    }

    /// <summary>
    /// Asks the job to stop unless it is writing, committed or finished.
    /// </summary>
    /// <param name="writing">Whether it was refused for being in its write phase.</param>
    /// <returns>Whether the cancel was asked for.</returns>
    public bool TryCancel(out bool writing)
    {
        lock (_lock)
        {
            writing = (_writing || _committed) && _finishedUtc is null;
            if (_finishedUtc is not null || _writing || _committed)
            {
                return false;
            }

            _cancel.Cancel();
            return true;
        }
    }

    /// <summary>Marks it running.</summary>
    public void MarkRunning()
    {
        lock (_lock)
        {
            _state = Running;
        }
    }

    /// <summary>
    /// Marks it finished.
    /// </summary>
    /// <param name="state">The final state.</param>
    /// <param name="report">The report, for a job that succeeded.</param>
    /// <param name="error">The error it failed with, if any.</param>
    /// <param name="utc">When.</param>
    /// <param name="message">The words, in place of the error's own.</param>
    public void Finish(string state, object? report, PluginException? error, DateTime utc, string? message = null)
    {
        lock (_lock)
        {
            _state = state;
            _writing = false;
            _phase = "Done";
            _finishedUtc = utc;
            _report = report;
            _error = message ?? error?.Message;
            _errorCode = error?.Code;
            _errorStatus = error is null ? null : KitProblemDetails.GetStatusCode(error.Kind);
        }
    }

    /// <summary>Releases the cancellation once the job finished.</summary>
    public void DisposeToken() => _cancel.Dispose();

    /// <summary>
    /// Withdraws the export's file, if any, and deletes it: it is offered no more from here on. The job forgets the file only
    /// once it is gone (found while verifying 0.3.19: it was forgotten first, and a delete that failed was reported done);
    /// one that can't be deleted now stays <see cref="FileDeletePending"/>, for the runner's per-minute expiry to try again.
    /// </summary>
    /// <param name="logger">Where a failure is logged.</param>
    /// <returns>Whether there was a file, and whether it is gone.</returns>
    public FileDeletion DeleteFile(ILogger logger)
    {
        string? file;
        bool retry;
        lock (_lock)
        {
            file = _file;
            retry = _fileWithdrawn;
            _fileWithdrawn = true;
        }

        if (file is null)
        {
            return FileDeletion.None;
        }

        try
        {
            System.IO.File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (retry)
            {
                logger.LogDebug(ex, "The export file of data job {JobId} still couldn't be deleted; the next minute tries again", Id);
            }
            else
            {
                logger.LogWarning(ex, "The export file of data job {JobId} couldn't be deleted (it is still open); it is no longer offered, and is tried again every minute", Id);
            }

            return FileDeletion.Pending;
        }

        lock (_lock)
        {
            if (string.Equals(_file, file, StringComparison.Ordinal))
            {
                _file = null;
            }
        }

        return FileDeletion.Deleted;
    }

    /// <summary>
    /// Gets the job's answer.
    /// </summary>
    /// <returns>The DTO.</returns>
    public UserRatingsJobDto ToDto()
    {
        lock (_lock)
        {
            return new UserRatingsJobDto
            {
                Id = Id.ToString("N"),
                Kind = Kind,
                DryRun = DryRun,
                State = _state,
                Phase = _phase,
                Writing = _writing,
                Processed = _processed,
                Total = _total,
                StartedUtc = StartedUtc,
                FinishedUtc = _finishedUtc,
                Report = _report,
                Error = _error,
                ErrorCode = _errorCode,
                ErrorStatus = _errorStatus,
                HasFile = _state == Succeeded && _file is not null && !_fileWithdrawn,
                FileExpiresUtc = ExpiresUtc()
            };
        }
    }

    private DateTime? ExpiresUtc()
        => _state == Succeeded && _file is not null && !_fileWithdrawn && _finishedUtc is { } finished ? finished + AdminJobRunner.FileLifetime : null;
}
