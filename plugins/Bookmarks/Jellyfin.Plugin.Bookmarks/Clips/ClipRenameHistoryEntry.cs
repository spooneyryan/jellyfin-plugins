using System;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// One clip file rename the server finished, as <see cref="ClipRenameHistory"/> keeps it: which record, the file's path
/// before and after, the file's identity, and when the record named the new path.
/// </summary>
internal sealed record ClipRenameHistoryEntry
{
    /// <summary>
    /// Gets the record's id (its bookmark's).
    /// </summary>
    public Guid BookmarkId { get; init; }

    /// <summary>
    /// Gets the file's full path before the rename.
    /// </summary>
    public string From { get; init; } = string.Empty;

    /// <summary>
    /// Gets the file's full path after it, in the same folder.
    /// </summary>
    public string To { get; init; } = string.Empty;

    /// <summary>
    /// Gets the file's identity (a rename keeps it), or <c>null</c> when it couldn't be read.
    /// </summary>
    public ClipFileIdentity? File { get; init; }

    /// <summary>
    /// Gets when the record named the new path (UTC).
    /// </summary>
    public DateTime FinishedUtc { get; init; }
}
