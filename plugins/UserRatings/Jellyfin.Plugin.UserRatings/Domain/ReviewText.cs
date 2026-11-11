using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.UserRatings.Domain;

/// <summary>
/// A review's text: plain text, normalised before it is stored or measured. Trimmed; <c>\r\n</c> and
/// <c>\r</c> become <c>\n</c>; control characters other than <c>\n</c> and <c>\t</c> are removed (NUL included, which
/// SQLite's <c>length()</c> stops at); a lone surrogate becomes U+FFFD (what SQLite's UTF-8 would store
/// anyway, so the stored text is the text checked); more than 3 blank lines in a row collapse to 2; empty is no review.
/// The length is in UTF-16 code units, as the web client's <c>maxlength</c> and counter count it.
/// </summary>
/// <remarks>
/// <para><b>Format characters</b> (Unicode category Cf: U+200B zero width space, U+200D zero width joiner, U+2060 word joiner,
/// U+FEFF, the bidi marks, embeddings, overrides and isolates, the tag characters). A review of
/// nothing but those and white space reads as empty, so it is none: the 400 "needs some text", never a blank review that
/// counts in <c>ReviewCount</c>. In a review with text they are kept as written: joiners build emoji and some scripts'
/// letters, and the bidi controls are how a member mixes right-to-left and left-to-right words.</para>
/// <para><b>Bidi controls are kept, so the page isolates each review</b>. An override such as U+202E with no
/// U+202C to end it would otherwise reorder what follows it on the page (the next reviewer's name, the stars). The page renders
/// every review text, and every user name, in its own element with <c>dir="auto"</c> and <c>unicode-bidi: isolate</c> (a
/// <c>&lt;bdi&gt;</c> or its CSS), which ends every embedding, override and isolate at the element's end.</para>
/// <para><b>Names are another rule</b> (since 0.3.19): a name an import file brings (a former member's,
/// a reason's value) drops the bidi controls and U+FEFF (<see cref="IsBidiControlOrBom"/>), keeping the joiners, since a
/// name is a label shown in lists, logs and reasons, where an override has no use; a review keeps them as written.</para>
/// </remarks>
internal static class ReviewText
{
    /// <summary>The 400's message for a review that is empty once normalised.</summary>
    public const string EmptyMessage = "A review needs some text.";

    /// <summary>
    /// Tells whether a character is a bidi control or the byte order mark: the embeddings and overrides (U+202A to U+202E),
    /// the isolates (U+2066 to U+2069), the marks (U+200E, U+200F) and U+FEFF. Names drop them
    /// (<c>ImportRowRules.CleanName</c>); a review keeps them (above). Not among them: the joiners U+200D and U+200C, which
    /// emoji and some scripts need, and the other format characters.
    /// </summary>
    /// <param name="c">The character.</param>
    /// <returns>Whether it is one.</returns>
    public static bool IsBidiControlOrBom(char c)
        => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\uFEFF';

    /// <summary>
    /// Normalises a review.
    /// </summary>
    /// <param name="text">The text as sent.</param>
    /// <returns>The normalised text, or <c>null</c> when nothing is left.</returns>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c is '\n' or '\t')
            {
                builder.Append(c);
            }
            else if (char.IsControl(c))
            {
                // Removed: NUL, the other C0 controls, DEL and the C1 controls.
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                builder.Append('�');
            }
            else
            {
                builder.Append(c);
            }
        }

        var collapsed = CollapseBlankLines(builder.ToString()).Trim();
        return HasText(collapsed) ? collapsed : null;
    }

    /// <summary>
    /// Tells whether a text has a character that is neither white space nor a format character (Unicode Cf), counted by code
    /// point, so a tag character outside the BMP is a format character too.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether there is something to read.</returns>
    internal static bool HasText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Normalises a review and checks it against the limit (the <c>MaxReviewLength</c> setting).
    /// </summary>
    /// <param name="text">The text as sent.</param>
    /// <param name="maxLength">The limit, in UTF-16 code units.</param>
    /// <returns>The normalised text.</returns>
    /// <exception cref="PluginException">400: empty once normalised, or longer than the limit.</exception>
    public static string Validate(string? text, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        var normalized = Normalize(text) ?? throw PluginException.Invalid(EmptyMessage);
        if (normalized.Length > maxLength)
        {
            throw PluginException.Invalid(string.Create(
                CultureInfo.InvariantCulture,
                $"A review can be at most {maxLength} characters; this one has {normalized.Length}."));
        }

        return normalized;
    }

    // More than 3 blank (empty or white-space) lines in a row become 2 empty lines; 3 or fewer stay as they are.
    private static string CollapseBlankLines(string text)
    {
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        var blanks = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                blanks.Add(line);
                continue;
            }

            Flush(kept, blanks);
            kept.Add(line);
        }

        Flush(kept, blanks);
        return string.Join('\n', kept);
    }

    private static void Flush(List<string> kept, List<string> blanks)
    {
        if (blanks.Count > 3)
        {
            kept.Add(string.Empty);
            kept.Add(string.Empty);
        }
        else
        {
            kept.AddRange(blanks);
        }

        blanks.Clear();
    }
}
