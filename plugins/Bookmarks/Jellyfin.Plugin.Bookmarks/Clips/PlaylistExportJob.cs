using System;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.Bookmarks.Api.Models;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// One playlist export (since 1.5.0 / 2.3.0), kept in memory by <see cref="PlaylistExportService"/>: its plan, where it is,
/// and, while it runs, its ffmpeg job (progress and cancellation, as a clip job's).
/// </summary>
internal sealed class PlaylistExportJob
{
    private readonly Lock _lock = new();
    private BookmarkPlaylistExportState _state = BookmarkPlaylistExportState.Queued;
    private string? _error;
    private string _fileName;
    private long? _fileSize;
    private DateTime? _completedAt;
    private RunningClip? _running;
    private bool _cancelRequested;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistExportJob"/> class.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="requestedAt">When it was asked for.</param>
    public PlaylistExportJob(PlaylistExportPlan plan, DateTime requestedAt)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
        RequestedAt = requestedAt;
        _fileName = Path.GetFileName(plan.OutputPath);
    }

    /// <summary>Gets the export's id.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>Gets the plan.</summary>
    public PlaylistExportPlan Plan { get; }

    /// <summary>Gets the user it is for.</summary>
    public Guid UserId => Plan.UserId;

    /// <summary>Gets when it was asked for.</summary>
    public DateTime RequestedAt { get; }

    /// <summary>Gets where it is.</summary>
    public BookmarkPlaylistExportState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Gets a value indicating whether it waits or runs.</summary>
    public bool IsActive => State is BookmarkPlaylistExportState.Queued or BookmarkPlaylistExportState.Running;

    /// <summary>Gets a value indicating whether a cancellation was asked for.</summary>
    public bool CancelRequested
    {
        get
        {
            lock (_lock)
            {
                return _cancelRequested;
            }
        }
    }

    /// <summary>
    /// Claims a waiting job for the worker, with its ffmpeg job.
    /// </summary>
    /// <param name="running">The ffmpeg job.</param>
    /// <returns>Whether it was waiting (not cancelled meanwhile).</returns>
    public bool TryStart(RunningClip running)
    {
        lock (_lock)
        {
            if (_state != BookmarkPlaylistExportState.Queued || _cancelRequested)
            {
                return false;
            }

            _state = BookmarkPlaylistExportState.Running;
            _running = running;
            return true;
        }
    }

    /// <summary>
    /// Asks for its cancellation: a waiting job is cancelled at once, a running one is stopped (the worker then records it).
    /// </summary>
    public void RequestCancel()
    {
        RunningClip? running;
        lock (_lock)
        {
            if (_state is not (BookmarkPlaylistExportState.Queued or BookmarkPlaylistExportState.Running))
            {
                return;
            }

            _cancelRequested = true;
            if (_state == BookmarkPlaylistExportState.Queued)
            {
                _state = BookmarkPlaylistExportState.Cancelled;
                _completedAt = DateTime.UtcNow;
                return;
            }

            running = _running;
        }

        running?.Cancel();
    }

    /// <summary>
    /// Records its end.
    /// </summary>
    /// <param name="state">Completed, Failed or Cancelled.</param>
    /// <param name="error">Why it failed.</param>
    /// <param name="fileName">The file's name, once made.</param>
    /// <param name="fileSize">The file's size, once made.</param>
    public void Finish(BookmarkPlaylistExportState state, string? error = null, string? fileName = null, long? fileSize = null)
    {
        lock (_lock)
        {
            _state = state;
            _error = error;
            _fileName = fileName ?? _fileName;
            _fileSize = fileSize;
            _completedAt = DateTime.UtcNow;
            _running = null;
        }
    }

    /// <summary>
    /// Gets the export as the API answers it.
    /// </summary>
    /// <returns>The DTO.</returns>
    public BookmarkPlaylistExportJobDto ToDto()
    {
        lock (_lock)
        {
            return new BookmarkPlaylistExportJobDto
            {
                Id = Id,
                PlaylistId = Plan.PlaylistId,
                PlaylistName = Plan.PlaylistName,
                State = _state,
                Progress = _state == BookmarkPlaylistExportState.Running && _running is { } running ? Math.Round(running.Progress * 100, 1) : null,
                Error = _error,
                FileName = _fileName,
                FileSize = _fileSize,
                VideoCount = Plan.Items.Count,
                DurationTicks = Plan.Duration.Ticks,
                EstimatedBytes = Plan.EstimatedBytes,
                RequestedAt = RequestedAt,
                CompletedAt = _completedAt
            };
        }
    }
}
