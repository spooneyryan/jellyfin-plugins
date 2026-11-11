using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A restored or imported record named again after its clip file's rename: a snapshot or an export from before a rename
/// holds the clip's old path, which the file has left, so the record would read "File missing" (the sweep marking it,
/// the retention archiving it 30 days on) while the renamed file sits in the clip folder with no record naming it, and
/// a delete of the bookmark with its clip would leave that file behind. Run on the document as it is about to be
/// written: an admin's Restore snapshot (<c>BookmarkAdminTools.AfterRestore</c>'s write), the restore at load
/// (<c>BookmarkStore.AfterRestoreAtLoad</c>), an import (Replace and Merge, <c>BookmarkImportAdapter</c>), and at
/// startup and daily from the history alone (a clip folder that was away at the restore).
/// </summary>
/// <remarks>
/// <para>A record is looked at only when there is something to relink it to (the file system is asked about no other).</para>
/// <list type="number">
/// <item>Its clip's path is <see cref="ClipPathVerdict.Missing"/> (its clip folder is there, the file isn't), and the data
/// before (an admin's restore, an import) had the same record naming another file in the same folder that is there: the
/// record takes that file's path, size and range (a rename, or a remake, made after the snapshot); else</item>
/// <item>the history (<see cref="ClipRenameHistory"/>) has renames of that record from its path: followed from rename to
/// rename (a chain of renames) to a path that holds the renamed file by its identity (<see cref="ClipFileIdentity.IsSameFile"/>;
/// a rename whose identity couldn't be read takes the file there). The chain goes on past a name that was used again (a
/// file there that isn't the renamed one, or one another record names or that waits to be deleted) and takes a later
/// rename's file that is.</item>
/// </list>
/// <para><b>A stranger to its file</b> (a restore or an import only, never the startup's and the daily maintenance's
/// relink of the data as it is, where a remade clip may sit under a name an old rename left): a record whose path holds
/// a file that is another record's or another file. Either another record names that path and named it in the data
/// before while this one didn't (an Import Merge of an old export, the file's owner kept), or the history's last rename
/// of this record from that path recorded another file than the one there now, and the record's clip is older than that
/// rename (its file was renamed away and the name used again, by another user's clip perhaps). It is judged before the
/// others, and relinked as above when its renamed file can be found; otherwise it lets go of the file instead of
/// sharing it: its clip keeps no file and reads Failed with <see cref="LetGoMessage"/>, and the other record keeps the
/// file (a record without a path can't read "File missing", and any path would be someone else's).</para>
/// <para>Never a file that another record names, nor one waiting to be deleted (two records must never name one file, and the
/// retry would delete it); nothing is moved, deleted or written but the records returned. A record named again loses its
/// missing mark. Logged with the counts at Information (a Warning for the records let go), each record's id and paths at
/// Debug.</para>
/// </remarks>
internal static class ClipRenameRelink
{
    /// <summary>
    /// The most renames one record's chain is followed through.
    /// </summary>
    public const int MaxSteps = 32;

    /// <summary>
    /// The error of a clip whose record let go of a file it would have shared with another record.
    /// </summary>
    public const string LetGoMessage = "After a restore or an import this clip's file couldn't be told apart from another clip's, so it was let go of; the other clip keeps the file. Make the clip again.";

    /// <summary>
    /// Names renamed clip files again in a restored or imported document (see the class remarks).
    /// </summary>
    /// <param name="document">The document as it is about to be written.</param>
    /// <param name="before">The document the data had before (an admin's restore, an import), or <c>null</c>.</param>
    /// <param name="history">The renames finished lately, oldest first.</param>
    /// <param name="fileSystem">The file system the paths are judged on.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="relinked">How many records name their renamed file again.</param>
    /// <returns>The document to write (the same one when nothing changed).</returns>
    public static BookmarkStoreDocument Apply(
        BookmarkStoreDocument document,
        BookmarkStoreDocument? before,
        IReadOnlyList<ClipRenameHistoryEntry> history,
        IClipFileSystem fileSystem,
        ILogger logger,
        out int relinked)
        => Apply(document, before, history, fileSystem, logger, restored: true, out relinked, out _);

