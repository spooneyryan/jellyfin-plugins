using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// An entry of a duplicate group: a movie, or a version set Jellyfin already groups, shown as its main version.
/// </summary>
public sealed class LibraryMaintenanceDuplicateGroupEntryDto
{
    /// <summary>
    /// Gets or sets the item id of the entry's main version (the one the Open link goes to).
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the name Jellyfin stores.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original title, when Jellyfin has one.
    /// </summary>
    public string? OriginalTitle { get; set; }

    /// <summary>
    /// Gets or sets the edition the title rules found (<c>directors cut</c>), or null.
    /// </summary>
    public string? Edition { get; set; }

    /// <summary>
    /// Gets or sets the year compared, or null when unknown.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets where the year came from: <c>ProductionYear</c>, <c>PremiereDate</c>, <c>Title</c> or <c>Unknown</c>.
    /// </summary>
    public string YearSource { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the libraries the entry's versions are in.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateGroupLibraryDto> Libraries { get; set; } = [];

    /// <summary>
    /// Gets or sets the main version's path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the main version shares its folder with other movies.
    /// </summary>
    public bool IsInMixedFolder { get; set; }

    /// <summary>
    /// Gets or sets the video width, or null when unknown.
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Gets or sets the video height, or null when unknown.
    /// </summary>
    public int? Height { get; set; }

    /// <summary>
    /// Gets or sets the resolution by width (2160p, 1440p, 1080p, 720p, SD), or null when the width is unknown.
    /// </summary>
    public string? ResolutionLabel { get; set; }

    /// <summary>
    /// Gets or sets the main version's size in bytes, or null when unknown.
    /// </summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the main version's bitrate, or null when unknown.
    /// </summary>
    public int? TotalBitrate { get; set; }

    /// <summary>
    /// Gets or sets the main version's runtime in ticks, or null when unknown.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the main version's container, or null.
    /// </summary>
    public string? Container { get; set; }

    /// <summary>
    /// Gets or sets the main version's video type (<c>VideoFile</c>, <c>Dvd</c>, <c>BluRay</c>, <c>Iso</c>).
    /// </summary>
    public string VideoType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the versions Jellyfin plays from the entry.
    /// </summary>
    public int SourceCount { get; set; }

    /// <summary>
    /// Gets or sets the item ids of the entry's other indexed versions.
    /// </summary>
    public IReadOnlyList<Guid> LinkedVersionIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the main version's local alternates.
    /// </summary>
    public int LocalAlternateCount { get; set; }

    /// <summary>
    /// Gets or sets the main version's stacked parts.
    /// </summary>
    public int PartCount { get; set; }

    /// <summary>
    /// Gets or sets what is wrong with the version set, <c>None</c> when nothing is.
    /// </summary>
    public string VersionState { get; set; } = "None";

    /// <summary>
    /// Gets or sets the TMDb and IMDb ids.
    /// </summary>
    public LibraryMaintenanceDuplicateProviderIdsDto ProviderIds { get; set; } = new();

    /// <summary>
    /// Gets or sets when the main version was added (UTC).
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets the entry's own flags: <c>BrokenMerge</c>, <c>ContainsDiscRip</c>, <c>ContainsShortcut</c>,
    /// <c>ContainsPlaceholder</c>, <c>YearUnknown</c>.
    /// </summary>
    public IReadOnlyList<string> Flags { get; set; } = [];

    /// <summary>
    /// Gets or sets the entry's ignore state: <c>Active</c> before 1.0.x's ignore marks.
    /// </summary>
    public string EntryIgnoreState { get; set; } = "Active";

    /// <summary>
    /// Gets or sets the entries it is marked "not a duplicate" with (empty before 1.0.x).
    /// </summary>
    public IReadOnlyList<Guid> MarkedWith { get; set; } = [];
}
