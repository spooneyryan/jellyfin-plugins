using System;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What the kit needs of a detached item-linked record to find its item again: its id, its owner,
/// the stored snapshot, and when it was attached and detached. An <see cref="IDetachedRecordOwner"/> lists its detached
/// records this way, and <see cref="ArchivedRecord"/> carries the same fields in the archive.
/// </summary>
internal record DetachedRecord
{
    /// <summary>
    /// Gets the record's id in its store (a GUID's text for Bookmarks' records), unique in the store and its archive.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the user the record belongs to: the resolver sees only the items that user can see. Null for
    /// a server-wide record (Library Maintenance's ignore marks, auto-collection pins).
    /// </summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// Gets the snapshot the record stored of its item.
    /// </summary>
    public required IdentitySnapshot Identity { get; init; }

    /// <summary>
    /// Gets when the record got its current item id; journal lines committed before it are not followed.
    /// </summary>
    public DateTime? AttachedUtc { get; init; }

    /// <summary>
    /// Gets when the record was detached.
    /// </summary>
    public required DateTime DetachedUtc { get; init; }
}
