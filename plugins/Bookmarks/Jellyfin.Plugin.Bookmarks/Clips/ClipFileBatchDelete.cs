using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A user's delete of several records' clip files, file first (as one delete does): the bulk delete of bookmarks
/// with their clips (<c>POST /Bookmarks/BulkDelete</c> with <c>DeleteClips</c>) and of clips (since 1.2.0,
/// <c>POST /Bookmarks/Clips/BulkDelete</c>). Each record's file is deleted inside the store's write of the records, so the
/// record checked is the one written, and only once it is gone does the record change; a file in use is tried again in the
/// next write, after the pause outside the store's lock, up to <see cref="ClipFileOps.Attempts"/> times. A path the guard
/// refuses and a clip folder that can't be reached are found before the first write, so a slow drive never holds the store's
/// lock, and nothing of them is touched: that answer comes from that first check alone (<see cref="ClipFileOps.Refusal"/>),
/// never from a second look that could delete the file outside the store's write.
/// </summary>
/// <remarks>
/// It writes only when a user asks (a read-only store refuses with 503 before the file is touched), so it asks no
/// <see cref="BackgroundWriteGate"/>. Each write's changed records go to <c>afterWrite</c> at once, before anything else can
/// end the request (a job of a bookmark already gone is stopped even when the request is aborted during a
/// pause).
/// </remarks>
internal static class ClipFileBatchDelete
{
    /// <summary>
    /// Deletes the clip files of the records with the given ids that are targets, and changes each record once its file is
    /// gone.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="files">The clip file operations.</param>
    /// <param name="ids">The records, each once.</param>
    /// <param name="isTarget">Whether a record, as the store's lock sees it, may be changed (the caller's own, still the
    /// kind the request is for); any other is left exactly as it is and reported neither changed nor refused.</param>
    /// <param name="change">The record once its clip file is gone (or it had none): the new record, or <c>null</c> to remove
    /// it. It may be called more than once for a record, so it changes nothing but its result.</param>
    /// <param name="afterWrite">Called right after each write with the records it changed, as they were before.</param>
    /// <param name="isInProgress">Whether a target, as the store's lock sees it, is left exactly as it is, its file too,
    /// because its clip is being made, and reported so; <c>null</c> when every target is deleted.</param>
    /// <param name="requireRecognisedFolder">Whether a file gone from a clip folder that is there but isn't recognised as the
    /// clips' own (no marker file of its own; <see cref="ClipFileOps.IsRecognisedRoot"/>) is left as it is, reported as refused
    /// (<see cref="ClipFileOps.Unrecognised"/>), instead of counting as deleted: the clips' bulk delete of the My Clips page
    /// and, since 1.2.0, the bookmarks' bulk delete with their clips (whose record would otherwise go while
    /// the file is probably fine in the real folder).</param>
    /// <param name="cancellationToken">Cancels waiting for the store and the pauses.</param>
    /// <returns>The records changed, those left as they were with the result of their file's delete, and those left because
    /// their clip was being made.</returns>
    public static async Task<ClipFileBatchOutcome> RunAsync(
        IBookmarkStore store,
        ClipFileOps files,
        IReadOnlyList<Guid> ids,
        Func<Bookmark, bool> isTarget,
        Func<Bookmark, Bookmark?> change,
        Action<IReadOnlyDictionary<Guid, Bookmark>> afterWrite,
        Func<Bookmark, bool>? isInProgress,
        bool requireRecognisedFolder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(isTarget);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(afterWrite);

        // Each clip folder's marker looked at once per look (the first check, then each write), not once per clip.
        var looks = new Dictionary<string, bool>(ClipRootPaths.Comparer);
        bool Unrecognised(ClipPathCheck check)
        {
            if (!requireRecognisedFolder || check is not { Verdict: ClipPathVerdict.Missing, Root: { } root })
            {
                return false;
            }

            if (!looks.TryGetValue(root, out var recognised))
            {
                recognised = files.IsRecognisedRoot(root);
                looks[root] = recognised;
            }

            return !recognised;
        }

        var changed = new Dictionary<Guid, Bookmark>();
        var refused = new Dictionary<Guid, ClipFileResult>();
        var inProgress = new HashSet<Guid>();
        var kept = new HashSet<Guid>();
        var pending = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (store.Get(id) is { Clip.FilePath: { Length: > 0 } path } current
                && isTarget(current)
                && isInProgress?.Invoke(current) != true
                && files.Check(path) is { } check
                && (check.IsRefused || check.IsUnreachable || Unrecognised(check)))
            {
                // Nothing is touched: the refusal is this check's, only logged, as for one delete.
                refused[id] = check.IsRefused || check.IsUnreachable ? files.Refusal(check, id, path) : files.Unrecognised(check, id, path);
            }
            else
            {
                pending.Add(id);
            }
        }

