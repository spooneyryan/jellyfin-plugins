using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A clip's rename on the My Clips page (Rename and Rename file, since 1.2.0): the record's name, its clip file's name,
/// or both in one write, for the clip's owner only. Composed by <see cref="ClipService"/>; the file part goes through
/// <see cref="ClipFileOps"/> (<see cref="ClipFileOps.RenameOnce"/>).
/// </summary>
/// <remarks>
/// <para><b>All or nothing.</b> With its file, the rename is one write of the record, made after the file was moved inside it
/// (file first, then the record, each attempt inside the store's write, the pauses outside its lock): a file in use
/// after three attempts is 409 with the name unchanged too ("nothing renamed"), a record whose write fails has its file
/// put back. A crash between the move and the write is finished afterwards from the rename's journal entry
/// (<see cref="ClipFileOps.FinishRenamesAsync"/>).</para>
/// <para><b>What may be renamed.</b> The caller's own clip only (an admin's renames of other users' clips are not
/// offered), a live bookmark's or a deleted bookmark's kept one; never one being made (Queued or Running, as the
/// store's lock sees it): 409 "Wait for the clip to finish or cancel it first." Its file only from a clip folder
/// that is there (503 otherwise, nothing changed), only while the file is there (404, and a file gone from a folder that isn't
/// recognised as the clips' own is its folder's 503), and only a path the guard accepts (404).</para>
/// <para><b>The new file name</b> is the one Bookmarks gives the clip now (<see cref="ClipFileNaming.BuildRenamedFileName"/>:
/// the record's name (never its note since 1.2.0), and the range the file holds), in the same folder, made unique
/// (<see cref="ClipFileOps.ChooseRenameTarget"/>, under <see cref="ClipFileOps.NameGate"/>). A name the file has already,
/// numbered or not, is no rename. The name's <c>UpdatedAt</c> moves only when the name changes (the token of an open
/// editor); renaming the file alone is the clip's bookkeeping, as a finished job's is.</para>
/// <para>Its log lines name the record and the user, never a name or a path: those are at Debug.</para>
/// </remarks>
internal sealed class ClipFileRenamer
{
    /// <summary>
    /// The 404 message of an id that is no clip of the caller's.
    /// </summary>
    public const string NotFoundMessage = "Clip not found.";

    /// <summary>
    /// The 409 message of a clip being made (a rename or a delete while the clip is being made again).
    /// </summary>
    public const string InProgressMessage = "Wait for the clip to finish or cancel it first.";

    /// <summary>
    /// The 404 message of a clip without a file (a failed job's).
    /// </summary>
    public const string NoFileMessage = "This clip has no file to rename.";

    /// <summary>
    /// The 404 message of a clip whose file is gone from a clip folder that is there.
    /// </summary>
    public const string FileMissingMessage = "The clip file isn't there any more, so it can't be renamed; nothing was changed. Reload to see it.";

    /// <summary>
    /// The 409 message of a clip file whose name doesn't say its range, and whose record doesn't either.
    /// </summary>
    public const string NoRangeMessage = "This clip file's name isn't one Bookmarks gives, and its range isn't known, so it can't be renamed; nothing was changed.";

    /// <summary>
    /// The 409 message of a new file name taken again and again while the rename tried it (another program writing there).
    /// </summary>
    public const string NameTakenMessage = "Another file took the clip's new name while it was being renamed; nothing was changed. Try again.";

    private readonly IBookmarkStore _store;
    private readonly ClipFileOps _files;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFileRenamer"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="files">The clip file operations.</param>
    /// <param name="logger">The logger.</param>
    public ClipFileRenamer(IBookmarkStore store, ClipFileOps files, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _files = files;
        _logger = logger;
    }

