using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Works out a playlist export (since 1.5.0 / 2.3.0): which of a playlist's entries are joined, in playlist order, and why
/// the others are left out; the joined file's format, size and time; its folder and name.
/// </summary>
/// <remarks>
/// <para><b>Entries.</b> Each one is taken whole, in the playlist's order, when it is a video (a movie, an episode, a home
/// video, a music video, a Bookmarks clip in a clip library: any video item) that the user may see and download, whose
/// media source is a regular file that is there, with a video stream and a known length. Otherwise it is left out with its
/// reason (<see cref="PlaylistExportSkipReasons"/>): not in the library any more, not available to the user (its name not
/// shown), not a video (music, photos, books, folders), not downloadable, not a regular file (a disc, an ISO, a stream, a
/// <c>.strm</c>), its file missing (offline), no video stream, its length unknown. The video's own version is taken (else its
/// first), and its audio stream as Jellyfin plays it for the user by default (their audio language preference, else the
/// file's default, else the first); subtitles are never burned in.</para>
/// <para><b>Folder and name</b> as the user's clips: the clip folder, in the user's own folder when clips are organised by
/// user, named after the playlist (<see cref="PlaylistExportNaming"/>), with <c> (2)</c> and on when the name is taken by
/// any file, clip being written or clip record (the clips' rule, <see cref="ClipFileOps.NameTaken"/>).</para>
/// </remarks>
internal sealed class PlaylistExportPlanner
{
    /// <summary>The most videos one export joins: 100 (ffmpeg opens them all at once, and their paths go on its command line).</summary>
    public const int MaxItems = 100;

    /// <summary>The problem of an export without a video.</summary>
    public const string NoVideosMessage = "This playlist has no videos that can be exported.";

    private readonly IPlaylistExportLibrary _library;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly BookmarkPaths _paths;
    private readonly ClipFileOps? _files;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaylistExportPlanner"/> class.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <param name="mediaEncoder">Jellyfin's ffmpeg.</param>
    /// <param name="configurationProvider">The plugin configuration.</param>
    /// <param name="paths">The plugin's paths (the default clip folder).</param>
    /// <param name="files">The clip file operations, for the names taken; without them, a file or a <c>.partial</c> there.</param>
    public PlaylistExportPlanner(
        IPlaylistExportLibrary library,
        IMediaEncoder mediaEncoder,
        IPluginConfigurationProvider configurationProvider,
        BookmarkPaths paths,
        ClipFileOps? files = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(mediaEncoder);
        ArgumentNullException.ThrowIfNull(configurationProvider);
        ArgumentNullException.ThrowIfNull(paths);
        _library = library;
        _mediaEncoder = mediaEncoder;
        _configurationProvider = configurationProvider;
        _paths = paths;
        _files = files;
    }

    /// <summary>
    /// Works out the export of a playlist for a user.
    /// </summary>
    /// <param name="playlistId">The playlist.</param>
    /// <param name="user">The user, who must be allowed to make clips (the caller checks).</param>
    /// <returns>The plan, or <c>null</c> when the user may not open such a playlist.</returns>
    /// <exception cref="ClipExtractionException">ffmpeg isn't configured, the clip folder setting isn't a folder, or the clip
    /// folder can't be reached (<see cref="ClipExtractionException.FolderUnreachable"/>).</exception>
    public PlaylistExportPlan? Plan(Guid playlistId, User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_library.FindPlaylist(playlistId, user) is not { } playlist)
        {
            return null;
        }

