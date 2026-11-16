namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A copy's audio track.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareAudioDto
{
    /// <summary>
    /// Gets or sets the track's index in the file.
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the language (<c>eng</c>), or null when the file names none.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Gets or sets the codec (<c>truehd</c>, <c>ac3</c>), or null.
    /// </summary>
    public string? Codec { get; set; }

    /// <summary>
    /// Gets or sets the channels, or null.
    /// </summary>
    public int? Channels { get; set; }

    /// <summary>
    /// Gets or sets the channel layout (<c>5.1(side)</c>), or null.
    /// </summary>
    public string? ChannelLayout { get; set; }

    /// <summary>
    /// Gets or sets the track's title in the file, or null.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the track is a commentary (its title or comment says so).
    /// </summary>
    public bool IsCommentary { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the file marks it as the default track.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is in a file of its own beside the video.
    /// </summary>
    public bool IsExternal { get; set; }
}
