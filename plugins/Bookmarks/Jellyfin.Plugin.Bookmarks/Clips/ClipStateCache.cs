using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.Bookmarks.Storage;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What the My Clips list knows about the clip files: the guard's verdict on each clip path
/// (<see cref="ClipFileGuard"/>: in a clip folder that is there and present, missing, in a folder that can't be
/// reached, or not a path Bookmarks may use), kept for <see cref="Lifetime"/>; and a file's last write time, when the
/// list dates a clip by it.
/// </summary>
/// <remarks>
/// <para><b>A verdict is one look.</b> Each request reads through a <see cref="Reading"/> of its own, which asks whether a clip
/// folder is there once (for every verdict it works out under that folder) and the attributes of each step below it once
/// (the per-user folder once for all its files), and keeps none of those answers past the request: only whole verdicts are
/// kept. So no verdict pairs an earlier "the clip folder is there" with a later "nothing at the file", which would read the
/// clips of a clip folder unplugged meanwhile as missing. A clip folder unplugged shows as offline once the
/// verdicts of its clips are up; a clip whose file was there is never read as missing because of it.</para>
/// <para><b>For display only.</b> Nothing that deletes, moves or serves a file uses it: <see cref="ClipFileOps"/> asks the file
/// system again every time. So the list may show a file deleted in Explorer as there, one put back as missing, or a clip
/// folder unplugged or plugged in again as it was, for up to <see cref="Lifetime"/>. What Bookmarks does to a clip file
/// itself shows at once: <see cref="ClipFileOps"/> tells the cache about every file it moves into place (a finished clip, a
/// rename) or deletes (<see cref="Forget"/>), whose verdict and write time go, and a reading under way at that moment keeps
/// nothing it found (found in testing: a clip made again under the name of a file that had gone read "missing" for up to 30 s
/// after it was done, and the page's Forget then deleted it).</para>
/// <para><b>Missing only in the clips' own folder</b>: a file gone from a clip folder that is there is marked
/// <see cref="ClipPathCheck.RootUnrecognised"/> when the folder has no marker file of its own
/// (<see cref="ClipRootMarker"/>; the marker read once per folder and reading), which the list shows as its folder
/// offline, never as missing, as the missing-clip sweep and the clip worker leave such a folder alone. A marker
/// Bookmarks writes or adopts forgets the folder's verdicts at once (<see cref="ForgetUnder"/>).</para>
/// <para>An error is a verdict like any other (<see cref="ClipPathVerdict.Unreadable"/>; a share that doesn't answer is asked
/// once per request, and only while a verdict of one of its clips is up). The verdicts and times of paths no longer asked
/// for are dropped once there are many.</para>
/// </remarks>
internal sealed class ClipStateCache
{
    /// <summary>
    /// How many verdicts (or times) are kept before those past their time are dropped.
    /// </summary>
    internal const int SweepAbove = 10_000;

    private readonly IBookmarkStore _store;
    private readonly IClipFileSystem _fileSystem;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Kept<ClipPathCheck>> _verdicts = new(ClipRootPaths.Comparer);
    private readonly ConcurrentDictionary<string, Kept<DateTime?>> _writeTimes = new(ClipRootPaths.Comparer);

