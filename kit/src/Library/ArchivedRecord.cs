using System;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// A detached record moved out of its live store into the archive: the fields the kit
/// resolves it by, plus the record itself in its store's shape, which the export's <c>Archive</c> carries and
/// the owner puts back when the record is reattached.
/// </summary>
/// <remarks>
/// An owner can also move records into its archive for a reason of its own (<see cref="Reason"/>; Bookmarks' deleted
/// bookmarks whose clip file has been missing longer than its retention, <c>ClipMissing</c>), through
/// <see cref="DetachedRecords.ArchiveForReasonAsync"/>: nothing user-written is hard-deleted on a timer. Such a record
/// waits for no item, so it is never re-resolved; it is exported, imported and purged like the others, and its
/// <see cref="DetachedRecord.DetachedUtc"/> is when its reason began (for <c>ClipMissing</c>, when the clip file was
/// found missing).
/// </remarks>
internal sealed record ArchivedRecord : DetachedRecord
{
    /// <summary>
    /// Gets the record in its store's JSON shape (the kit's format: PascalCase, nulls omitted).
    /// </summary>
    public required JsonObject Record { get; init; }

    /// <summary>
    /// Gets when it was archived; set by the archive.
    /// </summary>
    public DateTime ArchivedUtc { get; init; }

    /// <summary>
    /// Gets the archive line's sequence number; set by the archive. The daily reattach goes round the archive in this
    /// order, least recently checked first.
    /// </summary>
    public long Seq { get; init; }

    /// <summary>
    /// Gets why the owner archived the record when it wasn't the kit's retention of a detached record: <c>null</c> for
    /// a record detached from its item longer than the retention (<see cref="DetachedRecords.ArchiveDueAsync"/>),
    /// otherwise the owner's reason (Bookmarks' <c>ClipMissing</c>). A record with a reason is never re-resolved by the
    /// reattach: an archived tombstone is not re-resolved.
    /// </summary>
    public string? Reason { get; init; }
}
