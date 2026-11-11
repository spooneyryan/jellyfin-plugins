using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>A series the signed-in user can see, for the <c>Series</c> rule's picker.</summary>
public sealed class SmartSearchSeriesDto
{
    /// <summary>Gets or sets the series' id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the series' name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the year it began, or <c>null</c>.</summary>
    public int? ProductionYear { get; set; }

    /// <summary>Gets or sets the library the user sees it in.</summary>
    public Guid LibraryId { get; set; }
}
