using Jellyfin.Plugin.SmartSearch.Query.Planning;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>The page a search asks for.</summary>
/// <param name="StartIndex">The first result's index.</param>
/// <param name="Limit">The page size, 1 to <see cref="QueryPlanner.PageWindow"/>.</param>
/// <param name="ResultToken">The token of an earlier page of the same search, or <c>null</c>.</param>
/// <param name="IncludeTotal">Whether to report the total (on the Fast path, one count in SQL).</param>
/// <param name="ForceResidual">Whether to plan with the scope alone pushed down, every rule checked in memory
/// (<see cref="Planning.PlanRequest.ScopeOnly"/>); its result is cached apart from the query's own.</param>
internal sealed record PageRequest(int StartIndex = 0, int Limit = 50, string? ResultToken = null, bool IncludeTotal = true, bool ForceResidual = false);
