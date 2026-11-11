using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What an item-linked record remembers about its library item, so that the record can be found again after the item's
/// id changed: every move or rename creates a new id (ids are MD5(type + path)). Records store it next to their
/// <c>ItemId</c>; <see cref="ItemIdentity.Capture"/> builds it and <see cref="IdentityResolver"/> matches it.
/// </summary>
/// <remarks>
/// <para>Stored in the plugin's own store in the kit's JSON format (PascalCase, nulls omitted). Provider-id maps are
/// normalised when set: blank keys and values are dropped, values trimmed, keys matched without regard to case (as
/// Jellyfin's own <c>ProviderIds</c> are) and written in ordinal order. A missing map reads as empty (or null for
/// <see cref="SeriesProviderIds"/>), so a hand-edited or imported record never meets a null map. A blank
/// <see cref="Path"/> is stored as null.</para>
/// <para>Two snapshots are equal when every field is, the maps compared by content; <see cref="Extra"/>, the unknown
/// fields a newer kit may have written, is not compared. <see cref="Path"/> is admin-only: it is never returned in a
/// user's DTO.</para>
/// </remarks>
internal sealed record IdentitySnapshot
{
    private static readonly IReadOnlyDictionary<string, string> _noIds = ImmutableSortedDictionary.Create<string, string>(StringComparer.OrdinalIgnoreCase);

    private readonly IReadOnlyDictionary<string, string> _providerIds = _noIds;
    private readonly IReadOnlyDictionary<string, string>? _seriesProviderIds;
    private readonly string? _path;

    /// <summary>
    /// Gets the item id the snapshot was taken of. <see cref="Guid.Empty"/> for a record that never had a library item
    /// (for example Bookmarks' adopted clips); <c>ILibraryManager.GetItemById(Guid.Empty)</c> throws, so no
    /// kit code looks it up.
    /// </summary>
    public Guid ItemId { get; init; }

    /// <summary>
    /// Gets the item's kind, a <c>BaseItemKind</c> name (<c>Movie</c>, <c>Episode</c>, <c>Series</c>, <c>Audio</c>...).
    /// Empty when unknown; the resolver then skips the provider-id and name steps.
    /// </summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>
    /// Gets the item's name (the title, not the display name with the year).
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets the item's production year.
    /// </summary>
    public int? Year { get; init; }

    /// <summary>
    /// Gets the item's own provider ids (for example <c>Tmdb</c>, <c>Imdb</c>, <c>Tvdb</c>); never null.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderIds
    {
        get => _providerIds;
        init => _providerIds = Normalize(value) ?? _noIds;
    }

    /// <summary>
    /// Gets the provider ids of the series, for episodes and seasons; null for other kinds or when the series was unknown.
    /// Episodes are matched by these plus <see cref="Season"/> and <see cref="Episode"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? SeriesProviderIds
    {
        get => _seriesProviderIds;
        init => _seriesProviderIds = Normalize(value);
    }

    /// <summary>
    /// Gets the series name, for episodes and seasons.
    /// </summary>
    public string? SeriesName { get; init; }

    /// <summary>
    /// Gets the season number: the episode's <c>ParentIndexNumber</c>, or the season's own <c>IndexNumber</c>.
    /// </summary>
    public int? Season { get; init; }

    /// <summary>
    /// Gets the episode number (<c>IndexNumber</c>), for episodes.
    /// </summary>
    public int? Episode { get; init; }

    /// <summary>
    /// Gets the id of the series, for seasons and episodes. Removing a folder raises <c>ItemRemoved</c> for the folder
    /// only, so a record detaches when this id is removed (<see cref="ItemIdentity.IsRemovedBy"/>).
    /// </summary>
    public Guid? SeriesId { get; init; }

    /// <summary>
    /// Gets the id of the season, for episodes (see <see cref="SeriesId"/>).
    /// </summary>
    public Guid? SeasonId { get; init; }

    /// <summary>
    /// Gets the id of the music album, for tracks (see <see cref="SeriesId"/>).
    /// </summary>
    public Guid? AlbumId { get; init; }

    /// <summary>
    /// Gets the item's runtime in ticks, for <see cref="RelinkPolicy"/> validators such as
    /// <see cref="RelinkPolicy.RuntimeWithin"/>.
    /// </summary>
    public long? RunTimeTicks { get; init; }

    /// <summary>
    /// Gets the item's path. Admin-only: never returned in a user's DTO. A blank path (empty or white space) is stored as
    /// null: Jellyfin's <c>FindByPath</c> of a blank path puts no path filter on its query and answers with the newest item
    /// of the whole library.
    /// </summary>
    public string? Path
    {
        get => _path;
        init => _path = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Gets the fields this version doesn't know, kept so that a newer kit's additions survive a round trip.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>
    /// Compares two snapshots field by field, the provider-id maps by content, without <see cref="Extra"/>.
    /// </summary>
    /// <param name="other">The other snapshot.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(IdentitySnapshot? other)
        => other is not null
            && (ReferenceEquals(this, other)
                || (ItemId == other.ItemId
                    && string.Equals(Kind, other.Kind, StringComparison.Ordinal)
                    && string.Equals(Name, other.Name, StringComparison.Ordinal)
                    && Year == other.Year
                    && SameIds(ProviderIds, other.ProviderIds)
                    && SameIds(SeriesProviderIds, other.SeriesProviderIds)
                    && string.Equals(SeriesName, other.SeriesName, StringComparison.Ordinal)
                    && Season == other.Season
                    && Episode == other.Episode
                    && SeriesId == other.SeriesId
                    && SeasonId == other.SeasonId
                    && AlbumId == other.AlbumId
                    && RunTimeTicks == other.RunTimeTicks
                    && string.Equals(Path, other.Path, StringComparison.Ordinal)));

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(ItemId);
        hash.Add(Kind, StringComparer.Ordinal);
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(Year);
        hash.Add(ProviderIds.Count);
        hash.Add(SeriesProviderIds?.Count);
        hash.Add(Season);
        hash.Add(Episode);
        hash.Add(RunTimeTicks);
        hash.Add(Path, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Normalises a provider-id map: blank keys and values dropped, values trimmed, keys case-insensitive and in ordinal
    /// order, the first of two keys that differ only in case kept.
    /// </summary>
    /// <param name="ids">The map, or null.</param>
    /// <returns>The normalised map, or null when <paramref name="ids"/> is null or has no usable entry.</returns>
    internal static IReadOnlyDictionary<string, string>? Normalize(IEnumerable<KeyValuePair<string, string>>? ids)
    {
        if (ids is null)
        {
            return null;
        }

        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ids.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            builder.TryAdd(key.Trim(), value.Trim());
        }

        return builder.Count == 0 ? null : builder.ToImmutable();
    }

    private static bool SameIds(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (a is null || b is null)
        {
            return (a?.Count ?? 0) == 0 && (b?.Count ?? 0) == 0;
        }

        return a.Count == b.Count
            && a.All(e => b.TryGetValue(e.Key, out var other) && string.Equals(e.Value, other, StringComparison.Ordinal));
    }
}
