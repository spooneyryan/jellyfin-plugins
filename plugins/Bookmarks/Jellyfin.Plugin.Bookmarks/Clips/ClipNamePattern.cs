using System.IO;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The names Bookmarks gives clip files: exactly what <see cref="ClipFileNaming.BuildFileName"/> and
/// <see cref="ClipFileNaming.MakeUnique"/> produce, <c>&lt;stem&gt; [hh.mm.ss-hh.mm.ss]</c>, then optionally
/// <c> (2)</c> (any number) or <c> (&lt;32 hex digits&gt;)</c>, then <c>.mp4</c> or <c>.mkv</c>, and, while the clip is
/// being written, <c>.partial</c>. The <c>.partial</c> janitor deletes only files whose names match it, so a clip
/// folder pointed at a library, or another tool's <c>*.partial</c> files, are never touched; the untracked-file scan
/// planned for 1.3.0 is to use it too.
/// </summary>
/// <remarks>
/// The expression, made exact: ASCII digits only (<c>[0-9]</c>, where .NET's <c>\d</c> would take any Unicode digit),
/// the end of the name with <c>\z</c> (where <c>$</c> also matches before a final line feed, which a file name can hold on
/// Linux), and case-sensitive, as Bookmarks writes the extensions and the fallback's hex digits in lower case.
/// </remarks>
internal static partial class ClipNamePattern
{
    /// <summary>
    /// The expression.
    /// </summary>
    public const string Expression =
        @"^(?<stem>.+) \[(?<start>[0-9]{2,}\.[0-9]{2}\.[0-9]{2})-(?<end>[0-9]{2,}\.[0-9]{2}\.[0-9]{2})\](?: \((?:[0-9]+|[0-9a-f]{32})\))?\.(?<ext>mp4|mkv)(?<partial>\.partial)?\z";

    /// <summary>
    /// Whether a file name is a finished clip's name Bookmarks gives (no <c>.partial</c>).
    /// </summary>
    /// <param name="fileName">The file name, without its folder.</param>
    /// <returns>Whether it matches.</returns>
    public static bool IsClipFileName(string? fileName) => Match(fileName) is { Success: true } match && !match.Groups["partial"].Success;

    /// <summary>
    /// Whether a file name is the name of a clip being written: a clip file's name Bookmarks gives, with <c>.partial</c>.
    /// </summary>
    /// <param name="fileName">The file name, without its folder.</param>
    /// <returns>Whether it matches.</returns>
    public static bool IsPartialFileName(string? fileName) => Match(fileName) is { Success: true } match && match.Groups["partial"].Success;

    /// <summary>
    /// Gets the stem of a finished clip's name: the part before its range, which <see cref="ClipFileNaming.BuildStem"/>
    /// gives (the My Clips list's "Name changed" badge).
    /// </summary>
    /// <param name="fileName">The file name, without its folder.</param>
    /// <returns>The stem, or <c>null</c> for a name Bookmarks doesn't give a finished clip.</returns>
    public static string? Stem(string? fileName)
        => Match(fileName) is { Success: true } match && !match.Groups["partial"].Success ? match.Groups["stem"].Value : null;

    /// <summary>
    /// Gets the range a finished clip's name says, as written in it (<c>01.52.10</c>, <c>01.55.00</c>): what a rename of its
    /// file keeps, so the new name says the range the file holds, whatever the record's ticks say.
    /// </summary>
    /// <param name="fileName">The file name, without its folder.</param>
    /// <returns>The start and end as written, or <c>null</c> for a name Bookmarks doesn't give a finished clip.</returns>
    public static (string Start, string End)? Range(string? fileName)
        => Match(fileName) is { Success: true } match && !match.Groups["partial"].Success ? (match.Groups["start"].Value, match.Groups["end"].Value) : null;

    private static Match? Match(string? fileName)
    {
        // A name only: a path's folders could match the stem.
        if (string.IsNullOrEmpty(fileName) || fileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return null;
        }

        return NameRegex().Match(fileName);
    }

    [GeneratedRegex(Expression, RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
