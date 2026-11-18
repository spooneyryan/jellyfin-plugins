using System;
using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Clips;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// The library as a playlist export sees it, for tests: playlists with their entries (and the users who may open them),
/// the items a user may not see or download, each video's media sources, and the posters.
/// </summary>
internal sealed class FakePlaylistExportLibrary : IPlaylistExportLibrary
{
    /// <summary>Gets the playlists by id.</summary>
    public Dictionary<Guid, PlaylistExportSource> Playlists { get; } = [];

    /// <summary>Gets the users who may open each playlist; a playlist not listed here is open to everyone.</summary>
    public Dictionary<Guid, HashSet<Guid>> PlaylistUsers { get; } = [];

    /// <summary>Gets the items no user may see.</summary>
    public HashSet<Guid> Hidden { get; } = [];

    /// <summary>Gets the items no user may download.</summary>
    public HashSet<Guid> NotDownloadable { get; } = [];

    /// <summary>Gets each video's media sources.</summary>
    public Dictionary<Guid, List<MediaSourceInfo>> Sources { get; } = [];

    /// <summary>Gets each item's poster.</summary>
    public Dictionary<Guid, string> Posters { get; } = [];

    /// <summary>
    /// A media source of a video file: one video stream, and an audio stream unless <paramref name="audio"/> is false.
    /// </summary>
    public static MediaSourceInfo FileSource(
        Guid itemId,
        string path,
        double seconds,
        int width = 1280,
        int height = 720,
        float frameRate = 25,
        bool audio = true,
        int? bitRate = null)
    {
        var streams = new List<MediaStream>
        {
            new() { Index = 0, Type = MediaStreamType.Video, Width = width, Height = height, RealFrameRate = frameRate, BitRate = bitRate, Codec = "h264" }
        };
        if (audio)
        {
            streams.Add(new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true });
        }

        return new MediaSourceInfo
        {
            Id = itemId.ToString("N"),
            Path = path,
            Protocol = MediaProtocol.File,
            VideoType = VideoType.VideoFile,
            RunTimeTicks = (long)(seconds * TimeSpan.TicksPerSecond),
            MediaStreams = streams
        };
    }

    /// <summary>Adds a playlist.</summary>
    public Guid AddPlaylist(string name, params BaseItem?[] entries)
    {
        var id = Guid.NewGuid();
        Playlists[id] = new PlaylistExportSource(id, name, entries);
        return id;
    }

    public PlaylistExportSource? FindPlaylist(Guid playlistId, User user)
        => Playlists.TryGetValue(playlistId, out var playlist)
            && (!PlaylistUsers.TryGetValue(playlistId, out var users) || users.Contains(user.Id))
                ? playlist
                : null;

    public bool IsVisible(BaseItem item, User user) => !Hidden.Contains(item.Id);

    public bool CanDownload(BaseItem item, User user) => !NotDownloadable.Contains(item.Id);

    public IReadOnlyList<MediaSourceInfo> GetMediaSources(Video video, User user)
        => Sources.TryGetValue(video.Id, out var sources) ? sources : [];

    public string? GetPosterPath(Guid itemId) => Posters.GetValueOrDefault(itemId);
}
