using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// <see cref="IPlaylistExportLibrary"/> over Jellyfin's library.
/// </summary>
internal sealed class LibraryPlaylistExportLibrary : IPlaylistExportLibrary
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryPlaylistExportLibrary"/> class.
    /// </summary>
    /// <param name="libraryManager">The library.</param>
    /// <param name="mediaSourceManager">The media sources.</param>
    public LibraryPlaylistExportLibrary(ILibraryManager libraryManager, IMediaSourceManager mediaSourceManager)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(mediaSourceManager);
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
    }

    /// <inheritdoc />
    public PlaylistExportSource? FindPlaylist(Guid playlistId, User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        BaseItem? item;
        try
        {
            item = playlistId.Equals(Guid.Empty) ? null : _libraryManager.GetItemById(playlistId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Jellyfin throws for ids it can't materialize.
            item = null;
        }

        if (item is not Playlist playlist || !playlist.IsVisible(user))
        {
            return null;
        }

        // Every linked entry in order: one Jellyfin can't resolve any more (its item gone) is null, so it is listed as
        // skipped rather than silently dropped.
        var resolved = playlist.GetManageableItems();
        var entries = new List<BaseItem?>(playlist.LinkedChildren.Length);
        var next = 0;
        foreach (var child in playlist.LinkedChildren)
        {
            if (next < resolved.Count && ReferenceEquals(resolved[next].Item1, child))
            {
                entries.Add(resolved[next].Item2);
                next++;
            }
            else
            {
                entries.Add(null);
            }
        }

        // A Jellyfin version that hands copies of the links back matches none of them: then the resolved entries alone, in
        // their order (an entry gone is left out unseen rather than every one reported gone).
        if (next == 0 && resolved.Count > 0)
        {
            return new PlaylistExportSource(playlist.Id, playlist.Name ?? string.Empty, resolved.Select(r => (BaseItem?)r.Item2).ToList());
        }

        entries.AddRange(resolved.Skip(next).Select(r => r.Item2));
        return new PlaylistExportSource(playlist.Id, playlist.Name ?? string.Empty, entries);
    }

    /// <inheritdoc />
    public bool IsVisible(BaseItem item, User user)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsVisibleStandalone(user);
    }

    /// <inheritdoc />
    public bool CanDownload(BaseItem item, User user)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.CanDownload(user);
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaSourceInfo> GetMediaSources(Video video, User user)
        => _mediaSourceManager.GetStaticMediaSources(video, false, user);

    /// <inheritdoc />
    public string? GetPosterPath(Guid itemId)
    {
        BaseItem? item;
        try
        {
            item = itemId.Equals(Guid.Empty) ? null : _libraryManager.GetItemById(itemId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            item = null;
        }

        return item?.GetImages(ImageType.Primary).FirstOrDefault() is { IsLocalFile: true, Path: { Length: > 0 } path } ? path : null;
    }
}
