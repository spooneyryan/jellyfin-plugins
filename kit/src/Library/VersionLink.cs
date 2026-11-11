using System;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// One entry of a primary version's list of linked alternate versions (<c>Video.LinkedAlternateVersions</c>, Jellyfin's
/// <c>LinkedChild</c>), as <see cref="VersionSets"/> reads it. It resolves the way Jellyfin
/// resolves it (<c>BaseItem.GetLinkedChild</c>, <c>FindLinkedChild</c>): the cached id, else the path, else the library
/// item id.
/// </summary>
/// <param name="ItemId">The cached item id. Jellyfin sets it to <see cref="Guid.Empty"/> once a lookup found nothing,
/// and then never looks the entry up again; null when it was never cached.</param>
/// <param name="Path">The item's path, looked up with <c>FindByPath</c> when there is no cached id or it doesn't
/// resolve.</param>
/// <param name="LibraryItemId">The item id a link without a path names (<c>LinkedChild.LibraryItemId</c>).</param>
internal sealed record VersionLink(Guid? ItemId, string? Path, string? LibraryItemId = null);
