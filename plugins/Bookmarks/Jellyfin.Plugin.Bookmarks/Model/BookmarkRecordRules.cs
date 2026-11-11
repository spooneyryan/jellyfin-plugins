using System;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.Bookmarks.Model;

/// <summary>
/// The rules every piece of Bookmarks code applies to a <see cref="Bookmark"/> record.
/// </summary>
internal static class BookmarkRecordRules
{
    /// <summary>
    /// Gets the snapshot of a record that has none: an empty item id and kind (so DTO mapping never meets a null
    /// <see cref="Bookmark.Identity"/>).
    /// </summary>
    public static IdentitySnapshot NoIdentity { get; } = new();

    /// <summary>
    /// Whether the record has a library item that may be looked up: its <see cref="Bookmark.ItemId"/> isn't empty and it
    /// wasn't adopted. The only gate in front of every <c>GetItemById</c>, identity backfill, refresh, verify
    /// sweep, reattach and <c>IdentityResolver</c> call, because 10.11.5's <c>LibraryManager.GetItemById(Guid.Empty)</c>
    /// throws (<c>LibraryManager.cs:1357-1362</c>). It ships in 1.1.0 so that a downgrade from 1.3.0, which creates
    /// adopted records, is safe.
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <returns>Whether the record's item may be looked up.</returns>
    public static bool HasLibraryItem(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return !bookmark.ItemId.Equals(Guid.Empty) && bookmark.Origin != BookmarkOrigin.Adopted;
    }

    /// <summary>
    /// Whether the record is a clip-only tombstone: the bookmark was deleted while its clip file was kept. Only
    /// clip operations see a tombstone; every bookmark list and lookup by id leaves it out.
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <returns>Whether it is a tombstone.</returns>
    public static bool IsTombstone(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return bookmark.DeletedUtc is not null;
    }

    /// <summary>
    /// Whether the record is a live bookmark, the opposite of <see cref="IsTombstone"/>.
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <returns>Whether it is live.</returns>
    public static bool IsLive(Bookmark bookmark) => !IsTombstone(bookmark);

    /// <summary>
    /// Whether the record is a live bookmark of the given user: the only records a user's bookmark operations may read or
    /// change. Every write of such an operation checks it again under the store's lock, so a record that
    /// became a tombstone or another user's since the request looked it up is left as it is.
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <param name="userId">The user.</param>
    /// <returns>Whether it is that user's live bookmark.</returns>
    public static bool IsLiveBookmarkOf(Bookmark bookmark, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return bookmark.UserId.Equals(userId) && IsLive(bookmark);
    }

    /// <summary>
    /// Whether a client's <c>IfUpdatedAt</c> token still matches the record, so its edit may be written:
    /// no token always matches (1.0's last-writer-wins); otherwise it must be the record's <see cref="Bookmark.UpdatedAt"/>,
    /// the value the API sent (UTC, to the tick). A token with nothing below the millisecond also matches the stored time
    /// cut to the millisecond, since a JavaScript <c>Date</c> keeps milliseconds only; <see cref="NextUpdatedAt"/> keeps
    /// two changes of a record apart by at least a millisecond, so such a token never matches a later change. A token with
    /// an offset is compared as the UTC time it names, one without any zone as UTC.
    /// </summary>
    /// <param name="bookmark">The record, as the store's lock sees it.</param>
    /// <param name="ifUpdatedAt">The client's token, or <c>null</c>.</param>
    /// <returns>Whether the edit may be written.</returns>
    public static bool IsUnchangedSince(Bookmark bookmark, DateTime? ifUpdatedAt)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        if (ifUpdatedAt is not DateTime token)
        {
            return true;
        }

