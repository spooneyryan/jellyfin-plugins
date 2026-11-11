using System;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// One clip of the My Clips page: a bookmark's clip, or the clip kept when its bookmark was
/// deleted, with what the bookmark stored about its video. The clip file's folder and the video's path never
/// reach it; only the file's name does.
/// </summary>
public sealed class BookmarkClipItemDto
{
    /// <summary>
    /// Gets or sets the bookmark's id, also a deleted bookmark's: the clip's routes take it
    /// (<c>/Bookmarks/{id}/Clip/File</c>, <c>DELETE /Bookmarks/{id}/Clip</c>).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bookmark was deleted while its clip was kept ("Bookmark deleted"): only
    /// the clip is left, so no bookmark route reaches it.
    /// </summary>
    public bool BookmarkDeleted { get; set; }

    /// <summary>
    /// Gets or sets where the bookmark came from: <c>Native</c>, <c>JellyfinEnhanced</c> or <c>Adopted</c> (reserved;
    /// not written by this version).
    /// </summary>
    public BookmarkOrigin Origin { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's name, or <c>null</c> when it is unnamed.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's note.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets the id of the video the clip was cut from, as the bookmark stored it.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the video's display name, as the bookmark stored it when it was made.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the video's kind, such as <c>Movie</c> or <c>Episode</c>, from the bookmark's identity snapshot.
    /// </summary>
    public string? ItemType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller can open the video now: Jellyfin's own
    /// check for this user, on every request. Without it the page shows no poster and no Play source; the clip file itself
    /// still downloads.
    /// </summary>
    public bool ItemAvailable { get; set; }

    /// <summary>
    /// Gets or sets the media source (version) that was playing when the bookmark was made, if known, for Play source.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the audio stream that was playing when the bookmark was made, if known, for Play source.
    /// </summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's start in ticks (it may have moved since the clip was made: <c>Clip.IsOutdated</c>).
    /// </summary>
    public long BookmarkStartTicks { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's end in ticks, or <c>null</c> for a single position.
    /// </summary>
    public long? BookmarkEndTicks { get; set; }

    /// <summary>
    /// Gets or sets the clip, with what the page works out about it: its state, its file and the badges' facts.
    /// </summary>
    public BookmarkClipDto Clip { get; set; } = new();

    /// <summary>
    /// Gets or sets when the bookmark was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when the bookmark was last changed (UTC); for a deleted bookmark, when it was deleted.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
