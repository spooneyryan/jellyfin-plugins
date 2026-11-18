using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What a playlist export asks of Jellyfin's library (since 1.5.0 / 2.3.0), behind an interface so its rules can be tested
/// without a library.
/// </summary>
internal interface IPlaylistExportLibrary
{
    /// <summary>
    /// Finds a playlist the user may open (its own, shared with them, or open to all).
    /// </summary>
    /// <param name="playlistId">The playlist's id.</param>
    /// <param name="user">The user.</param>
    /// <returns>The playlist's id, name and entries, or <c>null</c> when there is none the user may open.</returns>
    PlaylistExportSource? FindPlaylist(Guid playlistId, User user);

    /// <summary>
    /// Whether the user may see an item (Jellyfin's own rule: libraries, parental rating, tags).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="user">The user.</param>
    /// <returns>Whether they may.</returns>
    bool IsVisible(BaseItem item, User user);

    /// <summary>
    /// Whether the user may download an item (the rule clips have too).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="user">The user.</param>
    /// <returns>Whether they may.</returns>
    bool CanDownload(BaseItem item, User user);

    /// <summary>
    /// The video's media sources as the user would get them (Jellyfin's default audio stream chosen for them).
    /// </summary>
    /// <param name="video">The video.</param>
    /// <param name="user">The user.</param>
    /// <returns>The sources.</returns>
    IReadOnlyList<MediaSourceInfo> GetMediaSources(Video video, User user);

    /// <summary>
    /// The path of an item's Primary image, when it is a local file.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The path, or <c>null</c>.</returns>
    string? GetPosterPath(Guid itemId);
}
