using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Runs ffmpeg and reports its progress.
/// </summary>
internal interface IFfmpegRunner
{
    /// <summary>
    /// Runs ffmpeg to completion.
    /// </summary>
    /// <param name="ffmpegPath">The ffmpeg executable.</param>
    /// <param name="arguments">The arguments, which must include <c>-progress pipe:1</c> for progress reporting.</param>
    /// <param name="expectedDuration">The length of the output, used to turn positions into a fraction.</param>
    /// <param name="progress">Receives progress from 0 to 1.</param>
    /// <param name="cancellationToken">Kills ffmpeg when cancelled.</param>
    /// <returns>A task that completes when ffmpeg exits successfully.</returns>
    /// <exception cref="ClipExtractionException">ffmpeg couldn't start, failed, or stopped making progress.</exception>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        TimeSpan expectedDuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
