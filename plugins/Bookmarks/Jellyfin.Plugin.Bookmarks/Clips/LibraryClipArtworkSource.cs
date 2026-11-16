using System;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The real <see cref="IClipArtworkSource"/>: the video's images as the library records them (only those stored as local
/// files), and an image refresh of a clip that is a library item, queued with the provider manager.
/// </summary>
internal sealed class LibraryClipArtworkSource : IClipArtworkSource
{
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryClipArtworkSource"/> class.
    /// </summary>
    /// <param name="libraryManager">The library, for the video, its series and the clip's own item.</param>
    /// <param name="providerManager">Queues the clip's image refresh.</param>
    /// <param name="fileSystem">The refresh's directory service's file system.</param>
    /// <param name="logger">The logger.</param>
    public LibraryClipArtworkSource(ILibraryManager libraryManager, IProviderManager providerManager, IFileSystem fileSystem, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(providerManager);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(logger);
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public ClipArtworkImages? Find(Guid itemId)
    {
        if (itemId.Equals(Guid.Empty) || _libraryManager.GetItemById(itemId) is not { } item)
        {
            return null;
        }

        if (item is Episode episode)
        {
            var series = episode.SeriesId.Equals(Guid.Empty) ? null : _libraryManager.GetItemById(episode.SeriesId);
            var own = LocalImage(episode, ImageType.Primary);
            return new ClipArtworkImages(
                LocalImage(series, ImageType.Primary) ?? own,
                LocalImage(episode, ImageType.Backdrop) ?? LocalImage(series, ImageType.Backdrop),
                own);
        }

        return new ClipArtworkImages(LocalImage(item, ImageType.Primary), LocalImage(item, ImageType.Backdrop), null);
    }

    /// <inheritdoc />
    public bool RefreshClip(string clipPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(clipPath);
        if (_libraryManager.FindByPath(clipPath, false) is not { } clip)
        {
            return false;
        }

        _providerManager.QueueRefresh(
            clip.Id,
            new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.Default,
                ImageRefreshMode = MetadataRefreshMode.Default
            },
            RefreshPriority.Normal);
        _logger.LogDebug("Queued an image refresh of clip item {ItemId}", clip.Id);
        return true;
    }

    // The item's first image of a type, when it is a local file.
    private static string? LocalImage(BaseItem? item, ImageType type)
        => item?.GetImages(type).FirstOrDefault() is { IsLocalFile: true, Path: { Length: > 0 } path } ? path : null;
}
