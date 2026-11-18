using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Playlist exports (since 1.5.0 / 2.3.0, <see cref="IBookmarkPlaylistExport"/>): a background worker that joins a
/// playlist's videos into one MP4 in the clip folder, one export at a time, beside the clip jobs.
/// </summary>
/// <remarks>
/// <para><b>Clip rules.</b> An export follows a clip job's: the same permission (<c>EnableClips</c>, and the user may
/// download media), the same ffmpeg, encoder settings and clip folder (the user's folder in it); the free-space guard checks
/// it when asked for and when it starts, counts it with the clip jobs (<see cref="ClipStorageGuard.OtherReservations"/>) and
/// stops it when the drive is almost full; the clip folder must be there and be its clips' own (the marker), is recorded
/// and gets its marker (<see cref="ClipExtractionService.PrepareClipFolderAsync"/>, under <see cref="ClipFileOps.NameGate"/>);
/// the file is written as a <c>.partial</c> (<see cref="PlaylistExportNaming"/>), moved into place through the clip file
/// guard, never over a file, under a name chosen then; a failure or cancellation deletes the <c>.partial</c>.</para>
/// <para><b>Not a clip.</b> An export has no bookmark record: the clip lists, the missing-clip sweep, the renames, the
/// pending deletes and the clip artwork's clean-up work from records and from names Bookmarks gives clips, so they never
/// list, mark, move or delete an export or its poster. It is the user's file in the clip folder, theirs to delete.</para>
/// <para><b>Kept in memory.</b> Exports wait and run in memory: a restart ends them (the <c>.partial</c> of one that ran is
/// deleted by the janitor at the next start, as a clip's), and the list of finished ones (at most
/// <see cref="KeptFinished"/> a user) starts empty. Its log lines carry ids, counts, lengths and sizes; names and paths
/// (a playlist's name is the file's) at Debug only.</para>
/// </remarks>
internal sealed class PlaylistExportService : IBookmarkPlaylistExport, IDisposable
{
    /// <summary>The most exports a user may have waiting or running.</summary>
    public const int MaxActivePerUser = 3;

    /// <summary>The finished exports kept per user for the lists.</summary>
    public const int KeptFinished = 10;

    /// <summary>The error of an export asked for while the bookmark data is read-only.</summary>
    public const string ReadOnlyMessage = "The bookmark data is read-only right now, so nothing can be exported. Ask your server admin.";

    private readonly PlaylistExportPlanner _planner;
    private readonly IFfmpegRunner _runner;
    private readonly ClipFileOps _files;
    private readonly ClipStorageGuard _guard;
    private readonly ClipExtractionService _clips;
    private readonly IBookmarkStore _store;
    private readonly BackgroundWriteGate _gate;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly ILogger _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, PlaylistExportJob> _jobs = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _workerLock = new();
    private Task? _worker;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistExportService"/> class.
    /// </summary>
    /// <param name="planner">Works out exports.</param>
    /// <param name="runner">Runs ffmpeg.</param>
    /// <param name="files">The clip file operations.</param>
    /// <param name="guard">The free-space guard.</param>
    /// <param name="clips">The clip worker, for its clip folder rules.</param>
    /// <param name="store">The bookmark store (its clip folders).</param>
    /// <param name="gate">Keeps exports from starting while the bookmark data is read-only.</param>
    /// <param name="configurationProvider">The plugin configuration.</param>
    /// <param name="logger">The logger.</param>
    public PlaylistExportService(
        PlaylistExportPlanner planner,
        IFfmpegRunner runner,
        ClipFileOps files,
        ClipStorageGuard guard,
        ClipExtractionService clips,
        IBookmarkStore store,
        BackgroundWriteGate gate,
        IPluginConfigurationProvider configurationProvider,
        ILogger<PlaylistExportService> logger)
    {
        ArgumentNullException.ThrowIfNull(guard);
        _planner = planner;
        _runner = runner;
        _files = files;
        _guard = guard;
        _clips = clips;
        _store = store;
        _gate = gate;
        _configurationProvider = configurationProvider;
        _logger = logger;

        // The exports waiting or running count with the clip jobs in every free-space check.
        guard.OtherReservations = Reserved;
    }

