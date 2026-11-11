using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A request's delete of one record's clip file, file first: <c>DELETE /Bookmarks/{id}/Clip</c> and its Forget,
/// <c>DELETE /Bookmarks/{id}?deleteClip=true</c> (the manager's, for the record's owner) and, since 1.2.0,
/// <c>DELETE /Bookmarks/Admin/Clips/{id}</c> (the admins', for any user's clip). Moved out of <c>BookmarkManager</c> for
/// 1.2.0 unchanged, so the admins' delete is the users' own code with its own rules for which record it may change.
/// </summary>
/// <remarks>
/// <para>The record's clip file is deleted inside the store's write of the record, so the record the delete checks (still a
/// target, its clip file still that one) is the one it writes, and a delete racing another (a delete that keeps the clip, a
/// remap) never deletes a file the other one keeps. Only once the file is gone does <c>change</c> run. A file in use is tried
/// again, up to <see cref="ClipFileOps.Attempts"/> times, with the pauses outside the store's lock; then, as for a path
/// outside the clip folders (404, or for an admin 409) or a file that can't be deleted (503), nothing is written: the record,
/// its clip job and its file stay as they were. Every look at the file goes through <see cref="ClipFileOps.Check"/>, which
/// holds the paths of a rename that isn't finished, and the delete through <see cref="ClipFileOps.DeleteOnce"/>,
/// which keeps a file another record still names (Bookmarks 1.1.3).</para>
/// <para>It writes only when a user asks (a read-only store refuses with 503 before the file is touched; the callers ask the
/// store first), so it asks no <see cref="BackgroundWriteGate"/>.</para>
/// </remarks>
internal static class ClipFileSingleDelete
{
    /// <summary>
    /// Deletes a record's clip file and then changes the record.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="files">The clip file operations.</param>
    /// <param name="bookmarkId">The record.</param>
    /// <param name="onlyIfMissing">The My Clips page's Forget and Dismiss: no file is deleted at all; the change
    /// runs only while the clip has no file there, checked afresh under the store's lock; a file that is there (made again or
    /// put back since the page read it) is 409 with nothing changed.</param>
    /// <param name="requireRecognisedFolder">The clip routes (since 1.2.0, also a bookmark's delete with its
    /// clip): a file gone from a clip folder that is there but isn't recognised as the clips' own (no marker file of its own,
    /// as the sweep and the worker judge it) isn't gone either: 503 with nothing changed, before the lock and again under
    /// it.</param>
    /// <param name="isTarget">Whether the record is still one the caller may change; when it isn't, nothing is written and the
    /// change says so.</param>
    /// <param name="isInProgress">Whether a target's clip is being made, as the store's lock sees it, which the caller refuses
    /// to delete (the admins': 409 "Wait for the clip to finish or cancel it first.", nothing changed); <c>null</c>
    /// when a clip being made is the change's to handle (the users' delete stops its job).</param>
    /// <param name="change">The record once its file is gone: the new record, or <c>null</c> to remove it. It may be called more
    /// than once, so it changes nothing but its result.</param>
    /// <param name="writer">The user who changes the record, when that is its owner (the store notes their name).</param>
    /// <param name="forAdmin">Whether an admin route asked: a path outside the clip folders is then 409, not 404.</param>
    /// <param name="fileOutcome">Told, once the record is written, what became of its clip file: <see cref="ClipFileOutcome.Done"/>
    /// deleted, <see cref="ClipFileOutcome.Missing"/> gone already, <see cref="ClipFileOutcome.NamedByAnotherRecord"/> kept
    /// because another record still names it (Bookmarks 1.1.3), or <c>null</c> when no file was asked about (Forget, a clip
    /// without a file); <c>null</c> for a caller that doesn't ask (the admins' audit does).</param>
    /// <param name="cancellationToken">Cancels waiting for the store and the pauses.</param>
    /// <returns>The record before and after the write.</returns>
    /// <exception cref="Kit.Api.PluginException">404 outside the clip folders (users), 409 in use, being made, the file there
    /// after all (Forget) or outside the clip folders (admins), 503 a clip folder offline or not recognised, a rename not
    /// finished, or another refusal of the file system. Nothing changed in any case.</exception>
    public static async Task<BookmarkChange> RunAsync(
        IBookmarkStore store,
        ClipFileOps files,
        Guid bookmarkId,
        bool onlyIfMissing,
        bool requireRecognisedFolder,
        Func<Bookmark, bool> isTarget,
        Func<Bookmark, bool>? isInProgress,
        Func<Bookmark, Bookmark?> change,
        User? writer,
        bool forAdmin,
        Action<ClipFileOutcome?>? fileOutcome,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(isTarget);
        ArgumentNullException.ThrowIfNull(change);

        // A rename of the clip's file a crash or a failed write left half done is finished first: it holds both
        // of its paths, so the delete would be 503 otherwise (and is, while it can't be finished: nothing touched).
        if (files.HasUnfinishedRename(bookmarkId))
        {
            await files.FinishRenamesAsync(null, cancellationToken).ConfigureAwait(false);
        }

        if (isInProgress is not null && store.Get(bookmarkId) is { } beingMade && isTarget(beingMade) && isInProgress(beingMade))
        {
            throw BookmarkException.Conflict(ClipFileRenamer.InProgressMessage);
        }

        // A path the guard refuses, and a clip folder that isn't there or can't be reached (an unplugged drive, an offline
        // share: 503, and the file never counts as deleted), are answered before the store's lock is taken, so a
        // slow drive never holds up other writes. The answer is that check's alone: nothing is asked or touched again, so a
        // folder that came back meanwhile never has its file deleted outside the store's write.
        if (store.Get(bookmarkId) is { Clip.FilePath: { Length: > 0 } path } current
            && isTarget(current)
            && files.Check(path) is { } check)
        {
            if (check.IsRefused || check.IsUnreachable)
            {
                throw ClipFileOps.ToRequestError(files.Refusal(check, bookmarkId, path), forAdmin);
            }

            if (requireRecognisedFolder && check is { Verdict: ClipPathVerdict.Missing, Root: { } root } && !files.IsRecognisedRoot(root))
            {
                throw ClipFileOps.ToRequestError(files.Unrecognised(check, bookmarkId, path), forAdmin);
            }
        }

        for (var attempt = 1; ; attempt++)
        {
            ClipFileResult? failed = null;
            ClipFileOutcome? deleted = null;
            var fileThere = false;
            var making = false;
            var written = await store.UpdateOrRemoveAsync(
                bookmarkId,
                b =>
                {
                    failed = null;
                    deleted = null;
                    fileThere = false;
                    making = false;
                    if (!isTarget(b))
                    {
                        return b;
                    }

                    if (isInProgress?.Invoke(b) == true)
                    {
                        // Queued or running as the store's lock sees it (a remake asked for since the check above): left as
                        // it is, its job and its earlier file too.
                        making = true;
                        return b;
                    }

                    if (b.Clip?.FilePath is { Length: > 0 } file)
                    {
                        if (onlyIfMissing || requireRecognisedFolder)
                        {
                            var fresh = files.Check(file);
                            if (onlyIfMissing && fresh.Verdict == ClipPathVerdict.Exists)
                            {
                                fileThere = true;
                                return b;
                            }

                            if (onlyIfMissing && fresh.Verdict != ClipPathVerdict.Missing)
                            {
                                failed = files.Refusal(fresh, bookmarkId, file);
                                return b;
                            }

                            if (requireRecognisedFolder && fresh is { Verdict: ClipPathVerdict.Missing, Root: { } root } && !files.IsRecognisedRoot(root))
                            {
                                failed = files.Unrecognised(fresh, bookmarkId, file);
                                return b;
                            }
                        }

                        if (!onlyIfMissing)
                        {
                            var result = files.DeleteOnce(file, bookmarkId, attempt);
                            if (!result.IsGone)
                            {
                                failed = result;
                                return b;
                            }

                            deleted = result.Outcome;
                        }
                    }

                    return change(b);
                },
                cancellationToken,
                writer).ConfigureAwait(false);

            if (making)
            {
                throw BookmarkException.Conflict(ClipFileRenamer.InProgressMessage);
            }

            if (fileThere)
            {
                throw BookmarkException.Conflict(BookmarkManager.ClipFileIsThereMessage);
            }

            if (failed is null)
            {
                fileOutcome?.Invoke(deleted);
                return written;
            }

            if (attempt >= ClipFileOps.Attempts || !failed.WorthRetrying)
            {
                throw ClipFileOps.ToRequestError(failed, forAdmin);
            }

            await Task.Delay(files.RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }
}
