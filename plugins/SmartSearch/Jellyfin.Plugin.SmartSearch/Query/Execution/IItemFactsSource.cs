using System;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// Fetches facts for the engine's phase B: the values of <see cref="Fields.FieldCost.Facts"/>
/// fields (stream facts, the ratings facade) for a batch's undecided candidates, in chunks of
/// <see cref="ResidualEvaluator.FactsChunkSize"/>. None is registered yet (no field of this version needs facts); a query that uses a
/// facts field no source answers is refused with <c>FieldUnavailable</c>.
/// </summary>
internal interface IItemFactsSource
{
    /// <summary>Tells whether the source answers a field.</summary>
    /// <param name="fieldId">The field's id.</param>
    /// <returns>Whether it does.</returns>
    bool Answers(string fieldId);

    /// <summary>Fetches the facts of items.</summary>
    /// <param name="items">The items (at most <see cref="ResidualEvaluator.FactsChunkSize"/>).</param>
    /// <param name="fieldIds">The fields to fetch, all answered by this source.</param>
    /// <param name="viewerId">The user whose view the facts are for (a rating source's own stars): the searching user, or
    /// an auto-collection's evaluation user or creator; <c>null</c> for none.</param>
    /// <param name="cancellationToken">The evaluation's cancellation token.</param>
    /// <returns>Each item's facts by field id; an item left out can't be decided, which refuses the search.</returns>
    IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, object?>> Load(IReadOnlyList<BaseItem> items, IReadOnlyCollection<string> fieldIds, Guid? viewerId, CancellationToken cancellationToken);
}
