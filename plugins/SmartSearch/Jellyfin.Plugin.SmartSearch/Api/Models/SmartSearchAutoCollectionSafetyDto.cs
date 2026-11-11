using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>An auto-collection's safeguards against removing many members at once.</summary>
public sealed class SmartSearchAutoCollectionSafetyDto
{
    /// <summary>Gets or sets a value indicating whether a run may empty the collection without a review.</summary>
    public bool? AllowEmpty { get; set; }

    /// <summary>
    /// Gets or sets the share of members (1 to 100) a run may remove while the library looks unhealthy before it waits for
    /// a review; <c>null</c> for the server's setting.
    /// </summary>
    public int? MaxRemovalPercent { get; set; }

    /// <summary>Gets or sets a value indicating whether the body gave <see cref="MaxRemovalPercent"/> (even as <c>null</c>).</summary>
    [JsonIgnore]
    internal bool HasMaxRemovalPercent { get; set; }
}
