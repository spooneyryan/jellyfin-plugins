using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Bookmark names and the label clip files and titles use.
/// </summary>
internal static class BookmarkNames
{
    /// <summary>
    /// The longest name, in UTF-16 code units (Capabilities' <c>MaxNameLength</c>).
    /// </summary>
    public const int MaxNameLength = 100;

    /// <summary>
    /// The message of a name that is too long.
    /// </summary>
    public const string TooLongMessage = "Names can be at most 100 characters long.";

    /// <summary>
    /// The message of a name that isn't valid Unicode text (an unpaired surrogate).
    /// </summary>
    public const string InvalidTextMessage = "Names must be valid Unicode text.";

    /// <summary>
    /// Normalises a name: <c>null</c> or white space gives <c>null</c> (unnamed); otherwise the text is put in
    /// Unicode NFC, carriage returns, line feeds and tabs become spaces, other control characters are removed, runs of
    /// spaces collapse to one, and the result is trimmed (empty then means unnamed). Names need not be unique.
    /// </summary>
    /// <param name="name">The name as given.</param>
    /// <returns>The name to store, or <c>null</c>.</returns>
    /// <exception cref="BookmarkException">400: longer than <see cref="MaxNameLength"/>, or not valid Unicode.</exception>
    public static string? NormalizeName(string? name)
    {
        if (!TryNormalizeName(name, out var normalized, out var error))
        {
            throw BookmarkException.Invalid(error);
        }

        return normalized;
    }

    /// <summary>
    /// <see cref="NormalizeName"/> without throwing, for code that must not fail on a stored text (the 1→2 migration).
    /// </summary>
    /// <param name="name">The name as given.</param>
    /// <param name="normalized">The name to store, or <c>null</c> for unnamed.</param>
    /// <param name="error">Why the name can't be used, when the result is <c>false</c>.</param>
    /// <returns>Whether the name can be used.</returns>
    public static bool TryNormalizeName(string? name, out string? normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        error = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        string composed;
        try
        {
            composed = name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // An unpaired surrogate: string.Normalize refuses it.
            error = InvalidTextMessage;
            return false;
        }

        var builder = new StringBuilder(composed.Length);
        foreach (var c in composed)
        {
            var next = c is '\r' or '\n' or '\t' ? ' ' : c;
            if (char.IsControl(next) || (next == ' ' && builder.Length > 0 && builder[^1] == ' '))
            {
                continue;
            }

            builder.Append(next);
        }

        var text = builder.ToString().Trim();
        if (text.Length == 0)
        {
            return true;
        }

        if (text.Length > MaxNameLength)
        {
            error = TooLongMessage;
            return false;
        }

        normalized = text;
        return true;
    }

    /// <summary>
    /// A Jellyfin Enhanced label as a name and note: normalised as a name is (<see cref="NormalizeName"/>),
    /// an unpaired surrogate replaced by U+FFFD rather than refused; blank gives no name; a label longer than
    /// <see cref="MaxNameLength"/> gives its first 100 characters (never half a surrogate pair) as the name, and the whole
    /// label, up to <paramref name="maxNoteLength"/>, as the note.
    /// </summary>
    /// <param name="label">JE's label.</param>
    /// <param name="maxNoteLength">The longest note.</param>
    /// <returns>The name, or <c>null</c>; and the note, or <c>null</c> when the label fits the name.</returns>
    public static (string? Name, string? Note) FromLabel(string? label, int maxNoteLength)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return (null, null);
        }

        var builder = new StringBuilder(label.Length);
        for (var i = 0; i < label.Length; i++)
        {
            var c = label[i];
            if (char.IsHighSurrogate(c) && i + 1 < label.Length && char.IsLowSurrogate(label[i + 1]))
            {
                builder.Append(c).Append(label[++i]);
            }
            else
            {
                builder.Append(char.IsSurrogate(c) ? '�' : c);
            }
        }

        var text = builder.ToString();
        if (TryNormalizeName(text, out var name, out _))
        {
            return (name, null);
        }

        // Too long for a name: the same normalisation without the limit, then cut.
        var full = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormC))
        {
            var next = c is '\r' or '\n' or '\t' ? ' ' : c;
            if (char.IsControl(next) || (next == ' ' && full.Length > 0 && full[^1] == ' '))
            {
                continue;
            }

            full.Append(next);
        }

        var whole = full.ToString().Trim();
        return (Cut(whole, MaxNameLength).TrimEnd(), whole.Length > maxNoteLength ? Cut(whole, maxNoteLength).TrimEnd() : whole);
    }

    // The first characters of a text, never ending in the first half of a surrogate pair.
    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        return text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
    }

    /// <summary>
    /// The text a bookmark's own title shows its owner: its name, else its note, else nothing. Since Bookmarks 1.2.0
    /// no clip file name or clip title metadata takes it (<see cref="ClipFileLabel"/>): those are seen by admins.
    /// </summary>
    /// <param name="bookmark">The bookmark.</param>
    /// <returns>The label, or <c>null</c>.</returns>
    public static string? Label(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return !string.IsNullOrWhiteSpace(bookmark.Name) ? bookmark.Name
            : !string.IsNullOrWhiteSpace(bookmark.Note) ? bookmark.Note
            : null;
    }

    /// <summary>
    /// The text a clip's file name and its title metadata take from its bookmark (as decided for Bookmarks 1.2.0):
    /// the bookmark's name, else nothing, so the clip of a bookmark without a name is named after its video and its range
    /// alone. Never its note: admins see clip file names and paths (the All users scope of My Clips, the clip folder itself),
    /// and download the files, while a note is its owner's alone. Clips made before 1.2.0 for a bookmark
    /// without a name keep the note's words in their file names until Rename file renames them (their list shows "Name
    /// changed").
    /// </summary>
    /// <param name="bookmark">The bookmark.</param>
    /// <returns>The name, or <c>null</c>.</returns>
    public static string? ClipFileLabel(Bookmark bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);
        return string.IsNullOrWhiteSpace(bookmark.Name) ? null : bookmark.Name;
    }
}
