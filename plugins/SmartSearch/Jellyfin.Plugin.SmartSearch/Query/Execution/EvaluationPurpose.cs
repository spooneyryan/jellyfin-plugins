namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>What an evaluation is for: which slots it takes.</summary>
internal enum EvaluationPurpose
{
    /// <summary>A user's search (a search slot).</summary>
    Search,

    /// <summary>The auto-collections' preview (a search slot).</summary>
    Preview,

    /// <summary>The auto-collection worker (its own slot).</summary>
    AutoCollection
}
