using System;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// One rename of a clip file, as the rename journal records it before the file is moved
/// (<see cref="ClipRenameJournal"/>): which record, the file's full path before and after, the file's identity, and the
/// name change the same request makes, so a rename the server didn't finish (a crash between the move and the record's
/// write, or a write that failed and a move back that failed too) is finished afterwards from what is on disk.
/// </summary>
internal sealed record ClipRenameEntry
{
    /// <summary>
    /// Gets the record's id (its bookmark's).
    /// </summary>
    public Guid BookmarkId { get; init; }

    /// <summary>
    /// Gets the file's full path before the rename, as the record held it.
    /// </summary>
    public string From { get; init; } = string.Empty;

    /// <summary>
    /// Gets the file's new full path, in the same folder.
    /// </summary>
    public string To { get; init; } = string.Empty;

    /// <summary>
    /// Gets the file's identity before the move (a rename keeps it).
    /// </summary>
    public ClipFileIdentity? File { get; init; }

    /// <summary>
    /// Gets a value indicating whether the same request changes the record's name (<c>POST /Bookmarks/Clips/{id}/Name</c>).
    /// </summary>
    public bool NameChanged { get; init; }

    /// <summary>
    /// Gets the record's new name (<c>null</c>: unnamed), when <see cref="NameChanged"/>.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the record's <c>UpdatedAt</c> when the rename was decided, under the store's lock: a recovery applies the name
    /// only to a record still unchanged since (no edit made after it).
    /// </summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>
    /// Gets when the rename was made (UTC).
    /// </summary>
    public DateTime StartedUtc { get; init; }
}
