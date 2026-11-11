using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;

/// <summary>
/// Turns a movie title into the key Duplicates compares (rules N1 to N10 in that order). A pure function with
/// no Jellyfin type: the same title and options always give the same key.
/// </summary>
/// <remarks>
/// Guards: a title is cut at <see cref="MaxTitleLength"/> characters first; every regular expression has a
/// <see cref="MatchTimeoutMilliseconds"/> timeout, and a timeout gives the N1 form with its white space collapsed
/// (<see cref="TitleRules.Fallback"/>) instead of an exception; a rule that strips (N2, N3, N4, N7, N10) never leaves an
/// empty key, and a title of punctuation alone keeps its N1 form. Unbracketed years are never stripped, so
/// "Blade Runner 2049", "Death Race 2000" and "1917" keep them (N10's year window aside).
/// </remarks>
internal static partial class TitleNormalizer
{
    /// <summary>
    /// The rules' version, stored with ignore marks for display and in the grouping options' hash.
    /// </summary>
    /// <remarks>
    /// 1 for 1.0: no key is stored before 1.0 (the ignore marks will be the first), so the pre-1.0 rule changes kept it.
    /// <b>After 1.0, bump it with any change to what <see cref="Normalize(string?, NormalizationOptions?, bool)"/>
    /// returns</b>: a rule, a default option, the edition phrases, N10's window,
    /// and say so in the release notes.
    /// </remarks>
    public const int NormalizerVersion = 1;

    /// <summary>The longest title the rules read; a longer one is cut here first (<see cref="TitleRules.Truncated"/>).</summary>
    public const int MaxTitleLength = 512;

    /// <summary>The timeout of every regular expression the rules use.</summary>
    public const int MatchTimeoutMilliseconds = 100;

    // N1: the Greek tonos (and oxia) after NFKD.
    private const int GreekTonos = 0x0301;

    // N10's release tokens, in their N6 form: "blu-ray" and "web-dl" are two tokens there.
    private static readonly HashSet<string> ReleaseTokens = new(StringComparer.Ordinal)
    {
        "1080p", "720p", "2160p", "480p", "bluray", "bdrip", "brrip", "webrip", "hdtv", "x264", "x265", "h264", "h265", "hevc",
        "xvid", "dvdrip", "remux", "proper", "repack",
    };

    private static readonly (string First, string Second)[] ReleaseTokenPairs = [("blu", "ray"), ("web", "dl")];

    private static readonly HashSet<string> EnglishArticles = new(StringComparer.Ordinal) { "the", "a", "an" };

