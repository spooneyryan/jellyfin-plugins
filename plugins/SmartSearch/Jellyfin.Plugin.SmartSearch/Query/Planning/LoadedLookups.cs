using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// <see cref="IScopedLookups"/> read for each plan, without a cache: the user's episodes loaded in batches, and a studio
/// looked up with a small query. Both use the user's access query and <c>GetItemList</c>'s <c>IsVisible(user)</c>. The
/// server answers from the value index where it can (<c>ValueIndexService</c>) and falls back on these reads.
/// </summary>
internal sealed class LoadedLookups : IScopedLookups
{
    /// <summary>The most episodes read; past it a series name rule isn't pushed.</summary>
    public const int MaxEpisodes = DatabasePushdownFacts.MaxEpisodeSeries;

    /// <summary>The most items carrying a studio checked for one the user can see.</summary>
    public const int MaxStudioItems = 100;

    private const int BatchSize = 1_000;

    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoadedLookups"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    public LoadedLookups(ILibraryManager library)
    {
        ArgumentNullException.ThrowIfNull(library);
        _library = library;
    }

    /// <inheritdoc />
    public IReadOnlyList<EpisodeSeries>? EpisodeSeries(User user, IReadOnlyList<Guid> folders, bool includeMissing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(folders);
        cancellationToken.ThrowIfCancellationRequested();
        if (folders.Count == 0)
        {
            return [];
        }

        var query = Query(user, folders, [BaseItemKind.Episode], includeMissing);
        var ids = _library.GetItemIds(query);
        if (ids.Count > MaxEpisodes)
        {
            return null;
        }

        var pairs = new HashSet<EpisodeSeries>();
        foreach (var batch in ids.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            query.ItemIds = batch;
            foreach (var episode in _library.GetItemList(query).OfType<Episode>())
            {
                if (!LibraryAccess.IsOwned(episode))
                {
                    pairs.Add(new EpisodeSeries(episode.SeriesId, episode.SeriesName));
                }
            }
        }

        return [.. pairs];
    }

    /// <inheritdoc />
    public bool HasStudio(User user, IReadOnlyList<Guid> folders, IReadOnlyCollection<BaseItemKind> kinds, bool includeMissing, string name, Guid studioId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(kinds);
        cancellationToken.ThrowIfCancellationRequested();
        if (folders.Count == 0 || kinds.Count == 0)
        {
            return false;
        }

        // SQL's access first (a few ids), then IsVisible(user) on them; an item that only SQL lets through doesn't count.
        var query = Query(user, folders, [.. kinds], includeMissing);
        query.StudioIds = [studioId];
        query.Limit = MaxStudioItems;
        var ids = _library.GetItemIds(query);
        if (ids.Count == 0)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        query.Limit = null;
        query.ItemIds = [.. ids];
        return _library.GetItemList(query).Any(i => !LibraryAccess.IsOwned(i));
    }

    private static MediaBrowser.Controller.Entities.InternalItemsQuery Query(User user, IReadOnlyList<Guid> folders, BaseItemKind[] kinds, bool includeMissing)
    {
        var query = QueryPlanner.CreateAccessQuery(user, PlanPurpose.Search);
        query.TopParentIds = [.. folders];
        query.IncludeItemTypes = kinds;
        query.IsVirtualItem = includeMissing ? null : false;
        query.DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false };
        return query;
    }
}
