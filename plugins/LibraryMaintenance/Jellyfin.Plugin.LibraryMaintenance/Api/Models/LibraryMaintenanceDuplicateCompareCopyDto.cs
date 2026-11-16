using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// One file of a duplicate group, as Jellyfin's library describes it: its file, its video, audio and subtitle tracks,
/// its special features, when it was added and how often it was played.
/// </summary>
public sealed class LibraryMaintenanceDuplicateCompareCopyDto
{
    /// <summary>
    /// Gets or sets the copy's item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item id of the group entry the copy belongs to (its main version).
    /// </summary>
    public Guid EntryItemId { get; set; }

    /// <summary>
    /// Gets or sets what the copy is to its entry: <c>Main</c>, <c>Version</c> (a version Jellyfin groups with it) or
    /// <c>LocalAlternate</c> (a version in the same folder, named after it).
    /// </summary>
    public string Role { get; set; } = "Main";

    /// <summary>
    /// Gets or sets the name Jellyfin stores.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file's (or disc folder's) path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file's name, the last part of <see cref="Path"/>.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the folder that holds it.
    /// </summary>
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the file is there: <c>Present</c>, <c>Missing</c> (an unplugged drive, a moved file),
    /// <c>Unknown</c> (no answer within 3 s, or a device path, which is never opened) or <c>NotAFile</c> (a stream or a
    /// shortcut's address). Only checked whether it exists; never opened.
    /// </summary>
    public string FileState { get; set; } = "Unknown";

    /// <summary>
    /// Gets or sets the size in bytes, or null when unknown.
    /// </summary>
    public long? SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the container (<c>mkv</c>, <c>mp4</c>), or null.
    /// </summary>
    public string? Container { get; set; }

    /// <summary>
    /// Gets or sets the video type: <c>VideoFile</c>, <c>Dvd</c>, <c>BluRay</c> or <c>Iso</c>.
    /// </summary>
    public string VideoType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the runtime in ticks, or null when unknown.
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the overall bitrate, or null when unknown.
    /// </summary>
    public int? TotalBitrate { get; set; }

    /// <summary>
    /// Gets or sets the stacked parts the copy plays (1 for a single file).
    /// </summary>
    public int PartCount { get; set; } = 1;

    /// <summary>
    /// Gets or sets the first video track, or null when Jellyfin has none for the copy (not yet probed, or offline when
    /// it was scanned).
    /// </summary>
    public LibraryMaintenanceDuplicateCompareVideoDto? Video { get; set; }

    /// <summary>
    /// Gets or sets the audio tracks, in the file's order.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateCompareAudioDto> Audio { get; set; } = [];

    /// <summary>
    /// Gets or sets the subtitle tracks, embedded and external, in Jellyfin's order.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateCompareSubtitleDto> Subtitles { get; set; } = [];

    /// <summary>
    /// Gets or sets the special features: the extras Jellyfin found for the copy and its commentary tracks.
    /// </summary>
    public LibraryMaintenanceDuplicateCompareFeaturesDto SpecialFeatures { get; set; } = new();

    /// <summary>
    /// Gets or sets when the copy was added (UTC).
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets how the server's users played the copy, or null when it isn't counted.
    /// </summary>
    public LibraryMaintenanceDuplicateComparePlayedDto? Played { get; set; }
}
