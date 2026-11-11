using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Builds and compares <see cref="IdentitySnapshot"/>s: the snapshot of a library item, whether a
/// removed item takes a record with it, the provider ids that identify a kind, the lookup keys a consumer indexes its
/// detached records by, and a move-journal line applied to a snapshot.
/// </summary>
internal static class ItemIdentity
{
    private const int MaxParentSteps = 4;

    private static readonly string[] _videoProviders = ["Tmdb", "Imdb", "Tvdb"];
    private static readonly string[] _albumProviders = ["MusicBrainzAlbum", "MusicBrainzReleaseGroup", "AudioDbAlbum"];
    private static readonly string[] _trackProviders = ["MusicBrainzTrack", "MusicBrainzRecording"];
    private static readonly string[] _artistProviders = ["MusicBrainzArtist", "AudioDbArtist"];

    /// <summary>
    /// Takes the snapshot of a library item: id, kind, name, year, all its provider ids, runtime and path; for an episode
    /// or a season also the series' name, provider ids and id, the season and episode numbers and the season's id; for a
    /// track the album's id. The series and album are looked up through <paramref name="library"/>
    /// (<c>GetItemById</c>, cache-backed), never through Jellyfin's static <c>BaseItem.LibraryManager</c>; one that is
    /// gone leaves its fields empty, so a snapshot can also be taken inside an <c>ItemRemoved</c> handler.
    /// </summary>
    /// <remarks>
    /// The item must have its provider ids loaded: <c>GetItemById</c> loads them, a <c>GetItemList</c> query only when its
    /// <c>DtoOptions</c> asks for <c>ItemFields.ProviderIds</c>. Snapshots are refreshed when an item with records is
    /// updated, because provider ids arrive after identification.
    /// </remarks>
    /// <param name="item">The item.</param>
    /// <param name="library">The library manager.</param>
    /// <returns>The snapshot.</returns>
    public static IdentitySnapshot Capture(BaseItem item, ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(library);

        IReadOnlyDictionary<string, string>? seriesProviderIds = null;
        string? seriesName = null;
        Guid? seriesId = null, seasonId = null, albumId = null;
        int? season = null, episode = null;
        switch (item)
        {
            case Episode e:
                seriesId = NonEmpty(e.SeriesId);
                seasonId = NonEmpty(e.SeasonId);
                seriesName = NonBlank(e.SeriesName);
                season = e.ParentIndexNumber;
                episode = e.IndexNumber;
                seriesProviderIds = Lookup(library, seriesId)?.ProviderIds;
                break;
            case Season s:
                seriesId = NonEmpty(s.SeriesId);
                seriesName = NonBlank(s.SeriesName);
                season = s.IndexNumber;
                seriesProviderIds = Lookup(library, seriesId)?.ProviderIds;
                break;
            case Audio a:
                albumId = FindAlbum(a, library);
                break;
        }

        return new IdentitySnapshot
        {
            ItemId = item.Id,
            Kind = KindOf(item),
            Name = item.Name ?? string.Empty,
            Year = item.ProductionYear,
            ProviderIds = item.ProviderIds,
            SeriesProviderIds = seriesProviderIds,
            SeriesName = seriesName,
            Season = season,
            Episode = episode,
            SeriesId = seriesId,
            SeasonId = seasonId,
            AlbumId = albumId,
            RunTimeTicks = item.RunTimeTicks is > 0 ? item.RunTimeTicks : null,
            Path = NonBlank(item.Path)
        };
    }

