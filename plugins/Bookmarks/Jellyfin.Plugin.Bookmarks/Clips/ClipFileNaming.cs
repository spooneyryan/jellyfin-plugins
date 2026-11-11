using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Produces readable, filesystem-safe names for clip files and folders.
/// </summary>
internal static class ClipFileNaming
{
    private const int MaxNameLength = 80;

    // Characters that are invalid in Windows file names. Used on every platform so clip folders
    // stay usable over SMB shares.
    private static readonly char[] _invalidCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly string[] _reservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    /// <summary>
    /// Gets the file extension for a clip mode.
    /// </summary>
    /// <param name="mode">The clip mode.</param>
    /// <returns><c>.mp4</c> for accurate clips, <c>.mkv</c> for fast ones.</returns>
    public static string GetExtension(BookmarkClipMode mode) => mode == BookmarkClipMode.Fast ? ".mkv" : ".mp4";

    /// <summary>
    /// Gets the clip mode a clip file was made with, from its extension: the reverse of <see cref="GetExtension"/>, for a
    /// clip that outlived its job and is known only by its file (a clip-only tombstone; an adopted file).
    /// </summary>
    /// <param name="path">The clip file's path or name.</param>
    /// <returns><see cref="BookmarkClipMode.Fast"/> for <c>.mkv</c> in any case, otherwise <see cref="BookmarkClipMode.Accurate"/>.</returns>
    public static BookmarkClipMode GetMode(string? path)
        => string.Equals(Path.GetExtension(path), ".mkv", StringComparison.OrdinalIgnoreCase) ? BookmarkClipMode.Fast : BookmarkClipMode.Accurate;

