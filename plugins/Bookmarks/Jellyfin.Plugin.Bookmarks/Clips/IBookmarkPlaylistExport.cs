using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Playlist exports (since 1.5.0 / 2.3.0): a playlist's videos joined end to end, in playlist order, into one MP4 in the
/// caller's clip folder, re-encoded with the clip settings. The same permission as clips (clips on, and the user may
/// download media), only playlists the caller may open, only videos they may see and download.
/// </summary>
/// <remarks>
/// Public because the public controller takes it: DTOs, Jellyfin's public types and BCL types only.
/// </remarks>
public interface IBookmarkPlaylistExport
{
    /// <summary>
    /// Works out what exporting a playlist would make (<c>GET /Bookmarks/Playlists/{playlistId}/Export</c>).
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="isAdmin">Whether the caller is an admin (a free-space refusal names the drive and numbers).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The estimate.</returns>
    /// <exception cref="Kit.Api.PluginException">403 without the clip permission, 404 for a playlist the caller may not
    /// open, 400 when the clip settings can't make one, 503 when the clip folder can't be reached.</exception>
    Task<BookmarkPlaylistExportEstimateDto> EstimateAsync(User user, Guid playlistId, bool isAdmin, CancellationToken cancellationToken);

    /// <summary>
    /// Starts exporting a playlist (<c>POST /Bookmarks/Playlists/{playlistId}/Export</c>).
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="isAdmin">Whether the caller is an admin.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The export, queued.</returns>
    /// <exception cref="Kit.Api.PluginException">As <see cref="EstimateAsync"/>; 400 without a video or with too many; 409
    /// when that playlist is being exported for the caller already; 503 for too little free space, the clip folder away or
    /// read-only data.</exception>
    Task<BookmarkPlaylistExportJobDto> StartAsync(User user, Guid playlistId, bool isAdmin, CancellationToken cancellationToken);

    /// <summary>
    /// Lists the caller's exports since the server started, newest first (<c>GET /Bookmarks/Exports</c>).
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <returns>The exports.</returns>
    IReadOnlyList<BookmarkPlaylistExportJobDto> GetExports(User user);

    /// <summary>
    /// Gets one of the caller's exports (<c>GET /Bookmarks/Exports/{exportId}</c>).
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="exportId">The export's id.</param>
    /// <returns>The export.</returns>
    /// <exception cref="Kit.Api.PluginException">404 for one that isn't the caller's.</exception>
    BookmarkPlaylistExportJobDto GetExport(User user, Guid exportId);

    /// <summary>
    /// Cancels one of the caller's exports that waits or is being made; its unfinished file is deleted
    /// (<c>POST /Bookmarks/Exports/{exportId}/Cancel</c>).
    /// </summary>
    /// <param name="user">The caller.</param>
    /// <param name="exportId">The export's id.</param>
    /// <returns>The export as it is now (a running one reports Cancelled a moment later).</returns>
    /// <exception cref="Kit.Api.PluginException">404 for one that isn't the caller's.</exception>
    BookmarkPlaylistExportJobDto Cancel(User user, Guid exportId);
}
