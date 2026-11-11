using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Runs ffmpeg as a child process, parsing <c>-progress</c> output from stdout and keeping the tail of stderr for error messages.
/// </summary>
internal sealed class FfmpegRunner : IFfmpegRunner
{
    private const int MaxErrorLines = 20;

    private readonly ILogger<FfmpegRunner> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfmpegRunner"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public FfmpegRunner(ILogger<FfmpegRunner> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets how long ffmpeg may go without reporting progress before it is killed.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public async Task RunAsync(
        string ffmpegPath,
        IReadOnlyList<string> arguments,
        TimeSpan expectedDuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
        ArgumentNullException.ThrowIfNull(arguments);
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _logger.LogDebug("Starting {FfmpegPath} {Arguments}", ffmpegPath, string.Join(' ', arguments));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ClipExtractionException(
                string.Format(CultureInfo.InvariantCulture, "ffmpeg could not be started ({0}).", ex.Message),
                ex);
        }

        TryLowerPriority(process);

        using var stallCts = new CancellationTokenSource(StallTimeout);
        using var abortCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stallCts.Token);
        using var abortRegistration = abortCts.Token.Register(static state => TryKill((Process)state!), process);

        var outputTask = ReadProgressAsync(process.StandardOutput, expectedDuration, progress, stallCts);
        var errorTask = ReadErrorTailAsync(process.StandardError);

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        var errorOutput = await errorTask.ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (process.ExitCode == 0)
        {
            progress?.Report(1);
            return;
        }

        if (stallCts.IsCancellationRequested)
        {
            throw new ClipExtractionException(string.Format(
                CultureInfo.InvariantCulture,
                "ffmpeg stopped making progress for {0:0} seconds and was stopped.",
                StallTimeout.TotalSeconds));
        }

        // The exit code goes with the message: -28 (AVERROR(ENOSPC)) is a full disk, which the free-space guard maps along with
        // "No space left on device" in the error lines.
        throw new ClipExtractionException(string.IsNullOrEmpty(errorOutput)
            ? string.Format(CultureInfo.InvariantCulture, "ffmpeg failed with exit code {0}.", process.ExitCode)
            : string.Format(CultureInfo.InvariantCulture, "ffmpeg failed with exit code {0}: {1}", process.ExitCode, errorOutput))
        {
            ExitCode = process.ExitCode
        };
    }

    private static async Task<string> ReadErrorTailAsync(StreamReader reader)
    {
        var lines = new Queue<string>(MaxErrorLines);
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (lines.Count == MaxErrorLines)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line.Trim());
        }

        return string.Join(' ', lines);
    }

    private static void TryLowerPriority(Process process)
    {
        // Keep playback and transcoding responsive while clips encode.
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            // Not permitted on some platforms/containers, or the process already exited.
        }
    }

    private static void TryKill(Process process)
    {
        // This also runs from the stall timer's thread, where an unhandled exception would take the
        // whole server down; Kill can throw AggregateException when part of the process tree survives.
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already exited, or couldn't be killed; the caller reports the failure.
        }
    }

    private async Task ReadProgressAsync(
        StreamReader reader,
        TimeSpan expectedDuration,
        IProgress<double>? progress,
        CancellationTokenSource stallCts)
    {
        var finishing = false;
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (finishing)
            {
                continue;
            }

            // Any output means ffmpeg is alive; push the stall deadline back.
            stallCts.CancelAfter(StallTimeout);

            if (expectedDuration > TimeSpan.Zero && FfmpegProgressParser.TryParseOutputTime(line, out var outputTime))
            {
                var fraction = Math.Clamp(outputTime / expectedDuration, 0, 1);
                progress?.Report(fraction);

                // Finishing an MP4 (moving the index to the front) prints nothing and can take a while
                // for large files on slow disks, so stop watching for stalls once encoding is done.
                if (fraction >= 0.99)
                {
                    finishing = true;
                    stallCts.CancelAfter(Timeout.InfiniteTimeSpan);
                }
            }
        }
    }
}
