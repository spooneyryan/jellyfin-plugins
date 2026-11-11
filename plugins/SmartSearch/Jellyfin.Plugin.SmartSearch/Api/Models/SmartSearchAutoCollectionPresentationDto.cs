namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>How an auto-collection's collection looks.</summary>
public sealed class SmartSearchAutoCollectionPresentationDto
{
    /// <summary>
    /// Gets or sets the sort: <c>QueryOrder</c>, <c>PremiereDate</c> or <c>SortName</c>. An unlocked collection always
    /// sorts by release date (the server resets it at every refresh).
    /// </summary>
    public string? SortMode { get; set; }

    /// <summary>Gets or sets a value indicating whether the collection's metadata is locked (the default).</summary>
    public bool? Lock { get; set; }

    /// <summary>Gets or sets the overview mode: <c>LeaveAsIs</c> or <c>Managed</c>.</summary>
    public string? OverviewMode { get; set; }

    /// <summary>Gets or sets the overview written when it is managed.</summary>
    public string? Overview { get; set; }
}
