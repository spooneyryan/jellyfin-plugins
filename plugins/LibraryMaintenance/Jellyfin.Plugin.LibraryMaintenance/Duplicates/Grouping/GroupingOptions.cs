using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

/// <summary>
/// How movies are matched.
/// </summary>
internal enum MatchMode
{
    /// <summary>The same title key and the same year (the default).</summary>
    TitleAndYear = 0,

    /// <summary>The same title key, whatever the years.</summary>
    Title,
}

/// <summary>
/// How a movie with no year groups in <see cref="MatchMode.TitleAndYear"/> mode.
/// </summary>
internal enum UnknownYearPolicy
{
    /// <summary>Only with the other movies of its title that have no year (the default).</summary>
    MatchUnknownOnly = 0,

    /// <summary>With its title's movies of a year when they all have the same year; with several years, only with the
    /// other movies with no year, flagged <see cref="GroupFlags.Ambiguous"/>.</summary>
    MatchAny,
}

/// <summary>
/// The grouper's options (the extra signals come later): the mode, the
/// unknown-year policy and the title rules.
/// </summary>
internal sealed record GroupingOptions
{
    /// <summary>Gets the defaults: Title + Year, <see cref="UnknownYearPolicy.MatchUnknownOnly"/>, the default title rules.</summary>
    public static GroupingOptions Default { get; } = new();

    /// <summary>Gets the match mode.</summary>
    public MatchMode Mode { get; init; } = MatchMode.TitleAndYear;

    /// <summary>Gets the unknown-year policy (Title + Year only).</summary>
    public UnknownYearPolicy UnknownYear { get; init; } = UnknownYearPolicy.MatchUnknownOnly;

    /// <summary>Gets the title rules.</summary>
    public NormalizationOptions Normalization { get; init; } = NormalizationOptions.Default;

    /// <summary>
    /// Gets the options the settings give: the default mode, the unknown-year policy, the edition suffixes and
    /// phrases and the article languages. A mode or a policy is read by its name, ignoring case; anything else (a name this
    /// build doesn't know, which a newer build wrote, a number, a list) takes the default.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="today">Today, for rule N10's last year (this year + 1).</param>
    /// <returns>The options.</returns>
    public static GroupingOptions FromConfiguration(PluginConfiguration configuration, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new GroupingOptions
        {
            Mode = TryParseName<MatchMode>(configuration.DefaultMatchMode, out var mode) ? mode : MatchMode.TitleAndYear,
            UnknownYear = TryParseName<UnknownYearPolicy>(configuration.UnknownYearPolicy, out var policy) ? policy : UnknownYearPolicy.MatchUnknownOnly,
            Normalization = new NormalizationOptions
            {
                StripEditionSuffixes = configuration.StripEditionSuffixes,
                EditionPhrases = configuration.EditionPhrases?.ToArray() ?? [],
                ArticleLanguages = configuration.ArticleLanguages?.ToArray() ?? [],
                ReleaseNameMaxYear = today.Year + 1
            }
        };
    }

    /// <summary>
    /// Gets the options' hash (<c>OptionsHash</c>): the first 16 hex digits, in lower case, of SHA-256 over every
    /// option that changes the groups, and the normaliser's version. The edition phrases and the article languages count
    /// as sets (in their comparison forms); N10's last year counts only with N10 on. It keys the group result cache, and
    /// tells a client which options a result was grouped with.
    /// </summary>
    /// <returns>The hash.</returns>
    public string Hash() => Digest(
        "normalizer=" + TitleNormalizer.NormalizerVersion.ToString(CultureInfo.InvariantCulture),
        "mode=" + Mode.ToString(),
        "unknownYear=" + UnknownYear.ToString(),
        RuleLines());

    /// <summary>
    /// Gets the title rules' hash: the same digest over the normaliser's version and the title rules only, not the mode or
    /// the unknown-year policy. It keys the projection cache, and a change marks the index dirty.
    /// </summary>
    /// <returns>The hash.</returns>
    public string NormalizationHash() => Digest(
        "normalizer=" + TitleNormalizer.NormalizerVersion.ToString(CultureInfo.InvariantCulture),
        RuleLines());

    private static string Digest(params string[] lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))), 0, 8).ToLowerInvariant();

    private string RuleLines()
    {
        var rules = Normalization ?? NormalizationOptions.Default;
        return string.Join(
            '\n',
            "stripEditions=" + (rules.StripEditionSuffixes ? "1" : "0"),
            "editions=" + (rules.StripEditionSuffixes ? string.Join('\u001f', rules.EditionForms.Order(StringComparer.Ordinal)) : string.Empty),
            "articles=" + string.Join(',', rules.Articles.Order(StringComparer.Ordinal)),
            "romanNumerals=" + (rules.RomanNumerals ? "1" : "0"),
            "ignoreSpaces=" + (rules.IgnoreSpaces ? "1" : "0"),
            "releaseNames=" + (rules.ReleaseNameCleanup ? rules.ReleaseNameMaxYear.ToString(CultureInfo.InvariantCulture) : "0"));
    }

    /// <summary>
    /// Reads one of an enum's names, ignoring case and the spaces around it; never a number or a list, which
    /// <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> would take.
    /// </summary>
    /// <typeparam name="T">The enum.</typeparam>
    /// <param name="value">The text.</param>
    /// <param name="result">The value it names.</param>
    /// <returns>Whether it names one.</returns>
    public static bool TryParseName<T>(string? value, out T result)
        where T : struct, Enum
    {
        var name = value?.Trim();
        var match = name is { Length: > 0 } ? Enum.GetNames<T>().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) : null;
        result = match is null ? default : Enum.Parse<T>(match);
        return match is not null;
    }
}
