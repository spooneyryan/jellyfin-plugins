using System;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// One library change in a <see cref="LibraryChangeBatch"/> (<c>ItemChange{Kind, ItemId, ItemKind, TopParentId?,
/// Identity?}</c>). It holds ids and a few facts taken in the event handler, never the item itself, so a batch waiting
/// ten minutes pins no library objects; a consumer looks the item up when it handles the batch.
/// </summary>
/// <remarks>
/// Several events for the same item within one batch become one change (<see cref="LibraryChangeQueue"/>): an item added
/// and then updated is <see cref="LibraryChangeKind.Added"/>, the update reasons of repeated updates are combined, and an
/// item added or updated and then removed is only <see cref="LibraryChangeKind.Removed"/>. <see cref="Utc"/> is the time of
/// the last event merged into it.
/// </remarks>
internal sealed record ItemChange
{
    /// <summary>Gets what happened.</summary>
    public required LibraryChangeKind Kind { get; init; }

    /// <summary>Gets the item's id.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the item's kind; null for a type <see cref="BaseItemKind"/> doesn't name (a plugin's own item type).</summary>
    public BaseItemKind? ItemKind { get; init; }

    /// <summary>Gets a value indicating whether the item is a folder (series, season, album, artist, box set, folder...).</summary>
    public bool IsFolder { get; init; }

    /// <summary>Gets the event's parent: the folder the item was added to, updated under or removed from, when known.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>
    /// Gets the id of the item's top parent as Jellyfin stores it in <c>TopParentId</c>: the folder of the library path it
    /// is in (one of a library's <c>PhysicalFolderIds</c>, which <c>InternalItemsQuery.TopParentIds</c> takes; not the
    /// library's own id), or a plugin folder such as the collections folder. Only when the consumer asked for it
    /// (<see cref="LibraryChangeFilter.CaptureTopParent"/>) and it could be found.
    /// </summary>
    public Guid? TopParentId { get; init; }

    /// <summary>
    /// Gets the removed item's identity, captured inside the <c>ItemRemoved</c> handler because the item is gone afterwards,
    /// when the consumer asked for it (<see cref="LibraryChangeFilter.CaptureIdentity"/>, the default).
    /// Its series, season or album fields are empty when that container went with it.
    /// </summary>
    public IdentitySnapshot? Identity { get; init; }

    /// <summary>Gets the reasons of an update, combined over every update merged into this change.</summary>
    public ItemUpdateType UpdateReason { get; init; }

    /// <summary>Gets the user whose data was saved (<see cref="LibraryChangeKind.UserData"/>).</summary>
    public Guid? UserId { get; init; }

    /// <summary>Gets why the user data was saved (<see cref="LibraryChangeKind.UserData"/>; the last reason when merged).</summary>
    public UserDataSaveReason? SaveReason { get; init; }

    /// <summary>Gets when the monitor saw the (last) event.</summary>
    public DateTimeOffset Utc { get; init; }
}
