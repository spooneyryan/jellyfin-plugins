using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>Whether the mass-removal guard would hold a preview's removals back if it ran now.</summary>
public sealed class SmartSearchAutoCollectionGuardPreviewDto
{
    /// <summary>Gets or sets a value indicating whether it would.</summary>
    public bool WouldTrip { get; set; }

    /// <summary>Gets or sets why (see <see cref="SmartSearchAutoCollectionPendingGuardDto.Reasons"/>).</summary>
    public IReadOnlyList<string> Reasons { get; set; } = [];
}
