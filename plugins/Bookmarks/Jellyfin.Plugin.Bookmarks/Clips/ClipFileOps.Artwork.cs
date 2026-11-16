using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The clip artwork's hooks (since 1.3.0 / 2.1.0, <see cref="ClipArtwork"/>): the clip worker, the renames and the deletes
/// call them once their record is written, and none of them ever fails its caller: a failure is a Warning, and the daily
/// maintenance puts things right.
/// </summary>
internal sealed partial class ClipFileOps
{
    /// <summary>
    /// Gets or sets where the clip artwork comes from (the registrator sets it; resolved when it is first used, since
    /// <see cref="ClipArtwork"/> takes these file operations itself). Without it, the hooks do nothing.
    /// </summary>
    internal Func<ClipArtwork?>? Artwork { get; set; }

    /// <summary>
    /// Gets the file system these operations use, for <see cref="ClipArtwork"/>.
    /// </summary>
    internal IClipFileSystem FileSystem => _fileSystem;

    /// <summary>
    /// Gives a clip its video's artwork after it was made (<see cref="ClipArtwork.SyncAsync"/>), never failing.
    /// </summary>
    /// <param name="bookmarkId">The clip's record.</param>
    /// <returns>A task that completes when it is done.</returns>
    public async Task ArtworkForClipAsync(Guid bookmarkId)
    {
        try
        {
            if (Artwork?.Invoke() is { } artwork)
            {
                await artwork.SyncAsync(bookmarkId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not give the clip of bookmark {BookmarkId} its video's artwork; the daily maintenance tries again", bookmarkId);
        }
    }

    /// <summary>
    /// Deletes the artwork of clip files records let go of (deleted, forgotten, replaced), when nothing names those clips any
    /// more (<see cref="ClipArtwork.CleanUpAsync"/>), never failing.
    /// </summary>
    /// <param name="clipPaths">The clip files' paths, as the records named them.</param>
    /// <returns>A task that completes when it is done.</returns>
    public async Task ArtworkAfterLetGoAsync(IEnumerable<string?> clipPaths)
    {
        ArgumentNullException.ThrowIfNull(clipPaths);
        try
        {
            if (Artwork?.Invoke() is { } artwork)
            {
                await artwork.CleanUpAsync(clipPaths, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete the artwork of clip files that are gone; the daily maintenance tries again");
        }
    }

    /// <summary>
    /// Moves a renamed clip's artwork to its new name (<see cref="ClipArtwork.AfterRenameAsync"/>), never failing.
    /// </summary>
    /// <param name="bookmarkId">The clip's record.</param>
    /// <param name="from">The clip file's old full path.</param>
    /// <param name="to">Its new full path.</param>
    /// <returns>A task that completes when it is done.</returns>
    public async Task ArtworkAfterRenameAsync(Guid bookmarkId, string from, string to)
    {
        try
        {
            if (Artwork?.Invoke() is { } artwork)
            {
                await artwork.AfterRenameAsync(bookmarkId, from, to, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not move the artwork of the renamed clip of bookmark {BookmarkId}; the daily maintenance puts it right", bookmarkId);
        }
    }
}
