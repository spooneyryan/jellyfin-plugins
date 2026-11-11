using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Decides whether a stored clip path may be touched at all: every delete, move, serve
/// and pending-delete retry of <see cref="ClipFileOps"/>, and an import's clip paths, go through it first. A record's path
/// is not trusted just because the server wrote it: an import or a hand edit can bring any path.
/// </summary>
/// <remarks>
/// <para>A path passes when, after <see cref="Path.GetFullPath(string)"/> (a relative path resolves against the working
/// directory, as the file operations would), it is not a device path, its file name is one Bookmarks writes (<c>.mp4</c> or
/// <c>.mkv</c>; the worker's own temporary <c>.partial</c> of one where the caller allows it), it lies strictly under one of
/// the recorded clip folders (<c>ClipRoots</c>; separator-aware, and without regard to case on Windows, as
/// <see cref="ClipRootPaths"/> compares), and neither the file nor any folder between that clip folder and the file is a
/// reparse point. The clip folder itself may be one (an admin's folder on another drive).</para>
/// <para>A path in a clip folder that isn't there ("the file's root folder does not exist", an unplugged drive) is
/// <see cref="ClipPathVerdict.FolderUnavailable"/>, never <see cref="ClipPathVerdict.Missing"/>: the file is probably fine
/// on a drive that isn't connected, so an unplugged drive never causes a deletion or a forgotten file.</para>
/// <para>The file name rule is an addition to the clip folder rule: a clip folder can be broad (a drive root, a folder a
/// 1.0 record pointed into), and no clip file Bookmarks writes has another extension, so no other file in it can be
/// reached.</para>
/// </remarks>
internal static class ClipFileGuard
{
    /// <summary>
    /// The extension of a clip being written (<c>&lt;clip file&gt;.partial</c>).
    /// </summary>
    public const string PartialExtension = ".partial";

    /// <summary>
    /// Checks a path against the clip folders.
    /// </summary>
    /// <param name="path">The stored path.</param>
    /// <param name="roots">The recorded clip folders (<c>ClipRoots</c>).</param>
    /// <param name="fileSystem">The file system, for the clip folder's and the reparse-point checks (only read: the My Clips
    /// list judges through one request's view of it, and keeps the verdicts for a while, <see cref="ClipStateCache"/>).</param>
    /// <param name="allowPartial">Whether a clip's temporary <c>.partial</c> file passes too (the worker's own files).</param>
    /// <returns>The verdict, with the full path and its clip folder when the path is in one.</returns>
    public static ClipPathCheck Check(string? path, IEnumerable<string> roots, IClipPathProbe fileSystem, bool allowPartial = false)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (!TryGetFullPath(path, out var full))
        {
            return new ClipPathCheck(ClipPathVerdict.NotAPath, null, null);
        }

        if (!IsClipFileName(Path.GetFileName(full), allowPartial))
        {
            return new ClipPathCheck(ClipPathVerdict.NotAClipFile, full, null);
        }

        var root = FindRoot(full, roots);
        if (root is null)
        {
            return new ClipPathCheck(ClipPathVerdict.OutsideClipFolders, full, null);
        }

        // The clip folder itself first: without it (an unplugged drive whose letter is gone, a folder moved or
        // removed) every path under it reads as missing, although the file is probably fine where the folder is. Such a
        // file is never missing, so nothing deletes it or forgets it.
        bool folderThere;
        try
        {
            folderThere = fileSystem.FolderExists(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ClipPathCheck(ClipPathVerdict.Unreadable, full, root, ex);
        }

        if (!folderThere)
        {
            return new ClipPathCheck(ClipPathVerdict.FolderUnavailable, full, root);
        }

        // Every existing step from the clip folder down to the file: none may be a reparse point.
        var relative = full[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);
            FileAttributes? attributes;
            try
            {
                attributes = fileSystem.GetAttributes(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ClipPathCheck(ClipPathVerdict.Unreadable, full, root, ex);
            }

            var last = i == segments.Length - 1;
            if (attributes is not { } found)
            {
                return new ClipPathCheck(ClipPathVerdict.Missing, full, root);
            }

            if (found.HasFlag(FileAttributes.ReparsePoint))
            {
                return new ClipPathCheck(ClipPathVerdict.ReparsePoint, full, root);
            }

            if (last)
            {
                return found.HasFlag(FileAttributes.Directory)
                    ? new ClipPathCheck(ClipPathVerdict.NotAFile, full, root)
                    : new ClipPathCheck(ClipPathVerdict.Exists, full, root);
            }

            if (!found.HasFlag(FileAttributes.Directory))
            {
                // A file where a folder should be: the path can't exist.
                return new ClipPathCheck(ClipPathVerdict.Missing, full, root);
            }
        }

        return new ClipPathCheck(ClipPathVerdict.NotAPath, full, root);
    }

    /// <summary>
    /// Whether a file name is one Bookmarks gives a clip file: a stem and <c>.mp4</c> or <c>.mkv</c> (any case), optionally
    /// followed by <see cref="PartialExtension"/> where allowed.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="allowPartial">Whether the temporary <c>.partial</c> name passes.</param>
    /// <returns>Whether it is a clip file's name.</returns>
    public static bool IsClipFileName(string? fileName, bool allowPartial)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        if (allowPartial && fileName.EndsWith(PartialExtension, StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^PartialExtension.Length];
        }

        var extension = Path.GetExtension(fileName);
        return Path.GetFileNameWithoutExtension(fileName).Length > 0
            && (extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the full path the guard judges a stored path by: <see cref="Path.GetFullPath(string)"/>, refusing blanks, device
    /// paths (they skip the normalisation that removes <c>..</c>) and anything that isn't a fully qualified path afterwards
    /// (<see cref="ClipPathVerdict.NotAPath"/>).
    /// </summary>
    /// <param name="path">The stored path.</param>
    /// <param name="full">The full path, when it is one.</param>
    /// <returns>Whether the path is one.</returns>
    internal static bool TryGetFullPath(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || IsDevicePath(path))
        {
            return false;
        }

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return false;
        }

        return Path.IsPathFullyQualified(full) && !IsDevicePath(full);
    }

    private static bool IsDevicePath(string path)
        => path.Length >= 4
            && (path[0] == '\\' || path[0] == '/')
            && (path[1] == '\\' || path[1] == '/')
            && (path[2] == '?' || path[2] == '.')
            && (path[3] == '\\' || path[3] == '/');

    // The longest recorded clip folder the path lies strictly under (a folder is not a file of its own).
    internal static string? FindRoot(string full, IEnumerable<string> roots)
    {
        string? best = null;
        foreach (var candidate in roots)
        {
            if (!ClipRootPaths.TryNormalize(candidate, out var root) || IsDevicePath(root))
            {
                continue;
            }

            if (full.Length > root.Length && ClipRootPaths.IsUnder(full, root) && (best is null || root.Length > best.Length))
            {
                best = root;
            }
        }

        return best;
    }
}
