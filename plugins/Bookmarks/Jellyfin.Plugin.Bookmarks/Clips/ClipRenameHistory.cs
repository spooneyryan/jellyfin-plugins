using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The clip file renames finished lately: <c>renamed-clips.json</c> in the plugin's data folder, next to
/// the rename journal and <c>bookmarks.json</c>. A snapshot restored, by an admin or at a start that found the data missing
/// or unreadable, or an export imported, can hold a record from before its clip's rename, naming the file's old path; the
/// history lets the restore name the renamed file again (<see cref="ClipRenameRelink"/>) instead of leaving the file no
/// record names and the record "File missing".
/// </summary>
/// <remarks>
/// <para>Each rename is noted once its record names the new path (<see cref="ClipFileOps.FinishRename"/>, and a rename the
/// recovery finished), with the file's identity as it reads at the new path (FAT and exFAT can number a renamed
/// file anew). It keeps renames by count only, the newest <see cref="MaxEntries"/> (<see cref="Prune"/>; no retention
/// by days, which was shorter than the snapshots and exports a restore can start from); the renames of a clip file
/// deleted go with it (<see cref="ForgetChain"/>), those of records gone for good too (<see cref="Forget"/>: a deleted
/// user's under <c>DeletedUserPolicy=Delete</c>, and daily those of records with no clip file any more). It lives
/// outside <c>bookmarks.json</c> on purpose: a restore replaces that file's content with an older one, which wouldn't
/// know the renames made since.</para>
/// <para>Written as the journal's entries are (a file of its own name, flushed, moved into place and flushed again, through
/// <see cref="IClipFileSystem"/>); read again when the file changed on disk since (its size and last write time). A history
/// that can't be read counts as empty (a Warning) and is moved aside to <c>renamed-clips.unreadable-&lt;time&gt;.json</c>
/// before anything is written over it (kept as it is, and nothing written, when it can't be moved); one that can't be
/// written keeps what it had in memory (a Warning). It names the clip paths <c>bookmarks.json</c> names already.</para>
/// </remarks>
internal sealed class ClipRenameHistory
{
    /// <summary>
    /// The history's file name in the data folder (not <c>clip-rename-*</c>, which is the journal's).
    /// </summary>
    public const string FileName = "renamed-clips.json";

    /// <summary>
    /// The most renames it keeps (the oldest go first).
    /// </summary>
    public const int MaxEntries = 2000;

    /// <summary>
    /// The largest history file read.
    /// </summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// The start of the name an unreadable history is moved aside to (outside the leftovers' prefix, so the next start's
    /// clean-up never deletes it).
    /// </summary>
    public const string UnreadablePrefix = "renamed-clips.unreadable-";

    private const string LeftoverPrefix = "renamed-clips.json.";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly IClipFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly bool _ownsFile;
    private List<ClipRenameHistoryEntry>? _entries;

    // The file as it was when it was last read or written (its size and last write time; null when there was none), so a
    // file changed on disk since is read again.
    private (long Length, long WrittenTicks)? _stamp;

    // Whether an unreadable file is still in the history's place (it couldn't be moved aside): nothing is written over it.
    private bool _blocked;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipRenameHistory"/> class.
    /// </summary>
    /// <param name="folder">The plugin's data folder.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public ClipRenameHistory(string folder, IClipFileSystem fileSystem, ILogger logger)
        : this(folder, fileSystem, logger, ownsFile: true)
    {
    }