    /// <summary>
    /// Names renamed clip files again in a document (see the class remarks).
    /// </summary>
    /// <param name="document">The document as it is about to be written.</param>
    /// <param name="before">The document the data had before (an admin's restore, an import), or <c>null</c>.</param>
    /// <param name="history">The renames finished lately, oldest first.</param>
    /// <param name="fileSystem">The file system the paths are judged on.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="restored">Whether the document's records come from a restore or an import (records that are strangers to
    /// their files are judged), not the data as it is.</param>
    /// <param name="relinked">How many records name their renamed file again.</param>
    /// <param name="letGo">How many records let go of a file another record has.</param>
    /// <returns>The document to write (the same one when nothing changed).</returns>
    public static BookmarkStoreDocument Apply(
        BookmarkStoreDocument document,
        BookmarkStoreDocument? before,
        IReadOnlyList<ClipRenameHistoryEntry> history,
        IClipFileSystem fileSystem,
        ILogger logger,
        bool restored,
        out int relinked,
        out int letGo)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);
        relinked = 0;
        letGo = 0;
        var renames = history.Where(e => e is not null).GroupBy(e => e.BookmarkId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.FinishedUtc).ToList());
        var previous = before?.Bookmarks.Where(b => b?.Clip?.FilePath is { Length: > 0 }).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.Last());
        if (renames.Count == 0 && previous is not { Count: > 0 })
        {
            return document;
        }

        // Who names each path in the document (counted: two records can, as 1.1.2 could leave them), and who named it before.
        var named = new Dictionary<string, List<Guid>>(ClipRootPaths.Comparer);
        var paths = new string?[document.Bookmarks.Count];
        for (var i = 0; i < document.Bookmarks.Count; i++)
        {
            var record = document.Bookmarks[i];
            if (record?.Clip is not null && ClipFileGuard.TryGetFullPath(record.Clip.FilePath, out var full))
            {
                paths[i] = full;
                Name(named, full, record.Id);
            }
        }

        var owners = new Dictionary<string, HashSet<Guid>>(ClipRootPaths.Comparer);
        foreach (var record in before?.Bookmarks ?? [])
        {
            if (record?.Clip is not null && ClipFileGuard.TryGetFullPath(record.Clip.FilePath, out var full))
            {
                (owners.TryGetValue(full, out var ids) ? ids : owners[full] = []).Add(record.Id);
            }
        }

        var waiting = new HashSet<string>(ClipRootPaths.Comparer);
        foreach (var pending in document.PendingFileDeletes)
        {
            if (ClipFileGuard.TryGetFullPath(pending?.Path, out var full))
            {
                waiting.Add(full);
            }
        }

        bool Free(string path) => !named.ContainsKey(path) && !waiting.Contains(path);

        var roots = document.ClipRoots;
        var verdicts = new Dictionary<string, ClipPathCheck>(ClipRootPaths.Comparer);
        ClipPathCheck Look(string path) => verdicts.TryGetValue(path, out var known) ? known : verdicts[path] = ClipFileGuard.Check(path, roots, fileSystem);

        // The strangers first (they let go of the paths they name before the others' chains are followed), then the records
        // whose file is gone.
        var strangers = new List<int>();
        var missing = new List<int>();
        for (var i = 0; i < document.Bookmarks.Count; i++)
        {
            if (paths[i] is not { } path)
            {
                continue;
            }

            var record = document.Bookmarks[i];
            var chain = renames.GetValueOrDefault(record.Id);
            var hasEarlier = Earlier(previous, record, path) is not null;
            var fromHere = chain?.Exists(e => ClipRootPaths.Comparer.Equals(e.From, path)) == true;
            var shared = restored && IsSharedWithItsOwner(record.Id, path, named, owners);
            if (!hasEarlier && !fromHere && !shared)
            {
                continue;
            }

            var check = Look(path);
            if (check.Verdict == ClipPathVerdict.Missing)
            {
                if (hasEarlier || fromHere)
                {
                    missing.Add(i);
                }
            }
            else if (restored && check.Verdict == ClipPathVerdict.Exists && (shared || (fromHere && IsRenamedAway(record, path, chain!, fileSystem))))
            {
                strangers.Add(i);
            }
        }

        if (strangers.Count == 0 && missing.Count == 0)
        {
            return document;
        }

        List<Bookmark>? records = null;
        var strangerSet = strangers.ToHashSet();
        foreach (var i in strangers.Concat(missing))
        {
            var record = document.Bookmarks[i];
            var clip = record.Clip!;
            var path = paths[i]!;
            var stranger = strangerSet.Contains(i);
            if (stranger)
            {
                // It lets go of the path it names (another file, or another record's): never taken back below.
                Unname(named, path, record.Id);
            }

            ClipInfo? next = null;
            if (Earlier(previous, record, path) is { } found && Free(found.Path) && Look(found.Path) is { Verdict: ClipPathVerdict.Exists, FullPath: { } there })
            {
                // The file the data named just before: its path, size and range.
                next = clip with
                {
                    FilePath = there,
                    FileSize = found.Clip.FileSize ?? fileSystem.GetFileIdentity(there)?.Length ?? clip.FileSize,
                    FileStartTicks = found.Clip.FileStartTicks ?? clip.FileStartTicks,
                    FileEndTicks = found.Clip.FileEndTicks ?? clip.FileEndTicks,
                    MissingSinceUtc = null
                };
            }
            else if (renames.GetValueOrDefault(record.Id) is { } chain && Follow(chain, path, Look, fileSystem, Free) is { } renamed)
            {
                next = clip with { FilePath = renamed.Path, FileSize = renamed.Length, MissingSinceUtc = null };
            }

            if (next is not null)
            {
                records ??= document.Bookmarks.ToList();
                records[i] = record with { Clip = next };
                Name(named, next.FilePath!, record.Id);
                relinked++;
                logger.LogDebug("Bookmark {BookmarkId}'s clip named {From}, which its file had left: it names {To} again", record.Id, path, next.FilePath);
                continue;
            }

            if (stranger)
            {
                // Never two records on one file: this one keeps no file, and says why.
                records ??= document.Bookmarks.ToList();
                records[i] = record with
                {
                    Clip = clip with
                    {
                        Status = BookmarkClipStatus.Failed,
                        Error = LetGoMessage,
                        FilePath = null,
                        FileSize = null,
                        FileStartTicks = null,
                        FileEndTicks = null,
                        MissingSinceUtc = null
                    }
                };
                letGo++;
                logger.LogDebug("Bookmark {BookmarkId}'s clip named {Path}, a file another clip has: it was let go of", record.Id, path);
            }
        }

        if (records is null)
        {
            return document;
        }

        if (relinked > 0)
        {
            logger.LogInformation("{Count} restored or imported clips named a path their file had left after a rename: they name their renamed file again", relinked);
        }

        if (letGo > 0)
        {
            logger.LogWarning("{Count} restored or imported clips named a file another clip has now (its name used again since): they keep no file, and their owners can make them again", letGo);
        }

        return document with { Bookmarks = records };
    }

    // The file the data before had this record name, when it is another one in the same folder.
    private static (ClipInfo Clip, string Path)? Earlier(Dictionary<Guid, Bookmark>? previous, Bookmark record, string path)
        => previous?.GetValueOrDefault(record.Id) is { Clip: { } prior } && ClipFileGuard.TryGetFullPath(prior.FilePath, out var priorPath)
            && !ClipRootPaths.Comparer.Equals(priorPath, path)
            && string.Equals(Path.GetDirectoryName(priorPath), Path.GetDirectoryName(path), ClipRootPaths.Comparison)
            ? (prior, priorPath)
            : null;

    // Whether another record names the path too and named it before, while this one didn't: that one is the file's owner.
    private static bool IsSharedWithItsOwner(Guid id, string path, Dictionary<string, List<Guid>> named, Dictionary<string, HashSet<Guid>> owners)
        => named.TryGetValue(path, out var namers) && namers.Count > 1
            && owners.TryGetValue(path, out var owner) && !owner.Contains(id)
            && namers.Exists(other => !other.Equals(id) && owner.Contains(other));

    // Whether the record's file left this path in a rename made after its clip was, and another file is there now.
    private static bool IsRenamedAway(Bookmark record, string path, List<ClipRenameHistoryEntry> chain, IClipFileSystem fileSystem)
    {
        if (chain.LastOrDefault(e => ClipRootPaths.Comparer.Equals(e.From, path)) is not { File: { } renamedFile } rename)
        {
            return false;
        }

        // A clip made after that rename (a remake that took the name back) is the file's own.
        var clip = record.Clip!;
        if ((clip.CompletedAt ?? clip.RequestedAt) > rename.FinishedUtc)
        {
            return false;
        }

        return fileSystem.GetFileIdentity(path) is { } now && !ClipFileIdentity.IsSameFile(renamedFile, now);
    }

    // The rename chain of one record from a path: each rename's new path in turn, until one holds the renamed file by its
    // identity, which no record names and isn't waiting to be deleted. A file there that is another one, or someone else's,
    // is a name used again since: the chain goes on from it. A folder that can't be reached, a path that isn't a clip
    // file's, or a loop ends the chain without a relink.
    private static (string Path, long Length)? Follow(List<ClipRenameHistoryEntry> chain, string path, Func<string, ClipPathCheck> look, IClipFileSystem fileSystem, Func<string, bool> free)
    {
        var current = path;
        var seen = new HashSet<string>(ClipRootPaths.Comparer) { path };
        for (var step = 0; step < MaxSteps; step++)
        {
            var rename = chain.LastOrDefault(e => ClipRootPaths.Comparer.Equals(e.From, current));
            if (rename is null || !seen.Add(rename.To))
            {
                return null;
            }

            var check = look(rename.To);
            if (check.Verdict == ClipPathVerdict.Exists
                && free(check.FullPath!)
                && fileSystem.GetFileIdentity(check.FullPath!) is { } identity
                && (rename.File is null || ClipFileIdentity.IsSameFile(rename.File, identity)))
            {
                return (check.FullPath!, identity.Length);
            }

            if (check.Verdict is not (ClipPathVerdict.Exists or ClipPathVerdict.Missing))
            {
                return null;
            }

            current = check.FullPath!;
        }

        return null;
    }

    private static void Name(Dictionary<string, List<Guid>> named, string path, Guid id)
        => (named.TryGetValue(path, out var ids) ? ids : named[path] = []).Add(id);

    private static void Unname(Dictionary<string, List<Guid>> named, string path, Guid id)
    {
        if (named.TryGetValue(path, out var ids) && ids.Remove(id) && ids.Count == 0)
        {
            named.Remove(path);
        }
    }
}
