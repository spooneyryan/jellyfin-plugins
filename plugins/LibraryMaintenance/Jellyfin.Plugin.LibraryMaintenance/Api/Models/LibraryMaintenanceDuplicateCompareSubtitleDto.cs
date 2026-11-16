namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A copy's subtitle track, embedded in the file or a file of its own.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareSubtitleDto
{
    /// <summary>
    /// Gets or sets the track's index.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the language, or null when none is named.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the codec or format (<c>subrip</c>, <c>PGSSUB</c>, <c>ass</c>), or null.
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// Gets or sets the track's title, or null.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is forced.
    /// </summary>
    public bool IsForced { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is the default track.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is for the hearing impaired.
    /// </summary>
    public bool IsHearingImpaired { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is a file of its own (external) rather than in the video file.
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// Gets or sets an external subtitle's file name, or null for an embedded one.
    /// </summary>
    public string? FileName { get; set; }
}
