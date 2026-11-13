using System;
using System.Collections.Generic;
using System.IO;
using Emby.Naming.Common;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// Walks a folder unit (<c>ForeignMediaGuard</c>): the manifest, every junction or symbolic link inside
/// (read, never followed), and anything that belongs to another item: a video (<c>NamingOptions.VideoFileExtensions</c>)
/// whose <c>FindByPath</c> item isn't the primary, one of its versions or parts, or one of its extras, or a folder that is
/// an item of its own (a nested movie, DVD folder or series). A video no item claims (an ignored sample) is fine.
/// </summary>
internal static class ForeignMediaGuard
{
    /// <summary>The most entries one unit folder may hold; a bigger one is not one movie's folder.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>
    /// Gets the listing options Move reads folders with: every entry (hidden and system ones and links too), none
    /// followed, nothing recursed.
    /// </summary>
    public static EnumerationOptions Listing { get; } = new() { AttributesToSkip = 0, IgnoreInaccessible = true, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

    /// <summary>
    /// Walks the tree.
    /// </summary>
    /// <param name="root">The unit folder.</param>
    /// <param name="library">The library manager (<c>FindByPath</c>).</param>
    /// <param name="ownIds">The ids of the unit's items.</param>
    /// <param name="ownPaths">The paths of the unit's items (ignoring case).</param>
    /// <param name="naming">Jellyfin's naming options.</param>
    /// <param name="maxEntries">The most entries read before the walk stops (<see cref="MaxEntries"/>; tests pass fewer).</param>
    /// <returns>What was found.</returns>
    public static UnitTree Walk(string root, ILibraryManager library, IReadOnlySet<Guid> ownIds, IReadOnlySet<string> ownPaths, NamingOptions naming, int maxEntries = MaxEntries)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(ownIds);
        ArgumentNullException.ThrowIfNull(ownPaths);

        var entries = new List<UnitEntry>();
        var links = new List<string>();
        var foreign = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", Listing))
            {
                if (entries.Count >= maxEntries)
                {
                    return new UnitTree(entries, links, foreign, true);
                }

                var relative = Path.GetRelativePath(root, entry.FullName);
                var isDirectory = entry.Attributes.HasFlag(FileAttributes.Directory);
                entries.Add(new UnitEntry(relative, isDirectory, isDirectory ? 0 : ((FileInfo)entry).Length, entry.LastWriteTimeUtc));
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    links.Add(entry.FullName);
                    continue;
                }

                if (isDirectory)
                {
                    if (IsOther(KitItemQueries.FindAnyByPath(library, entry.FullName, null), entry.FullName, ownIds, ownPaths))
                    {
                        foreign.Add(entry.FullName);
                    }

                    pending.Push(entry.FullName);
                }
                else if (SidecarMatcher.IsVideo(entry.Name, naming) && IsOther(KitItemQueries.FindAnyByPath(library, entry.FullName, false), entry.FullName, ownIds, ownPaths))
                {
                    foreign.Add(entry.FullName);
                }
            }
        }

        return new UnitTree(entries, links, foreign, false);
    }

    private static bool IsOther(MediaBrowser.Controller.Entities.BaseItem? item, string path, IReadOnlySet<Guid> ownIds, IReadOnlySet<string> ownPaths)
        => item is not null && !ownIds.Contains(item.Id) && !ownPaths.Contains(path);
}
