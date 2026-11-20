using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// A clip's first frame without ffmpeg: writes <see cref="Frame"/> to the output, records each clip it was asked about, and
/// fails as told (after writing part of a file, as ffmpeg could).
/// </summary>
internal sealed class FakeClipFrameExtractor : IClipFrameExtractor
{
    /// <summary>Gets or sets what the "frame" file holds.</summary>
    public string Frame { get; set; } = "first frame";

    /// <summary>Gets or sets the failure to throw, after a partial file is written.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets the clip files asked about, in order.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Gets the output paths written to, in order.</summary>
    public List<string> Outputs { get; } = [];

    /// <inheritdoc />
    public async Task ExtractFirstFrameAsync(string clipPath, string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(clipPath);
        Outputs.Add(outputPath);
        if (Failure is not null)
        {
            await File.WriteAllTextAsync(outputPath, "half a frame", cancellationToken);
            throw Failure;
        }

        await File.WriteAllTextAsync(outputPath, Frame, cancellationToken);
    }
}
