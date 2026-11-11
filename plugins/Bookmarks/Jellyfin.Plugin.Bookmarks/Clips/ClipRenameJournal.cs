using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The journal of clip file renames: one small file per rename under way, <c>clip-rename-&lt;id&gt;.json</c> in
/// the plugin's data folder next to <c>bookmarks.json</c>, written and flushed to the disk before the clip file is moved and
/// deleted once the record naming the new path is written. A rename moves the file first and writes the record second,
/// so between the two a crash leaves the record naming a path the file has left; the entry says where it went, and
/// <see cref="ClipFileOps.FinishRenamesAsync"/> finishes it at the next start (before the clip jobs resume and the sweep
/// runs), at the daily maintenance, while one waits (after a minute, then less often), and before the next rename. While an entry is here, both of
/// its paths are held: <see cref="ClipFileOps.Check"/> reads them as not to be touched, so nothing deletes, forgets or moves
/// either until the rename is finished.
/// </summary>
/// <remarks>
/// <para>Read once, when first asked for (the entries left by the process before), and kept in memory from then on; every
/// change goes to the file first. Every file operation goes through <see cref="IClipFileSystem"/>, as for every other file
/// Bookmarks changes. The first read also deletes what a write that stopped half way left (<c>clip-rename-*.tmp</c>).</para>
/// <para><b>An entry that can't be read</b> (not JSON, not an entry, over <see cref="MaxBytes"/>) stays where
/// it is with a Warning, and since its file's name carries the record's id (<see cref="PathOf"/>), that clip is held as a
/// rename not finished (<see cref="UnreadableIds"/>): its record's path isn't deleted, forgotten or moved, and Diagnostics
/// counts it, until an admin has looked at the clip and removed the file.</para>
/// <para><b>Removing an entry</b> removes the one the caller finished, never a newer one written for the same clip meanwhile
/// (<see cref="Remove"/> compares them, the file deleted under the same lock).</para>
/// <para>The data folder holds <c>bookmarks.json</c>, which names every clip path already, so an entry adds nothing private to
/// it; its log lines name the record only, the paths at Debug.</para>
/// </remarks>
internal sealed class ClipRenameJournal
{
    /// <summary>
    /// The entries' file name prefix.
    /// </summary>
    public const string FilePrefix = "clip-rename-";

    /// <summary>
    /// The largest entry read (its paths are at most a few hundred characters); a larger one can't be read.
    /// </summary>
    public const int MaxBytes = 64 * 1024;

    private const string FileSuffix = ".json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _folder;
    private readonly IClipFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Dictionary<Guid, ClipRenameEntry>? _entries;
    private Dictionary<Guid, string> _unreadable = [];
    private List<string> _unreadableWithoutId = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipRenameJournal"/> class.
    /// </summary>
    /// <param name="folder">The plugin's data folder.</param>
    /// <param name="fileSystem">The file system.</param>
    /// <param name="logger">The logger.</param>
    public ClipRenameJournal(string folder, IClipFileSystem fileSystem, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);
        _folder = folder;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets the folder the entries are in.
    /// </summary>
    public string Folder => _folder;

