using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The filters, order and page of <c>GET /Bookmarks/Query</c> and <c>GET /Bookmarks/Groups</c>,
/// bound from the query string; each endpoint takes its own <c>sortBy</c> next to it. Every filter works on the caller's
/// own bookmarks as stored, so none asks the library; a filter left out matches everything.
/// </summary>
public sealed class BookmarkQueryFilter
{
    /// <summary>
    /// Gets or sets the text to look for in the bookmark's name and note, the video's display name and its series' name,
    /// ignoring case and accents; at most 100 characters (400 otherwise). White space alone searches for nothing.
    /// </summary>
    public string? SearchTerm { get; set; }

    /// <summary>
    /// Gets or sets a video id: only the bookmarks on it (the details page's "See all"). It only narrows the caller's own
    /// bookmarks by the id they stored, so the library isn't asked.
    /// </summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the kinds of video: <c>Movie</c>, <c>Episode</c>, <c>Other</c>, comma-separated or repeated; left out,
    /// every kind. A value that isn't one of them is a 400.
    /// </summary>
    [CommaSeparatedQuery]
    public IReadOnlyList<BookmarkItemType>? ItemTypes { get; set; }

    /// <summary>
    /// Gets or sets whether to list only ranges (<c>true</c>) or only single positions (<c>false</c>).
    /// </summary>
    public bool? HasRange { get; set; }

    /// <summary>
    /// Gets or sets whether to list only bookmarks with a clip file (<c>true</c>) or only those without one (<c>false</c>);
    /// a clip that is still waiting or being made has no file yet.
    /// </summary>
    public bool? HasClip { get; set; }

    /// <summary>
    /// Gets or sets whether to list only named bookmarks (<c>true</c>) or only unnamed ones (<c>false</c>, "Needs a name").
    /// </summary>
    public bool? Named { get; set; }

    /// <summary>
    /// Gets or sets whether to list only bookmarks whose video is in the library (<c>Available</c>) or only those whose
    /// video left it (<c>Missing</c>, "Not in library"); by the stored state, never by asking the library.
    /// </summary>
    public BookmarkAvailability? Availability { get; set; }

    /// <summary>
    /// Gets or sets the order of <c>sortBy</c>: <c>Ascending</c> or <c>Descending</c>. Left out, times and counts come
    /// newest or most first, names and positions from A or the start.
    /// </summary>
    public SortOrder? SortOrder { get; set; }

    /// <summary>
    /// Gets or sets how many bookmarks (or groups) of the list to skip; 0 by default.
    /// </summary>
    public int? StartIndex { get; set; }

    /// <summary>
    /// Gets or sets how many bookmarks (or groups) to return: 1 to 200; 50 bookmarks or 25 groups by default.
    /// </summary>
    public int? Limit { get; set; }
}
