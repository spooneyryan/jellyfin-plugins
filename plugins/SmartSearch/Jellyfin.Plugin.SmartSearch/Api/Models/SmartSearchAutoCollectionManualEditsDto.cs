using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>An auto-collection's manual changes: items kept in (pinned) and kept out (excluded).</summary>
public sealed class SmartSearchAutoCollectionManualEditsDto
{
    /// <summary>Gets or sets the mode: <c>Respect</c> (manual changes are kept) or <c>Overwrite</c>.</summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the pinned items' ids (at most 1,000); in a request, the whole list.</summary>
    public IReadOnlyList<Guid>? PinnedItemIds { get; set; }

    /// <summary>Gets or sets the excluded items' ids (at most 1,000); in a request, the whole list.</summary>
    public IReadOnlyList<Guid>? ExcludedItemIds { get; set; }

    /// <summary>Gets or sets the pinned items, named only when the caller may open them (answers only).</summary>
    public IReadOnlyList<SmartSearchAutoCollectionItemDto>? Pinned { get; set; }

    /// <summary>Gets or sets the excluded items, named only when the caller may open them (answers only).</summary>
    public IReadOnlyList<SmartSearchAutoCollectionItemDto>? Excluded { get; set; }
}
