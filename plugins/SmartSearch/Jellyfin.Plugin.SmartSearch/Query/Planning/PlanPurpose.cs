namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>What a plan is for.</summary>
internal enum PlanPurpose
{
    /// <summary>A user's search: <c>new InternalItemsQuery(user)</c>; the Fast path is allowed.</summary>
    Search,

    /// <summary>The auto-collections' <c>EvaluateAllAsync</c>: <c>new InternalItemsQuery()</c> and <c>SetUser</c> with the evaluation user, if any; never the Fast path.</summary>
    EvaluateAll
}
