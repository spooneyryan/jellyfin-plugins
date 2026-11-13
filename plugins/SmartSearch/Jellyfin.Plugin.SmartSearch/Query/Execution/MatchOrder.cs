using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The Residual path's order: the sort keys of each match are captured into its <see cref="MatchRow"/>
/// while the item is loaded, and the rows are sorted by them in memory, <b>as SQL orders them</b> on the Fast path
/// (10.11.5's <c>ApplyOrder</c> and <c>OrderMapper</c>), so a query's order never depends on the path it takes and matches
/// the library views, which sort in SQL: the stored columns compared as SQLite does (text by code point, a missing value
/// first), <c>SortName</c> then <c>Name</c> when it is the first key, the stored year, <c>InheritedParentalRatingValue</c>
/// for <c>OfficialRating</c>, and the user's row for <c>PlayCount</c> and <c>DatePlayed</c> (no row is less than a play count
/// of 0). Core's in-memory comparers (<c>ILibraryManager.Sort</c>) differ for the sort name (they ignore case), a missing
/// year and the rating, and are not used. The plugin keys have no SQL order: a missing value first. Ties, which SQL leaves
/// undefined, break on <c>SortName</c> then the id, so a result is the same on every run (the auto-collections depend on
/// this).
/// </summary>
internal sealed class MatchOrder : IComparer<MatchRow>
{
    private static readonly Dictionary<string, SortKind> Kinds = new(StringComparer.Ordinal)
    {
        ["SortName"] = SortKind.SortName,
        ["ProductionYear"] = SortKind.ProductionYear,
        ["PremiereDate"] = SortKind.PremiereDate,
        ["DateCreated"] = SortKind.DateCreated,
        ["CommunityRating"] = SortKind.CommunityRating,
        ["CriticRating"] = SortKind.CriticRating,
        ["Runtime"] = SortKind.Runtime,
        ["OfficialRating"] = SortKind.OfficialRating,
        ["VideoBitRate"] = SortKind.VideoBitRate,
        ["DatePlayed"] = SortKind.DatePlayed,
        ["PlayCount"] = SortKind.PlayCount,
        ["Random"] = SortKind.Random,
        ["FileSize"] = SortKind.FileSize,
        ["Resolution"] = SortKind.Resolution,
        ["SeriesName"] = SortKind.SeriesName
    };

    private readonly (SortKind Kind, bool Descending)[] _keys;
    private readonly Guid? _userId;
    private readonly Random _random;

    private MatchOrder((SortKind Kind, bool Descending)[] keys, Guid? userId, int seed)
    {
        _keys = keys;
        _userId = userId;
        _random = new Random(seed);
    }

    private enum SortKind
    {
        SortName,
        ProductionYear,
        PremiereDate,
        DateCreated,
        CommunityRating,
        CriticRating,
        Runtime,
        OfficialRating,
        VideoBitRate,
        DatePlayed,
        PlayCount,
        Random,
        FileSize,
        Resolution,
        SeriesName,

        // Not a key of its own: SQL's second key after a first SortName (ApplyOrder's ThenBy(Name)).
        Name
    }

    /// <summary>Gets a value indicating whether a key reads the user's data (<c>DatePlayed</c>, <c>PlayCount</c>).</summary>
    public bool NeedsUserData => _keys.Any(k => k.Kind is SortKind.DatePlayed or SortKind.PlayCount);

    /// <summary>Creates the order of a query: its sort keys, or <c>SortName</c> ascending without any.</summary>
    /// <param name="sort">The query's sort keys (validated).</param>
    /// <param name="catalog">The field catalog (the keys' canonical names).</param>
    /// <param name="userId">The user whose rows <c>PlayCount</c> and <c>DatePlayed</c> read, or <c>null</c>.</param>
    /// <param name="seed">The seed of <c>Random</c>'s keys.</param>
    /// <returns>The order.</returns>
    /// <exception cref="InvalidOperationException">A key isn't known (the query wasn't validated).</exception>
    public static MatchOrder For(IReadOnlyList<SmartSearchQuerySort>? sort, IFieldCatalog catalog, Guid? userId, int seed)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var keys = (sort ?? []).Select(s =>
        {
            var name = catalog.FindSortKey(s.Key?.Trim() ?? string.Empty) ?? throw new InvalidOperationException("The sort key " + s.Key + " isn't known; queries are validated before they run.");
            return (Kind: Kinds.TryGetValue(name, out var kind) ? kind : throw new InvalidOperationException("No comparer for the sort key " + name + "."), Descending: s.Order == SortOrder.Descending);
        }).ToList();
        if (keys.Count == 0)
        {
            keys.Add((SortKind.SortName, false));
        }

        // ApplyOrder: a first SortName key is followed by Name, in the same direction.
        if (keys[0].Kind == SortKind.SortName)
        {
            keys.Insert(1, (SortKind.Name, keys[0].Descending));
        }