        var given = ToUtc(token).Ticks;
        var stored = ToUtc(bookmark.UpdatedAt).Ticks;
        return given == stored
            || (given % TimeSpan.TicksPerMillisecond == 0 && given == stored - (stored % TimeSpan.TicksPerMillisecond));
    }

    /// <summary>
    /// The <see cref="Bookmark.UpdatedAt"/> of a user's edit of a record (the <c>IfUpdatedAt</c> token): now, but
    /// always in a later millisecond than the record's previous value, so every change gives a token no earlier one matches
    /// (<see cref="IsUnchangedSince"/>), also two changes within one millisecond or after the clock was set back.
    /// </summary>
    /// <param name="previous">The record's current <see cref="Bookmark.UpdatedAt"/>.</param>
    /// <param name="now">The time of the edit (UTC).</param>
    /// <returns>The new value (UTC).</returns>
    public static DateTime NextUpdatedAt(DateTime previous, DateTime now)
    {
        var before = ToUtc(previous).Ticks;
        var after = ToUtc(now).Ticks;
        var earliest = before - (before % TimeSpan.TicksPerMillisecond) + TimeSpan.TicksPerMillisecond;
        return new DateTime(Math.Max(after, earliest), DateTimeKind.Utc);
    }

    /// <summary>
    /// A clip's rename on the My Clips page (<c>POST /Bookmarks/Clips/{id}/Name</c>): the record's name (a live
    /// bookmark's, or the one a deleted bookmark's kept clip keeps), as <c>POST /Bookmarks/{id}/Name</c> renames a bookmark
    /// (an edit clears the "Relinked, check position" badge), with the next <see cref="Bookmark.UpdatedAt"/>.
    /// The clip file is the caller's to rename or not.
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <param name="name">The normalised name, or <c>null</c> for unnamed.</param>
    /// <param name="now">The time of the edit (UTC).</param>
    /// <returns>The renamed record.</returns>
    public static Bookmark WithClipName(Bookmark bookmark, string? name, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return bookmark with { Name = name, RelinkedUtc = null, RelinkStrength = null, UpdatedAt = NextUpdatedAt(bookmark.UpdatedAt, now) };
    }

    /// <summary>
    /// Turns a deleted bookmark whose clip file is kept into a clip-only tombstone:
    /// <see cref="Bookmark.DeletedUtc"/> is set, and so is <see cref="Bookmark.UpdatedAt"/> (the record's last change), and
    /// the clip is reduced to its file (<see cref="FileOnly"/>). Everything else, the name and note among it, is kept for the
    /// My Clips list.
    /// </summary>
    /// <param name="bookmark">The live bookmark.</param>
    /// <param name="deletedUtc">When it was deleted (UTC).</param>
    /// <returns>The tombstone, or <c>null</c> when the record has no clip file, and is removed instead. A record that
    /// is a tombstone already is returned as it is.</returns>
    public static Bookmark? ToTombstone(Bookmark bookmark, DateTime deletedUtc)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        if (IsTombstone(bookmark))
        {
            return bookmark;
        }

        return bookmark.Clip is { FilePath: { Length: > 0 } } clip
            ? bookmark with { DeletedUtc = deletedUtc, UpdatedAt = deletedUtc, Clip = FileOnly(clip) }
            : null;
    }

    /// <summary>
    /// Reduces a clip to its file, the only clip state a tombstone has: <see cref="ClipInfo.Status"/>
    /// <c>Completed</c> and no <see cref="ClipInfo.Error"/>, whatever the last job did (it was cancelled, it failed, or it
    /// still runs and will be discarded); the range is the file's when it is known; the mode is the file's, from its
    /// extension (<c>.mkv</c> is Fast, anything else Accurate); a job's size estimate is dropped, since no job belongs to it
    /// any more. <see cref="ClipInfo.CompletedAt"/> is kept only when the clip was <c>Completed</c>, the one state in which
    /// it is the file's completion time; any other state's belongs to a later job that didn't make the file (a remake that is
    /// queued or running, or that failed or was cancelled; the delete's own cancellation makes a queued one <c>Cancelled</c>
    /// at the delete's time), and the file's own time was dropped when that job was queued, so it is <c>null</c>: unknown.
    /// The request time (the last job's), the file's fields and <see cref="ClipInfo.Extra"/> are kept.
    /// </summary>
    /// <param name="clip">The clip, with its <see cref="ClipInfo.FilePath"/> set.</param>
    /// <returns>The clip in its file-only state.</returns>
    public static ClipInfo FileOnly(ClipInfo clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip with
        {
            Status = BookmarkClipStatus.Completed,
            Error = null,
            StartTicks = clip.FileStartTicks ?? clip.StartTicks,
            EndTicks = clip.FileEndTicks ?? clip.EndTicks,
            Mode = ClipFileNaming.GetMode(clip.FilePath),
            EstimatedBytes = null,
            CompletedAt = clip.Status == BookmarkClipStatus.Completed ? clip.CompletedAt : null
        };
    }

    /// <summary>
    /// Whether the record's clip is a job that is queued or being made (<c>Queued</c> or <c>Running</c>).
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <returns>Whether its clip is a job waiting or running.</returns>
    public static bool HasClipJob(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return bookmark.Clip?.Status is BookmarkClipStatus.Queued or BookmarkClipStatus.Running;
    }

    /// <summary>
    /// Ends a clip job that no clip worker runs: a record whose clip is <c>Queued</c> or <c>Running</c>
    /// and that came into the data as part of a whole document, a restored snapshot or an import, not through a request the
    /// worker queued. The worker takes such a job only at its next start, so until then it would count as a clip being made
    /// (refusing every restore and import), its owner's Cancel and a new request would leave it as it is, and after the
    /// restart a clip the owner had cancelled, or one made since, would be made again. A live bookmark's job is
    /// <c>Failed</c> with <paramref name="error"/>, finished at <paramref name="endedUtc"/>, the file of an earlier job kept
    /// (as every outcome of a job keeps it); a tombstone's clip is its file (<see cref="FileOnly"/>), the one clip state a
    /// tombstone has. Any other record is returned as it is.
    /// </summary>
    /// <param name="bookmark">The record.</param>
    /// <param name="error">Why the job ended, for its owner.</param>
    /// <param name="endedUtc">When it ended (UTC).</param>
    /// <returns>The record without a job waiting or running.</returns>
    public static Bookmark EndClipJobNoWorkerRuns(Bookmark bookmark, string error, DateTime endedUtc)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        ArgumentException.ThrowIfNullOrEmpty(error);
        if (!HasClipJob(bookmark))
        {
            return bookmark;
        }

        var clip = bookmark.Clip!;
        return IsTombstone(bookmark)
            ? bookmark with { Clip = FileOnly(clip) }
            : bookmark with { Clip = clip with { Status = BookmarkClipStatus.Failed, Error = error, CompletedAt = ToUtc(endedUtc) } };
    }

    // Stored and API times are UTC; a time with an offset (read as local) is converted, one without a zone taken as UTC.
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value
    };
}
