using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// Finds a junction or symbolic link on the way from a library location to a folder in it,
/// before anything below them is listed, opened, probed or asked for its volume. The location and
/// each folder below it down to the folder asked, both included, are read from the top with
/// <see cref="File.GetAttributes(string)"/>, which reads a link's own attributes and doesn't follow it, and the walk stops
/// at the first link: nothing below one is read.
/// </summary>
/// <remarks>
/// <para>A location that is itself a link counts (v1 fails closed; a later version may follow one, with evidence). Every
/// reparse point counts as a link, a server dedup file or a cloud-file placeholder too (v1; the MOUNT_POINT and SYMLINK
/// tags alone when there is a real case).</para>
/// <para>A folder that isn't there ends the walk with no link (the callers say it isn't there); one whose attributes
/// can't be read counts as a link. The folders above the location aren't read: the administrator chose the location.</para>
/// </remarks>
internal static class LinkGuard
{
    /// <summary>
    /// Finds the top-most link from a location down to a folder.
    /// </summary>
    /// <param name="location">The library location as configured; an empty one, or one that doesn't hold the folder,
    /// walks from the folder's drive root.</param>
    /// <param name="folder">The folder: a unit's root, a file set's directory, or the location itself.</param>
    /// <returns>The link, or null.</returns>
    public static FoundLink? Find(string location, string folder)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(folder);
        foreach (var (path, isLocation) in Segments(location, folder))
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new FoundLink(path, isLocation, true);
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new FoundLink(path, isLocation, false);
            }
        }

        return null;
    }

    // The location (or the drive root), then each folder below it down to the folder, top first.
    private static IEnumerable<(string Path, bool IsLocation)> Segments(string location, string folder)
    {
        var full = folder.Replace('/', '\\');
        var inLocation = location.Length > 0 && PathRules.IsSameOrUnder(full, location);
        var top = inLocation ? PathRules.TrimEnd(location.Replace('/', '\\')) : Path.GetPathRoot(full) ?? string.Empty;
        if (top.Length == 0)
        {
            yield break;
        }

        yield return (top, inLocation);
        var prefix = top.TrimEnd('\\').Length;
        var rest = full.TrimEnd('\\');
        var current = top;
        foreach (var name in (rest.Length > prefix ? rest[(prefix + 1)..] : string.Empty).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Join(current, name);
            yield return (current, false);
        }
    }
}
