using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The clip worker's clean-up at startup: it runs once in <see cref="ClipExtractionService"/>, after the startup gate
/// and before interrupted jobs are resumed, when no clip job can be running. It deletes the stale <c>.partial</c> files
/// clip jobs of an earlier run left behind, then retries the old clip files an automatic deletion couldn't delete
/// (<c>PendingFileDeletes</c>, through <see cref="ClipFileOps.RetryPendingDeletesAsync"/>).
/// </summary>
/// <remarks>
/// <para><b>Stale <c>.partial</c> files</b> (a server that stops in the middle of a clip leaves
/// <c>&lt;name&gt;.partial</c>, so the resumed job wrote <c>&lt;name&gt; (2)</c> and the stale file stayed for good). In every
/// recorded clip folder that is there (<c>ClipRoots</c>), the files in it and in the folders directly inside it
/// (the per-user folders; never deeper, never through a junction, symbolic link or mount point) whose names are a clip
/// being written (<see cref="ClipNamePattern"/>) and that were last written before this server run started
/// (<see cref="StartedUtc"/>) are deleted, through <see cref="ClipFileOps.DeleteStalePartialAsync"/> and its guard. Other programs' <c>*.partial</c> files, a
/// finished clip, and a file written since the start (another server's job, an ffmpeg an earlier run left running) are
/// never touched; a clip folder that isn't there (an unplugged drive) is left alone; a file that can't be deleted waits for
/// the next start. The count is logged at Information; paths only at Debug (a clip file's name holds its bookmark's name
/// or note). The setting <c>CleanUpPartialFilesOnStartup</c> turns this part off; the retry of
/// <c>PendingFileDeletes</c> still runs.</para>
/// <para>While the bookmark data is read-only it stays idle (<see cref="BackgroundWriteGate"/>,
/// <see cref="BackgroundWriter.PartialFileJanitor"/>): no file is deleted. A failure is logged and never
/// stops the worker.</para>
/// </remarks>
internal sealed class PartialFileJanitor
{
    private readonly ClipFileOps _files;
    private readonly IBookmarkStore _store;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly BackgroundWriteGate _gate;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PartialFileJanitor"/> class, for this server run: it is created with the
    /// clip worker that takes it, when the server starts, before any clip job of the run can run.
    /// </summary>
    /// <param name="files">The clip file operations.</param>
    /// <param name="store">The bookmark store, for its clip folders.</param>
    /// <param name="configurationProvider">The plugin configuration (<c>CleanUpPartialFilesOnStartup</c>).</param>
    /// <param name="gate">Keeps the janitor idle while the bookmark data is read-only.</param>
    /// <param name="logger">The logger.</param>
    public PartialFileJanitor(
        ClipFileOps files,
        IBookmarkStore store,
        IPluginConfigurationProvider configurationProvider,
        BackgroundWriteGate gate,
        ILogger<PartialFileJanitor> logger)
        : this(files, store, configurationProvider, gate, logger, DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PartialFileJanitor"/> class with a given start time (tests).
    /// </summary>
    /// <param name="files">The clip file operations.</param>
    /// <param name="store">The bookmark store, for its clip folders.</param>
    /// <param name="configurationProvider">The plugin configuration.</param>
    /// <param name="gate">Keeps the janitor idle while the bookmark data is read-only.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="startedUtc">When this server run started: a <c>.partial</c> file last written before it is stale.</param>
    internal PartialFileJanitor(
        ClipFileOps files,
        IBookmarkStore store,
        IPluginConfigurationProvider configurationProvider,
        BackgroundWriteGate gate,
        ILogger logger,
        DateTime startedUtc)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configurationProvider);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(logger);
        _files = files;
        _store = store;
        _configurationProvider = configurationProvider;
        _gate = gate;
        _logger = logger;
        StartedUtc = startedUtc.ToUniversalTime();
    }

    /// <summary>
    /// Gets when this server run started (UTC), which is when the janitor was created: a <c>.partial</c> file last written
    /// before it was left by an earlier run, since no clip job of this run starts before the janitor has run.
    /// </summary>
    /// <remarks>
    /// Not the process's start: Jellyfin 10.11 restarts in the same process (the dashboard's Restart, a plugin install:
    /// <c>Jellyfin.Server/Program.cs</c> runs the server again while <c>_restartOnShutdown</c>), building a new service
    /// provider, so the janitor of the new run is a new one, created at its start; the process's own start would keep
    /// an earlier run's leftovers until the process itself restarts. After a crash both times are the same run's.
    /// </remarks>
    public DateTime StartedUtc { get; }

    /// <summary>
    /// Runs the startup clean-up: the clip file renames a stop left half done, the stale <c>.partial</c> files,
    /// then the files waiting to be deleted.
    /// </summary>
    /// <param name="cancellationToken">Stops it (server shutdown).</param>
    /// <returns>What it did.</returns>
    public async Task<JanitorRun> RunAtStartupAsync(CancellationToken cancellationToken)
    {
        if (!_gate.MayWrite(BackgroundWriter.PartialFileJanitor))
        {
            return JanitorRun.Idle;
        }

        // First: a record naming the path a rename's file had left (the server stopped between the move and the record's
        // write) names the file again before anything else judges it (the sweep, a remake's clean-up, a user's Forget).
        var renames = ClipRenameRecovery.None;
        try
        {
            renames = await _files.FinishRenamesAsync(BackgroundWriter.PartialFileJanitor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected: the journal keeps them, and the daily maintenance or the next start tries again.
            _logger.LogError(ex, "Could not finish the clip file renames that weren't finished before the last shutdown");
        }

        // Then a record a restore left naming the path its clip file left in a rename names the file again, before
        // the stale .partial clean-up and the sweep judge it.
        var relinked = 0;
        try
        {
            relinked = await _files.RelinkRenamedAsync(BackgroundWriter.PartialFileJanitor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not name restored clips' renamed files again");
        }

        StalePartialCleanup? partials = null;
        try
        {
            partials = await CleanUpStalePartialsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected (each folder's and each file's failures are handled on their own): the files stay for the next
            // start, and the rest of the start goes on.
            _logger.LogError(ex, "Could not clean up the unfinished clip files (.partial) of clip jobs from before the last shutdown");
        }

        PendingFileDeleteRetry pending;
        try
        {
            pending = await _files.RetryPendingDeletesAsync(BackgroundWriter.PartialFileJanitor, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The store couldn't be written (a 503), or anything else: the files stay listed for the next start, and the
            // worker still resumes its jobs.
            _logger.LogError(ex, "Could not retry the clip files waiting to be deleted");
            pending = PendingFileDeleteRetry.None;
        }

        return new JanitorRun(partials, pending) { Renames = renames, Relinked = relinked };
    }

    // Deletes the stale .partial files in the recorded clip folders, unless CleanUpPartialFilesOnStartup is off
    // (null then).
    private async Task<StalePartialCleanup?> CleanUpStalePartialsAsync(CancellationToken cancellationToken)
    {
        if (!_configurationProvider.GetConfiguration().CleanUpPartialFilesOnStartup)
        {
            _logger.LogDebug("Unfinished clip files (.partial) are left alone at startup: CleanUpPartialFilesOnStartup is off");
            return null;
        }

        var seen = new HashSet<string>(ClipRootPaths.Comparer);
        int deleted = 0, notDeleted = 0, newer = 0, folders = 0, notThere = 0;
        foreach (var root in RecordedRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_files.FolderExists(root))
            {
                // An unplugged drive, a folder moved or removed: its files are left for a start that finds it.
                notThere++;
                _logger.LogDebug("Clip folder {Folder} isn't there; its unfinished clip files are left for a later start", root);
                continue;
            }

            List<ClipFolderFile> candidates;
            try
            {
                // The whole folder is read before anything in it is deleted; only names of clips being written are kept.
                candidates = _files.ListClipFolder(root).Where(f => ClipNamePattern.IsPartialFileName(Path.GetFileName(f.Path))).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // The message may name a per-user folder or a file: at Debug only.
                notThere++;
                _logger.LogWarning("Could not look for unfinished clip files in a clip folder ({Error}); it is looked at again at the next start", ex.GetType().Name);
                _logger.LogDebug(ex, "Could not list {Folder}", root);
                continue;
            }

            folders++;
            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(file.Path))
                {
                    // Under two recorded clip folders, one inside the other.
                    continue;
                }

                if (file.LastWriteTimeUtc >= StartedUtc)
                {
                    // Not left by an earlier run: another program's clip job, or an ffmpeg an earlier run left running.
                    newer++;
                    _logger.LogDebug("Kept {Path}: last written {Time:o}, since the server started ({Started:o})", file.Path, file.LastWriteTimeUtc, StartedUtc);
                    continue;
                }

                var result = await _files.DeleteStalePartialAsync(file.Path, cancellationToken).ConfigureAwait(false);
                switch (result.Outcome)
                {
                    case ClipFileOutcome.Done:
                        deleted++;
                        _logger.LogDebug("Deleted the unfinished clip file {Path}", file.Path);
                        break;
                    case ClipFileOutcome.Missing:
                        // Gone since the folder was listed.
                        break;
                    default:
                        // In use, refused or failed: ClipFileOps logged why; the next start tries again.
                        notDeleted++;
                        break;
                }
            }
        }

        var cleanup = new StalePartialCleanup(deleted, notDeleted, newer, folders, notThere);
        _logger.LogInformation(
            "Cleaned up the unfinished clip files (.partial) of clip jobs from before the last shutdown: {Deleted} deleted, {NotDeleted} could not be deleted, {Newer} written since the server started kept, in {Folders} clip folders ({NotThere} not there)",
            cleanup.Deleted,
            cleanup.NotDeleted,
            cleanup.Newer,
            cleanup.Folders,
            cleanup.FoldersNotThere);
        return cleanup;
    }

    // The recorded clip folders, normalised and each once.
    private List<string> RecordedRoots()
    {
        var roots = new List<string>();
        foreach (var candidate in _store.ClipRoots)
        {
            if (ClipRootPaths.TryNormalize(candidate, out var root) && !roots.Contains(root, ClipRootPaths.Comparer))
            {
                roots.Add(root);
            }
        }

        return roots;
    }
}
