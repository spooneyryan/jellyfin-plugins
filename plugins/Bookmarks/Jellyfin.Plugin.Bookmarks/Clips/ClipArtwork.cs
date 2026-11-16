using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A clip's video's artwork beside the clip file (since 1.3.0 / 2.1.0, the setting <c>GiveClipsVideoArtwork</c>): when the
/// clip folder is a Home Videos and Photos library, Jellyfin shows the film's poster and backdrop for the clip instead of a
/// frame. Beside <c>&lt;clip&gt;.mp4</c> it writes <c>&lt;clip&gt;-poster.&lt;ext&gt;</c> from the video's Primary image and
/// <c>&lt;clip&gt;-fanart.&lt;ext&gt;</c> from its first Backdrop; for an episode, the series' Primary image as the poster, the
/// episode's own as <c>&lt;clip&gt;-thumb.&lt;ext&gt;</c>, and the episode's backdrop or else the series'
/// (<see cref="ClipArtworkNames"/>, <see cref="IClipArtworkSource"/>).
/// </summary>
/// <remarks>
/// <para><b>Written</b> only for a clip that is <c>Completed</c>, whose file is there in a recorded clip folder or a folder
/// directly inside it, as the guard sees it (<see cref="ClipFileOps.Check"/>: no link on the way, no rename under way), whose
/// file name is one Bookmarks gives (<see cref="ClipNamePattern"/>), and whose video is still in the library. Only image files
/// stored locally are copied (never a remote URL), in their own format when it is JPEG, PNG or WebP. Each copy is written
/// under a temporary name of its own (<c>&lt;name&gt;.&lt;32 hex digits&gt;.tmp</c>) and then moved over the artwork file of
/// that exact name, the only files ever replaced; a copy whose size and last write time are the image's already is left as it
/// is. A clip whose file is a library item gets an image refresh queued (<see cref="IClipArtworkSource.RefreshClip"/>).</para>
/// <para><b>Deleted</b> only when orphaned: an artwork file named as above (or its temporary file), in a recorded clip
/// folder that is there and recognised by its marker (<see cref="ClipFileOps.IsRecognisedRoot"/>), in the folder itself or a
/// folder directly inside it, never through a link, when neither <c>&lt;clip&gt;.mp4</c> nor <c>&lt;clip&gt;.mkv</c> is there and
/// no record names a clip file of that name in that folder. Nothing else is ever touched.</para>
/// <para>Every operation runs under one lock, so a clean-up never meets a copy being written. Nothing is written or deleted
/// while the bookmark data is read-only. Log lines at Information and above carry ids and counts only (a clip file's name
/// holds its bookmark's name); paths go to Debug.</para>
/// </remarks>
internal sealed class ClipArtwork : IDisposable
{
    private readonly IBookmarkStore _store;
    private readonly ClipFileOps _files;
    private readonly IClipFileSystem _fileSystem;
    private readonly IClipArtworkSource _source;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipArtwork"/> class.
    /// </summary>
    /// <param name="store">The bookmark store: the clips and the clip folders.</param>
    /// <param name="files">The clip file operations: the guard, the clip folders' markers and the file system.</param>
    /// <param name="source">The videos' images and the clips' refresh.</param>
    /// <param name="configurationProvider">The setting <c>GiveClipsVideoArtwork</c>, read at every use.</param>
    /// <param name="logger">The logger.</param>
    public ClipArtwork(IBookmarkStore store, ClipFileOps files, IClipArtworkSource source, IPluginConfigurationProvider configurationProvider, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configurationProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _files = files;
        _fileSystem = files.FileSystem;
        _source = source;
        _configurationProvider = configurationProvider;
        _logger = logger;
    }

    private bool Enabled => _configurationProvider.GetConfiguration().GiveClipsVideoArtwork;

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// Gives one clip its video's artwork (after the clip is made).
    /// </summary>
    /// <param name="bookmarkId">The clip's record.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>What was done.</returns>
    public async Task<ClipArtworkRun> SyncAsync(Guid bookmarkId, CancellationToken cancellationToken)
    {
        var run = new ClipArtworkRun();
        if (!Enabled || _store.IsReadOnly || _store.Get(bookmarkId) is not { } record)
        {
            return run;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SyncOne(record, run);
        }
        finally
        {
            _lock.Release();
        }

        return run;
    }