    /// <summary>
    /// Gets an item's kind, its <c>BaseItemKind</c> name; the type's name for a type the enum doesn't know.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The kind.</returns>
    public static string KindOf(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            return item.GetBaseItemKind().ToString();
        }
        catch (ArgumentException)
        {
            return item.GetType().Name;
        }
    }

    /// <summary>
    /// Whether removing <paramref name="removedItemId"/> takes the snapshot's item with it: it is the item itself, or its
    /// series, season or album. Removing a folder raises <c>ItemRemoved</c> once, for the folder only
    /// (<c>LibraryManager.cs:409-466</c>), so a consumer detaches every record this returns true for. Other folders
    /// (a music artist, a box set, a plain folder) need the verify sweep.
    /// </summary>
    /// <param name="snapshot">The record's snapshot.</param>
    /// <param name="removedItemId">The id of the removed item.</param>
    /// <returns>Whether the record's item went with it.</returns>
    public static bool IsRemovedBy(IdentitySnapshot snapshot, Guid removedItemId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return removedItemId != Guid.Empty
            && (snapshot.ItemId == removedItemId
                || snapshot.SeriesId == removedItemId
                || snapshot.SeasonId == removedItemId
                || snapshot.AlbumId == removedItemId);
    }

    /// <summary>
    /// Gets the provider names whose ids identify an item of a kind (a Strong match): <c>Tmdb</c>, <c>Imdb</c>
    /// and <c>Tvdb</c> for films, series, seasons, episodes and other videos; the MusicBrainz and AudioDB release ids for
    /// albums, the track and recording ids for tracks, the artist ids for artists. Ids that many items share, such as a
    /// film's <c>TmdbCollection</c> or a track's album ids, never count.
    /// </summary>
    /// <param name="kind">The kind (<c>BaseItemKind</c> name).</param>
    /// <returns>The provider names.</returns>
    public static IReadOnlyList<string> StrongProviders(string kind) => kind switch
    {
        "MusicAlbum" => _albumProviders,
        "Audio" => _trackProviders,
        "MusicArtist" => _artistProviders,
        _ => _videoProviders
    };

    /// <summary>
    /// Gets the snapshot's own provider ids that identify its kind (<see cref="StrongProviders"/>), under the provider's
    /// canonical name.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The ids; empty when it has none.</returns>
    public static IReadOnlyDictionary<string, string> StrongIds(IdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Pick(snapshot.ProviderIds, StrongProviders(snapshot.Kind));
    }

    /// <summary>
    /// Gets the series provider ids of an episode or season snapshot that identify the series.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The ids; empty for other kinds or without series ids.</returns>
    public static IReadOnlyDictionary<string, string> StrongSeriesIds(IdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Pick(snapshot.SeriesProviderIds, _videoProviders);
    }

    /// <summary>
    /// Gets the lookup keys of a snapshot (<c>RatingKeys</c>): <c>Movie|Tmdb|603</c> for each of its own strong
    /// ids, and for an episode or season each series id with the numbers, <c>Episode|Tvdb|81189|S01E02</c> and
    /// <c>Season|Tvdb|81189|S01</c>. Values are lower case. A consumer keeps the keys of its detached records in a set
    /// and, when an item is added or updated, calls the resolver only for records that share a key with the item's own
    /// snapshot.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The keys, without duplicates.</returns>
    public static IReadOnlyList<string> StrongKeys(IdentitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var keys = new List<string>();
        if (snapshot.Kind.Length == 0)
        {
            return keys;
        }

        foreach (var (provider, value) in StrongIds(snapshot))
        {
            keys.Add(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Kind}|{provider}|{value.ToLowerInvariant()}"));
        }

        var numbers = snapshot switch
        {
            { Kind: "Episode", Season: { } s, Episode: { } e } => string.Create(CultureInfo.InvariantCulture, $"S{s:D2}E{e:D2}"),
            { Kind: "Season", Season: { } s } => string.Create(CultureInfo.InvariantCulture, $"S{s:D2}"),
            _ => null
        };
        if (numbers is not null)
        {
            foreach (var (provider, value) in StrongSeriesIds(snapshot))
            {
                keys.Add(string.Create(CultureInfo.InvariantCulture, $"{snapshot.Kind}|{provider}|{value.ToLowerInvariant()}|{numbers}"));
            }
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Applies a move-journal line to a snapshot (container ids are rewritten too when a series or season moves): the
    /// item id becomes <c>NewId</c> (and the path <c>NewPath</c>, when given) if it was <c>OldId</c>, and so does a
    /// series, season or album id that was <c>OldId</c>.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="line">The Committed line.</param>
    /// <returns>The updated snapshot, or <paramref name="snapshot"/> itself when the line doesn't concern it.</returns>
    public static IdentitySnapshot ApplyMove(IdentitySnapshot snapshot, MoveJournalLine line)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(line);

        var moved = snapshot.ItemId == line.OldId;
        if (!moved && snapshot.SeriesId != line.OldId && snapshot.SeasonId != line.OldId && snapshot.AlbumId != line.OldId)
        {
            return snapshot;
        }

        return snapshot with
        {
            ItemId = moved ? line.NewId : snapshot.ItemId,
            Path = moved ? line.NewPath ?? snapshot.Path : snapshot.Path,
            SeriesId = snapshot.SeriesId == line.OldId ? line.NewId : snapshot.SeriesId,
            SeasonId = snapshot.SeasonId == line.OldId ? line.NewId : snapshot.SeasonId,
            AlbumId = snapshot.AlbumId == line.OldId ? line.NewId : snapshot.AlbumId
        };
    }

    /// <summary>
    /// Picks the given providers' ids from a map, under the providers' canonical names.
    /// </summary>
    /// <param name="ids">The map (keys matched without regard to case), or null.</param>
    /// <param name="providers">The provider names.</param>
    /// <returns>The ids found.</returns>
    internal static IReadOnlyDictionary<string, string> Pick(IReadOnlyDictionary<string, string>? ids, IReadOnlyList<string> providers)
    {
        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ids is null)
        {
            return picked;
        }

        foreach (var provider in providers)
        {
            if (TryGet(ids, provider, out var value))
            {
                picked[provider] = value;
            }
        }

        return picked;
    }

    /// <summary>
    /// Looks a provider id up without regard to the key's case and ignores blank values.
    /// </summary>
    /// <param name="ids">The map.</param>
    /// <param name="provider">The provider name.</param>
    /// <param name="value">The trimmed value.</param>
    /// <returns>Whether there is a value.</returns>
    internal static bool TryGet(IReadOnlyDictionary<string, string> ids, string provider, out string value)
    {
        value = string.Empty;
        if (!ids.TryGetValue(provider, out var found))
        {
            found = ids.FirstOrDefault(e => string.Equals(e.Key, provider, StringComparison.OrdinalIgnoreCase)).Value;
        }

        if (string.IsNullOrWhiteSpace(found))
        {
            return false;
        }

        value = found.Trim();
        return true;
    }

    private static Guid? FindAlbum(Audio audio, ILibraryManager library)
    {
        // Tracks sit in their album's folder, or in a disc folder ("CD1") inside it.
        var parentId = audio.ParentId;
        for (var step = 0; step < MaxParentSteps && parentId != Guid.Empty; step++)
        {
            switch (library.GetItemById(parentId))
            {
                case MusicAlbum album:
                    return album.Id;
                case Folder folder and not (CollectionFolder or AggregateFolder or UserRootFolder):
                    parentId = folder.ParentId;
                    break;
                default:
                    return null;
            }
        }

        return null;
    }

    private static BaseItem? Lookup(ILibraryManager library, Guid? id)
        => id is { } value && value != Guid.Empty ? library.GetItemById(value) : null;

    private static Guid? NonEmpty(Guid id) => id == Guid.Empty ? null : id;

    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
