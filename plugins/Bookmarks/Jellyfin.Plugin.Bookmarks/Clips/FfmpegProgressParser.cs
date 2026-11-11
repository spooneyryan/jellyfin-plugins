using System;
using System.Globalization;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Parses the <c>key=value</c> lines ffmpeg writes with <c>-progress</c>.
/// </summary>
internal static class FfmpegProgressParser
{
    /// <summary>
    /// Reads the output position from a progress line.
    /// </summary>
    /// <param name="line">One line of progress output.</param>
    /// <param name="outputTime">The position written so far, when the line carries one.</param>
    /// <returns><c>true</c> if the line contained a valid output position.</returns>
    public static bool TryParseOutputTime(ReadOnlySpan<char> line, out TimeSpan outputTime)
    {
        outputTime = TimeSpan.Zero;

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        // Despite its name, out_time_ms is also in microseconds (a long-standing ffmpeg quirk).
        if (key.Equals("out_time_us", StringComparison.Ordinal) || key.Equals("out_time_ms", StringComparison.Ordinal))
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds) && microseconds >= 0)
            {
                outputTime = TimeSpan.FromTicks(microseconds * (TimeSpan.TicksPerMillisecond / 1000));
                return true;
            }

            return false;
        }

        if (key.Equals("out_time", StringComparison.Ordinal))
        {
            // Early in a run ffmpeg can report "N/A" or a small negative time; ignore both.
            return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out outputTime) && outputTime >= TimeSpan.Zero;
        }

        return false;
    }
}
