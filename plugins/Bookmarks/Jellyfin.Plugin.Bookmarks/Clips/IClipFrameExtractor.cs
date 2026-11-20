using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Takes a clip file's first frame as an image (since 1.7.0 / 2.5.0, the setting <c>UseClipFirstFrameAsThumbnail</c>):
/// <see cref="ClipFrameExtractor"/> with Jellyfin's ffmpeg is the real one; tests give their own.
/// </summary>
internal interface IClipFrameExtractor
{
    /// <summary>
    /// Writes the clip file's very first frame as a JPEG to a new file.
    /// </summary>
    /// <param name="clipPath">The clip file's full path.</param>
    /// <param name="outputPath">The JPEG's full path, a name that isn't taken (<see cref="ClipArtwork"/>'s temporary name).
    /// Whatever the extractor leaves there on a failure is the caller's to delete.</param>
    /// <param name="cancellationToken">Stops the extraction.</param>
    /// <returns>A task that completes when the image is written.</returns>
    /// <exception cref="ClipExtractionException">No ffmpeg is configured, or it failed or wrote nothing.</exception>
    Task ExtractFirstFrameAsync(string clipPath, string outputPath, CancellationToken cancellationToken);
}
