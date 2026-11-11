using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>A Residual run's result.</summary>
/// <param name="OrderedIds">The representatives' ids, ordered and limited.</param>
/// <param name="CandidateCount">The candidate ids SQL returned.</param>
/// <param name="MatchCount">The candidates that matched (before collapse, grouping and the limit).</param>
/// <param name="Batches">The hydration batches.</param>
/// <param name="FactsFetched">The candidates whose facts phase B fetched.</param>
internal sealed record ResidualResult(IReadOnlyList<Guid> OrderedIds, int CandidateCount, int MatchCount, int Batches, int FactsFetched);
