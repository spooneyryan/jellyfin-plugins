using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>The body of <c>POST …/{id}/Guard/Apply</c> and <c>…/Guard/Keep</c>.</summary>
public sealed class SmartSearchAutoCollectionGuardRequestDto
{
    /// <summary>Gets or sets the <c>DetectedUtc</c> of the held removals the caller reviewed.</summary>
    public DateTime? DetectedUtc { get; set; }
}
