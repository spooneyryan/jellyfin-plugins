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
/// <para><b>The clip's first frame</b> (since 1.7.0 / 2.5.0, the setting <c>UseClipFirstFrameAsThumbnail</c>, on by
/// default): Jellyfin's ffmpeg takes the clip file's very first frame (<see cref="IClipFrameExtractor"/>) as
/// <c>&lt;clip&gt;-poster.jpg</c>, and a copy of it as <c>&lt;clip&gt;-thumb.jpg</c>, in place of the film's poster and an
/// episode's image; <c>-fanart</c> stays the film's backdrop while <c>GiveClipsVideoArtwork</c> is on (the frame is written
/// with that setting off too, and needs no library item: it comes from the clip file). The frame is written under a
/// temporary name, given the clip file's last write time, then moved over the poster; a poster whose last write time is the
/// clip file's is that file's frame already, so nothing is taken again, while a clip made again (a new file, a new time)
/// gets a new frame. A failure (no ffmpeg, an error, a timeout) is a Warning with the bookmark's id, deletes the temporary
/// file, and the film's images are written as without the setting. With the setting off the artwork is as before it; a
/// poster or thumb that is the clip file's frame (its time) goes when the video has no image of that kind to replace it.</para>
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
    private readonly IClipFrameExtractor? _frames;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipArtwork"/> class.
    /// </summary>
    /// <param name="store">The bookmark store: the clips and the clip folders.</param>
    /// <param name="files">The clip file operations: the guard, the clip folders' markers and the file system.</param>
    /// <param name="source">The videos' images and the clips' refresh.</param>
    /// <param name="configurationProvider">The settings <c>GiveClipsVideoArtwork</c> and <c>UseClipFirstFrameAsThumbnail</c>,
    /// read at every use.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="frames">Takes a clip's first frame; without it (tests of the video's artwork alone) no frame is taken,
    /// whatever the setting says.</param>
    public ClipArtwork(IBookmarkStore store, ClipFileOps files, IClipArtworkSource source, IPluginConfigurationProvider configurationProvider, ILogger logger, IClipFrameExtractor? frames = null)
    {
        _frames = frames;
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

    // Whether anything is written: the video's artwork, or the clip's first frame.
    private bool Enabled => VideoArtwork || Frames;

    private bool VideoArtwork => _configurationProvider.GetConfiguration().GiveClipsVideoArtwork;

    private bool Frames => _frames is not null && _configurationProvider.GetConfiguration().UseClipFirstFrameAsThumbnail;

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
            await SyncOneAsync(record, run, cancellationToken).ConfigureAwait(false);
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
                    await SyncOneAsync(record, run, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                run.Skipped = _frames is not null ? "GiveClipsVideoArtwork and UseClipFirstFrameAsThumbnail are off" : "GiveClipsVideoArtwork is off";
            }

            CleanUpAll(run, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }

        _logger.LogInformation(
            "Clip artwork: {Written} files written for {Clips} clips ({UpToDate} up to date, {Failed} failed; {Frames} first frames taken, {FramesFailed} could not be), {Deleted} orphaned files deleted ({NotDeleted} could not be), {FoldersLeftOut} clip folders left out",
            run.Written,
            run.ClipsWritten,
            run.UpToDate,
            run.Failed,
            run.Frames,
            run.FramesFailed,
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
                await SyncOneAsync(record, run, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }

        await CleanUpAsync([from], cancellationToken).ConfigureAwait(false);
        return run;
    }

    // Whether an image is the frame of the clip file there now: it was given that file's last write time when written (a
    // file of the clip's name made again has a new time, so it gets a new frame; a rename keeps the time).
    private static bool IsFrameOf(ClipFileIdentity? image, ClipFileIdentity clipFile)
        => image is not null && image.Length > 0 && image.WrittenUtc.Ticks == clipFile.WrittenUtc.Ticks;

    // One clip's artwork, when it may have some; a failure is a Warning and counted. The video's artwork needs its library
    // item; the clip's first frame (since 1.7.0 / 2.5.0) only the clip file. A frame that couldn't be taken leaves the poster
    // and the thumb to the video's images, as without the setting.
    private async Task SyncOneAsync(Bookmark record, ClipArtworkRun run, CancellationToken cancellationToken)
    {
        var videoArtwork = VideoArtwork && BookmarkRecordRules.HasLibraryItem(record);
        var frames = Frames;
        if (record.Clip is not { Status: BookmarkClipStatus.Completed, FilePath: { Length: > 0 } path }
            || !(videoArtwork || frames)
            || Locate(path) is not { } clip
            || clip.Verdict != ClipPathVerdict.Exists)
        {
            return;
        }

        try
        {
            var written = 0;
            var framed = false;
            if (frames && await WriteFrameAsync(record.Id, clip, run, cancellationToken).ConfigureAwait(false) is { } frame)
            {
                framed = true;
                written += frame;
            }

            var images = videoArtwork ? _source.Find(record.ItemId) : null;
            if (images is null)
            {
                if (written > 0)
                {
                    Refresh(clip, run);
                }

                return;
            }

            var frameSettingOff = !_configurationProvider.GetConfiguration().UseClipFirstFrameAsThumbnail;
            foreach (var kind in ClipArtworkNames.Kinds)
            {
                if (framed && kind is ClipArtworkNames.Poster or ClipArtworkNames.Thumb)
                {
                    continue;
                }

                var image = images.For(kind);
                if (image is null && frameSettingOff && kind is ClipArtworkNames.Poster or ClipArtworkNames.Thumb)
                {
                    // The setting turned off: a frame this clip file was given goes when no image of the video replaces it.
                    DeleteFrame(clip, kind, run);
                    continue;
                }

                if (WriteArtwork(clip, kind, image, run))
                {
                    written++;
                }
            }

            if (written > 0)
            {
                Refresh(clip, run);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Failed++;
            _logger.LogWarning("Could not give the clip of bookmark {BookmarkId} its video's artwork: {Error}", record.Id, ex.GetType().Name);
            _logger.LogDebug(ex, "Could not give {Path} its video's artwork", clip.FullPath);
        }
    }

    // A clip with new artwork: counted, and its image refresh queued when it is a library item.
    private void Refresh(ClipLocation clip, ClipArtworkRun run)
    {
        run.ClipsWritten++;
        if (_source.RefreshClip(clip.FullPath))
        {
            run.Refreshed++;
        }
    }

    // The clip's first frame as its poster (<clip>-poster.jpg) and thumb (<clip>-thumb.jpg): how many files were written (0
    // when both are the clip file's frame already), or null when there is no frame to show (a path that isn't a plain
    // artwork file, the clip file's time unreadable, ffmpeg failing), and the video's images take their places.
    private async Task<int?> WriteFrameAsync(Guid bookmarkId, ClipLocation clip, ClipArtworkRun run, CancellationToken cancellationToken)
    {
        var poster = Path.Combine(clip.Folder, ClipArtworkNames.FileName(clip.Stem, ClipArtworkNames.Poster, ClipArtworkNames.FrameExtension));
        var thumb = Path.Combine(clip.Folder, ClipArtworkNames.FileName(clip.Stem, ClipArtworkNames.Thumb, ClipArtworkNames.FrameExtension));
        if (!IsWritable(poster) || !IsWritable(thumb) || _fileSystem.GetFileIdentity(clip.FullPath) is not { } clipFile)
        {
            return null;
        }

        var written = 0;
        if (IsFrameOf(_fileSystem.GetFileIdentity(poster), clipFile))
        {
            run.UpToDate++;
        }
        else
        {
            var temporary = poster + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await _frames!.ExtractFirstFrameAsync(clip.FullPath, temporary, cancellationToken).ConfigureAwait(false);
                _fileSystem.SetLastWriteTimeUtc(temporary, clipFile.WrittenUtc);
                _fileSystem.ReplaceFile(temporary, poster);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Nothing partial stays: the temporary file goes (or the daily clean-up deletes it, as a stale .tmp of ours).
                TryDelete(temporary);
                run.FramesFailed++;
                _logger.LogWarning("Could not take the first frame of the clip of bookmark {BookmarkId} ({Error}); its video's images are used", bookmarkId, ex.GetType().Name);
                _logger.LogDebug(ex, "Could not take the first frame of {Path}", clip.FullPath);
                return null;
            }
            catch
            {
                // Cancelled (the maintenance stopping): the temporary file goes too.
                TryDelete(temporary);
                throw;
            }

            run.Frames++;
            run.Written++;
            written++;
            _logger.LogDebug("Wrote {Path}, the first frame of its clip", poster);
        }

        // The thumb: a copy of the poster, with its time (the clip file's).
        if (_fileSystem.GetFileIdentity(poster) is { } frame && _fileSystem.GetFileIdentity(thumb) is { } current && current.Length == frame.Length && current.WrittenUtc.Ticks == frame.WrittenUtc.Ticks)
        {
            run.UpToDate++;
        }
        else
        {
            var temporary = thumb + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                _fileSystem.CopyFile(poster, temporary);
                _fileSystem.ReplaceFile(temporary, thumb);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            run.Written++;
            written++;
            _logger.LogDebug("Wrote {Path}, the first frame of its clip", thumb);
        }

        // The same kinds in another format (the video's images before) go: Jellyfin would take either.
        foreach (var kind in new[] { ClipArtworkNames.Poster, ClipArtworkNames.Thumb })
        {
            foreach (var other in ClipArtworkNames.Extensions)
            {
                if (!string.Equals(other, ClipArtworkNames.FrameExtension, StringComparison.Ordinal))
                {
                    DeleteArtwork(Path.Combine(clip.Folder, ClipArtworkNames.FileName(clip.Stem, kind, other)), run);
                }
            }
        }

        return written;
    }

    // With the setting off: the poster or thumb that is this clip file's first frame (a .jpg with the clip file's time) is
    // deleted, when the video has no image of that kind to put there instead.
    private void DeleteFrame(ClipLocation clip, string kind, ClipArtworkRun run)
    {
        var path = Path.Combine(clip.Folder, ClipArtworkNames.FileName(clip.Stem, kind, ClipArtworkNames.FrameExtension));
        if (_fileSystem.GetFileIdentity(clip.FullPath) is { } clipFile && IsFrameOf(_fileSystem.GetFileIdentity(path), clipFile))
        {
            DeleteArtwork(path, run);
        }
    }

    // Whether an artwork path may be written: nothing there, or a plain file of that very name (never a link or a folder).
    private bool IsWritable(string target)
    {
        var attributes = _fileSystem.GetAttributes(target);
        if (attributes is { } found && (found.HasFlag(FileAttributes.ReparsePoint) || found.HasFlag(FileAttributes.Directory) || !IsExactName(target)))
        {
            _logger.LogDebug("Left {Path} as it is: a link, a folder or a name in other case, not an artwork file", target);
            return false;
        }

        return true;
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
                || (_fileSystem.GetAttributes(to) is not null && !IsCaseChangeOf(from, to)))
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

    // A clip renamed in case alone ("Case" to "CASE"): on Windows its artwork's new name finds the file itself, which is
    // then renamed in place (the same folder, the same name but for case).
    private static bool IsCaseChangeOf(string from, string to)
        => !string.Equals(from, to, StringComparison.Ordinal) && string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

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