    /// <summary>
    /// The daily maintenance's step: every clip given its artwork (which also gives the clips made before this version theirs),
    /// then every orphaned artwork file in the clip folders deleted. A failure of one clip or one file is a Warning and the
    /// rest go on.
    /// </summary>
    /// <param name="cancellationToken">Cancels between clips and files.</param>
    /// <returns>What was done.</returns>
    public async Task<ClipArtworkRun> SyncAllAsync(CancellationToken cancellationToken)
    {
        var run = new ClipArtworkRun();
        if (_store.IsReadOnly)
        {
            run.Skipped = "ReadOnly";
            return run;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Enabled)
            {
                foreach (var record in _store.GetClipRecords(null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SyncOne(record, run);
                }
            }
            else
            {
                run.Skipped = "GiveClipsVideoArtwork is off";
            }

            CleanUpAll(run, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation(
            "Clip artwork: {Written} files written for {Clips} clips ({UpToDate} up to date, {Failed} failed), {Deleted} orphaned files deleted ({NotDeleted} could not be), {FoldersLeftOut} clip folders left out",
            run.Written,
            run.ClipsWritten,
            run.UpToDate,
            run.Failed,
            run.Deleted,
            run.NotDeleted,
            run.FoldersLeftOut);
        return run;
    }

    /// <summary>
    /// Deletes the orphaned artwork of the given clip files (after records let go of them): only the artwork named after
    /// those files, when they are gone and nothing names them.
    /// </summary>
    /// <param name="clipPaths">The clip files' paths.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>What was done.</returns>
    public async Task<ClipArtworkRun> CleanUpAsync(IEnumerable<string?> clipPaths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clipPaths);
        var run = new ClipArtworkRun();
        var paths = clipPaths.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!).ToList();
        if (paths.Count == 0 || _store.IsReadOnly)
        {
            return run;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var named = NamedClips();
            var recognised = new Dictionary<string, bool>(ClipRootPaths.Comparer);
            foreach (var path in paths)
            {
                if (Locate(path) is not { } clip)
                {
                    continue;
                }

                if (!recognised.TryGetValue(clip.Root, out var isOurs))
                {
                    isOurs = _files.IsRecognisedRoot(clip.Root);
                    recognised[clip.Root] = isOurs;
                }

                if (!isOurs || !IsOrphaned(clip.Folder, clip.Stem, named))
                {
                    continue;
                }

                foreach (var kind in ClipArtworkNames.Kinds)
                {
                    foreach (var extension in ClipArtworkNames.Extensions)
                    {
                        DeleteArtwork(Path.Combine(clip.Folder, ClipArtworkNames.FileName(clip.Stem, kind, extension)), run);
                    }
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        return run;
    }

    /// <summary>
    /// Moves a renamed clip's artwork to its new name (never over a file), deletes what is left under the old name once it is
    /// orphaned, and gives the clip what is missing.
    /// </summary>
    /// <param name="bookmarkId">The clip's record, which names <paramref name="to"/> now.</param>
    /// <param name="from">The clip file's old full path.</param>
    /// <param name="to">Its new full path.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>What was done.</returns>
    public async Task<ClipArtworkRun> AfterRenameAsync(Guid bookmarkId, string from, string to, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        var run = new ClipArtworkRun();
        if (_store.IsReadOnly)
        {
            return run;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Locate(from) is { } old && Locate(to) is { } renamed && ClipRootPaths.Comparer.Equals(old.Folder, renamed.Folder))
            {
                foreach (var kind in ClipArtworkNames.Kinds)
                {
                    foreach (var extension in ClipArtworkNames.Extensions)
                    {
                        MoveArtwork(
                            Path.Combine(old.Folder, ClipArtworkNames.FileName(old.Stem, kind, extension)),
                            Path.Combine(renamed.Folder, ClipArtworkNames.FileName(renamed.Stem, kind, extension)),
                            run);
                    }
                }
            }

            if (Enabled && _store.Get(bookmarkId) is { } record)
            {
                SyncOne(record, run);
            }
        }
        finally
        {
            _lock.Release();
        }

        await CleanUpAsync([from], cancellationToken).ConfigureAwait(false);
        return run;
    }

    // One clip's artwork, when it may have some; a failure is a Warning and counted.
    private void SyncOne(Bookmark record, ClipArtworkRun run)
    {
        if (record.Clip is not { Status: BookmarkClipStatus.Completed, FilePath: { Length: > 0 } path }
            || !BookmarkRecordRules.HasLibraryItem(record)
            || Locate(path) is not { } clip
            || clip.Verdict != ClipPathVerdict.Exists)
        {
            return;
        }

        try
        {
            if (_source.Find(record.ItemId) is not { } images)
            {
                return;
            }

            var written = 0;
            foreach (var kind in ClipArtworkNames.Kinds)
            {
                if (WriteArtwork(clip, kind, images.For(kind), run))
                {
                    written++;
                }
            }

            if (written > 0)
            {
                run.ClipsWritten++;
                if (_source.RefreshClip(clip.FullPath))
                {
                    run.Refreshed++;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Failed++;
            _logger.LogWarning("Could not give the clip of bookmark {BookmarkId} its video's artwork: {Error}", record.Id, ex.GetType().Name);
            _logger.LogDebug(ex, "Could not give {Path} its video's artwork", clip.FullPath);
        }
    }

    // One artwork file from its image: whether it was written.
    private bool WriteArtwork(ClipLocation clip, string kind, string? image, ClipArtworkRun run)
    {
        if (image is null || ClipArtworkNames.ExtensionFor(image) is not { } extension || _fileSystem.GetFileIdentity(image) is not { } source)
        {
            // No image of that kind, a format not copied, or no file at its path (a cache cleared): nothing written.
            return false;
        }

        var target = Path.Combine(clip.Folder, ClipArtworkNames.FileName(clip.Stem, kind, extension));
        var attributes = _fileSystem.GetAttributes(target);
        if (attributes is { } found && (found.HasFlag(FileAttributes.ReparsePoint) || found.HasFlag(FileAttributes.Directory) || !IsExactName(target)))
        {
            _logger.LogDebug("Left {Path} as it is: a link, a folder or a name in other case, not an artwork file", target);
            return false;
        }

        var wrote = false;
        if (attributes is not null
            && _fileSystem.GetFileIdentity(target) is { } current
            && current.Length == source.Length
            && current.WrittenUtc.Ticks == source.WrittenUtc.Ticks)
        {
            run.UpToDate++;
        }
        else
        {
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                _fileSystem.CopyFile(image, temporary);
                _fileSystem.ReplaceFile(temporary, target);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            run.Written++;
            wrote = true;
            _logger.LogDebug("Wrote {Path} from {Image}", target, image);
        }

        // The same kind in another format (the video's image changed format) goes: Jellyfin would take either.
        foreach (var other in ClipArtworkNames.Extensions)
        {
            if (!string.Equals(other, extension, StringComparison.Ordinal))
            {
                DeleteArtwork(Path.Combine(clip.Folder, ClipArtworkNames.FileName(clip.Stem, kind, other)), run);
            }
        }

        return wrote;
    }

    // Every recorded clip folder that is there and recognised: its orphaned artwork files (and stale temporary ones) deleted.
    private void CleanUpAll(ClipArtworkRun run, CancellationToken cancellationToken)
    {
        var named = NamedClips();
        var seen = new HashSet<string>(ClipRootPaths.Comparer);
        foreach (var candidate in _store.ClipRoots)
        {
            if (!ClipRootPaths.TryNormalize(candidate, out var root) || !seen.Add(root))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_files.IsRecognisedRoot(root))
            {
                // Not there (an unplugged drive), or not recognised as the clips' own: nothing in it is touched.
                run.FoldersLeftOut++;
                continue;
            }

            List<string> found;
            try
            {
                found = _files.ListClipFolder(root).Select(f => f.Path).Where(p => ClipArtworkNames.Parse(Path.GetFileName(p)) is not null).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                run.FoldersLeftOut++;
                _logger.LogWarning("Could not look for orphaned clip artwork in a clip folder ({Error}); it is looked at again tomorrow", ex.GetType().Name);
                _logger.LogDebug(ex, "Could not list {Folder}", root);
                continue;
            }

            foreach (var path in found)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = ClipArtworkNames.Parse(Path.GetFileName(path))!;
                var folder = Path.GetDirectoryName(path)!;
                if (name.IsTemporary || IsOrphaned(folder, name.ClipStem, named))
                {
                    DeleteArtwork(path, run);
                }
            }
        }
    }

    // Whether a clip's artwork is orphaned: neither of its clip files is there and no record names one of them.
    private bool IsOrphaned(string folder, string stem, HashSet<string> named)
    {
        foreach (var extension in new[] { ".mp4", ".mkv" })
        {
            var clip = Path.Combine(folder, stem + extension);
            if (named.Contains(clip) || _fileSystem.GetAttributes(clip) is not null)
            {
                return false;
            }
        }

        return true;
    }

    // The full paths every record's clip file has, compared as clip paths are.
    private HashSet<string> NamedClips()
    {
        var named = new HashSet<string>(ClipRootPaths.Comparer);
        foreach (var record in _store.GetClipRecords(null))
        {
            if (record.Clip?.FilePath is { Length: > 0 } path && ClipFileGuard.TryGetFullPath(path, out var full))
            {
                named.Add(full);
            }
        }

        return named;
    }

    // Where a clip file is, as the guard sees it: in a recorded clip folder that is there, in it or a folder directly inside it,
    // with a name Bookmarks gives; null otherwise (refused, offline, deeper, another name, a rename under way).
    private ClipLocation? Locate(string path)
    {
        var check = _files.Check(path);
        if (!check.IsInClipFolders || check.FullPath is not { } full || check.Root is not { } root)
        {
            return null;
        }

        var folder = Path.GetDirectoryName(full);
        if (folder is null
            || ClipArtworkNames.ClipStem(Path.GetFileName(full)) is not { } stem
            || !(ClipRootPaths.Comparer.Equals(folder, root) || (Path.GetDirectoryName(folder) is { } parent && ClipRootPaths.Comparer.Equals(parent, root))))
        {
            return null;
        }

        return new ClipLocation(full, root, folder, stem, check.Verdict);
    }

    // Deletes one artwork file of ours, when it is there and is a file (never a link or a folder).
    private void DeleteArtwork(string path, ClipArtworkRun run)
    {
        if (ClipArtworkNames.Parse(Path.GetFileName(path)) is null)
        {
            return;
        }

        try
        {
            if (_fileSystem.GetAttributes(path) is not { } attributes
                || attributes.HasFlag(FileAttributes.ReparsePoint)
                || attributes.HasFlag(FileAttributes.Directory)
                || !IsExactName(path))
            {
                return;
            }

            _fileSystem.DeleteFile(path);
            run.Deleted++;
            _logger.LogDebug("Deleted the clip artwork {Path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            run.NotDeleted++;
            _logger.LogWarning("Could not delete an orphaned clip artwork file ({Error}); it is tried again tomorrow", ex.GetType().Name);
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    // Moves one artwork file of ours to a renamed clip's name, never over a file.
    private void MoveArtwork(string from, string to, ClipArtworkRun run)
    {
        try
        {
            if (_fileSystem.GetAttributes(from) is not { } attributes
                || attributes.HasFlag(FileAttributes.ReparsePoint)
                || attributes.HasFlag(FileAttributes.Directory)
                || !IsExactName(from)
                || _fileSystem.GetAttributes(to) is not null)
            {
                return;
            }

            _fileSystem.MoveFile(from, to);
            run.Moved++;
            _logger.LogDebug("Moved the clip artwork {From} to {To}", from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not move a renamed clip's artwork file ({Error}); it is written again under the new name", ex.GetType().Name);
            _logger.LogDebug(ex, "Could not move {From} to {To}", from, to);
        }
    }

    // Whether the file at a path has that very name, case included (Windows finds Heat-poster.JPG for Heat-poster.jpg, and
    // Bookmarks writes only lower case).
    private bool IsExactName(string path) => string.Equals(_fileSystem.GetOnDiskName(path), Path.GetFileName(path), StringComparison.Ordinal);

    private void TryDelete(string path)
    {
        try
        {
            _fileSystem.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the daily clean-up, which deletes stale temporary artwork files.
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }

    private sealed record ClipLocation(string FullPath, string Root, string Folder, string Stem, ClipPathVerdict Verdict);
}
