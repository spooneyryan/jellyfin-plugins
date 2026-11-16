using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A copy's special features: the extras Jellyfin holds for it (trailers, featurettes, deleted scenes…, not its theme
/// songs and videos), counted by kind and by where they are, and its commentary tracks.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareFeaturesDto
{
    /// <summary>
    /// Gets or sets the number of special features: the extras and the commentary tracks.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the extras by kind (<c>Trailer</c>, <c>Featurette</c>, <c>DeletedScene</c>,
    /// <c>BehindTheScenes</c>…), the most first; the commentary tracks as <c>Commentary</c>.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateCompareCountDto> ByKind { get; set; } = [];

    /// <summary>
    /// Gets or sets the extras by where they are: <c>BesideFile</c> (in the copy's own folder, named after it or in a
    /// mixed folder), <c>ExtrasFolder</c> (a folder in the copy's folder, such as <c>extras</c> or <c>trailers</c>; its
    /// name in <c>Folder</c>, one count per folder) or <c>Elsewhere</c> (outside the copy's folder, or no file); the
    /// commentary tracks as <c>AudioTrack</c>. Trailer addresses Jellyfin keeps from the internet are not counted.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateCompareCountDto> ByLocation { get; set; } = [];

    /// <summary>
    /// Gets or sets each extra file (the first 50), sorted by kind then name.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateCompareFeatureDto> Items { get; set; } = [];
}