        var configuration = _configurationProvider.GetConfiguration();
        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoderPath))
        {
            throw new ClipExtractionException("ffmpeg isn't configured on this server.");
        }

        if (!ClipRootPaths.TryGetCurrent(configuration, _paths, out var root))
        {
            throw new ClipExtractionException(ClipRootPaths.InvalidFolderMessage);
        }

        var (items, skipped) = Select(playlist.Entries, user);
        var encoding = ClipEncodingOptions.FromConfiguration(configuration);
        var format = PlaylistExportFormat.Choose(items, encoding.MaxHeight);
        var duration = TimeSpan.FromTicks(items.Sum(i => i.Duration.Ticks));

        var outputFolder = configuration.OrganizeClipsByUser
            ? Path.Combine(root, ClipFileNaming.BuildFolderName(user.Username, user.Id.ToString("N", CultureInfo.InvariantCulture)))
            : root;
        string outputPath;
        try
        {
            outputPath = ClipFileNaming.MakeUnique(outputFolder, PlaylistExportNaming.BuildFileName(playlist.Name), NameTaken());
        }
        catch (ClipFolderUnreachableException ex)
        {
            throw new ClipExtractionException(ClipFileOps.FolderUnreachableClipMessage, ex) { FolderUnreachable = true };
        }

        string? problem = null;
        if (items.Count == 0)
        {
            problem = NoVideosMessage;
        }
        else if (items.Count > MaxItems)
        {
            problem = string.Format(CultureInfo.InvariantCulture, "An export joins at most {0} videos; this playlist has {1}. Split it into smaller playlists.", MaxItems, items.Count);
        }

        return new PlaylistExportPlan
        {
            PlaylistId = playlist.Id,
            PlaylistName = playlist.Name,
            UserId = user.Id,
            Items = items,
            Skipped = skipped,
            Format = format,
            Encoding = encoding,
            Duration = duration,
            EstimatedBytes = PlaylistExportEstimator.EstimateBytes(items, format, encoding),
            EstimatedTime = PlaylistExportEstimator.EstimateTime(items, format, encoding),
            EncoderPath = encoderPath,
            Root = root,
            OutputFolder = outputFolder,
            OutputPath = outputPath,
            PosterPath = ChoosePoster(playlist.Id, items),
            Problem = problem
        };
    }

    /// <summary>
    /// The names a new export may not take: the clips' rule (<see cref="ClipFileOps.NameTaken"/>), or without the clip file
    /// operations a file or a <c>.partial</c> there.
    /// </summary>
    /// <returns>Whether a path is taken.</returns>
    internal Func<string, bool> NameTaken()
        => _files?.NameTaken(retryOnce: true) ?? (path => File.Exists(path) || File.Exists(path + ClipFileGuard.PartialExtension));

    /// <summary>
    /// Picks the entries joined, in order, and the reasons of those left out (<see cref="PlaylistExportPlanner"/>'s remarks).
    /// </summary>
    /// <param name="entries">The playlist's entries.</param>
    /// <param name="user">The user.</param>
    /// <returns>The videos and the entries left out.</returns>
    internal (List<PlaylistExportItem> Items, List<PlaylistExportSkip> Skipped) Select(IReadOnlyList<BaseItem?> entries, User user)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var items = new List<PlaylistExportItem>();
        var skipped = new List<PlaylistExportSkip>();
        foreach (var entry in entries)
        {
            var (item, reason) = Resolve(entry, user);
            if (item is not null)
            {
                items.Add(item);
            }
            else
            {
                // An item the user may not see, or one gone, is never named.
                var named = entry is not null && reason != PlaylistExportSkipReasons.NotAvailable;
                skipped.Add(new PlaylistExportSkip(named ? entry!.Name : null, reason!));
            }
        }

        return (items, skipped);
    }

    private (PlaylistExportItem? Item, string? Reason) Resolve(BaseItem? entry, User user)
    {
        if (entry is null)
        {
            return (null, PlaylistExportSkipReasons.NotInLibrary);
        }

        if (!_library.IsVisible(entry, user))
        {
            return (null, PlaylistExportSkipReasons.NotAvailable);
        }

        if (entry is not Video video || entry.IsFolder)
        {
            return (null, PlaylistExportSkipReasons.NotAVideo);
        }

        if (!_library.CanDownload(video, user))
        {
            return (null, PlaylistExportSkipReasons.NotDownloadable);
        }

        var sources = _library.GetMediaSources(video, user);
        var source = sources.FirstOrDefault(s => string.Equals(s.Id, video.Id.ToString("N", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            ?? (sources.Count > 0 ? sources[0] : null);
        if (source is null
            || video.IsShortcut
            || source.Protocol != MediaProtocol.File
            || source.IsoType is not null
            || source.VideoType is not (null or VideoType.VideoFile)
            || string.IsNullOrEmpty(source.Path)
            || string.Equals(Path.GetExtension(source.Path), ".strm", StringComparison.OrdinalIgnoreCase))
        {
            return (null, PlaylistExportSkipReasons.NotAFile);
        }

        if (!File.Exists(source.Path))
        {
            return (null, PlaylistExportSkipReasons.FileMissing);
        }

        var stream = source.VideoStream;
        if (stream is null || ClipPlanner.GetInputIndex(source, stream) is not int videoIndex)
        {
            return (null, PlaylistExportSkipReasons.NoVideoStream);
        }

        var ticks = source.RunTimeTicks is > 0 ? source.RunTimeTicks.Value : video.RunTimeTicks ?? 0;
        if (ticks <= 0)
        {
            return (null, PlaylistExportSkipReasons.LengthUnknown);
        }

        var audio = ClipPlanner.SelectAudioStream(source, null);
        var bitrate = stream.BitRate is int streamRate && streamRate > 0 ? streamRate : ClipSizeEstimator.SourceBitrate(source);
        return (new PlaylistExportItem
        {
            ItemId = video.Id,
            Name = video.Name ?? string.Empty,
            InputPath = source.Path,
            Duration = TimeSpan.FromTicks(ticks),
            VideoStreamIndex = videoIndex,
            AudioStreamIndex = ClipPlanner.GetInputIndex(source, audio),
            Width = stream.Width,
            Height = stream.Height,
            FrameRate = stream.RealFrameRate ?? stream.AverageFrameRate,
            IsInterlaced = stream.IsInterlaced,
            Tonemap = ClipPlanner.SelectTonemap(stream, _mediaEncoder),
            IsDolbyVision = stream.VideoRangeType == VideoRangeType.DOVI,
            SourceBitrate = bitrate
        }, null);
    }

    // The playlist's own Primary image, else its first video's: a local file only.
    private string? ChoosePoster(Guid playlistId, IReadOnlyList<PlaylistExportItem> items)
        => _library.GetPosterPath(playlistId) ?? (items.Count > 0 ? _library.GetPosterPath(items[0].ItemId) : null);
}
