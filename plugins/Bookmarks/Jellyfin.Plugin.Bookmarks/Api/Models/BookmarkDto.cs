using System;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// A bookmark as returned by the API.
/// </summary>
public sealed class BookmarkDto
{
    /// <summary>
    /// Gets or sets the bookmark id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the bookmarked item's id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item's display name, captured when the bookmark was created.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the item's type name, such as <c>Movie</c> or <c>Episode</c>: the kind in the bookmark's identity
    /// snapshot since schema 2.
    /// </summary>
    public string? ItemType { get; set; }

    /// <summary>
    /// Gets or sets the video's year, from the bookmark's identity snapshot, when known. New in 1.1.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Gets or sets an episode's series name, from the bookmark's identity snapshot. New in 1.1.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets an episode's season number, from the bookmark's identity snapshot. New in 1.1.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets an episode's number, from the bookmark's identity snapshot. New in 1.1.
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the media source (version) that was playing, if known.
    /// </summary>
    public string? MediaSourceId { get; set; }

    /// <summary>
    /// Gets or sets the audio stream index that was playing, if known.
    /// </summary>
    public int? AudioStreamIndex { get; set; }

    /// <summary>
    /// Gets or sets the bookmarked position (or range start) in ticks.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the range end in ticks, or <c>null</c> for a single position.
    /// </summary>
    public long? EndTicks { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's name, or <c>null</c> when it is unnamed. New in 1.1: the 1→2 upgrade
    /// moves a short single-line 1.0 note here.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the note.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets when the bookmark was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when the bookmark was last changed (UTC).
    /// </summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the latest clip, if one was requested.
    /// </summary>
    public BookmarkClipDto? Clip { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bookmark's video has left the library ("Not in library"):
    /// the bookmark waits to be reattached when the video comes back, moved or renamed. New in 1.1.
    /// </summary>
    public bool Detached { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bookmark was reattached to another file of its video by a provider-id
    /// match, whose positions may differ ("Relinked, check position"); the next edit or rename clears it.
    /// New in 1.1.
    /// </summary>
    public bool Relinked { get; set; }

    /// <summary>
    /// Gets or sets whether the caller can open the bookmark's video now: Jellyfin's own
    /// check for this user, on every request, of each video on the page. <c>false</c> for a detached bookmark and for a
    /// video the caller lost access to ("Unavailable": no poster, no play); only <c>GET /Bookmarks/Query</c> and
    /// <c>/Groups</c> fill it, every other route leaves it out (<c>null</c>). New in 1.1.
    /// </summary>
    public bool? ItemAvailable { get; set; }

    /// <summary>
    /// Gets or sets where the bookmark came from: <c>Native</c> (made with Bookmarks, every 1.0 bookmark too),
    /// <c>JellyfinEnhanced</c> (imported) or <c>Adopted</c> (reserved for a clip file adopted by an admin; not written by
    /// this version). New in 1.1.
    /// </summary>
    public BookmarkOrigin Origin { get; set; }
}
