using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Writes a collection through the server's own collection calls: one remove and one add at most, each only when it has
/// items, then the presentation (and the rating) saved when they differ or members were removed.
/// </summary>
/// <remarks>
/// <para><b>What it can't do.</b> The add appends, so the query's order can't be kept: such a collection is sorted by name,
/// with a warning. The remove finds a link by its cached item id, which a dead link doesn't have: dead links are never
/// passed, they stay (counted in the state) and don't count as a change, so they cause no write. Duplicate links stay
/// too. The direct writer prunes both the next time it writes.</para>
/// <para><b>Events.</b> The server's calls fire the collection events (and queue a refresh each); the edit listener ignores
/// them while <see cref="CollectionWriteSuppression"/> holds the collection.</para>
/// </remarks>
internal sealed class ManagerApiWriter
{
    private readonly ICollectionPersistence _persistence;
    private readonly DirectReplaceWriter _direct;
    private readonly CollectionWriteSuppression _suppression;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, bool> _warned = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ManagerApiWriter"/> class.
    /// </summary>
    /// <param name="persistence">The server's collection calls.</param>
    /// <param name="direct">The direct writer (loading members, the presentation and its save).</param>
    /// <param name="suppression">The collections being written.</param>
    /// <param name="logger">The logger.</param>
    public ManagerApiWriter(ICollectionPersistence persistence, DirectReplaceWriter direct, CollectionWriteSuppression suppression, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(direct);
        ArgumentNullException.ThrowIfNull(suppression);
        ArgumentNullException.ThrowIfNull(logger);
        _persistence = persistence;
        _direct = direct;
        _suppression = suppression;
        _logger = logger;
    }

    /// <summary>Gets what this writer can change of a difference: not dead or duplicate links, not the order.</summary>
    /// <param name="diff">The difference.</param>
    /// <returns>The part it writes.</returns>
    public static CollectionDiff Writable(CollectionDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return diff with { Dead = 0, Duplicates = 0, OrderChanged = false };
    }

    /// <summary>Gets the definition as this writer can show it: the query's order becomes the name's (with a warning, once).</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>The definition to compare and write.</returns>
    public AutoCollectionDefinition Shown(AutoCollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Presentation.EffectiveSortMode != SmartSearchSortMode.QueryOrder)
        {
            return definition;
        }

        if (_warned.TryAdd(definition.Id, true))
        {
            _logger.LogWarning("Auto-collection {Id} is sorted by its query, which the server's collection calls can't keep; it is sorted by name.", definition.Id);
        }

        return definition with { Presentation = definition.Presentation with { SortMode = SmartSearchSortMode.SortName } };
    }

    /// <summary>
    /// Removes and adds the members that differ, then saves the presentation when it differs or members went: on the
    /// collection as the server holds it after its calls, read again (each call loads and saves its own copy, so the one
    /// located before them is stale and saving it would undo them).
    /// </summary>
    /// <param name="box">The collection (found, marked).</param>
    /// <param name="diff">What to change (<see cref="Writable"/>, against <see cref="Shown"/>).</param>
    /// <param name="members">The members it keeps, in order (the rating's).</param>
    /// <param name="definition">The definition as shown.</param>
    /// <param name="cancellationToken">Cancels the write before the first call.</param>
    /// <param name="accept">Decides, given the members that can't be loaded any more, whether to write; without, it always writes.</param>
    /// <returns>Whether it wrote (<c>false</c> when <paramref name="accept"/> refused).</returns>
    public async Task<bool> WriteAsync(BoxSet box, CollectionDiff diff, IReadOnlyList<Guid> members, AutoCollectionDefinition definition, CancellationToken cancellationToken, Func<IReadOnlyCollection<Guid>, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(diff);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(definition);

        // The server's add throws on any id it can't find: only items that exist are passed.
        var items = _direct.Load(members, cancellationToken);
        var added = diff.Added.Where(items.ContainsKey).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (accept is not null && !accept(DirectReplaceWriter.Missing(members, items)))
        {
            return false;
        }

        // Only the events of the items passed are the plugin's own; an edit of another item meanwhile is a person's.
        using (_suppression.Begin(box.Id, [.. diff.Removed, .. added]))
        {
            if (diff.Removed.Count > 0)
            {
                await _persistence.RemoveAsync(box.Id, diff.Removed).ConfigureAwait(false);
            }

            if (added.Count > 0)
            {
                await _persistence.AddAsync(box.Id, added).ConfigureAwait(false);
            }
        }

        // The add sets the rating; a removal doesn't, and neither call sets the presentation.
        if (diff.Presentation.Count > 0 || diff.Removed.Count > 0)
        {
            var fresh = _persistence.Reload(box.Id);
            if (fresh is null || !CollectionLocator.HasTether(fresh, definition.Id))
            {
                _logger.LogWarning("Auto-collection {Id}: its collection changed hands during the write; its presentation is left for the next run.", definition.Id);
                return true;
            }

            var kept = members.Distinct().Where(items.ContainsKey).Select(id => items[id]).ToList();
            await _direct.SavePresentationAsync(fresh, kept, definition).ConfigureAwait(false);
        }

        return true;
    }
}
