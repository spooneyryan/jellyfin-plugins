using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;

/// <summary>
/// The options of <c>GET /Groups</c> and <c>GET /Groups/{groupId}</c> (1.0's set), parsed: the grouping options
/// (the settings' defaults, overridden by <c>mode</c> and <c>unknownYear</c>), the libraries asked for, and the filters,
/// the order and the page. Names are read ignoring case; anything else is a 400.
/// </summary>
internal sealed record GroupsQuery
{
    /// <summary>The page size when none is asked for.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The largest page.</summary>
    public const int MaxLimit = 200;

    /// <summary>The longest text filter.</summary>
    public const int MaxSearchLength = 200;

    /// <summary>Gets the grouping options.</summary>
    public required GroupingOptions Grouping { get; init; }

    /// <summary>Gets the libraries asked for, or null for every library the caller can see.</summary>
    public IReadOnlyList<Guid>? LibraryIds { get; init; }

    /// <summary>Gets the confidences to show, or null for all.</summary>
    public IReadOnlySet<GroupConfidence>? Confidence { get; init; }

    /// <summary>Gets the text filter on the title, or null.</summary>
    public string? Search { get; init; }

    /// <summary>Gets the order.</summary>
    public GroupSort Sort { get; init; }

    /// <summary>Gets the first group of the page.</summary>
    public int StartIndex { get; init; }

    /// <summary>Gets the page size.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>
    /// Parses the query.
    /// </summary>
    /// <param name="configuration">The settings, for the default mode, unknown-year policy and title rules.</param>
    /// <param name="today">Today, for rule N10's last year.</param>
    /// <param name="mode"><c>TitleAndYear</c> or <c>Title</c>; null for the settings' default.</param>
    /// <param name="unknownYear"><c>MatchUnknownOnly</c> or <c>MatchAny</c>; null for the settings' default.</param>
    /// <param name="libraryIds">Library ids, comma-separated; null or empty for every library.</param>
    /// <param name="confidence">Confidences, comma-separated; null or empty for all.</param>
    /// <param name="search">The text filter.</param>
    /// <param name="sort">The order's name; null for <see cref="GroupSort.Confidence"/>.</param>
    /// <param name="startIndex">The first group, 0 or more.</param>
    /// <param name="limit">The page size, 1 to <see cref="MaxLimit"/>.</param>
    /// <returns>The query.</returns>
    /// <exception cref="PluginException">Kind <see cref="PluginErrorKind.Invalid"/> (400) for a bad option.</exception>
    public static GroupsQuery Parse(
        PluginConfiguration configuration,
        DateTime today,
        string? mode,
        string? unknownYear,
        string? libraryIds,
        string? confidence,
        string? search,
        string? sort,
        int? startIndex,
        int? limit)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = GroupingOptions.FromConfiguration(configuration, today);
        var grouping = defaults with
        {
            Mode = IsAbsent(mode) ? defaults.Mode : Name<MatchMode>("mode", mode!),
            UnknownYear = IsAbsent(unknownYear) ? defaults.UnknownYear : Name<UnknownYearPolicy>("unknownYear", unknownYear!),
        };

        var ids = List(libraryIds).Select(text => Guid.TryParse(text, out var id) && id != Guid.Empty
            ? id
            : throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"libraryIds: \"{Shorten(text)}\" is not a library id."))).Distinct().ToList();
        var confidences = List(confidence).Select(text => Name<GroupConfidence>("confidence", text)).ToHashSet();

        var filter = search?.Trim();
        if (filter is { Length: > MaxSearchLength })
        {
            throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"search is longer than {MaxSearchLength} characters."));
        }

        if (startIndex is < 0)
        {
            throw PluginException.Invalid("startIndex must be 0 or more.");
        }

        if (limit is < 1 or > MaxLimit)
        {
            throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"limit must be 1 to {MaxLimit}."));
        }

        return new GroupsQuery
        {
            Grouping = grouping,
            LibraryIds = ids.Count == 0 ? null : ids,
            Confidence = confidences.Count == 0 ? null : confidences,
            Search = string.IsNullOrEmpty(filter) ? null : filter,
            Sort = IsAbsent(sort) ? GroupSort.Confidence : Name<GroupSort>("sort", sort!),
            StartIndex = startIndex ?? 0,
            Limit = limit ?? DefaultLimit,
        };
    }

    private static bool IsAbsent(string? value) => string.IsNullOrWhiteSpace(value);

    private static IEnumerable<string> List(string? value)
        => (value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static T Name<T>(string option, string value)
        where T : struct, Enum
        => GroupingOptions.TryParseName<T>(value, out var result)
            ? result
            : throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"{option} must be {string.Join(", ", Enum.GetNames<T>())}; \"{Shorten(value)}\" is none of them."));

    // An option is echoed back in its error, at most 40 characters of it.
    private static string Shorten(string value) => value.Length <= 40 ? value : value[..40] + "…";
}
