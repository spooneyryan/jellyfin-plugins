using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Resolves bookmarks against the library to produce clip jobs.
/// </summary>
internal sealed class ClipPlanner : IClipPlanner
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly BookmarkPaths _paths;
    private readonly ClipFileOps? _files;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipPlanner"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="mediaEncoder">The media encoder, for the ffmpeg path and its capabilities.</param>
    /// <param name="configurationProvider">The plugin configuration.</param>
    /// <param name="paths">The plugin's paths.</param>
    /// <param name="files">The clip file operations, whose rule of taken names a new clip's name follows; without them
    /// (a test's planner) only a file there, or its <c>.partial</c>, takes a name.</param>
    public ClipPlanner(
        ILibraryManager libraryManager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IMediaEncoder mediaEncoder,
        IPluginConfigurationProvider configurationProvider,
        BookmarkPaths paths,
        ClipFileOps? files = null)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _mediaSourceManager = mediaSourceManager;
        _mediaEncoder = mediaEncoder;
        _configurationProvider = configurationProvider;
        _paths = paths;
        _files = files;
    }

    /// <inheritdoc />
    public ClipPlan Plan(Bookmark bookmark, BookmarkClipMode mode)
    {
        ArgumentNullException.ThrowIfNull(bookmark);

        if (bookmark.EndTicks is not long endTicks || endTicks <= bookmark.StartTicks)
        {
            throw new ClipExtractionException("The bookmark needs an end time after its start time to make a clip.");
        }

        var configuration = _configurationProvider.GetConfiguration();
        var duration = TimeSpan.FromTicks(endTicks - bookmark.StartTicks);
        if (configuration.MaxClipDurationSeconds > 0 && duration > TimeSpan.FromSeconds(configuration.MaxClipDurationSeconds))
        {
            throw new ClipExtractionException(string.Format(
                CultureInfo.InvariantCulture,
                "Clips can be at most {0} long; this one is {1}.",
                FormatDuration(TimeSpan.FromSeconds(configuration.MaxClipDurationSeconds)),
                FormatDuration(duration)));
        }

        var encoderPath = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(encoderPath))
        {
            throw new ClipExtractionException("ffmpeg isn't configured on this server.");
        }

        var user = _userManager.GetUserById(bookmark.UserId)
            ?? throw new ClipExtractionException("The bookmark's owner no longer exists.");
        // An adopted clip has no library item, and 10.11.5 throws for an empty id.
        var item = (BookmarkRecordRules.HasLibraryItem(bookmark) ? _libraryManager.GetItemById<Video>(bookmark.ItemId, user) : null)
            ?? throw new ClipExtractionException("The bookmarked video is no longer in the library.");

        var source = SelectMediaSource(item, user, bookmark.MediaSourceId);
        var video = source.VideoStream;
        var audio = SelectAudioStream(source, bookmark.AudioStreamIndex);

        var root = GetRoot(configuration);
        var outputDirectory = configuration.OrganizeClipsByUser
            ? Path.Combine(root, ClipFileNaming.BuildFolderName(user.Username, user.Id.ToString("N", CultureInfo.InvariantCulture)))
            : root;

        // The bookmark's name, never its note (admins see file names and paths): a bookmark without a name gives a clip
        // named after its video and its range.
        var fileName = ClipFileNaming.BuildFileName(bookmark.ItemName, BookmarkNames.ClipFileLabel(bookmark), bookmark.StartTicks, endTicks, mode);

        // A name is taken by any file there, or by a clip being written right now
        // (its .partial file); since 1.2.0 also by any record's path, its file there or not (a "File missing"
        // record would otherwise name the new clip's file, and its Forget or Delete, or its remake's clean-up, delete it), a
        // file waiting to be deleted and a rename's paths: the rule renames choose by (ClipFileOps.NameTaken). A clip folder
        // that can't answer a look-up (a drive that isn't ready, a share that doesn't answer) ends the plan at the first one
        // (since 1.2.0): before, every one of up to a thousand names was asked about, on the request's thread or under the
        // worker's name gate. A look-up that fails is asked once more, a pause later. The bookmark's own previous clip
        // doesn't take its name since Bookmarks 1.2.1 (Bookmarks 1.1 gave a remake a fresh name, " (2)" and on, so it
        // never had to replace a file that may be in use): a remake whose name is its own file's writes its .partial
        // and replaces the file at the end, or takes a fresh name then if the file is in use. Its file is reusable only
        // while nothing else holds it (ClipFileOps.ReusableOwnPath).
        var own = _files?.ReusableOwnPath(bookmark.Id, bookmark.Clip?.FilePath);
        string outputPath;
        try
        {
            outputPath = ClipFileNaming.MakeUnique(
                outputDirectory,
                fileName,
                _files?.NameTaken(own, retryOnce: true) ?? (path => File.Exists(path) || File.Exists(path + ClipFileGuard.PartialExtension)));
        }
        catch (ClipFolderUnreachableException ex)
        {
            throw new ClipExtractionException(ClipFileOps.FolderUnreachableClipMessage, ex) { FolderUnreachable = true };
        }

        var tempPath = outputPath + ClipFileGuard.PartialExtension;

        var command = new ClipCommand
        {
            InputPath = source.Path,
            OutputPath = tempPath,
            Mode = mode,
            Start = TimeSpan.FromTicks(bookmark.StartTicks),
            Duration = duration,
            VideoStreamIndex = GetInputIndex(source, video),
            AudioStreamIndex = GetInputIndex(source, audio),
            IsInterlaced = video?.IsInterlaced ?? false,
            SourceHeight = video?.Height,
            Tonemap = SelectTonemap(video),
            IsDolbyVision = video?.VideoRangeType == VideoRangeType.DOVI,
            Title = BuildTitle(bookmark, endTicks),
            Encoding = ClipEncodingOptions.FromConfiguration(configuration)
        };

        return new ClipPlan
        {
            EncoderPath = encoderPath,
            Command = command,
            OutputPath = outputPath,
            TempPath = tempPath,
            Duration = duration,
            Root = root,

            // The free-space guard's estimate: the request checks it and keeps it with the queued job.
            EstimatedBytes = ClipSizeEstimator.Estimate(command, source, video),
            ReplacesOwnFile = own is not null && PathsEqual(outputPath, own)
        };
    }

    /// <summary>
    /// Compares two paths using the platform's file system case rules.
    /// </summary>
    /// <param name="first">The first path.</param>
    /// <param name="second">The second path.</param>
    /// <returns><c>true</c> if both refer to the same file name.</returns>
    internal static bool PathsEqual(string? first, string? second)
        => string.Equals(
            first,
            second,
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static MediaStream? SelectAudioStream(MediaSourceInfo source, int? preferredIndex)
    {
        var audioStreams = source.MediaStreams
            .Where(s => s.Type == MediaStreamType.Audio && !s.IsExternal)
            .ToList();

        if (audioStreams.Count == 0)
        {
            return null;
        }

        return (preferredIndex is int preferred ? audioStreams.Find(s => s.Index == preferred) : null)
            ?? (source.DefaultAudioStreamIndex is int defaultIndex ? audioStreams.Find(s => s.Index == defaultIndex) : null)
            ?? audioStreams.Find(s => s.IsDefault)
            ?? audioStreams[0];
    }

    private static int? GetInputIndex(MediaSourceInfo source, MediaStream? stream)
    {
        if (stream is null)
        {
            return null;
        }

        // Same mapping Jellyfin uses for transcodes: the position among streams from the same file.
        var index = EncodingHelper.FindIndex(source.MediaStreams, stream);
        return index >= 0 ? index : null;
    }

    private static string BuildTitle(Bookmark bookmark, long endTicks)
    {
        var range = string.Create(
            CultureInfo.InvariantCulture,
            $"{FormatClock(bookmark.StartTicks)}-{FormatClock(endTicks)}");
        // The file's title metadata as its name: never the note, which an admin's download would carry.
        return BookmarkNames.ClipFileLabel(bookmark) is not { } label
            ? $"{bookmark.ItemName} ({range})"
            : $"{bookmark.ItemName} - {label} ({range})";
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string FormatClock(long ticks)
    {
        var time = TimeSpan.FromTicks(ticks);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}");
    }

    private MediaSourceInfo SelectMediaSource(Video item, User user, string? mediaSourceId)
    {
        // Without the bookmark's version (none saved, or gone), the item's own file: Jellyfin 12 lists the version the user
        // last played first, so the first source would depend on the user's history.
        var sources = _mediaSourceManager.GetStaticMediaSources(item, false, user);
        var source = (string.IsNullOrEmpty(mediaSourceId)
                ? null
                : sources.FirstOrDefault(s => string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase)))
            ?? sources.FirstOrDefault(s => string.Equals(s.Id, item.Id.ToString("N", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            ?? (sources.Count > 0 ? sources[0] : null)
            ?? throw new ClipExtractionException("The video has no playable media source.");

        if (source.Protocol != MediaProtocol.File
            || source.IsoType is not null
            || source.VideoType is not (null or VideoType.VideoFile))
        {
            throw new ClipExtractionException("Clips can only be made from regular video files (not discs, ISOs or streams).");
        }

        if (string.IsNullOrEmpty(source.Path) || !File.Exists(source.Path))
        {
            throw new ClipExtractionException("The video file can't be found on the server.");
        }

        return source;
    }

    private TonemapMode SelectTonemap(MediaStream? video)
    {
        if (video is null || video.VideoRange != VideoRange.HDR)
        {
            return TonemapMode.None;
        }

        if (_mediaEncoder.SupportsFilter("tonemapx"))
        {
            return TonemapMode.TonemapX;
        }

        return video.VideoRangeType != VideoRangeType.DOVI && _mediaEncoder.SupportsFilter("zscale")
            ? TonemapMode.ZScale
            : TonemapMode.None;
    }

    // The configured clip folder, or the default one, as ClipRoots records it: absolute and without a trailing separator,
    // so every clip file path the worker stores is absolute and lies under a recorded folder.
    private string GetRoot(PluginConfiguration configuration)
        => ClipRootPaths.TryGetCurrent(configuration, _paths, out var root)
            ? root
            : throw new ClipExtractionException(ClipRootPaths.InvalidFolderMessage);
}
