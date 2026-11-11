using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings.Lifecycle;

/// <summary>
/// Whether a rating may detach from an item the library no longer has, or must stay attached because the item's library
/// folder is unavailable (the lifecycle's data-safety rule): a rating is never detached because a drive is unmounted (an
/// external drive, for example), a mount point is empty, a folder can't be read, or Jellyfin didn't recognise a folder whose files are
/// still there. One instance answers for one batch or one sweep: it reads the libraries' folders once
/// (<c>ILibraryManager.GetVirtualFolders</c>) and looks at each folder on disk once.
/// </summary>
/// <remarks>
/// <para><b>The rule</b>, for the item's stored path:</para>
/// <list type="number">
/// <item>No library folder is known (none configured, or they can't be read): keep it attached (<see cref="NoLibraryFolders"/>).</item>
/// <item>The path is inside a library folder: when that folder is missing, can't be read or is empty, keep it attached
/// (<see cref="FolderUnavailable"/>); when the path itself is still on disk, keep it attached (<see cref="StillOnDisk"/>: the
/// file is there, so the item will be back, or Jellyfin is ignoring it); when a folder between the two is there but can't
/// be read (a junction or share whose target is offline), keep it attached (<see cref="FolderUnavailable"/>); otherwise it
/// may detach.</item>
/// <item>No path, or a path in no library folder (a library removed, a folder taken out of one): keep it attached while any
/// library folder is unavailable (the item may have been on it), otherwise it may detach.</item>
/// </list>
/// <para>Core 10.11.5 skips a top-level library folder that is inaccessible or empty, but a folder whose listing fails part
/// way still reads as empty there (<c>Folder.ValidateChildrenInternal2</c>, fixed after 10.11), and its startup cleanup has
/// removed items before; this check doesn't depend on either.</para>
/// </remarks>
internal sealed class LibraryAvailability
{
    /// <summary>The reason a rating stays attached: no library folder is known.</summary>
    public const string NoLibraryFolders = "NoLibraryFolders";

    /// <summary>The reason a rating stays attached: its library folder is missing, unreadable or empty.</summary>
    public const string FolderUnavailable = "LibraryFolderUnavailable";

    /// <summary>The reason a rating stays attached: its item's file or folder is still on disk.</summary>
    public const string StillOnDisk = "StillOnDisk";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly List<string> _folders;
    private readonly Dictionary<string, bool> _available = new(PathComparer);

    private LibraryAvailability(List<string> folders)
    {
        _folders = folders;
    }

    /// <summary>
    /// Gets how paths compare: without case on Windows.
    /// </summary>
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Gets the library folders, deepest first.
    /// </summary>
    public IReadOnlyList<string> Folders => _folders;

    /// <summary>
    /// Gets a value indicating whether any library folder is missing, unreadable or empty (or none is known).
    /// </summary>
    public bool AnyUnavailable => _folders.Count == 0 || _folders.Any(f => !IsAvailable(f));

