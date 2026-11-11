using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>A pinned or excluded item.</summary>
public sealed class SmartSearchAutoCollectionItemDto
{
    /// <summary>Gets or sets the item's id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item's name: <c>null</c> when the caller can't open the item, or it is gone.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets when it was pinned or excluded.</summary>
    public DateTime AddedUtc { get; set; }

    /// <summary>Gets or sets when it was found gone, or <c>null</c>.</summary>
    public DateTime? DetachedUtc { get; set; }
}