    /// <inheritdoc />
    public async Task<BookmarkPlaylistExportEstimateDto> EstimateAsync(User user, Guid playlistId, bool isAdmin, CancellationToken cancellationToken)
    {
        EnsureCanExport(user);
        var plan = PlanFor(user, playlistId);
        string? problem = plan.Problem;
        if (problem is null)
        {
            var space = await _guard.CheckBeforeQueueAsync(plan.OutputFolder, plan.EstimatedBytes, Guid.Empty, cancellationToken).ConfigureAwait(false);
            if (space.Refused)
            {
                problem = isAdmin ? space.AdminMessage : space.UserMessage;
            }
        }

        if (problem is null && _store.IsReadOnly)
        {
            problem = ReadOnlyMessage;
        }

        return new BookmarkPlaylistExportEstimateDto
        {
            PlaylistId = plan.PlaylistId,
            PlaylistName = plan.PlaylistName,
            VideoCount = plan.Items.Count,
            Skipped = plan.Skipped.Select(s => new BookmarkPlaylistExportSkippedDto { Name = s.Name, Reason = s.Reason }).ToList(),
            DurationTicks = plan.Duration.Ticks,
            Width = plan.Format.Width,
            Height = plan.Format.Height,
            FrameRate = Math.Round(plan.Format.FrameRate, 3),
            EstimatedBytes = plan.EstimatedBytes,
            EstimatedSeconds = plan.EstimatedTime.TotalSeconds,
            FileName = Path.GetFileName(plan.OutputPath),
            IsLarge = plan.IsLarge,
            CanExport = problem is null,
            Problem = problem
        };
    }

    /// <inheritdoc />
    public async Task<BookmarkPlaylistExportJobDto> StartAsync(User user, Guid playlistId, bool isAdmin, CancellationToken cancellationToken)
    {
        EnsureCanExport(user);
        if (_store.IsReadOnly || !_gate.MayWrite(BackgroundWriter.ClipWorker))
        {
            throw new BookmarkException(PluginErrorKind.Unavailable, ReadOnlyMessage);
        }

        var plan = PlanFor(user, playlistId);
        if (plan.Problem is not null)
        {
            throw BookmarkException.Invalid(plan.Problem);
        }

        var active = _jobs.Values.Where(j => j.UserId.Equals(user.Id) && j.IsActive).ToList();
        if (active.Any(j => j.Plan.PlaylistId.Equals(plan.PlaylistId)))
        {
            throw BookmarkException.Conflict("This playlist is being exported already; see its progress on the My Clips page.");
        }

        if (active.Count >= MaxActivePerUser)
        {
            throw BookmarkException.Conflict(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"You have {active.Count} exports waiting or being made; start another once one is done."));
        }

        var space = await _guard.CheckBeforeQueueAsync(plan.OutputFolder, plan.EstimatedBytes, Guid.Empty, cancellationToken).ConfigureAwait(false);
        if (space.Refused)
        {
            throw new BookmarkException(PluginErrorKind.Unavailable, isAdmin ? space.AdminMessage : space.UserMessage);
        }