    /// <summary>
    /// Reads the libraries' folders now.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="logger">Where a failed read is reported.</param>
    /// <returns>The check.</returns>
    public static LibraryAvailability Read(ILibraryManager library, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(logger);
        var folders = new List<string>();
        try
        {
            foreach (var folder in library.GetVirtualFolders() ?? [])
            {
                foreach (var location in folder?.Locations ?? [])
                {
                    if (TryNormalize(location, out var full))
                    {
                        folders.Add(full);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No folder known: every rating whose item is gone stays attached until they can be read.
            logger.LogWarning(ex, "Could not read the libraries' folders; ratings whose items are gone stay attached until they can be read");
            folders.Clear();
        }

        return new LibraryAvailability(folders.Distinct(PathComparer).OrderByDescending(f => f.Length).ToList());
    }

    /// <summary>
    /// Gets the library folder a path is in (the deepest one), for the mass-detach guard; null for a path in none.
    /// </summary>
    /// <param name="path">The item's stored path, or null.</param>
    /// <returns>The library folder, normalised, or null.</returns>
    public string? FolderOf(string? path)
        => TryNormalize(path, out var full) ? _folders.FirstOrDefault(f => IsUnder(full, f)) : null;

    /// <summary>
    /// Whether two paths are in the same library folder (the deepest one of each), for the ratings the mass-detach guard
    /// holds, which may follow a rename inside their folder only; false when either is in none.
    /// </summary>
    /// <param name="path">One path, or null.</param>
    /// <param name="other">The other path, or null.</param>
    /// <returns>Whether they are.</returns>
    public bool InSameFolder(string? path, string? other)
        => FolderOf(path) is { } folder && FolderOf(other) is { } otherFolder && PathComparer.Equals(folder, otherFolder);

    /// <summary>
    /// Tells why a rating on an item at <paramref name="path"/> must stay attached, or null when it may detach.
    /// </summary>
    /// <param name="path">The item's stored path (<c>ItemPath</c>), or null.</param>
    /// <returns><see cref="NoLibraryFolders"/>, <see cref="FolderUnavailable"/>, <see cref="StillOnDisk"/>, or null.</returns>
    public string? HoldReason(string? path)
    {
        if (_folders.Count == 0)
        {
            return NoLibraryFolders;
        }

        if (TryNormalize(path, out var full) && _folders.FirstOrDefault(f => IsUnder(full, f)) is { } folder)
        {
            if (!IsAvailable(folder))
            {
                return FolderUnavailable;
            }

            if (Exists(full))
            {
                return StillOnDisk;
            }

            // A folder between the library folder and the item that is there but can't be read (a junction or share whose
            // target is offline): the item may well be behind it.
            return UnreadableAncestor(full, folder) ? FolderUnavailable : null;
        }

        return AnyUnavailable ? FolderUnavailable : null;
    }

    /// <summary>
    /// Normalises a path for comparison: full, without a trailing separator (a root keeps its own).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="full">The normalised path.</param>
    /// <returns>Whether it is a path.</returns>
    internal static bool TryNormalize(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="path"/> is inside <paramref name="folder"/> (both normalised).
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>Whether it is.</returns>
    internal static bool IsUnder(string path, string folder)
    {
        if (path.Length <= folder.Length || !path.StartsWith(folder, PathComparison))
        {
            return false;
        }

        return Path.EndsInDirectorySeparator(folder) || path[folder.Length] == Path.DirectorySeparatorChar || path[folder.Length] == Path.AltDirectorySeparatorChar;
    }

    // Walks up from the item's folder to the library folder: the first folder that exists decides (a normal deletion
    // leaves a readable one); one that exists but can't be listed means the item may be behind it.
    private static bool UnreadableAncestor(string path, string folder)
    {
        try
        {
            for (var dir = Path.GetDirectoryName(path); dir is not null && IsUnder(dir, folder); dir = Path.GetDirectoryName(dir))
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                using var entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
                _ = entries.MoveNext();
                return false;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    // Hidden and System both: what Windows keeps at a volume's root. On Linux and macOS nothing is System, so every entry
    // counts.
    private static bool IsVolumeSystemEntry(FileSystemInfo entry)
        => (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System);

    private static bool Exists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Can't tell: as if it were there, so the rating stays attached.
            return true;
        }
    }

    // A library folder is available when it exists, can be listed and has at least one entry (core's IsAccessible) besides
    // the hidden system folders every Windows volume root has ($RECYCLE.BIN, System Volume Information): a drive letter given
    // to an empty volume, or a mount point that is only a volume's root, is no library.
    private bool IsAvailable(string folder)
    {
        if (_available.TryGetValue(folder, out var known))
        {
            return known;
        }

        bool available;
        try
        {
            var info = new DirectoryInfo(folder);
            available = info.Exists && info.EnumerateFileSystemInfos().Any(e => !IsVolumeSystemEntry(e));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            available = false;
        }

        _available[folder] = available;
        return available;
    }
}