    /// <summary>
    /// Builds a clip file name such as <c>Heat (1995) - Bank job [01.52.10-01.55.00].mp4</c>.
    /// </summary>
    /// <param name="itemName">The item's display name.</param>
    /// <param name="note">The bookmark's name, if any (<c>BookmarkNames.ClipFileLabel</c>: never its note since 1.2.0).</param>
    /// <param name="startTicks">The clip start.</param>
    /// <param name="endTicks">The clip end.</param>
    /// <param name="mode">The clip mode, which picks the extension.</param>
    /// <returns>The file name.</returns>
    public static string BuildFileName(string itemName, string? note, long startTicks, long endTicks, BookmarkClipMode mode)
    {
        var name = BuildStem(itemName, note);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name} [{FormatTimestamp(startTicks)}-{FormatTimestamp(endTicks)}]{GetExtension(mode)}");
    }

    /// <summary>
    /// Builds the part of a clip file name before its range, such as <c>Heat (1995) - Bank job</c>: what
    /// <see cref="BuildFileName"/> starts with, and what <see cref="ClipNamePattern.Stem"/> reads back from a file name. The My
    /// Clips list compares the two for its "Name changed" badge (<c>IsNameOutdated</c>).
    /// </summary>
    /// <param name="itemName">The item's display name.</param>
    /// <param name="note">The bookmark's name, if any (<c>BookmarkNames.ClipFileLabel</c>: never its note since 1.2.0).</param>
    /// <returns>The stem.</returns>
    public static string BuildStem(string itemName, string? note)
    {
        var name = Sanitize(itemName, MaxNameLength);
        if (name.Length == 0)
        {
            name = "Clip";
        }

        var noteText = Sanitize(note, MaxNameLength);
        if (noteText.Length > 0)
        {
            name += " - " + noteText;
        }

        return name;
    }

    /// <summary>
    /// Builds the name a clip file gets when it is renamed to match its record (Rename and Rename file): the
    /// stem <see cref="BuildStem"/> gives now, the range the file's own name says (<see cref="ClipNamePattern.Range"/>; else the
    /// file's range in the record, as <see cref="BuildFileName"/> writes it), and the extension of the file's mode, without the
    /// <c> (2)</c> a taken name had (the rename numbers it again when the name is taken). The same rules as a new clip's name,
    /// so the same safety: Windows' invalid characters replaced, no trailing dots or spaces, each part at most 80 characters, and
    /// never a reserved device name (the range follows the stem).
    /// </summary>
    /// <param name="itemName">The video's display name, as the record stores it.</param>
    /// <param name="label">The record's name (<c>BookmarkNames.ClipFileLabel</c>: never its note since 1.2.0).</param>
    /// <param name="currentFileName">The file's name now.</param>
    /// <param name="fileStartTicks">The file's range start in the record, for a name that doesn't say it.</param>
    /// <param name="fileEndTicks">The file's range end in the record.</param>
    /// <returns>The file name, or <c>null</c> when neither the name nor the record says the file's range.</returns>
    public static string? BuildRenamedFileName(string itemName, string? label, string currentFileName, long? fileStartTicks, long? fileEndTicks)
    {
        string start;
        string end;
        if (ClipNamePattern.Range(currentFileName) is { } range)
        {
            (start, end) = range;
        }
        else if (fileStartTicks is long s && fileEndTicks is long e)
        {
            (start, end) = (FormatTimestamp(s), FormatTimestamp(e));
        }
        else
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{BuildStem(itemName, label)} [{start}-{end}]{GetExtension(GetMode(currentFileName))}");
    }

    /// <summary>
    /// Builds a folder name for a user, falling back when the name has no usable characters.
    /// </summary>
    /// <param name="name">The user name.</param>
    /// <param name="fallback">Used when <paramref name="name"/> sanitizes to nothing.</param>
    /// <returns>The folder name.</returns>
    public static string BuildFolderName(string? name, string fallback)
    {
        var folder = Sanitize(name, MaxNameLength);
        if (folder.Length == 0)
        {
            return fallback;
        }

        return _reservedNames.Contains(folder, StringComparer.OrdinalIgnoreCase) ? folder + "_" : folder;
    }

    /// <summary>
    /// Formats a position as <c>hh.mm.ss</c>, which is safe in file names.
    /// </summary>
    /// <param name="ticks">The position.</param>
    /// <returns>The formatted position.</returns>
    public static string FormatTimestamp(long ticks)
    {
        var time = TimeSpan.FromTicks(Math.Max(0, ticks));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}.{time.Minutes:00}.{time.Seconds:00}");
    }

    /// <summary>
    /// Returns a path in <paramref name="directory"/> that isn't taken, adding <c> (2)</c>, <c> (3)</c>... to the name as needed.
    /// </summary>
    /// <param name="directory">The target directory.</param>
    /// <param name="fileName">The preferred file name.</param>
    /// <param name="isTaken">Whether a candidate path is already in use.</param>
    /// <returns>A free path.</returns>
    public static string MakeUnique(string directory, string fileName, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);

        var path = Path.Combine(directory, fileName);
        if (!isTaken(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{stem} ({i}){extension}"));
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{stem} ({Guid.NewGuid():N}){extension}"));
    }

    /// <summary>
    /// Replaces characters that aren't allowed in file names and tidies whitespace.
    /// </summary>
    /// <remarks>
    /// Since 1.2.0 a lone surrogate (half of a character outside the Basic Multilingual Plane, such as an emoji) is
    /// dropped, and the cut to <paramref name="maxLength"/> never splits a character: it ends before the first character, with
    /// its combining marks, its emoji modifiers or the rest of its emoji sequence, that wouldn't fit whole (UAX #29's grapheme
    /// clusters, as .NET's <see cref="StringInfo"/> reads them). A cut that split a surrogate pair left a file name ffmpeg and
    /// the JSON store write with U+FFFD in its place, so the clip's record named a file that never existed and the next start
    /// deleted ffmpeg's output as a stale <c>.partial</c> (reproduced on Bookmarks 1.1.2).
    /// </remarks>
    /// <param name="value">The raw text.</param>
    /// <param name="maxLength">The maximum length of the result, in UTF-16 code units.</param>
    /// <returns>The sanitized text, possibly empty; never with a lone surrogate.</returns>
    public static string Sanitize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (char.IsSurrogate(character))
            {
                // A pair is one character, kept whole; a lone half is none, and is dropped.
                if (char.IsHighSurrogate(character) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    builder.Append(character).Append(value[i + 1]);
                    lastWasSpace = false;
                    i++;
                }

                continue;
            }

            // "Title: Subtitle" reads better as "Title - Subtitle" than "Title_ Subtitle".
            var replacement = character switch
            {
                ':' => " -",
                _ when char.IsControl(character) || char.IsWhiteSpace(character) => " ",
                _ when Array.IndexOf(_invalidCharacters, character) >= 0 => "_",
                _ => null
            };

            if (replacement is null)
            {
                builder.Append(character);
                lastWasSpace = false;
                continue;
            }

            foreach (var replacementCharacter in replacement)
            {
                if (replacementCharacter == ' ')
                {
                    if (lastWasSpace)
                    {
                        continue;
                    }

                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }

                builder.Append(replacementCharacter);
            }
        }

        var result = builder.ToString().Trim();
        if (result.Length > maxLength)
        {
            result = Cut(result, maxLength);
        }

        // Windows drops trailing dots and spaces from names, which would make the path we record wrong.
        return result.TrimEnd('.', ' ');
    }

    // The longest start of the text, at most maxLength code units, that ends on a character's boundary (a grapheme cluster's):
    // never half a surrogate pair, never a letter without its combining marks. A first cluster longer than the limit (a
    // letter with dozens of marks) is cut at the limit, still never inside a surrogate pair.
    private static string Cut(string text, int maxLength)
    {
        var length = 0;
        while (length < text.Length)
        {
            var next = StringInfo.GetNextTextElementLength(text, length);
            if (length + next > maxLength)
            {
                break;
            }

            length += next;
        }

        if (length == 0)
        {
            length = maxLength > 0 && char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        }

        return text[..length];
    }
}
