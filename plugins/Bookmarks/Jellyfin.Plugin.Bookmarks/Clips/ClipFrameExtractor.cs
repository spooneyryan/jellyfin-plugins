using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The real <see cref="IClipFrameExtractor"/> (since 1.7.0 / 2.5.0): Jellyfin's configured ffmpeg (the encoder path the clip
/// jobs use, <c>IMediaEncoder.EncoderPath</c>, read at every use) runs <see cref="FfmpegFrameCommandBuilder"/>'s command
/// through the clip jobs' runner (<see cref="IFfmpegRunner"/>: below-normal priority, killed when it stalls or is cancelled,
/// the tail of its errors in the exception).
/// </summary>
/// <remarks>
/// One frame takes well under a second; <see cref="Timeout"/> stops one that doesn't finish (a file on a drive that stopped
/// answering). An exit code of 0 with no file, or an empty one, is a failure too.
/// </remarks>
internal sealed class ClipFrameExtractor : IClipFrameExtractor
{
    private readonly Func<string?> _encoderPath;
    private readonly IFfmpegRunner _runner;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFrameExtractor"/> class.
    /// </summary>
    /// <param name="encoderPath">Jellyfin's ffmpeg, read at every use (empty when none is configured).</param>
    /// <param name="runner">Runs ffmpeg.</param>
    public ClipFrameExtractor(Func<string?> encoderPath, IFfmpegRunner runner)
    {
        ArgumentNullException.ThrowIfNull(encoderPath);
        ArgumentNullException.ThrowIfNull(runner);
        _encoderPath = encoderPath;
        _runner = runner;
    }

    /// <summary>
    /// Gets how long one extraction may take.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <inheritdoc />
    public async Task ExtractFirstFrameAsync(string clipPath, string outputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(clipPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var ffmpeg = _encoderPath();
        if (string.IsNullOrEmpty(ffmpeg))
        {
            throw new ClipExtractionException("ffmpeg isn't configured on this server.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await _runner.RunAsync(ffmpeg, FfmpegFrameCommandBuilder.Build(clipPath, outputPath), TimeSpan.Zero, null, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClipExtractionException("ffmpeg took too long to take the clip's first frame.", ex);
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
        {
            throw new ClipExtractionException("ffmpeg wrote no image of the clip's first frame.");
        }
    }
}
