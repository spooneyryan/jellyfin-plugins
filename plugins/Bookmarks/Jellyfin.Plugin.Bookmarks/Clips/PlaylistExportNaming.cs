using System;
using System.Globalization;
using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The names of a playlist export's files (since 1.5.0 / 2.3.0).
/// </summary>
/// <remarks>
/// <para><b>The export</b> is <c>&lt;playlist name&gt;.mp4</c>, the name cleaned by the clip names' rules
/// (<see cref="ClipFileNaming.Sanitize"/>: Windows' invalid characters replaced, <c>:</c> as <c> -</c>, at most 80
/// characters, never a reserved device name, no trailing dot or space; "Playlist" when nothing is left), with square
/// brackets as round ones, so that it is never a name Bookmarks gives a clip (<see cref="ClipNamePattern"/>, which ends with
/// <c>[hh.mm.ss-hh.mm.ss]</c>): the clip maintenance (the <c>.partial</c> janitor, the clip artwork's clean-up, the
/// renames) acts only on such names and records, so it never touches an export or its poster. Made unique with
/// <c> (2)</c>, <c> (3)</c>... as clips are.</para>
/// <para><b>While it is written</b> it is <c>&lt;name&gt; [00.00.00-&lt;length&gt;] (&lt;32 hex digits&gt;).mp4.partial</c>
/// beside it: a name the janitor takes for a clip being written, so one a crash or a restart left is deleted at the next
/// start like a clip's, and no clip or other export ever takes it.</para>
/// <para><b>Its poster</b> is <c>&lt;name&gt;-poster.&lt;ext&gt;</c>, the name Jellyfin's local image provider takes for a
/// video's Primary image in a folder of videos (as clip artwork, <see cref="ClipArtworkNames"/>); not a clip's artwork name, so
/// the clip artwork's daily clean-up never takes it for an orphan.</para>
/// </remarks>
internal static class PlaylistExportNaming
{
    /// <summary>The name when a playlist's name has no usable characters.</summary>
    public const string Fallback = "Playlist";

    /// <summary>
    /// Builds an export's file name from its playlist's name.
    /// </summary>
    /// <param name="playlistName">The playlist's name.</param>
    /// <returns>The file name, such as <c>Road trip.mp4</c>.</returns>
    public static string BuildFileName(string? playlistName)
    {
        var cleaned = (playlistName ?? string.Empty).Replace('[', '(').Replace(']', ')');
        return ClipFileNaming.BuildFolderName(cleaned, Fallback) + ".mp4";
    }

    /// <summary>
    /// Builds the temporary file's path an export is written to, beside its file.
    /// </summary>
    /// <param name="outputPath">The export's path.</param>
    /// <param name="duration">Its length.</param>
    /// <param name="jobId">The export's id, which makes the name its own.</param>
    /// <returns>The path.</returns>
    public static string BuildTempPath(string outputPath, TimeSpan duration, Guid jobId)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        var folder = Path.GetDirectoryName(outputPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{stem} [{ClipFileNaming.FormatTimestamp(0)}-{ClipFileNaming.FormatTimestamp(duration.Ticks)}] ({jobId:N}).mp4{ClipFileGuard.PartialExtension}");
        return Path.Combine(folder, name);
    }

    /// <summary>
    /// Builds the poster's path for an export.
    /// </summary>
    /// <param name="outputPath">The export's path.</param>
    /// <param name="extension">The image's extension (<see cref="ClipArtworkNames.ExtensionFor"/>).</param>
    /// <returns>The path.</returns>
    public static string BuildPosterPath(string outputPath, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        var folder = Path.GetDirectoryName(outputPath) ?? string.Empty;
        return Path.Combine(folder, ClipArtworkNames.FileName(Path.GetFileNameWithoutExtension(outputPath), ClipArtworkNames.Poster, extension));
    }
}
