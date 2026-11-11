using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.Bookmarks.Clips;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// The real file system with faults on demand (for the file-operation tests): a delete or move that throws what Windows
/// throws for a file in use, paths that read as reparse points, attributes that can't be read. Every call is recorded. For
/// clip rename: a move of a clip file (not a <c>.partial</c>) with <see cref="RenameJournalFolder"/> set asserts that the
/// rename's journal entry is on disk and readable at that moment (the crash guarantee: the entry before the move), flushes are
/// recorded and can fail, and a file's identity can be changed.
/// </summary>
internal sealed class FakeClipFileSystem : IClipFileSystem
{
    private readonly ConcurrentQueue<string> _deletes = new();
    private readonly ConcurrentQueue<(string Source, string Destination)> _moves = new();
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _markerWrites = new();
    private readonly ConcurrentQueue<string> _flushes = new();

    /// <summary>Gets the HResult of <c>ERROR_SHARING_VIOLATION</c>.</summary>
    public static int SharingViolationHResult => unchecked((int)0x80070020);

    /// <summary>Gets the HResult of <c>ERROR_LOCK_VIOLATION</c>.</summary>
    public static int LockViolationHResult => unchecked((int)0x80070021);

    /// <summary>
    /// Gets or sets what a delete of a path throws on its given attempt (1-based, counted per path), if anything; the
    /// real delete runs otherwise.
    /// </summary>
    public Func<string, int, Exception?>? DeleteFailure { get; set; }

    /// <summary>Gets or sets what a move of a source path throws on its given attempt, if anything.</summary>
    public Func<string, int, Exception?>? MoveFailure { get; set; }

    /// <summary>Gets or sets what reading a path's attributes throws, if anything.</summary>
    public Func<string, Exception?>? AttributesFailure { get; set; }

    /// <summary>Gets or sets what asking whether a folder is there throws, if anything (the real one never throws).</summary>
    public Func<string, Exception?>? FolderFailure { get; set; }

    /// <summary>Gets or sets what listing a clip folder throws, if anything, before its first file.</summary>
    public Func<string, Exception?>? ListFailure { get; set; }

    /// <summary>Gets or sets what reading a clip folder's marker file throws, if anything.</summary>
    public Func<string, Exception?>? MarkerReadFailure { get; set; }

    /// <summary>Gets or sets what writing a clip folder's marker file throws, if anything.</summary>
    public Func<string, Exception?>? MarkerWriteFailure { get; set; }

    /// <summary>
    /// Gets or sets the plugin's data folder: when set, a move of a clip file (a rename's, or its move back) throws unless the
    /// journal there holds an entry for that move (either way round) that can be read, as the server reads it.
    /// </summary>
    public string? RenameJournalFolder { get; set; }

    /// <summary>
    /// Gets or sets what a move of a source path throws after it moved the file (a share's answer lost on the way), if
    /// anything.
    /// </summary>
    public Func<string, Exception?>? FailAfterMove { get; set; }

    /// <summary>Gets or sets whether a flush of a path fails (returns false), if anything.</summary>
    public Func<string, bool>? FlushFailure { get; set; }

    /// <summary>Gets or sets a change to the identity a path reads with (the real one given), if anything.</summary>
    public Func<string, ClipFileIdentity?, ClipFileIdentity?>? IdentityFilter { get; set; }

    /// <summary>Gets every flush asked for, in order.</summary>
    public IReadOnlyList<string> Flushes => _flushes.ToList();

    /// <summary>Gets the clip folders whose marker file was written, in order (failed writes included).</summary>
    public IReadOnlyList<string> MarkerWrites => _markerWrites.ToList();

