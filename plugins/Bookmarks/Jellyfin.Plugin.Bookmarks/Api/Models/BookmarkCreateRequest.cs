using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// Request body for creating a bookmark.
/// </summary>
/// <remarks>
/// <c>CreateBookmarkRequest</c> in Bookmarks 1.0, renamed in 1.1.0 for the schema prefix.
/// Its JSON is unchanged; only its OpenAPI schema id changed.
/// </remarks>
public sealed class BookmarkCreateRequest
{
    /// <summary>
    /// Gets or sets the id of the video to bookmark.
    /// </summary>
    [Required]
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the bookmarked position (or range start) in ticks.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the range end in ticks, or <c>null</c> for a single position.
    /// </summary>
    public long? EndTicks { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's name, or <c>null</c> (or white space) for an unnamed bookmark (new in 1.1):
    /// one line of at most 100 characters once normalised (line breaks and tabs become spaces, other control characters
    /// are dropped, runs of spaces become one); a longer one is a 400.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets an optional note.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets the media source (version) that was playing, if known.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the audio stream index that was playing, if known.
    /// </summary>
    public int? AudioStreamIndex { get; set; }
}