    private ClipRenameHistory(string folder, IClipFileSystem fileSystem, ILogger logger, bool ownsFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);
        _folder = folder;
        _fileSystem = fileSystem;
        _logger = logger;
        _ownsFile = ownsFile;
    }

    /// <summary>
    /// Gets the history file's full path.
    /// </summary>
    public string FilePath => Path.Combine(_folder, FileName);

    /// <summary>
    /// Gets the renames noted, oldest first.
    /// </summary>
    public IReadOnlyList<ClipRenameHistoryEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return Load().ToList();
            }
        }
    }

    /// <summary>
    /// Reads the history of a data folder once, for the restore at load (<c>BookmarkStore.AfterRestoreAtLoad</c>), which runs
    /// before any service exists.
    /// </summary>
    /// <param name="folder">The plugin's data folder.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The renames noted, oldest first; none when there is no history or it can't be read.</returns>
    public static IReadOnlyList<ClipRenameHistoryEntry> Read(string folder, IClipFileSystem fileSystem, ILogger logger)
        => new ClipRenameHistory(folder, fileSystem, logger, ownsFile: false).Entries;

    /// <summary>
    /// Notes a rename whose record names its new path now. The same rename noted twice (a stop before its journal entry went)
    /// is kept once.
    /// </summary>
    /// <param name="rename">The rename, as the journal recorded it (with the file's identity).</param>
    /// <param name="finishedUtc">When it was finished.</param>
    public void Add(ClipRenameEntry rename, DateTime finishedUtc)
    {
        ArgumentNullException.ThrowIfNull(rename);
        lock (_lock)
        {
            var entries = Load();
            if (entries.Exists(e => e.BookmarkId.Equals(rename.BookmarkId) && string.Equals(e.From, rename.From, StringComparison.Ordinal) && string.Equals(e.To, rename.To, StringComparison.Ordinal)))
            {
                return;
            }

            var next = entries.Append(new ClipRenameHistoryEntry
            {
                BookmarkId = rename.BookmarkId,
                From = rename.From,
                To = rename.To,
                File = rename.File,
                FinishedUtc = finishedUtc
            }).ToList();
            if (next.Count > MaxEntries)
            {
                next = next.Skip(next.Count - MaxEntries).ToList();
            }

            Save(next);
        }
    }

    /// <summary>
    /// Keeps the newest <see cref="MaxEntries"/> renames (the daily maintenance; by count only, never by
    /// age).
    /// </summary>
    /// <returns>How many were dropped.</returns>
    public int Prune()
    {
        lock (_lock)
        {
            var entries = Load();
            if (entries.Count <= MaxEntries)
            {
                return 0;
            }

            var dropped = entries.Count - MaxEntries;
            Save(entries.Skip(dropped).ToList());
            return dropped;
        }
    }

    /// <summary>
    /// Forgets every rename of the given records (records gone for good: a deleted user's, or records without a clip file any
    /// more).
    /// </summary>
    /// <param name="bookmarkIds">The records' ids.</param>
    /// <returns>How many were dropped.</returns>
    public int Forget(IReadOnlySet<Guid> bookmarkIds)
    {
        ArgumentNullException.ThrowIfNull(bookmarkIds);
        lock (_lock)
        {
            var entries = Load();
            var kept = entries.Where(e => !bookmarkIds.Contains(e.BookmarkId)).ToList();
            var dropped = entries.Count - kept.Count;
            if (dropped > 0)
            {
                Save(kept);
            }

            return dropped;
        }
    }

    /// <summary>
    /// Forgets the renames of a record that led to a clip file just deleted: the one whose new path is the file's, the
    /// one before it, and so on, compared as clip paths are. Other renames of the record stay (an earlier file of it,
    /// kept by a deleted bookmark, may still be named again by a restore).
    /// </summary>
    /// <param name="bookmarkId">The record's id.</param>
    /// <param name="fullPath">The deleted file's full path.</param>
    /// <returns>How many were dropped.</returns>
    public int ForgetChain(Guid bookmarkId, string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        lock (_lock)
        {
            var entries = Load();
            if (!entries.Exists(e => e.BookmarkId.Equals(bookmarkId)))
            {
                return 0;
            }

            var ends = new HashSet<string>(ClipRootPaths.Comparer) { fullPath };
            var dropped = new HashSet<ClipRenameHistoryEntry>(ReferenceEqualityComparer.Instance);
            for (var found = true; found;)
            {
                found = false;
                foreach (var entry in entries)
                {
                    if (entry.BookmarkId.Equals(bookmarkId) && !dropped.Contains(entry) && ends.Contains(entry.To))
                    {
                        dropped.Add(entry);
                        ends.Add(entry.From);
                        found = true;
                    }
                }
            }

            if (dropped.Count > 0)
            {
                Save(entries.Where(e => !dropped.Contains(e)).ToList());
            }

            return dropped.Count;
        }
    }

    // The renames, read the first time and again whenever the file changed on disk since; an unreadable file counts as
    // none, with a Warning, and is moved aside. The first read also deletes what a write that stopped half way left.
    private List<ClipRenameHistoryEntry> Load()
    {
        var stamp = ReadStamp();
        if (_entries is not null && stamp == _stamp)
        {
            return _entries;
        }

        var first = _entries is null;
        var entries = new List<ClipRenameHistoryEntry>();
        var unreadable = false;
        try
        {
            if (_ownsFile && first)
            {
                _fileSystem.DeleteRenameJournalLeftovers(_folder, LeftoverPrefix);
            }

            var text = _fileSystem.ReadRenameHistory(FilePath);
            if (text is not null)
            {
                var document = text.Length == 0 ? null : JsonSerializer.Deserialize<HistoryDocument>(text, JsonOptions);
                if (document?.Renames is null)
                {
                    _logger.LogWarning("The history of clip file renames ({File}) can't be read; it starts empty", FileName);
                    unreadable = true;
                }
                else
                {
                    entries = document.Renames
                        .Where(e => e is not null && !e.BookmarkId.Equals(Guid.Empty) && !string.IsNullOrEmpty(e.From) && !string.IsNullOrEmpty(e.To))
                        .OrderBy(e => e.FinishedUtc)
                        .ToList();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning("The history of clip file renames ({File}) can't be read ({Error}); it starts empty", FileName, ex.GetType().Name);
            _logger.LogDebug(ex, "Could not read {Path}", FilePath);
            unreadable = ex is JsonException;
        }

        _blocked = false;
        if (unreadable && _ownsFile)
        {
            SetAside();
        }

        _entries = entries;
        _stamp = ReadStamp();
        return entries;
    }

    // An unreadable history moved aside, so the next write doesn't put an empty one over what an admin may want to look at.
    private void SetAside()
    {
        var aside = Path.Combine(_folder, UnreadablePrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + ".json");
        try
        {
            _fileSystem.SetAsideRenameHistory(FilePath, aside);
            _logger.LogWarning("The history of clip file renames that couldn't be read was moved aside to {File}", Path.GetFileName(aside));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _blocked = true;
            _logger.LogWarning("The history of clip file renames that couldn't be read couldn't be moved aside ({Error}); nothing is written over it", ex.GetType().Name);
            _logger.LogDebug(ex, "Could not move {Path} aside", FilePath);
        }
    }

    // The history file's size and last write time, or null when there is none (or it can't be looked at).
    private (long Length, long WrittenTicks)? ReadStamp()
        => _fileSystem.GetFileIdentity(FilePath) is { } identity ? (identity.Length, identity.WrittenUtc.Ticks) : null;

    // Written first, kept in memory once it is: one that can't be written keeps what was there.
    private void Save(List<ClipRenameHistoryEntry> entries)
    {
        if (_blocked)
        {
            _logger.LogWarning("The history of clip file renames isn't written: an unreadable one is still in its place");
            return;
        }

        try
        {
            _fileSystem.WriteRenameJournal(FilePath, JsonSerializer.Serialize(new HistoryDocument { Version = 1, Renames = entries }, JsonOptions));
            _entries = entries;
            _stamp = ReadStamp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not write the history of clip file renames ({Error}); a restore from before a rename may not find the renamed clip", ex.GetType().Name);
            _logger.LogDebug(ex, "Could not write {Path}", FilePath);
        }
    }

    private sealed class HistoryDocument
    {
        public int Version { get; set; }

        public List<ClipRenameHistoryEntry>? Renames { get; set; }
    }
}
