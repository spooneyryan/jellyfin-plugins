using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A duplicate group's copies side by side (<c>GET …/Duplicates/Groups/{groupId}/Compare</c>): one column per file the
/// caller can see, each entry's versions included, read from Jellyfin's library on request. Read-only.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareDto
{
    /// <summary>
    /// Gets or sets the group's id.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the group's title (the suggested keep's name).
    /// </summary>
    public string DisplayTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the suggested keep's item id (advisory only), as in the group.
    /// </summary>
    public System.Guid SuggestedKeepId { get; set; }

    /// <summary>
    /// Gets or sets the copies: each entry's main version first, then its other versions, the entries in the group's
    /// order (the suggested keep first).
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateCompareCopyDto> Copies { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether copies were left out: a group of more than 24 files shows the first 24.
    /// </summary>
    public bool Truncated { get; set; }

    /// <summary>
    /// Gets or sets, for each compared detail the copies differ in, the copies with the best value (the highest), in
    /// the order of <c>Field</c>: <c>Resolution</c>, <c>Size</c>, <c>Bitrate</c>, <c>VideoBitrate</c>, <c>BitDepth</c>,
    /// <c>DynamicRange</c>, <c>AudioTracks</c>, <c>AudioChannels</c>, <c>SubtitleTracks</c>, <c>SpecialFeatures</c>.
    /// A detail every copy shares, or none has, is left out. Advice only.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateCompareBestDto> Best { get; set; } = [];
}