    /// <summary>
    /// Renames a clip of the caller's: its record's name (<paramref name="changeName"/>), its file (<paramref name="renameFile"/>)
    /// or both, in one write.
    /// </summary>
    /// <param name="user">The caller, the clip's owner.</param>
    /// <param name="bookmarkId">The clip's id (its bookmark's).</param>
    /// <param name="changeName">Whether the name changes.</param>
    /// <param name="name">The new name, normalised (<c>BookmarkNames.NormalizeName</c>), or <c>null</c> for unnamed.</param>
    /// <param name="renameFile">Whether the file gets the name Bookmarks gives the clip now.</param>
    /// <param name="ifUpdatedAt">The record's <c>UpdatedAt</c> as the caller last read it: a record changed since is 409 (for
    /// Rename file too: a rename elsewhere would give the file another name than its confirmation
    /// said).</param>
    /// <param name="cancellationToken">Cancels waiting for the store and between attempts.</param>
    /// <returns>The record as written.</returns>
    /// <exception cref="PluginException">404 not the caller's clip, no file or its file gone (or outside the clip folders); 409
    /// being made, changed since <paramref name="ifUpdatedAt"/>, the file in use, or no range known; 503 read-only data, the clip
    /// folder offline or not recognised, the file or the record not written, a rename not finished. Nothing changed in any
    /// case.</exception>
    public async Task<Bookmark> RenameAsync(User user, Guid bookmarkId, bool changeName, string? name, bool renameFile, DateTime? ifUpdatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        _store.ThrowIfReadOnly();

        // A rename of this clip's file that a crash or a failed write left half done is finished first (it holds the paths).
        if (_files.HasUnfinishedRename(bookmarkId))
        {
            await _files.FinishRenamesAsync(null, cancellationToken).ConfigureAwait(false);
        }

        var current = _store.Get(bookmarkId);
        if (current is not { Clip: not null } || !current.UserId.Equals(user.Id))
        {
            throw BookmarkException.NotFound(NotFoundMessage);
        }

        if (BookmarkRecordRules.HasClipJob(current))
        {
            throw BookmarkException.Conflict(InProgressMessage);
        }

        if (renameFile)
        {
            // Before the store's lock, as the deletes answer them: a path the guard refuses, a folder that can't be reached, a
            // file gone. Each is checked again under the lock.
            PreCheck(current, bookmarkId);
            await _files.NameGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await WriteAsync(user, bookmarkId, changeName, name, renameFile, ifUpdatedAt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (renameFile)
            {
                _files.NameGate.Release();
            }
        }
    }

    // The answers that need no lock: no file, a path refused, a folder offline or not recognised, a file gone.
    private void PreCheck(Bookmark current, Guid bookmarkId)
    {
        if (current.Clip!.FilePath is not { Length: > 0 } path)
        {
            throw BookmarkException.NotFound(NoFileMessage);
        }

        var check = _files.Check(path);
        if (check.IsRefused || check.IsUnreachable)
        {
            throw ClipFileOps.ToRequestError(_files.Refusal(check, bookmarkId, path, "rename"));
        }

        if (check is { Verdict: ClipPathVerdict.Missing, Root: { } root })
        {
            if (!_files.IsRecognisedRoot(root))
            {
                throw ClipFileOps.ToRequestError(_files.Unrecognised(check, bookmarkId, path));
            }

            _files.ForgetVerdict(path);
            throw BookmarkException.NotFound(FileMissingMessage);
        }
    }

    private async Task<Bookmark> WriteAsync(User user, Guid bookmarkId, bool changeName, string? name, bool renameFile, DateTime? ifUpdatedAt, CancellationToken cancellationToken)
    {
        // A clip folder that can't answer the look-up of the new name gets one more try, after a pause outside the store's lock
        // (a drive spinning up, a share that missed one answer); then 503 with nothing changed.
        var unreachableTries = 0;
        for (var attempt = 1; ; attempt++)
        {
            string? refusal = null;
            PluginErrorKind refusalKind = PluginErrorKind.NotFound;
            ClipFileResult? failed = null;
            ClipRenameEntry? moved = null;
            Exception? unreachable = null;
            Bookmark? written;
            try
            {
                written = await _store.UpdateAsync(
                    bookmarkId,
                    b =>
                    {
                        refusal = null;
                        failed = null;
                        moved = null;
                        unreachable = null;
                        if (b.Clip is null || !b.UserId.Equals(user.Id))
                        {
                            (refusal, refusalKind) = (NotFoundMessage, PluginErrorKind.NotFound);
                            return b;
                        }

                        if (BookmarkRecordRules.HasClipJob(b))
                        {
                            (refusal, refusalKind) = (InProgressMessage, PluginErrorKind.Conflict);
                            return b;
                        }

                        if (!BookmarkRecordRules.IsUnchangedSince(b, ifUpdatedAt))
                        {
                            (refusal, refusalKind) = (BookmarkManager.ChangedElsewhereMessage, PluginErrorKind.Conflict);
                            return b;
                        }

                        var after = changeName ? BookmarkRecordRules.WithClipName(b, name, DateTime.UtcNow) : b;
                        if (!renameFile)
                        {
                            return after;
                        }

                        // The file part, the record as the store's lock sees it.
                        if (b.Clip.FilePath is not { Length: > 0 } path)
                        {
                            (refusal, refusalKind) = (NoFileMessage, PluginErrorKind.NotFound);
                            return b;
                        }

                        var fresh = _files.Check(path);
                        if (fresh is { Verdict: ClipPathVerdict.Missing, Root: { } root })
                        {
                            if (_files.IsRecognisedRoot(root))
                            {
                                _files.ForgetVerdict(path);
                                (refusal, refusalKind) = (FileMissingMessage, PluginErrorKind.NotFound);
                            }
                            else
                            {
                                failed = _files.Unrecognised(fresh, b.Id, path);
                            }

                            return b;
                        }

                        if (fresh.Verdict != ClipPathVerdict.Exists)
                        {
                            failed = _files.Refusal(fresh, b.Id, path, "rename");
                            return b;
                        }

                        var source = fresh.FullPath!;
                        if (ClipFileNaming.BuildRenamedFileName(after.ItemName, BookmarkNames.ClipFileLabel(after), Path.GetFileName(source), after.Clip!.FileStartTicks, after.Clip.FileEndTicks) is not { } fileName)
                        {
                            (refusal, refusalKind) = (NoRangeMessage, PluginErrorKind.Conflict);
                            return b;
                        }

                        if (!_files.TryChooseRenameTarget(source, fileName, out var target, out var lookUp))
                        {
                            // Nothing chosen, nothing moved: the attempt ends, and is made again outside the lock.
                            unreachable = lookUp ?? new IOException(ClipFileOps.FolderUnreachableMessage);
                            return b;
                        }

                        if (string.Equals(target, source, StringComparison.Ordinal))
                        {
                            // Its name already (numbered, when the plain one is taken): only the name changes, if it does.
                            return after;
                        }

                        // Another record names this file too (after an import, a restore or a version before
                        // 1.1.3): moving it would leave that one "File missing", so nothing moves.
                        if (_files.NamedByAnother(source, b.Id) is { } other)
                        {
                            _logger.LogWarning("Refused to rename the clip file of bookmark {BookmarkId}: bookmark {OtherId} names that file too", b.Id, other.Id);
                            (refusal, refusalKind) = (ClipFileOps.NamedByAnotherMessage, PluginErrorKind.Conflict);
                            return b;
                        }

                        var entry = new ClipRenameEntry
                        {
                            BookmarkId = b.Id,
                            From = source,
                            To = target,
                            NameChanged = changeName,
                            Name = name,
                            UpdatedAt = b.UpdatedAt,
                            StartedUtc = DateTime.UtcNow
                        };
                        var result = _files.RenameOnce(entry, attempt, out var recorded);
                        if (result.Outcome != ClipFileOutcome.Done)
                        {
                            failed = result;
                            return b;
                        }

                        moved = recorded!;
                        return after with { Clip = after.Clip with { FilePath = target, MissingSinceUtc = null } };
                    },
                    cancellationToken,
                    user).ConfigureAwait(false);
            }
            catch (Exception ex) when (moved is not null)
            {
                if (_store.Get(bookmarkId) is { Clip.FilePath: { } now } stored && string.Equals(now, moved.To, StringComparison.Ordinal))
                {
                    // Written after all (the failure came after the write): the rename is made.
                    _files.FinishRename(moved);
                    return stored;
                }

                // The file moved, and the record's write failed: the file goes back, so nothing changed; if it can't, the
                // rename's journal entry finishes it once the data can be written.
                var back = await _files.UndoRenameAsync(moved, CancellationToken.None).ConfigureAwait(false);
                _logger.LogWarning("The rename of user {UserId}'s clip of bookmark {BookmarkId} couldn't be written: {Error}", user.Id, bookmarkId, ex.Message);
                throw back
                    ? ex as PluginException ?? PluginException.Unavailable(ClipFileOps.FailedMessage)
                    : PluginException.Unavailable(ClipFileOps.RenameUnfinishedMessage);
            }

            if (moved is not null)
            {
                _files.FinishRename(moved);
                if (changeName)
                {
                    _logger.LogInformation("User {UserId} renamed the clip of bookmark {BookmarkId} and its file", user.Id, bookmarkId);
                }
                else
                {
                    _logger.LogInformation("User {UserId} renamed the clip file of bookmark {BookmarkId} to its clip's name", user.Id, bookmarkId);
                }

                _logger.LogDebug("Renamed {From} to {To} for bookmark {BookmarkId}", moved.From, moved.To, bookmarkId);
                return written!;
            }

            if (refusal is not null)
            {
                throw new BookmarkException(refusalKind, refusal);
            }

            if (unreachable is not null)
            {
                if (++unreachableTries < 2)
                {
                    await Task.Delay(_files.RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _files.WarnRenameTargetUnreachable(unreachable);
                throw PluginException.Unavailable(ClipFileOps.FolderUnreachableMessage);
            }

            if (failed is null)
            {
                if (written is null)
                {
                    throw BookmarkException.NotFound(NotFoundMessage);
                }

                if (changeName)
                {
                    _logger.LogInformation("User {UserId} renamed the clip of bookmark {BookmarkId} (its file: {File})", user.Id, bookmarkId, renameFile ? "has that name already" : "not renamed");
                }

                return written;
            }

            if ((failed.WorthRetrying || failed.NameTaken) && attempt < ClipFileOps.Attempts)
            {
                await Task.Delay(_files.RetryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw failed.NameTaken ? BookmarkException.Conflict(NameTakenMessage) : ClipFileOps.ToRequestError(failed);
        }
    }
}
