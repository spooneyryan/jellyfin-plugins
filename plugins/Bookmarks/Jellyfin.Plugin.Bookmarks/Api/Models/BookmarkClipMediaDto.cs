using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// What a video's page needs to show and play one of the caller's clips as a card (<c>GET /Bookmarks/Clips/Media</c>, since
/// 1.6.0 / 2.4.0): the Jellyfin library item its file is, when the caller can open it, and its artwork files.
/// </summary>
public sealed class BookmarkClipMediaDto
{
    /// <summary>
    /// Gets or sets the clip's id (its bookmark's).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the library item whose file is the clip file (a clip folder that is a library, such as a Home Videos
    /// library over it), when the caller can open that item; else <c>null</c>.
    /// </summary>
    public Guid? LibraryItemId { get; set; }

    /// <summary>
    /// Gets or sets the clip's artwork files that are there, in the order Poster, Fanart, Thumb.
    /// </summary>
    public IReadOnlyList<BookmarkClipArtworkDto> Artwork { get; set; } = [];
}