        var job = new PlaylistExportJob(plan, DateTime.UtcNow);
        _jobs[job.Id] = job;
        Prune(user.Id);
        _queue.Writer.TryWrite(job.Id);
        EnsureWorker();
        _logger.LogInformation(
            "Queued playlist export {ExportId} of playlist {PlaylistId} for user {UserId}: {Videos} videos ({Skipped} left out), {Duration} long, about {Bytes} bytes",
            job.Id,
            plan.PlaylistId,
            user.Id,
            plan.Items.Count,
            plan.Skipped.Count,
            plan.Duration,
            plan.EstimatedBytes);
        return job.ToDto();
    }

    /// <inheritdoc />
    public IReadOnlyList<BookmarkPlaylistExportJobDto> GetExports(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _jobs.Values
            .Where(j => j.UserId.Equals(user.Id))
            .OrderByDescending(j => j.RequestedAt)
            .Select(j => j.ToDto())
            .ToList();
    }

    /// <inheritdoc />
    public BookmarkPlaylistExportJobDto GetExport(User user, Guid exportId) => OwnJob(user, exportId).ToDto();

    /// <inheritdoc />
    public BookmarkPlaylistExportJobDto Cancel(User user, Guid exportId)
    {
        var job = OwnJob(user, exportId);
        job.RequestCancel();
        return job.ToDto();
    }

    /// <summary>
    /// Runs one export now, as the worker does (tests).
    /// </summary>
    /// <param name="exportId">The export.</param>
    /// <param name="stoppingToken">The server's stopping token.</param>
    /// <returns>A task that completes when it ended.</returns>
    internal Task RunExportAsync(Guid exportId, CancellationToken stoppingToken) => ProcessAsync(exportId, stoppingToken);

    /// <summary>
    /// Stops the worker at the server's shutdown: a running export is stopped and its <c>.partial</c> deleted.
    /// </summary>
    public void Dispose()
    {
        Task? worker;
        lock (_workerLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            worker = _worker;
        }

        _stopping.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            // A moment for ffmpeg to be stopped and the .partial deleted; the janitor deletes it at the next start otherwise.
            worker?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "The playlist export worker ended with an error");
        }

        _stopping.Dispose();
    }

    // The worker, started with the first export: the exports one at a time, until the server stops.
    private void EnsureWorker()
    {
        lock (_workerLock)
        {
            if (_disposed || _worker is { IsCompleted: false })
            {
                return;
            }

            var token = _stopping.Token;
            _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var exportId in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(exportId, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Unexpected error in the playlist export worker for export {ExportId}", exportId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: the exports waiting end with the process.
        }
    }

    // What the free-space guard counts besides the clip jobs: the exports waiting or running but the one checked.
    private long Reserved(Guid except)
        => _jobs.Values.Where(j => j.IsActive && !j.Id.Equals(except)).Sum(j => j.Plan.EstimatedBytes);

    private void EnsureCanExport(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (!_configurationProvider.GetConfiguration().EnableClips)
        {
            throw BookmarkException.Forbidden("Clip extraction is turned off on this server.");
        }

        if (!user.HasPermission(PermissionKind.EnableContentDownloading))
        {
            throw BookmarkException.Forbidden("Exporting a playlist requires permission to download media.");
        }
    }

    private PlaylistExportPlan PlanFor(User user, Guid playlistId)
    {
        try
        {
            return _planner.Plan(playlistId, user) ?? throw BookmarkException.NotFound("Playlist not found.");
        }
        catch (ClipExtractionException ex) when (ex.FolderUnreachable)
        {
            throw new BookmarkException(PluginErrorKind.Unavailable, ex.Message);
        }
        catch (ClipExtractionException ex)
        {
            throw new BookmarkException(ex.Message, ex);
        }
    }

    private PlaylistExportJob OwnJob(User user, Guid exportId)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _jobs.TryGetValue(exportId, out var job) && job.UserId.Equals(user.Id)
            ? job
            : throw BookmarkException.NotFound("Export not found.");
    }

    // At most KeptFinished finished exports a user, the newest.
    private void Prune(Guid userId)
    {
        foreach (var old in _jobs.Values
            .Where(j => j.UserId.Equals(userId) && !j.IsActive)
            .OrderByDescending(j => j.RequestedAt)
            .Skip(KeptFinished))
        {
            _jobs.TryRemove(old.Id, out _);
        }
    }

    private async Task ProcessAsync(Guid exportId, CancellationToken stoppingToken)
    {
        if (!_jobs.TryGetValue(exportId, out var job))
        {
            return;
        }

        using var running = new RunningClip(stoppingToken);
        if (!job.TryStart(running))
        {
            return;
        }

        var plan = job.Plan;
        string? tempPath = null;
        FfmpegFilterScriptFile? script = null;
        try
        {
            if (!_gate.MayWrite(BackgroundWriter.ClipWorker))
            {
                throw new ClipExtractionException(ReadOnlyMessage);
            }

            tempPath = await PrepareAsync(job, running.Token).ConfigureAwait(false);
            _logger.LogInformation("Exporting playlist {PlaylistId} ({ExportId}): {Videos} videos, {Duration} long", plan.PlaylistId, job.Id, plan.Items.Count, plan.Duration);
            _logger.LogDebug("Playlist export {ExportId} is written to {Path}", job.Id, tempPath);

            script = await FfmpegFilterScriptFile.WriteAsync(job.Id, FfmpegJoinCommandBuilder.BuildFilterGraph(plan.Items, plan.Format, plan.Encoding), _logger, running.Token).ConfigureAwait(false);
            var arguments = FfmpegJoinCommandBuilder.Build(plan.Items, plan.Format, plan.Encoding, script.Path, tempPath, plan.PlaylistName);

            using (var watch = CancellationTokenSource.CreateLinkedTokenSource(running.Token))
            {
                var watchdog = _guard.WatchAsync(plan.OutputFolder, running, job.Id, watch.Token);
                try
                {
                    await _runner.RunAsync(plan.EncoderPath, arguments, plan.Duration, running, running.Token).ConfigureAwait(false);
                }
                finally
                {
                    await watch.CancelAsync().ConfigureAwait(false);
                    await watchdog.ConfigureAwait(false);
                }
            }

            var (outputPath, moved) = await MoveIntoPlaceAsync(tempPath, plan, job.Id).ConfigureAwait(false);
            if (moved.Outcome != ClipFileOutcome.Done)
            {
                throw ClipStorageGuard.IsDiskFull(moved.HResult)
                    ? new ClipExtractionException("The export couldn't be put in place: the clip drive is full.", new IOException(moved.Error, moved.HResult!.Value))
                    : new ClipExtractionException("The export couldn't be put in place: " + (moved.Error ?? "the move failed."));
            }

            tempPath = null;
            var size = new FileInfo(outputPath).Length;
            WritePoster(plan, outputPath, job.Id);
            job.Finish(BookmarkPlaylistExportState.Completed, fileName: Path.GetFileName(outputPath), fileSize: size);
            _logger.LogInformation("Playlist export {ExportId} written ({Size} bytes)", job.Id, size);
            _logger.LogDebug("Playlist export {ExportId} written to {Path}", job.Id, outputPath);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await DeleteTemporaryAsync(tempPath, job.Id).ConfigureAwait(false);
            job.Finish(BookmarkPlaylistExportState.Cancelled, "The server stopped while the export was made. Export the playlist again.");
            _logger.LogInformation("Playlist export {ExportId} ended by the server's shutdown", job.Id);
        }
        catch (OperationCanceledException) when (running.StopReason == ClipStopReason.LowDiskSpace)
        {
            await DeleteTemporaryAsync(tempPath, job.Id).ConfigureAwait(false);
            job.Finish(BookmarkPlaylistExportState.Failed, ClipStorageGuard.AlmostFullMessage);
        }
        catch (OperationCanceledException)
        {
            await DeleteTemporaryAsync(tempPath, job.Id).ConfigureAwait(false);
            job.Finish(BookmarkPlaylistExportState.Cancelled);
            _logger.LogInformation("Playlist export {ExportId} cancelled", job.Id);
        }
        catch (Exception ex)
        {
            var diskFull = ex is ClipExtractionException { FromStorageGuard: true }
                ? null
                : await _guard.DescribeFailureAsync(ex, plan.OutputFolder, tempPath is not null, job.Id).ConfigureAwait(false);
            await DeleteTemporaryAsync(tempPath, job.Id).ConfigureAwait(false);
            var expected = diskFull is not null || ex is ClipExtractionException or IOException or UnauthorizedAccessException;
            var message = diskFull ?? RedactPaths(ex.Message, plan, tempPath);
            job.Finish(BookmarkPlaylistExportState.Failed, expected ? message : "Unexpected error: " + message);
            if (expected)
            {
                _logger.LogWarning("Playlist export {ExportId} failed: {Error}", job.Id, ClipFiles.RedactForLog(ex.Message, plan.OutputPath, tempPath));
                _logger.LogDebug(ex, "Playlist export {ExportId} failed", job.Id);
            }
            else
            {
                _logger.LogError(ex, "Playlist export {ExportId} failed unexpectedly", job.Id);
            }
        }
        finally
        {
            script?.Dispose();
        }
    }

    // As a clip job starts: the free space again, the clip folder's rules (there, its own, recorded, marked), then the
    // .partial reserved, all under the name gate; then the folder once more. Returns the .partial's path.
    private async Task<string> PrepareAsync(PlaylistExportJob job, CancellationToken cancellationToken)
    {
        var plan = job.Plan;
        await _files.NameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var space = await _guard.CheckAtClaimAsync(plan.OutputFolder, plan.EstimatedBytes, job.Id, cancellationToken).ConfigureAwait(false);
            if (space.Refused)
            {
                throw new ClipExtractionException(space.UserMessage) { FromStorageGuard = true };
            }

            var marked = await _clips.PrepareClipFolderAsync(plan.Root, plan.OutputFolder, job.Id, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(plan.OutputFolder);
            var tempPath = PlaylistExportNaming.BuildTempPath(plan.OutputPath, plan.Duration, job.Id);
            using (new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                // Reserved: the janitor's name for a file being written, this export's own.
            }

            if (marked && ClipRootPaths.TryNormalize(plan.Root, out var root) && _files.CheckRoot(root) != ClipRootState.Marked)
            {
                await DeleteTemporaryAsync(tempPath, job.Id).ConfigureAwait(false);
                throw new ClipExtractionException(ClipExtractionService.ClipFolderAwayMessage);
            }

            return tempPath;
        }
        catch (ClipFolderUnreachableException ex)
        {
            throw new ClipExtractionException(ClipFileOps.FolderUnreachableClipMessage, ex) { FolderUnreachable = true };
        }
        finally
        {
            _files.NameGate.Release();
        }
    }

    // The finished file into place under a name free now (the planned one, or with " (2)" when it was taken meanwhile),
    // chosen and moved into under the name gate, never over a file.
    private async Task<(string Path, ClipFileResult Moved)> MoveIntoPlaceAsync(string tempPath, PlaylistExportPlan plan, Guid exportId)
    {
        await _files.NameGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            string outputPath;
            try
            {
                outputPath = ClipFileNaming.MakeUnique(plan.OutputFolder, PlaylistExportNaming.BuildFileName(plan.PlaylistName), _planner.NameTaken());
            }
            catch (ClipFolderUnreachableException ex)
            {
                throw new ClipExtractionException(ClipFileOps.FolderUnreachableClipMessage, ex) { FolderUnreachable = true };
            }

            return (outputPath, await _files.MoveAsync(tempPath, outputPath, exportId, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            _files.NameGate.Release();
        }
    }

    // The playlist's poster (else its first video's) beside the export as <name>-poster.<ext>, when clips get artwork
    // (GiveClipsVideoArtwork): copied, never over a file. A failure is a Warning; the export stands.
    private void WritePoster(PlaylistExportPlan plan, string outputPath, Guid exportId)
    {
        if (!_configurationProvider.GetConfiguration().GiveClipsVideoArtwork
            || plan.PosterPath is not { } image
            || ClipArtworkNames.ExtensionFor(image) is not { } extension)
        {
            return;
        }

        var target = PlaylistExportNaming.BuildPosterPath(outputPath, extension);
        try
        {
            if (File.Exists(image) && !File.Exists(target))
            {
                File.Copy(image, target, overwrite: false);
                _logger.LogDebug("Gave playlist export {ExportId} its poster {Path}", exportId, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not give playlist export {ExportId} its poster: {Error}", exportId, ex.GetType().Name);
            _logger.LogDebug(ex, "Could not copy {Image} to {Path}", image, target);
        }
    }

    private async Task DeleteTemporaryAsync(string? path, Guid exportId)
    {
        if (path is not null)
        {
            await _files.DeleteTemporaryAsync(path, exportId).ConfigureAwait(false);
        }
    }

    // A failure's message without the server's folders: the export's files and the videos by their names.
    private static string RedactPaths(string message, PlaylistExportPlan plan, string? tempPath)
    {
        var paths = new List<string>();
        if (tempPath is not null)
        {
            paths.Add(tempPath);
        }

        paths.Add(plan.OutputPath);
        paths.AddRange(plan.Items.Select(i => i.InputPath).OrderByDescending(p => p.Length));
        foreach (var path in paths)
        {
            message = message.Replace(path, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        }

        return message.Replace(plan.OutputFolder, "the clip folder", StringComparison.OrdinalIgnoreCase);
    }
}
