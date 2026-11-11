using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;

/// <summary>
/// The options of <see cref="TitleNormalizer"/>: which of the optional rules run, and their word lists. Pure:
/// no Jellyfin type, so the normaliser's tests need nothing of the server.
/// </summary>
internal sealed class NormalizationOptions
{
    /// <summary>
    /// The edition phrases rule N4 cuts by default, matched as a whole segment in their N1 and N6 forms.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultEditionPhrases =
    [
        "director's cut", "extended", "extended cut", "extended edition", "unrated", "unrated cut", "theatrical", "theatrical cut",
        "remastered", "special edition", "ultimate edition", "collector's edition", "anniversary edition", "final cut", "the final cut",
        "redux", "imax", "imax edition", "criterion", "criterion collection", "open matte", "3d", "uncut", "restored"
    ];

    /// <summary>
    /// The leading articles of each language rules N7 (<c>En</c>) and N7b (the others) drop. <c>l</c> is the
    /// elided French and Italian article (<c>L'Avventura</c>).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ArticlesByLanguage = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["En"] = ["the", "a", "an"],
        ["Fr"] = ["le", "la", "les", "l", "un", "une"],
        ["De"] = ["der", "die", "das", "ein", "eine"],
        ["Es"] = ["el", "la", "los", "las"],
        ["It"] = ["il", "lo", "la", "gli", "i", "l"],
        ["Nl"] = ["de", "het", "een"],
    };

    private HashSet<string>? _editionForms;
    private HashSet<string>? _articles;

    /// <summary>Gets the defaults: the 1.0 settings'.</summary>
    public static NormalizationOptions Default { get; } = new();

    /// <summary>Gets a value indicating whether rule N4 cuts edition suffixes. Default on.</summary>
    public bool StripEditionSuffixes { get; init; } = true;

    /// <summary>Gets the edition phrases rule N4 cuts.</summary>
    public IReadOnlyList<string> EditionPhrases { get; init; } = DefaultEditionPhrases;

    /// <summary>
    /// Gets the languages whose leading articles are dropped: <c>En</c> is rule N7 (default), the others rule N7b. An empty
    /// list drops none; an unknown language is ignored.
    /// </summary>
    public IReadOnlyList<string> ArticleLanguages { get; init; } = ["En"];

    /// <summary>Gets a value indicating whether rule N8 turns the standalone roman numerals II to XX into digits. Default off.
    /// It reads any such word as a numeral ("Malcolm X" is "malcolm 10"), which the setting's text must say.</summary>
    public bool RomanNumerals { get; init; }

    /// <summary>Gets a value indicating whether rule N9 removes every space from the key. Default off.</summary>
    public bool IgnoreSpaces { get; init; }

    /// <summary>
    /// Gets a value indicating whether rule N10 cleans up release names. Default off; it applies only to titles the
    /// caller marks as release names (an item with no TMDb or IMDb id whose name is its file or folder name).
    /// </summary>
    public bool ReleaseNameCleanup { get; init; }

    /// <summary>
    /// Gets the last year rule N10 takes as a release name's year (the current year + 1, which the caller knows); 0
    /// takes none. The window keeps "Blade Runner 2049 1080p" whole.
    /// </summary>
    public int ReleaseNameMaxYear { get; init; }

    /// <summary>Gets the edition phrases in the form rule N4 compares (N1 and N6 applied).</summary>
    internal IReadOnlySet<string> EditionForms => _editionForms ??= (EditionPhrases ?? [])
        .Select(TitleNormalizer.ComparisonForm)
        .Where(p => p.Length > 0)
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>Gets the articles rules N7 and N7b drop.</summary>
    internal IReadOnlySet<string> Articles => _articles ??= (ArticleLanguages ?? [])
        .Where(l => l is not null && ArticlesByLanguage.ContainsKey(l))
        .SelectMany(l => ArticlesByLanguage[l])
        .ToHashSet(StringComparer.Ordinal);
}
