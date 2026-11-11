using System;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// One clip of any user on the My Clips page's "All users" scope: what
/// <see cref="BookmarkClipItemDto"/> carries, with its owner, the clip file's full path and its clip folder's id, and
/// <b>without the bookmark's note</b>: admins see the bookmark's name, which the clip file's name
/// carries anyway, never the note a user wrote. There is no <c>Note</c> member at all, so none can be sent.
/// </summary>
public sealed class BookmarkAdminClipItemDto
{
    /// <summary>
    /// Gets or sets the bookmark's id, also a deleted bookmark's: the admin clip routes take it.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the clip's owner.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the owner's name on this server now, or <c>"Deleted user (&lt;id&gt;)"</c> for an id no user of this server
    /// has (a deleted user's clips kept under <c>KeepAsOrphan</c>, or data restored from another server).
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether no user of this server has the owner's id.
    /// </summary>
    public bool UserDeleted { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bookmark was deleted while its clip was kept ("Bookmark deleted").
    /// </summary>
    public bool BookmarkDeleted { get; set; }

    /// <summary>
    /// Gets or sets where the bookmark came from: <c>Native</c>, <c>JellyfinEnhanced</c> or <c>Adopted</c> (reserved;
    /// not written by this version).
    /// </summary>
    public BookmarkOrigin Origin { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's name, or <c>null</c> when it is unnamed. Never its note.
    /// </summary>
    public string? Name { get; set; }

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
    /// Gets or sets a value indicating whether the caller (the admin) can open the video now: Jellyfin's own check as the
    /// caller, on every request; false for an API key, which has no user to ask for.
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
    /// Gets or sets the bookmark's start in ticks.
    /// </summary>
    public long BookmarkStartTicks { get; set; }

    /// <summary>
    /// Gets or sets the bookmark's end in ticks, or <c>null</c> for a single position.
    /// </summary>
    public long? BookmarkEndTicks { get; set; }

    /// <summary>
    /// Gets or sets the clip, with what the page works out about it, as the owner's own list has it.
    /// </summary>
    public BookmarkClipDto Clip { get; set; } = new();

    /// <summary>
    /// Gets or sets the clip file's full path on the server, as the record names it, or <c>null</c> for a clip without a file
    /// (paths appear only in admin answers).
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Gets or sets the id of the recorded clip folder the file lies in (the first 12 hex digits of the SHA-256 of its path,
    /// as Diagnostics names it), or <c>null</c> when the file is in none of them.
    /// </summary>
    public string? RootId { get; set; }

    /// <summary>
    /// Gets or sets when the bookmark was created (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets when the bookmark was last changed (UTC); for a deleted bookmark, when it was deleted.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