        return new MatchOrder([.. keys], userId, seed);
    }

    /// <summary>
    /// Compares two texts as SQLite's default (BINARY) collation does: by their UTF-8 bytes, which is their code points'
    /// order (UTF-16's ordinal order differs above U+D7FF); a missing text first.
    /// </summary>
    /// <param name="x">One text.</param>
    /// <param name="y">The other.</param>
    /// <returns>The order.</returns>
    internal static int CompareText(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }

        var a = x.EnumerateRunes();
        var b = y.EnumerateRunes();
        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA || !hasB)
            {
                return hasA ? 1 : (hasB ? -1 : 0);
            }

            var c = a.Current.Value.CompareTo(b.Current.Value);
            if (c != 0)
            {
                return c;
            }
        }
    }

    /// <summary>Captures a match's row: its id, version-set links, presentation key, sort name and sort keys.</summary>
    /// <param name="item">The item, loaded.</param>
    /// <param name="userData">The user's data for it, or <c>null</c>.</param>
    /// <returns>The row.</returns>
    public MatchRow Capture(BaseItem item, UserItemData? userData)
    {
        ArgumentNullException.ThrowIfNull(item);
        var values = new object?[_keys.Length];
        for (var i = 0; i < _keys.Length; i++)
        {
            values[i] = Value(_keys[i].Kind, item, userData);
        }

        return new MatchRow(item.Id, item.SortName, item.PresentationUniqueKey, IsVersioned(item) ? VersionMember.From(item) : null, values);
    }

    /// <inheritdoc />
    public int Compare(MatchRow? x, MatchRow? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        for (var i = 0; i < _keys.Length; i++)
        {
            var c = CompareValues(_keys[i].Kind, x.Keys[i], y.Keys[i]);
            if (c != 0)
            {
                return _keys[i].Descending ? -c : c;
            }
        }

        var byName = CompareText(x.SortName, y.SortName);
        return byName != 0 ? byName : x.Id.CompareTo(y.Id);
    }

    // A member of a version set: a secondary (its PrimaryVersionId) or a primary (its linked versions).
    internal static bool IsVersioned(BaseItem item)
        => item is Video video && (KitItemQueries.PrimaryVersionText(video) is not null || video.LinkedAlternateVersions.Length > 0);

    private static int CompareValues(SortKind kind, object? x, object? y) => kind switch
    {
        SortKind.SortName or SortKind.Name or SortKind.SeriesName => CompareText((string?)x, (string?)y),
        SortKind.CommunityRating or SortKind.CriticRating => ((double)x!).CompareTo((double)y!),
        _ => ((long)x!).CompareTo((long)y!)
    };

    // OrderMapper: the premiere date, else January 1st of the production year (DateTime.MinValue.AddYears(year - 1)),
    // else NULL, which SQLite puts first.
    private static long PremiereTicks(BaseItem item)
    {
        if (item.PremiereDate is { } premiere)
        {
            return premiere.Ticks;
        }

        if (item.ProductionYear is { } year && year is >= 1 and <= 9999)
        {
            return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        }

        return long.MinValue;
    }

    // The user's row of the item, as SQL's UserData.FirstOrDefault(UserId) finds one: none without a user or a row.
    private bool HasUserRow(BaseItem item)
        => _userId is { } userId && item.UserData?.Any(u => u.UserId.Equals(userId)) == true;

    // Each key as OrderMapper (10.11.5) reads it; NULL, which SQLite puts first, as the type's minimum.
    private object? Value(SortKind kind, BaseItem item, UserItemData? data) => kind switch
    {
        SortKind.SortName => item.SortName,
        SortKind.Name => item.Name,
        SortKind.ProductionYear => item.ProductionYear is { } year ? year : long.MinValue,
        SortKind.PremiereDate => PremiereTicks(item),
        SortKind.DateCreated => item.DateCreated.Ticks,
        // REAL columns: the float widened to a double.
        SortKind.CommunityRating => item.CommunityRating is { } community ? (double)community : double.NegativeInfinity,
        SortKind.CriticRating => item.CriticRating is { } critic ? (double)critic : double.NegativeInfinity,
        SortKind.Runtime => item.RunTimeTicks ?? long.MinValue,
        // The rating's score as the library stored it, the parents' when the item has none.
        SortKind.OfficialRating => item.InheritedParentalRatingValue is { } rating ? rating : long.MinValue,
        SortKind.VideoBitRate => item.TotalBitrate is { } bitrate ? (long)bitrate : long.MinValue,
        // The user's row: its date or play count; no row, or no date, is NULL.
        SortKind.DatePlayed => data?.LastPlayedDate is { } played && HasUserRow(item) ? played.Ticks : long.MinValue,
        SortKind.PlayCount => data is not null && HasUserRow(item) ? data.PlayCount : long.MinValue,
        // RandomComparer orders by fresh GUIDs on every comparison; a key per match keeps the sort consistent.
        SortKind.Random => _random.NextInt64(),
        // The plugin keys: missing first.
        SortKind.FileSize => item.Size ?? long.MinValue,
        SortKind.Resolution => (long)item.Width,
        SortKind.SeriesName => item is Episode episode ? episode.SeriesName : item is Series ? item.Name : null,
        _ => throw new InvalidOperationException("No comparer for " + kind + ".")
    };
}