    // N8: II to XX; never I.
    private static readonly Dictionary<string, string> RomanNumerals = new(StringComparer.Ordinal)
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9",
        ["x"] = "10", ["xi"] = "11", ["xii"] = "12", ["xiii"] = "13", ["xiv"] = "14", ["xv"] = "15", ["xvi"] = "16",
        ["xvii"] = "17", ["xviii"] = "18", ["xix"] = "19", ["xx"] = "20",
    };

    // N4's separators: a hyphen, an en dash or an em dash between spaces, or a colon and a space.
    private static readonly string[] EditionSeparators = [" - ", " – ", " — ", ": "];

    /// <summary>
    /// Normalises a title.
    /// </summary>
    /// <param name="title">The title: a movie's <c>Name</c> (or <c>OriginalTitle</c>); <c>null</c> or white space gives an
    /// empty key.</param>
    /// <param name="options">The options; <c>null</c> takes <see cref="NormalizationOptions.Default"/>.</param>
    /// <param name="isReleaseName">Whether the title is a release name (no TMDb or IMDb id, and the <c>Name</c> is the
    /// file or folder name); only then does N10 run, and only when <see cref="NormalizationOptions.ReleaseNameCleanup"/>
    /// is on.</param>
    /// <returns>The key, the edition, the bracketed year and the rules that changed the title.</returns>
    public static NormalizedTitle Normalize(string? title, NormalizationOptions? options = null, bool isReleaseName = false)
        => Normalize(title, options, isReleaseName, Run);

    /// <summary>
    /// Gets a phrase's comparison form (N1 and N6): how N4 and N10 compare edition phrases, and the form
    /// <see cref="NormalizedTitle.Edition"/> takes.
    /// </summary>
    /// <param name="phrase">The phrase.</param>
    /// <returns>The form; empty for a phrase of punctuation or white space.</returns>
    internal static string ComparisonForm(string? phrase)
        => string.IsNullOrWhiteSpace(phrase) ? string.Empty : Punctuate(Fold(phrase), out _);

    /// <summary>The guards around the rules; the tests pass rules that time out.</summary>
    /// <param name="title">The title.</param>
    /// <param name="options">The options; <c>null</c> takes <see cref="NormalizationOptions.Default"/>.</param>
    /// <param name="isReleaseName">Whether the title is a release name.</param>
    /// <param name="rules">The rules: the trimmed, length-capped title, the options, the release-name flag and the guards'
    /// flags give the result.</param>
    /// <returns>The rules' result, or <see cref="Fallback"/>'s when a rule timed out.</returns>
    internal static NormalizedTitle Normalize(string? title, NormalizationOptions? options, bool isReleaseName, Func<string, NormalizationOptions, bool, TitleRules, NormalizedTitle> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (string.IsNullOrWhiteSpace(title))
        {
            return new NormalizedTitle(string.Empty, null, null, TitleRules.None);
        }

        var applied = TitleRules.None;
        var text = title.Trim();
        if (text.Length > MaxTitleLength)
        {
            // Never between the halves of a surrogate pair.
            var length = char.IsHighSurrogate(text[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
            text = text[..length].TrimEnd();
            applied |= TitleRules.Truncated;
        }

        try
        {
            return rules(text, options ?? NormalizationOptions.Default, isReleaseName, applied);
        }
        catch (RegexMatchTimeoutException)
        {
            return Fallback(text, applied);
        }
    }

    /// <summary>The key when a rule timed out: the N1 form with its white space collapsed.</summary>
    /// <param name="text">The trimmed, length-capped title.</param>
    /// <param name="applied">The guards' flags so far.</param>
    /// <returns>The result, flagged <see cref="TitleRules.Fallback"/>.</returns>
    internal static NormalizedTitle Fallback(string text, TitleRules applied)
        => new(Squeeze(Fold(text)), null, null, applied | TitleRules.N1 | TitleRules.Fallback);

    private static NormalizedTitle Run(string text, NormalizationOptions options, bool isReleaseName, TitleRules applied)
    {
        // N1.
        var folded = Fold(text);
        if (!string.Equals(folded, text, StringComparison.Ordinal))
        {
            applied |= TitleRules.N1;
        }

        var s = Squeeze(folded);
        if (s.Length == 0)
        {
            // Invisible characters alone (U+200B, U+FEFF): no title, as white space is.
            return new NormalizedTitle(string.Empty, null, null, applied);
        }

        string? tagEdition = null;
        var suffixEditions = new List<string>();
        int? year = null;

        // N2: the id tags Jellyfin reads from a path, and {edition-…}.
        var untagged = ProviderTag().Replace(s, " ");
        untagged = EditionTag().Replace(untagged, m =>
        {
            tagEdition ??= NullIfEmpty(ComparisonForm(m.Groups[1].Value));
            return " ";
        });
        untagged = Squeeze(untagged);
        if (untagged.Length > 0 && !string.Equals(untagged, s, StringComparison.Ordinal))
        {
            s = untagged;
            applied |= TitleRules.N2;
        }

        // N3, and again after N4 when the year came before the edition: "Movie (2020) (Extended)".
        var n3 = TryStripBracketYear(ref s, ref year);
        if (options.StripEditionSuffixes)
        {
            // N4, up to three segments: "Movie - Extended (Remastered)".
            for (var i = 0; i < 3 && TryCutEdition(ref s, options.EditionForms, out var edition); i++)
            {
                suffixEditions.Insert(0, edition);
                applied |= TitleRules.N4;
            }

            if (!n3 && suffixEditions.Count > 0)
            {
                n3 = TryStripBracketYear(ref s, ref year);
            }
        }

        if (n3)
        {
            applied |= TitleRules.N3;
        }

        // N5: L.A. = LA, S.W.A.T. = SWAT; a dot that joined the acronym to a word leaves a space.
        var acronyms = DottedAcronym().Replace(s, m =>
        {
            var letters = m.Value.Replace(".", string.Empty, StringComparison.Ordinal);
            var next = m.Index + m.Length;
            return m.Value.EndsWith('.') && next < s.Length && char.IsLetterOrDigit(s[next]) ? letters + " " : letters;
        });
        if (!string.Equals(acronyms, s, StringComparison.Ordinal))
        {
            s = acronyms;
            applied |= TitleRules.N5;
        }

        // N7's trailing form ("Matrix, The") and N7b's elided article ("L'Avventura") need the comma and the apostrophe
        // N6 removes.
        applied |= StripArticleBeforePunctuation(ref s, options);

        // N6.
        var key = Punctuate(s, out var n6);
        if (n6)
        {
            applied |= TitleRules.N6;
        }

        if (key.Length == 0)
        {
            // Punctuation alone ("!!!"): the N1 form.
            return new NormalizedTitle(Squeeze(folded), null, null, applied);
        }

        var tokens = key.Split(' ').ToList();
        List<string>? releaseEditions = null;
        if (options.ReleaseNameCleanup && isReleaseName && CleanReleaseName(tokens, options, ref year, out releaseEditions))
        {
            applied |= TitleRules.N10;
        }

        // N7 and N7b: one leading article, never the last word.
        if (tokens.Count > 1 && options.Articles.Contains(tokens[0]))
        {
            applied |= EnglishArticles.Contains(tokens[0]) ? TitleRules.N7 : TitleRules.N7b;
            tokens.RemoveAt(0);
        }

        // N8.
        if (options.RomanNumerals)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                if (RomanNumerals.TryGetValue(tokens[i], out var digits))
                {
                    tokens[i] = digits;
                    applied |= TitleRules.N8;
                }
            }
        }

        // N9.
        if (options.IgnoreSpaces && tokens.Count > 1)
        {
            applied |= TitleRules.N9;
            key = string.Concat(tokens);
        }
        else
        {
            key = string.Join(' ', tokens);
        }

        var editionText = tagEdition
            ?? (suffixEditions.Count > 0 ? string.Join(' ', suffixEditions) : null)
            ?? (releaseEditions is { Count: > 0 } ? string.Join(' ', releaseEditions) : null);
        return new NormalizedTitle(key, editionText, year, applied);
    }

    private static bool TryStripBracketYear(ref string s, ref int? year)
    {
        var match = BracketYear().Match(s);
        if (!match.Success)
        {
            return false;
        }

        // Never the whole title: "(2020)" stays.
        var rest = s[..match.Index].TrimEnd();
        if (rest.Length == 0)
        {
            return false;
        }

        s = rest;
        year = int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryCutEdition(ref string s, IReadOnlySet<string> editions, out string edition)
    {
        edition = string.Empty;
        if (editions.Count == 0)
        {
            return false;
        }

        // A trailing bracketed segment: "Aliens (Special Edition)".
        var bracket = TrailingBracket().Match(s);
        if (bracket.Success && TryCut(ref s, bracket.Index, bracket.Groups[1].Value, editions, ref edition))
        {
            return true;
        }

        // The segment after the last separator: "Blade Runner - The Final Cut". Never a title with no separator:
        // "The Final Cut" and "Apocalypse Now Redux" stay whole.
        var at = -1;
        var separator = string.Empty;
        foreach (var candidate in EditionSeparators)
        {
            var index = s.LastIndexOf(candidate, StringComparison.Ordinal);
            if (index > at)
            {
                at = index;
                separator = candidate;
            }
        }

        return at > 0 && TryCut(ref s, at, s[(at + separator.Length)..], editions, ref edition);
    }

    private static bool TryCut(ref string s, int at, string segment, IReadOnlySet<string> editions, ref string edition)
    {
        // The whole segment is an edition phrase; "Mission: Impossible - Fallout" stays whole.
        var form = ComparisonForm(segment);
        var rest = s[..at].TrimEnd();
        if (rest.Length == 0 || !editions.Contains(form))
        {
            return false;
        }

        s = rest;
        edition = form;
        return true;
    }

    private static TitleRules StripArticleBeforePunctuation(ref string s, NormalizationOptions options)
    {
        var articles = options.Articles;
        if (articles.Count == 0)
        {
            return TitleRules.None;
        }

        var applied = TitleRules.None;
        var trailing = TrailingArticle().Match(s);
        if (trailing.Success && trailing.Index > 0 && articles.Contains(trailing.Groups[1].Value))
        {
            var rest = s[..trailing.Index].TrimEnd();
            if (rest.Length > 0)
            {
                applied |= EnglishArticles.Contains(trailing.Groups[1].Value) ? TitleRules.N7 : TitleRules.N7b;
                s = rest;
            }
        }

        // The elided French and Italian article: "L'Avventura" = "Avventura".
        if (applied == TitleRules.None && articles.Contains("l") && s.Length > 2 && s[0] == 'l' && IsApostrophe(s[1]))
        {
            var rest = s[2..].TrimStart();
            if (rest.Length > 0 && char.IsLetter(rest[0]))
            {
                applied |= TitleRules.N7b;
                s = rest;
            }
        }

        return applied;
    }

    private static bool CleanReleaseName(List<string> tokens, NormalizationOptions options, ref int? year, out List<string> editions)
    {
        editions = [];
        var changed = false;

        // Cut at the first release token, never the first word: "1080p" alone stays.
        for (var i = 1; i < tokens.Count; i++)
        {
            var pair = i + 1 < tokens.Count && ReleaseTokenPairs.Contains((tokens[i], tokens[i + 1]));
            if (ReleaseTokens.Contains(tokens[i]) || pair)
            {
                tokens.RemoveRange(i, tokens.Count - i);
                changed = true;
                break;
            }
        }

        // Then one trailing year in the window, with the edition phrases after it: "curse of chucky 2013 unrated" =
        // "curse of chucky", 2013. Phrases before the year are the title's ("step up 3d 2010" = "step up 3d", "the final
        // cut 2004" = "the final cut"), and a bracketed year (N3) leaves the tokens alone. The strip never leaves only an
        // article ("the 2012" stays "the 2012"). A single word before the year is taken, edition phrases or not:
        // "alien 1979 extended" = "alien", 1979, Extended; "death race 2000 1080p" = "death race",
        // 2000 is the window's inherent limit (a year in the title looks like a release's year).
        if (year is not null)
        {
            return changed;
        }

        IReadOnlySet<string> forms = options.StripEditionSuffixes ? options.EditionForms : new HashSet<string>();
        var longest = forms.Count == 0 ? 0 : forms.Max(f => f.Count(c => c == ' ') + 1);
        var found = new List<string>();
        var end = tokens.Count;
        var more = true;
        while (more)
        {
            more = false;
            for (var n = Math.Min(longest, end - 1); n >= 1; n--)
            {
                var phrase = string.Join(' ', tokens.Skip(end - n).Take(n));
                if (forms.Contains(phrase))
                {
                    found.Insert(0, phrase);
                    end -= n;
                    more = true;
                    break;
                }
            }
        }

        // The year is the token before the phrases, never the first word.
        var at = end - 1;
        if (at < 1 || !TryReleaseYear(tokens[at], options.ReleaseNameMaxYear, out var taken))
        {
            return changed;
        }

        var onlyAnArticle = at == 1 && (EnglishArticles.Contains(tokens[0]) || options.Articles.Contains(tokens[0]));
        if (onlyAnArticle)
        {
            return changed;
        }

        tokens.RemoveRange(at, tokens.Count - at);
        editions.AddRange(found);
        year = taken;
        return true;
    }

    private static bool TryReleaseYear(string token, int maxYear, out int year)
    {
        year = 0;
        return token.Length == 4
            && token.All(char.IsAsciiDigit)
            && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && year >= 1900
            && year <= maxYear;
    }

    /// <summary>N1: the invisible characters removed (<see cref="IsInvisible"/>); NFKD; the marks on Latin letters and
    /// the Greek tonos dropped (other marks stay, so "й" and "が" do); NFC; lower case; ß, æ, œ, ø, ł, đ, the dotless ı,
    /// the final ς and the Cyrillic ё folded.</summary>
    private static string Fold(string text)
    {
        var decomposed = StripInvisible(Sanitize(text)).Replace('´', '\'').Replace('ʼ', '\'').Normalize(NormalizationForm.FormKD);
        var kept = new StringBuilder(decomposed.Length);
        var latinBase = false;
        var greekBase = false;
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                if (!latinBase && !(greekBase && rune.Value == GreekTonos))
                {
                    kept.Append(rune.ToString());
                }

                continue;
            }

            var letter = Rune.IsLetter(rune);
            latinBase = letter && IsLatin(rune.Value);
            greekBase = letter && IsGreek(rune.Value);
            kept.Append(rune.ToString());
        }

        var lower = kept.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return lower
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("œ", "oe", StringComparison.Ordinal)
            .Replace('ø', 'o')
            .Replace('ł', 'l')
            .Replace('đ', 'd')
            .Replace('ı', 'i')
            .Replace('ς', 'σ')
            .Replace('ё', 'е');
    }

    /// <summary>Removes the characters a title can carry without showing them: format characters (category Cf: the zero
    /// width space and joiners, U+00AD, U+2060, U+FEFF, the direction marks and embeddings), the variation selectors
    /// U+FE00 to U+FE0F and U+E0100 to U+E01EF, the keycap U+20E3, and the Hangul fillers U+115F, U+3164 and U+FFA0
    /// (letters by category, blank on screen). The jungseong filler U+1160 stays: it is part of an old
    /// Hangul syllable written in jamo.</summary>
    private static string StripInvisible(string text)
    {
        StringBuilder? visible = null;
        var at = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsInvisible(rune))
            {
                visible ??= new StringBuilder(text, 0, at, text.Length);
            }
            else
            {
                visible?.Append(rune.ToString());
            }

            at += rune.Utf16SequenceLength;
        }

        return visible?.ToString() ?? text;
    }

    private static bool IsInvisible(Rune rune)
        => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format
            || rune.Value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) or 0x20E3 or 0x115F or 0x3164 or 0xFFA0;

    /// <summary>N6: a standalone &amp; is "and"; apostrophes go; every other character that isn't a letter, a digit or a
    /// mark is a space; white space collapses.</summary>
    private static string Punctuate(string s, out bool changed)
    {
        var result = new StringBuilder(s.Length);
        var onKept = false;
        for (var i = 0; i < s.Length;)
        {
            Rune.DecodeFromUtf16(s.AsSpan(i), out var rune, out var consumed);
            var c = s[i];
            if (c == '&' && (i == 0 || char.IsWhiteSpace(s[i - 1])) && (i + 1 == s.Length || char.IsWhiteSpace(s[i + 1])))
            {
                result.Append("and");
                onKept = false;
            }
            else if (IsApostrophe(c))
            {
                // Removed, with no space: "Schindler's" = "Schindlers".
            }
            else if (Rune.IsLetterOrDigit(rune) || (IsMark(rune) && onKept))
            {
                // A mark stays with the letter or digit it is on; one on a symbol or a space goes ("❤⃝").
                result.Append(s, i, consumed);
                onKept = true;
            }
            else if (!IsMark(rune))
            {
                result.Append(' ');
                onKept = false;
            }

            i += consumed;
        }

        var punctuated = Squeeze(result.ToString());
        changed = !string.Equals(punctuated, s, StringComparison.Ordinal);
        return punctuated;
    }

    private static bool IsMark(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static bool IsApostrophe(char c) => c is '\'' or '’' or '‘' or '´' or '`';

    private static bool IsGreek(int value) => value is (>= 0x0370 and <= 0x03FF) or (>= 0x1F00 and <= 0x1FFF);

    private static bool IsLatin(int value)
        => value is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= 0x00C0 and <= 0x024F) or (>= 0x1E00 and <= 0x1EFF)
            or (>= 0x2C60 and <= 0x2C7F) or (>= 0xA720 and <= 0xA7FF) or (>= 0xAB30 and <= 0xAB6F);

    /// <summary>Replaces lone surrogates, which <see cref="string.Normalize()"/> refuses, with U+FFFD.</summary>
    private static string Sanitize(string text)
    {
        StringBuilder? fixedText = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var lone = (char.IsHighSurrogate(c) && (i + 1 == text.Length || !char.IsLowSurrogate(text[i + 1])))
                || (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(text[i - 1])));
            if (lone)
            {
                fixedText ??= new StringBuilder(text, 0, i, text.Length);
                fixedText.Append('�');
            }
            else
            {
                fixedText?.Append(c);
            }
        }

        return fixedText?.ToString() ?? text;
    }

    /// <summary>Collapses every run of white space to one space, and trims.</summary>
    private static string Squeeze(string s)
    {
        var result = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                space = result.Length > 0;
                continue;
            }

            if (space)
            {
                result.Append(' ');
                space = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    // N2: [tmdbid-603], [imdbid=tt0083658], [tvdbid-…], {tmdb-603}, {imdb-…}; anywhere in the title.
    [GeneratedRegex(@"\s*(?:\[\s*(?:tmdb|imdb|tvdb)(?:id)?\s*[-=][^\[\]]*\]|\{\s*(?:tmdb|imdb|tvdb)(?:id)?\s*[-=][^{}]*\})", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex ProviderTag();

    // N2: {edition-Director's Cut}.
    [GeneratedRegex(@"\s*\{\s*edition\s*[-=]\s*([^{}]*)\}", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex EditionTag();

    // N3: one trailing bracketed year, 1800 to 2099.
    [GeneratedRegex(@"\s*[(\[]\s*((?:18|19|20)[0-9]{2})\s*[)\]]\z", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex BracketYear();

    // N4: a trailing bracketed segment.
    [GeneratedRegex(@"\s*[(\[]([^()\[\]]+)[)\]]\z", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex TrailingBracket();

    // N5: single letters joined by dots, standing alone.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[a-z](?:\.[a-z])+(?![\p{L}\p{N}])\.?", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex DottedAcronym();

    // N7: the trailing article after a comma, with French and Italian's elided form: "Matrix, The", "Avventura, L'".
    [GeneratedRegex(@"\s*,\s*(\p{L}+)['’]?\s*\z", RegexOptions.CultureInvariant, MatchTimeoutMilliseconds)]
    private static partial Regex TrailingArticle();
}