    /// <summary>Gets the paths that read as reparse points (a junction, a symbolic link), compared without regard to case.</summary>
    public ISet<string> ReparsePoints { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets every delete asked for, in order.</summary>
    public IReadOnlyList<string> Deletes => _deletes.ToList();

    /// <summary>Gets every move asked for, in order.</summary>
    public IReadOnlyList<(string Source, string Destination)> Moves => _moves.ToList();

    /// <summary>
    /// Makes an exception like the one .NET throws when Windows refuses a file in use.
    /// </summary>
    /// <param name="path">The file, which the message names as .NET's does.</param>
    /// <param name="lockViolation">A lock violation instead of a sharing violation.</param>
    /// <returns>The exception.</returns>
    public static IOException InUse(string path, bool lockViolation = false)
        => new($"The process cannot access the file '{path}' because it is being used by another process.", lockViolation ? LockViolationHResult : SharingViolationHResult);

    /// <summary>Makes every delete of a path fail as in use.</summary>
    /// <param name="path">The path.</param>
    public void HoldOpen(string path)
    {
        var held = DeleteFailure;
        DeleteFailure = (p, attempt) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ? InUse(p) : held?.Invoke(p, attempt);
        var moves = MoveFailure;
        MoveFailure = (p, attempt) => string.Equals(p, path, StringComparison.OrdinalIgnoreCase) ? InUse(p) : moves?.Invoke(p, attempt);
    }

    /// <summary>Lets every delete and move of every path through again.</summary>
    public void Release()
    {
        DeleteFailure = null;
        MoveFailure = null;
    }

    public FileAttributes? GetAttributes(string path)
    {
        if (AttributesFailure?.Invoke(path) is { } failure)
        {
            throw failure;
        }

        var attributes = PhysicalClipFileSystem.Instance.GetAttributes(path);
        return ReparsePoints.Contains(Path.TrimEndingDirectorySeparator(path))
            ? (attributes ?? FileAttributes.Directory) | FileAttributes.ReparsePoint
            : attributes;
    }

    public bool FolderExists(string path)
        => FolderFailure?.Invoke(path) is { } failure ? throw failure : PhysicalClipFileSystem.Instance.FolderExists(path);

    public IEnumerable<ClipFolderFile> ListClipFolder(string path)
        => ListFailure?.Invoke(path) is { } failure ? throw failure : PhysicalClipFileSystem.Instance.ListClipFolder(path);

    public void DeleteFile(string path)
    {
        _deletes.Enqueue(path);
        var attempt = _attempts.AddOrUpdate("delete:" + path, 1, (_, n) => n + 1);
        if (DeleteFailure?.Invoke(path, attempt) is { } failure)
        {
            throw failure;
        }

        PhysicalClipFileSystem.Instance.DeleteFile(path);
    }

    public void MoveFile(string source, string destination)
    {
        if (RenameJournalFolder is { } folder && !source.EndsWith(ClipFileGuard.PartialExtension, StringComparison.OrdinalIgnoreCase) && !IsJournalled(folder, source, destination))
        {
            throw new InvalidOperationException($"A clip file was moved before its rename was in the journal: {source} -> {destination}");
        }

        _moves.Enqueue((source, destination));
        var attempt = _attempts.AddOrUpdate("move:" + source, 1, (_, n) => n + 1);
        if (MoveFailure?.Invoke(source, attempt) is { } failure)
        {
            throw failure;
        }

        PhysicalClipFileSystem.Instance.MoveFile(source, destination);
        if (FailAfterMove?.Invoke(source) is { } late)
        {
            throw late;
        }
    }

    // A remade clip over its own file (Bookmarks 1.2.1): a move, which fails as one of either path does (a file held open).
    public void ReplaceFile(string source, string destination)
    {
        _moves.Enqueue((source, destination));
        var attempt = _attempts.AddOrUpdate("move:" + source, 1, (_, n) => n + 1);
        if ((MoveFailure?.Invoke(source, attempt) ?? MoveFailure?.Invoke(destination, attempt)) is { } failure)
        {
            throw failure;
        }

        PhysicalClipFileSystem.Instance.ReplaceFile(source, destination);
    }

    public DateTime? GetLastWriteTimeUtc(string path) => PhysicalClipFileSystem.Instance.GetLastWriteTimeUtc(path);

    public bool FlushFile(string path)
    {
        _flushes.Enqueue(path);
        return FlushFailure?.Invoke(path) != true && PhysicalClipFileSystem.Instance.FlushFile(path);
    }

    public ClipFileIdentity? GetFileIdentity(string path)
    {
        var identity = PhysicalClipFileSystem.Instance.GetFileIdentity(path);
        return IdentityFilter is { } filter ? filter(path, identity) : identity;
    }

    public string? ReadRootMarker(string folder)
        => MarkerReadFailure?.Invoke(folder) is { } failure ? throw failure : PhysicalClipFileSystem.Instance.ReadRootMarker(folder);

    public void WriteRootMarker(string folder, string text)
    {
        _markerWrites.Enqueue(folder);
        if (MarkerWriteFailure?.Invoke(folder) is { } failure)
        {
            throw failure;
        }

        PhysicalClipFileSystem.Instance.WriteRootMarker(folder, text);
    }

    // Whether the journal in a folder holds an entry, readable as the server reads it, for a move (either way round).
    private static bool IsJournalled(string folder, string source, string destination)
    {
        if (!Directory.Exists(folder))
        {
            return false;
        }

        foreach (var file in Directory.GetFiles(folder, ClipRenameJournal.FilePrefix + "*.json"))
        {
            ClipRenameEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<ClipRenameEntry>(File.ReadAllText(file));
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not null
                && ((string.Equals(entry.From, source, StringComparison.Ordinal) && string.Equals(entry.To, destination, StringComparison.Ordinal))
                    || (string.Equals(entry.From, destination, StringComparison.Ordinal) && string.Equals(entry.To, source, StringComparison.Ordinal))))
            {
                return true;
            }
        }

        return false;
    }
}
