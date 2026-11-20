using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Builds the ffmpeg arguments that take a clip file's very first frame as a JPEG (since 1.7.0 / 2.5.0,
/// <see cref="ClipFrameExtractor"/>), returned as a list for <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
/// </summary>
/// <remarks>
/// <para>The first video stream's first decoded frame (<c>-map 0:v:0 -frames:v 1</c>, no seek), with square pixels
/// (<c>scale</c> by the sample aspect ratio, which ffmpeg reads as 1 when the file says none, then <c>setsar=1</c>), even
/// sides, as a baseline JPEG at quality 2 (<c>mjpeg</c>, <c>-f image2 -update 1</c>: one image, whatever the output's name).
/// No audio, subtitles, data or metadata. <c>-n</c>: ffmpeg never writes over a file (the output is a temporary name of
/// our own that isn't there yet). Paths go as <c>file:</c> URLs, as the clip commands', so a name is never read as a
/// protocol or an option.</para>
/// <para>The clip is what Bookmarks wrote: an Accurate clip is SDR H.264 already (HDR tone-mapped when it was made); a Fast
/// clip copies its video's stream, so an HDR source's frame is converted without tone mapping and may look pale.</para>
/// </remarks>
internal static class FfmpegFrameCommandBuilder
{
    /// <summary>
    /// The video filter: square pixels and even sides.
    /// </summary>
    public const string VideoFilter = "scale=trunc(iw*sar/2)*2:trunc(ih/2)*2,setsar=1";

    /// <summary>
    /// Builds the argument list.
    /// </summary>
    /// <param name="clipPath">The clip file's full path.</param>
    /// <param name="outputPath">The JPEG's full path: a name that isn't taken.</param>
    /// <returns>The ffmpeg arguments, excluding the executable.</returns>
    public static IReadOnlyList<string> Build(string clipPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(clipPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        return
        [
            "-hide_banner",
            "-nostdin",
            "-n",
            "-loglevel",
            "error",
            "-nostats",
            "-progress",
            "pipe:1",
            "-i",
            "file:" + clipPath,
            "-map",
            "0:v:0",
            "-frames:v",
            "1",
            "-an",
            "-sn",
            "-dn",
            "-map_metadata",
            "-1",
            "-vf",
            VideoFilter,
            "-c:v",
            "mjpeg",
            "-q:v",
            "2",
            "-f",
            "image2",
            "-update",
            "1",
            "file:" + outputPath
        ];
    }
}
