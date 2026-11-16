using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The names of a clip's artwork files (since 1.3.0 / 2.1.0, <see cref="ClipArtwork"/>): beside a clip file
/// <c>&lt;clip&gt;.mp4</c> (or <c>.mkv</c>), <c>&lt;clip&gt;-poster.&lt;ext&gt;</c>, <c>&lt;clip&gt;-fanart.&lt;ext&gt;</c> and
/// <c>&lt;clip&gt;-thumb.&lt;ext&gt;</c>, with <c>&lt;ext&gt;</c> one of <c>.jpg</c>, <c>.jpeg</c>, <c>.png</c> and
/// <c>.webp</c>, the names Jellyfin's local image provider takes for a video in a folder of many videos (Primary, Backdrop,
/// Thumb), and, while one is written, that name with <c>.&lt;32 hex digits&gt;.tmp</c> after it.
/// </summary>
/// <remarks>
/// A name is ours only when <c>&lt;clip&gt;</c> is a name Bookmarks gives a clip (<see cref="ClipNamePattern"/>: it ends with
/// the range <c>[hh.mm.ss-hh.mm.ss]</c>, then optionally <c> (2)</c> or <c> (&lt;32 hex digits&gt;)</c>), so a user's own
/// <c>Holiday-poster.jpg</c> is never one. Case-sensitive, as Bookmarks writes the names in lower case.
/// </remarks>
internal static partial class ClipArtworkNames
{
    /// <summary>The Primary image's kind: <c>-poster</c>.</summary>
    public const string Poster = "poster";

    /// <summary>The Backdrop image's kind: <c>-fanart</c>.</summary>
    public const string Fanart = "fanart";

    /// <summary>The Thumb image's kind: <c>-thumb</c>.</summary>
    public const string Thumb = "thumb";

    /// <summary>
    /// The expression of an artwork file's name, before its clip part is checked against <see cref="ClipNamePattern"/>.
    /// </summary>
    public const string Expression = @"^(?<clip>.+)-(?<kind>poster|fanart|thumb)(?<ext>\.(?:jpg|jpeg|png|webp))(?<temp>\.[0-9a-f]{32}\.tmp)?\z";

    /// <summary>
    /// Gets the kinds, in the order they are written.
    /// </summary>
    public static IReadOnlyList<string> Kinds { get; } = [Poster, Fanart, Thumb];

    /// <summary>
    /// Gets the extensions an artwork file may have (Jellyfin's image formats that Bookmarks copies).
    /// </summary>
    public static IReadOnlyList<string> Extensions { get; } = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>
    /// Gets the clip part of a clip file's name that its artwork is named after: the name without <c>.mp4</c> or
    /// <c>.mkv</c>, for a name Bookmarks gives a finished clip only.
    /// </summary>
    /// <param name="clipFileName">The clip file's name, without its folder.</param>
    /// <returns>The name without its extension, or <c>null</c>.</returns>
    public static string? ClipStem(string? clipFileName)
        => ClipNamePattern.IsClipFileName(clipFileName) ? Path.GetFileNameWithoutExtension(clipFileName) : null;

    /// <summary>
    /// Gets the extension an artwork file copied from an image gets: the image's own, in lower case, when it is one of
    /// <see cref="Extensions"/>.
    /// </summary>
    /// <param name="imagePath">The image's path.</param>
    /// <returns>The extension, or <c>null</c> for a format Bookmarks doesn't copy (<c>.gif</c>, <c>.svg</c>, <c>.tbn</c>).</returns>
    public static string? ExtensionFor(string? imagePath)
    {
        var extension = Path.GetExtension(imagePath)?.ToLowerInvariant();
        return extension is not null && ((IList<string>)Extensions).Contains(extension) ? extension : null;
    }

    /// <summary>
    /// Builds an artwork file's name.
    /// </summary>
    /// <param name="clipStem">The clip part (<see cref="ClipStem"/>).</param>
    /// <param name="kind">One of <see cref="Kinds"/>.</param>
    /// <param name="extension">One of <see cref="Extensions"/>.</param>
    /// <returns>The file name.</returns>
    public static string FileName(string clipStem, string kind, string extension) => clipStem + "-" + kind + extension;

    /// <summary>
    /// Reads an artwork file's name.
    /// </summary>
    /// <param name="fileName">The file name, without its folder.</param>
    /// <returns>What it says, or <c>null</c> for a name that isn't a Bookmarks clip's artwork.</returns>
    public static ClipArtworkName? Parse(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return null;
        }

        var match = NameRegex().Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        var clip = match.Groups["clip"].Value;

        // The clip part must be a name Bookmarks gives a clip, as its file would be named.
        if (!ClipNamePattern.IsClipFileName(clip + ".mp4"))
        {
            return null;
        }

        return new ClipArtworkName(clip, match.Groups["kind"].Value, match.Groups["ext"].Value, match.Groups["temp"].Success);
    }

    [GeneratedRegex(Expression, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