        for (var attempt = 1; pending.Count > 0; attempt++)
        {
            var retry = new HashSet<Guid>();
            var gone = new Dictionary<Guid, Bookmark>();
            var failed = new Dictionary<Guid, ClipFileResult>();
            var making = new HashSet<Guid>();
            var keptNow = new HashSet<Guid>();
            var attemptPending = pending;
            var attemptNumber = attempt;
            await store.UpdateDocumentAsync(
                document =>
                {
                    retry.Clear();
                    gone.Clear();
                    failed.Clear();
                    making.Clear();
                    keptNow.Clear();
                    looks.Clear();

                    // The records this write lets go of their files may name one file between them (Bookmarks 1.1.3): it is
                    // deleted once, for them all; one any other record names is kept for that one.
                    var deletedWith = document.Bookmarks
                        .Where(b => attemptPending.Contains(b.Id) && isTarget(b) && isInProgress?.Invoke(b) != true)
                        .Select(b => b.Id)
                        .ToHashSet();
                    var records = new List<Bookmark>(document.Bookmarks.Count);
                    foreach (var bookmark in document.Bookmarks)
                    {
                        if (!attemptPending.Contains(bookmark.Id) || !isTarget(bookmark))
                        {
                            records.Add(bookmark);
                            continue;
                        }

                        if (isInProgress?.Invoke(bookmark) == true)
                        {
                            // A clip being made, as the store's lock sees it: its record, its job and its earlier file stay.
                            records.Add(bookmark);
                            making.Add(bookmark.Id);
                            continue;
                        }

                        if (requireRecognisedFolder && bookmark.Clip?.FilePath is { Length: > 0 } missing && files.Check(missing) is { } fresh && Unrecognised(fresh))
                        {
                            // Gone from a folder that isn't the clips' own, as the store's lock sees it: left as it is.
                            records.Add(bookmark);
                            failed[bookmark.Id] = files.Unrecognised(fresh, bookmark.Id, missing);
                            continue;
                        }

                        if (bookmark.Clip?.FilePath is { Length: > 0 } file && files.DeleteOnce(file, bookmark.Id, attemptNumber, deletedWith) is { } result)
                        {
                            if (!result.IsGone)
                            {
                                records.Add(bookmark);
                                if (result.WorthRetrying && attemptNumber < ClipFileOps.Attempts)
                                {
                                    retry.Add(bookmark.Id);
                                }
                                else
                                {
                                    failed[bookmark.Id] = result;
                                }

                                continue;
                            }

                            if (result.Outcome == ClipFileOutcome.NamedByAnotherRecord)
                            {
                                keptNow.Add(bookmark.Id);
                            }
                        }

                        gone[bookmark.Id] = bookmark;
                        if (change(bookmark) is { } after)
                        {
                            records.Add(after);
                        }
                    }

                    return gone.Count == 0 ? document : document with { Bookmarks = records };
                },
                cancellationToken).ConfigureAwait(false);

            // Right after the write that changed them, before the pause the request's abort may end.
            afterWrite(gone);
            foreach (var (id, before) in gone)
            {
                changed[id] = before;
            }

            foreach (var (id, result) in failed)
            {
                refused[id] = result;
            }

            inProgress.UnionWith(making);
            if (gone.Count > 0)
            {
                kept.UnionWith(keptNow);
            }

            // An id that is no longer a target (deleted meanwhile in another tab) is neither changed nor refused.
            pending = retry;
            if (pending.Count > 0)
            {
                await Task.Delay(files.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ClipFileBatchOutcome(changed, refused, inProgress) { FilesKept = kept };
    }
}
