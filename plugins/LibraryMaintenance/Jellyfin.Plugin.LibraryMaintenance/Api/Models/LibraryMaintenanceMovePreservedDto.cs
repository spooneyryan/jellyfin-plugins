using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// What a move keeps, as the plan sees it now; the job takes the definitive snapshot when it starts.
/// </summary>
public sealed class LibraryMaintenanceMovePreservedDto
{
    /// <summary>
    /// Gets or sets how many users have watch state, a rating or a favourite on the unit's items.
    /// </summary>
    public int UserDataUsers { get; set; }

    /// <summary>
    /// Gets or sets how many users marked it a favourite.
    /// </summary>
    public int Favorites { get; set; }

    /// <summary>
    /// Gets or sets how many users have a resume point.
    /// </summary>
    public int ResumePoints { get; set; }

    /// <summary>
    /// Gets or sets the date added that is kept.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets the collections it is in.
    /// </summary>
    public IReadOnlyList<string> Collections { get; set; } = [];

    /// <summary>
    /// Gets or sets the playlists it is in.
    /// </summary>
    public IReadOnlyList<string> Playlists { get; set; } = [];

    /// <summary>
    /// Gets or sets how metadata edits carry over (<c>MetadataCarryOver</c>).
    /// </summary>
    public string CarryOverMode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item's locked fields.
    /// </summary>
    public IReadOnlyList<string> LockedFields { get; set; } = [];

    /// <summary>
    /// Gets or sets how many of its images live in Jellyfin's metadata folder rather than next to the media.
    /// </summary>
    public int InternalImages { get; set; }

    /// <summary>
    /// Gets or sets how many downloaded subtitles live in Jellyfin's data; null until the job's snapshot counts them.
    /// </summary>
    public int? InternalSubtitles { get; set; }

    /// <summary>
    /// Gets or sets how many trickplay resolutions it has; null until the job's snapshot counts them.
    /// </summary>
    public int? TrickplayResolutions { get; set; }

    /// <summary>
    /// Gets or sets the installed plugins Move updates (none yet).
    /// </summary>
    public IReadOnlyList<string> HookConsumers { get; set; } = [];
}
