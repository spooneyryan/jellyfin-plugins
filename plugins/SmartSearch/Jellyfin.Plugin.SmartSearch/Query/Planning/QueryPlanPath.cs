namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>How a plan is run.</summary>
internal enum QueryPlanPath
{
    /// <summary>Nothing can match (no library or item kind left, or a pushed set that is empty): answer without querying.</summary>
    Empty,

    /// <summary>Every rule is pushed as Exact and every sort key is core's: one <c>GetItemsResult</c> page query (search only).</summary>
    Fast,

    /// <summary>One <c>GetItemIds</c> for the candidates, hydrated in batches and re-checked in memory.</summary>
    Residual
}
