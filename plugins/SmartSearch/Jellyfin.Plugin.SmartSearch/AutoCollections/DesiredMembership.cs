using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.Query.Execution;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What a collection's members should be: the query's result, then the pins, without the excluded items.</summary>
/// <param name="Ids">The members, in order: the query's (without the excluded items, then limited), then the pins that aren't among them.</param>
/// <param name="QueryCount">How many of the query's results were taken: its members plus the excluded items passed over before the limit.</param>
/// <param name="CandidateCount">How many candidates the query checked.</param>
/// <param name="PinsAdded">The pins added after the query's result.</param>
/// <param name="PinsSkipped">The pins left out: gone, or outside the creator's (and evaluation user's) libraries.</param>
/// <param name="ExcludedCount">The query's results taken out because they are excluded (those before the limit was reached).</param>
/// <param name="LibraryIds">The libraries the query was evaluated over.</param>
/// <param name="Error">The reason the membership can't be used (<see cref="AutoCollectionReasons.TooManyMembers"/>), or <c>null</c>.</param>
/// <param name="Evaluation">The engine's result.</param>
internal sealed record DesiredMembership(
    IReadOnlyList<Guid> Ids,
    int QueryCount,
    int CandidateCount,
    int PinsAdded,
    int PinsSkipped,
    int ExcludedCount,
    IReadOnlyList<Guid> LibraryIds,
    string? Error,
    EvaluationResult Evaluation)
{
    /// <summary>Gets the libraries every member comes from (the creator's, pins included), or <c>null</c> when unknown.</summary>
    public IReadOnlyList<Guid>? SourceLibraryIds { get; init; }

    /// <summary>Gets a value indicating whether the membership can be written.</summary>
    public bool Ok => Error is null;
}
