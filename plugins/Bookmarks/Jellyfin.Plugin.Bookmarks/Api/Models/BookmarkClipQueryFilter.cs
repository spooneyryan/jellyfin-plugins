using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The filters and page of <c>GET /Bookmarks/Clips</c>, the My Clips page's list, bound
/// from the query string; the order is <c>sortBy</c> next to it. Every filter works on the caller's own clips; a filter left
/// out matches everything.
/// </summary>
public sealed class BookmarkClipQueryFilter
{
    /// <summary>
    /// Gets or sets the text to look for in the bookmark's name and note, the video's display name and the clip file's name,
    /// ignoring case and accents; at most 100 characters (400 otherwise). White space alone searches for nothing.
    /// </summary>
    public string? SearchTerm { get; set; }

    /// <summary>
    /// Gets or sets the states to list (<c>state=Ready,InProgress,Failed,Missing,FolderUnavailable</c>, comma-separated or
    /// repeated); left out, every state. A value that isn't one of them is a 400.
    /// </summary>
    [CommaSeparatedQuery]
    public IReadOnlyList<BookmarkClipState>? State { get; set; }

    /// <summary>
    /// Gets or sets the mode to list: <c>Accurate</c> (MP4) or <c>Fast</c> (MKV); left out, both. A clip being made counts
    /// as the mode it is made in, any other clip with a file as its file's.
    /// </summary>
    public BookmarkClipMode? Mode { get; set; }

    /// <summary>
    /// Gets or sets the kinds of video: <c>Movie</c>, <c>Episode</c>, <c>Other</c>, comma-separated or repeated; left out,
    /// every kind. A value that isn't one of them is a 400.
    /// </summary>
    [CommaSeparatedQuery]
    public IReadOnlyList<BookmarkItemType>? ItemTypes { get; set; }

    /// <summary>
    /// Gets or sets whether the clips of deleted bookmarks are listed too (clips kept when their bookmark was deleted);
    /// <c>true</c> when left out.
    /// </summary>
    public bool? IncludeDeleted { get; set; }

    /// <summary>
    /// Gets or sets a video id: only the clips of bookmarks on it. It only narrows the caller's own clips by the id they
    /// stored, so the library isn't asked.
    /// </summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets how many clips of the list to skip; 0 by default.
    /// </summary>
    public int? StartIndex { get; set; }

    /// <summary>
    /// Gets or sets how many clips to return: 1 to 200; 50 by default.
    /// </summary>
    public int? Limit { get; set; }
}
