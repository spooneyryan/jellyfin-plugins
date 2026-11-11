using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// One video's bookmarks in the grouped view of the My Bookmarks page, <c>GET /Bookmarks/Groups</c>: the
/// caller's bookmarks on the video that match the filters, ordered by position. The video's fields come from what the
/// bookmarks stored about it (their identity snapshot), never from the library, so a video the caller can no
/// longer open discloses nothing new.
/// </summary>
public sealed class BookmarkGroupDto
{
    /// <summary>
    /// Gets or sets the video's id, as the bookmarks stored it.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the video's display name, such as <c>Heat (1995)</c> or <c>Show S01E02 - Title</c>, as its most
    /// recently changed bookmark stored it.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video's kind, such as <c>Movie</c> or <c>Episode</c>, or <c>null</c> when it isn't known.
    /// </summary>
    public string? ItemType { get; set; }

    /// <summary>
    /// Gets or sets the video's year, when known.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets an episode's series name.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets an episode's season number.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets an episode's number.
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller can open the video now: in the library and in a library they may
    /// see (Jellyfin's own check for this user, on every request). <c>false</c> for a video that left the library and for
    /// one the caller lost access to ("Unavailable"): no poster, no play.
    /// </summary>
    public bool ItemAvailable { get; set; }

    /// <summary>
    /// Gets or sets how many of the caller's bookmarks on the video match the filters.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the latest <c>UpdatedAt</c> of those bookmarks (UTC).
    /// </summary>
    public DateTime LatestUpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the bookmarks, by position, at most 100 (<see cref="BookmarksTruncated"/> says when there are more).
    /// </summary>
    public IReadOnlyList<BookmarkDto> Bookmarks { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the group has more bookmarks than <see cref="Bookmarks"/> holds: filter by
    /// the video (<c>GET /Bookmarks/Query?itemId=</c>) to page through all of them.
    /// </summary>
    public bool BookmarksTruncated { get; set; }
}
