using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;

/// <summary>
/// The title rules (N1 to N10, DEVELOPMENT.md's table) that changed a title, and the normaliser's guards.
/// </summary>
[Flags]
internal enum TitleRules
{
    /// <summary>No rule changed the title.</summary>
    None = 0,

    /// <summary>N1: trimmed, diacritics of Latin letters dropped, letters folded, lower case.</summary>
    N1 = 1,

    /// <summary>N2: a path tag removed (<c>[tmdbid-…]</c>, <c>{edition-…}</c>, …).</summary>
    N2 = 1 << 1,

    /// <summary>N3: a trailing bracketed year removed.</summary>
    N3 = 1 << 2,

    /// <summary>N4: an edition suffix removed.</summary>
    N4 = 1 << 3,

    /// <summary>N5: a dotted acronym collapsed.</summary>
    N5 = 1 << 4,

    /// <summary>N6: <c>&amp;</c>, apostrophes, punctuation or white space changed.</summary>
    N6 = 1 << 5,

    /// <summary>N7: an English article dropped (leading, or the trailing ", The").</summary>
    N7 = 1 << 6,

    /// <summary>N7b: another language's article dropped.</summary>
    N7b = 1 << 7,

    /// <summary>N8: a roman numeral turned into digits.</summary>
    N8 = 1 << 8,

    /// <summary>N9: the spaces removed.</summary>
    N9 = 1 << 9,

    /// <summary>N10: a release name cut at its first release token, or its year removed.</summary>
    N10 = 1 << 10,

    /// <summary>The title was longer than <see cref="TitleNormalizer.MaxTitleLength"/> and was cut there first.</summary>
    Truncated = 1 << 11,

    /// <summary>A rule's regular expression timed out; the key is the N1 form with its white space collapsed.</summary>
    Fallback = 1 << 12,
}

/// <summary>
/// A normalised title: the comparison key, the edition the title named, and the year it carried.
/// </summary>
/// <param name="Key">The comparison key; empty only for an empty or white-space title (the index skips those).</param>
/// <param name="Edition">The edition, in its comparison form (<c>directors cut</c>), from a <c>{edition-…}</c> tag (N2), a
/// suffix (N4) or a release name (N10); or <c>null</c>.</param>
/// <param name="BracketYear">The year the title carried: a trailing bracketed year (N3) or a release name's year (N10).</param>
/// <param name="AppliedRules">The rules that changed the title.</param>
internal sealed record NormalizedTitle(string Key, string? Edition, int? BracketYear, TitleRules AppliedRules);