    /// <summary>
    /// Gets the entries there are now: renames under way, or not finished.
    /// </summary>
    public IReadOnlyList<ClipRenameEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return Load().Values.ToList();
            }
        }
    }

    /// <summary>
    /// Gets the ids of the records whose entry can't be read (from its file's name): each such clip is held as a rename not
    /// finished.
    /// </summary>
    public IReadOnlyCollection<Guid> UnreadableIds
    {
        get
        {
            lock (_lock)
            {
                Load();
                return _unreadable.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// Gets the file names of the entries that can't be read, those whose name carries no record's id included (for
    /// Diagnostics: an admin looks at them).
    /// </summary>
    public IReadOnlyList<string> UnreadableFiles
    {
        get
        {
            lock (_lock)
            {
                Load();
                return _unreadable.Values.Concat(_unreadableWithoutId).Order(StringComparer.OrdinalIgnoreCase).ToList();
            }
        }
    }

    /// <summary>
    /// Gets the path of a record's entry.
    /// </summary>
    /// <param name="bookmarkId">The record's id.</param>
    /// <returns>The entry's full path.</returns>
    public string PathOf(Guid bookmarkId) => Path.Combine(_folder, FilePrefix + bookmarkId.ToString("N", CultureInfo.InvariantCulture) + FileSuffix);

    /// <summary>
    /// Gets a record's entry, if a rename of its file is under way or not finished.
    /// </summary>
    /// <param name="bookmarkId">The record's id.</param>
    /// <returns>The entry, or <c>null</c>.</returns>
    public ClipRenameEntry? Get(Guid bookmarkId)
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault(bookmarkId);
        }
    }

    /// <summary>
    /// Whether a record's entry is there but can't be read.
    /// </summary>
    /// <param name="bookmarkId">The record's id.</param>
    /// <returns>Whether it is.</returns>
    public bool IsUnreadable(Guid bookmarkId)
    {
        lock (_lock)
        {
            Load();
            return _unreadable.ContainsKey(bookmarkId);
        }
    }

    /// <summary>
    /// Gets the entry one of whose paths is the given one (before or after its rename), compared as clip paths are.
    /// </summary>
    /// <param name="fullPath">A full path.</param>
    /// <returns>The entry, or <c>null</c>.</returns>
    public ClipRenameEntry? Holding(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        lock (_lock)
        {
            var entries = Load();
            if (entries.Count == 0)
            {
                return null;
            }

            return entries.Values.FirstOrDefault(e => ClipRootPaths.Comparer.Equals(e.From, fullPath) || ClipRootPaths.Comparer.Equals(e.To, fullPath));
        }
    }

    /// <summary>
    /// Records a rename before its file is moved: the entry's file is written and flushed first (also its move into place),
    /// then kept in memory.
    /// </summary>
    /// <param name="entry">The rename.</param>
    /// <exception cref="IOException">The entry couldn't be written (nothing is held then).</exception>
    /// <exception cref="UnauthorizedAccessException">The server may not write it.</exception>
    public void Add(ClipRenameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var entries = Load();
            _fileSystem.WriteRenameJournal(PathOf(entry.BookmarkId), JsonSerializer.Serialize(entry, JsonOptions));
            entries[entry.BookmarkId] = entry;
            _unreadable.Remove(entry.BookmarkId);
        }
    }

    /// <summary>
    /// Ends a rename (finished, or undone): the entry given, and only while it is the one the journal holds for its record (a
    /// newer one written for the same clip meanwhile stays), forgotten in memory and its file deleted, under one lock. A file
    /// that can't be deleted is a Warning; the next start finds the rename finished from what is on disk and deletes it then.
    /// </summary>
    /// <param name="entry">The rename that ended.</param>
    /// <returns>Whether it was the journal's (and is gone from it now).</returns>
    public bool Remove(ClipRenameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var entries = Load();
            if (!entries.TryGetValue(entry.BookmarkId, out var held) || !Equals(held, entry))
            {
                return false;
            }

            entries.Remove(entry.BookmarkId);
            try
            {
                _fileSystem.DeleteRenameJournal(PathOf(entry.BookmarkId));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Could not delete the journal entry of a finished clip file rename of bookmark {BookmarkId} ({Error}); it is looked at again at the next start", entry.BookmarkId, ex.GetType().Name);
                _logger.LogDebug(ex, "Could not delete {Path}", PathOf(entry.BookmarkId));
            }

            return true;
        }
    }

    // The entries, read from the data folder the first time (the ones the process before left).
    private Dictionary<Guid, ClipRenameEntry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        var entries = new Dictionary<Guid, ClipRenameEntry>();
        var unreadable = new Dictionary<Guid, string>();
        var withoutId = new List<string>();
        IReadOnlyList<(string Path, string? Text)> files;
        try
        {
            files = _fileSystem.ReadRenameJournal(_folder, FilePrefix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Asked again next time: a data folder that can't be read now holds nothing yet.
            _logger.LogWarning("Could not read the journal of clip file renames ({Error})", ex.GetType().Name);
            _logger.LogDebug(ex, "Could not read the journal of clip file renames in {Folder}", _folder);
            return entries;
        }

        DeleteLeftovers();
        foreach (var (path, text) in files)
        {
            var name = Path.GetFileName(path);
            ClipRenameEntry? entry = null;
            if (text is not null)
            {
                try
                {
                    entry = JsonSerializer.Deserialize<ClipRenameEntry>(text, JsonOptions);
                }
                catch (JsonException)
                {
                    // An entry that can't be read: below.
                }
            }

            if (entry is not null && !entry.BookmarkId.Equals(Guid.Empty) && !string.IsNullOrEmpty(entry.From) && !string.IsNullOrEmpty(entry.To)
                && string.Equals(name, Path.GetFileName(PathOf(entry.BookmarkId)), StringComparison.OrdinalIgnoreCase))
            {
                entries[entry.BookmarkId] = entry;
                continue;
            }

            if (IdOf(name) is { } id)
            {
                unreadable[id] = name;
                if (text is null)
                {
                    _logger.LogWarning("A journal entry of a clip file rename is too large to be read ({File}, over 64 KB): the clip of bookmark {BookmarkId} is left as it is (nothing deletes, forgets or moves it) until the file is removed; see Diagnostics' ClipRenames", name, id);
                }
                else
                {
                    _logger.LogWarning("A journal entry of a clip file rename can't be read ({File}): the clip of bookmark {BookmarkId} is left as it is (nothing deletes, forgets or moves it) until the file is removed; see Diagnostics' ClipRenames", name, id);
                }
            }
            else
            {
                withoutId.Add(name);
                _logger.LogWarning("A file in the journal of clip file renames can't be read and names no bookmark ({File}); it is left as it is", name);
            }
        }

        if (entries.Count > 0)
        {
            _logger.LogInformation("Found {Count} clip file renames that weren't finished (the server stopped during one, or a write failed); each is finished once its clip folder can be reached", entries.Count);
        }

        _unreadable = unreadable;
        _unreadableWithoutId = withoutId;
        _entries = entries;
        return entries;
    }

    // What a journal write that stopped half way left (its .tmp), deleted at the first read: no write of this process is under
    // way before it (each takes the journal's lock, which the first read holds).
    private void DeleteLeftovers()
    {
        try
        {
            var deleted = _fileSystem.DeleteRenameJournalLeftovers(_folder, FilePrefix);
            if (deleted > 0)
            {
                _logger.LogInformation("Deleted {Count} unfinished writes of the journal of clip file renames (.tmp files)", deleted);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the unfinished writes of the journal of clip file renames in {Folder}", _folder);
        }
    }

    // The record's id an entry's file name carries: clip-rename-<32 hex digits>.json.
    private static Guid? IdOf(string fileName)
    {
        if (!fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var id = fileName[FilePrefix.Length..^FileSuffix.Length];
        return id.Length == 32 && Guid.TryParseExact(id, "N", out var parsed) && !parsed.Equals(Guid.Empty) ? parsed : null;
    }
}
