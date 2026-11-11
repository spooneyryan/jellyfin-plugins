using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.Query.Planning;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>A whole evaluation.</summary>
/// <param name="OrderedIds">The representatives' ids, ordered and limited.</param>
/// <param name="CandidateCount">The candidates checked.</param>
/// <param name="Plan">The plan.</param>
/// <param name="Warnings">Warnings (none yet).</param>
/// <param name="Elapsed">The time it took.</param>
internal sealed record EvaluationResult(IReadOnlyList<Guid> OrderedIds, int CandidateCount, QueryPlan Plan, IReadOnlyList<string> Warnings, TimeSpan Elapsed);