    // How many times Bookmarks has changed a clip file (Forget): a reading begun before the latest change keeps nothing.
    private long _changes;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipStateCache"/> class over the real file system.
    /// </summary>
    /// <param name="store">The bookmark store, for its recorded clip folders (<c>ClipRoots</c>).</param>
    public ClipStateCache(IBookmarkStore store)
        : this(store, PhysicalClipFileSystem.Instance, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipStateCache"/> class (tests).
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="fileSystem">The file system it asks.</param>
    /// <param name="timeProvider">The clock of the verdicts' lifetime.</param>
    internal ClipStateCache(IBookmarkStore store, IClipFileSystem fileSystem, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _fileSystem = fileSystem;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets how long a verdict, or a file's last write time, is kept: 30 s.
    /// </summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets whether a record's clip file rename isn't finished (the list's "Rename not finished",
    /// asked afresh for every row, never kept): <see cref="ClipFileOps"/> sets it from its rename journal; none in a test that
    /// has no clip file operations.
    /// </summary>
    public Func<Guid, bool>? RenameUnfinished { get; set; }

    /// <summary>
    /// Begins one request's reading of the clip files: the verdicts kept, and those it works out from answers it asks
    /// together.
    /// </summary>
    /// <returns>The reading, for one request (it isn't meant to be shared between threads).</returns>
    public Reading Read() => new(this);

    /// <summary>
    /// Forgets what is known about a clip file Bookmarks has just moved into place, renamed or deleted, so the list judges it
    /// again at once; a reading under way keeps nothing it found. <see cref="ClipFileOps"/> calls it after each such change.
    /// </summary>
    /// <param name="path">The file's path (a full path, as the guard gives it).</param>
    public void Forget(string? path)
    {
        if (!ClipFileGuard.TryGetFullPath(path, out var full))
        {
            return;
        }

        // The count first, then the entries: a reading that stores a verdict after this sees the count changed and takes it
        // back (Keep), so nothing found before the change outlives it.
        Interlocked.Increment(ref _changes);
        _verdicts.TryRemove(full, out _);
        _writeTimes.TryRemove(full, out _);
    }

    /// <summary>
    /// Forgets the verdicts of every clip file in a clip folder whose marker file Bookmarks has just written or adopted
    /// (<see cref="ClipFileOps.WriteRootMarkerAsync"/>: the clip worker, the sweep's backfill, an admin's confirmation), so a
    /// folder recognised now shows its missing files as missing at once, not as its folder offline for up to
    /// <see cref="Lifetime"/>; a reading under way keeps nothing it found.
    /// </summary>
    /// <param name="folder">The clip folder.</param>
    public void ForgetUnder(string? folder)
    {
        if (!ClipRootPaths.TryNormalize(folder, out var root))
        {
            return;
        }

        Interlocked.Increment(ref _changes);
        foreach (var path in _verdicts.Keys)
        {
            if (ClipRootPaths.IsUnder(path, root))
            {
                _verdicts.TryRemove(path, out _);
            }
        }
    }

    private static void Sweep<T>(ConcurrentDictionary<string, Kept<T>> kept, DateTimeOffset now)
    {
        if (kept.Count <= SweepAbove)
        {
            return;
        }

        foreach (var (path, entry) in kept)
        {
            if (entry.Until <= now)
            {
                kept.TryRemove(path, out _);
            }
        }
    }

    // Keeps what a reading found, unless Bookmarks changed a clip file since the reading began: what it found may be from
    // before that change. The second look takes back an entry stored while a change was being made (Forget counts first).
    private void Keep<T>(ConcurrentDictionary<string, Kept<T>> kept, string path, T value, DateTimeOffset now, long changes)
    {
        if (Volatile.Read(ref _changes) != changes)
        {
            return;
        }

        var entry = new Kept<T>(value, now + Lifetime);
        kept[path] = entry;
        if (Volatile.Read(ref _changes) != changes)
        {
            kept.TryRemove(new KeyValuePair<string, Kept<T>>(path, entry));
            return;
        }

        Sweep(kept, now);
    }

    /// <summary>
    /// One request's reading of the clip files (<see cref="Read"/>): the verdicts the cache keeps, and the others worked out
    /// from answers asked together, once each, while it lasts.
    /// </summary>
    internal sealed class Reading
    {
        private readonly ClipStateCache _cache;
        private readonly long _changes;
        private readonly Probe _probe;
        private readonly Dictionary<string, bool> _recognised = new(ClipRootPaths.Comparer);
        private IReadOnlyList<string>? _roots;

        internal Reading(ClipStateCache cache)
        {
            _cache = cache;
            _changes = Volatile.Read(ref cache._changes);
            _probe = new Probe(cache._fileSystem);
        }

        /// <summary>
        /// Judges a clip path as <see cref="ClipFileGuard.Check"/> does, against the recorded clip folders: the verdict kept
        /// for it, else one worked out now.
        /// </summary>
        /// <param name="path">The record's clip path.</param>
        /// <returns>The verdict.</returns>
        public ClipPathCheck Check(string? path)
        {
            if (!ClipFileGuard.TryGetFullPath(path, out var full))
            {
                return new ClipPathCheck(ClipPathVerdict.NotAPath, null, null);
            }

            var now = _cache._timeProvider.GetUtcNow();
            if (_cache._verdicts.TryGetValue(full, out var kept) && kept.Until > now)
            {
                return kept.Value;
            }

            var verdict = ClipFileGuard.Check(full, _roots ??= _cache._store.ClipRoots, _probe);

            // A file gone from a clip folder that is there is missing only when the folder is the one its clips were made in,
            // as its marker file says: otherwise (an empty mount point, a disk that isn't mounted, a folder made in
            // place of the real one) the file is probably fine where the real folder is, and the page offers nothing
            // that would forget it.
            if (verdict is { Verdict: ClipPathVerdict.Missing, Root: { } root } && !IsRecognised(root))
            {
                verdict = verdict with { RootUnrecognised = true };
            }

            // Only a verdict that asked the file system is kept: one of a path under a recorded clip folder. The guard refuses
            // the others without asking anything, and they follow the recorded clip folders at once.
            if (verdict.Root is not null)
            {
                _cache.Keep(_cache._verdicts, full, verdict, now, _changes);
            }

            return verdict;
        }

        /// <summary>
        /// Gets when a clip file was last written (UTC): the time kept for it, else the file system's now.
        /// </summary>
        /// <param name="fullPath">The file's full path (<see cref="ClipPathCheck.FullPath"/> of a file that is there).</param>
        /// <returns>The time, or <c>null</c> when it isn't there or can't be read.</returns>
        public DateTime? LastWriteTimeUtc(string fullPath)
        {
            ArgumentException.ThrowIfNullOrEmpty(fullPath);
            var now = _cache._timeProvider.GetUtcNow();
            if (_cache._writeTimes.TryGetValue(fullPath, out var kept) && kept.Until > now)
            {
                return kept.Value;
            }

            DateTime? time;
            try
            {
                time = _cache._fileSystem.GetLastWriteTimeUtc(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only a date for the list: a file whose time can't be read is dated by its job.
                time = null;
            }

            _cache.Keep(_cache._writeTimes, fullPath, time, now, _changes);
            return time;
        }

        // Whether a clip folder that is there is recognised as the clips' own, as ClipFileOps.CheckRoot judges it (Marked, or
        // Adoptable: a marker that names it while the store records no token yet), asked once per folder and reading: the
        // folder's answer is the one the guard had for this reading, and its marker file is read once. A marker that can't be
        // read counts as none.
        private bool IsRecognised(string root)
        {
            if (_recognised.TryGetValue(root, out var known))
            {
                return known;
            }

            bool recognised;
            try
            {
                recognised = _probe.FolderExists(root)
                    && ClipRootMarker.Judge(_cache._fileSystem.ReadRootMarker(root), root, _cache._store.GetClipRootToken(root)) is ClipRootState.Marked or ClipRootState.Adoptable;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                recognised = false;
            }

            _recognised[root] = recognised;
            return recognised;
        }
    }

    /// <summary>
    /// A verdict or a time, and until when it is kept.
    /// </summary>
    private sealed record Kept<T>(T Value, DateTimeOffset Until);

    /// <summary>
    /// The guard's read-only calls within one reading: each folder's and each path's answer asked once, an error kept like
    /// an answer (and thrown again, as an exception of its own, each time it is read).
    /// </summary>
    private sealed class Probe : IClipPathProbe
    {
        private readonly IClipFileSystem _fileSystem;
        private readonly Dictionary<string, Answer<bool>> _folders = new(ClipRootPaths.Comparer);
        private readonly Dictionary<string, Answer<FileAttributes?>> _attributes = new(ClipRootPaths.Comparer);

        public Probe(IClipFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public FileAttributes? GetAttributes(string path) => Ask(_attributes, path, _fileSystem.GetAttributes);

        public bool FolderExists(string path) => Ask(_folders, path, _fileSystem.FolderExists);

        private static T Ask<T>(Dictionary<string, Answer<T>> answers, string path, Func<string, T> ask)
        {
            Answer<T>? answer;
            lock (answers)
            {
                if (!answers.TryGetValue(path, out answer))
                {
                    try
                    {
                        answer = new Answer<T>(ask(path), null);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        answer = new Answer<T>(default!, ex);
                    }

                    answers[path] = answer;
                }
            }

            // The guard turns it into its Unreadable verdict.
            return answer.Error is { } error ? throw new IOException(error.Message, error) : answer.Value;
        }

        /// <summary>
        /// An answer of the file system, or the error that came instead.
        /// </summary>
        private sealed record Answer<T>(T Value, Exception? Error);
    }
}
