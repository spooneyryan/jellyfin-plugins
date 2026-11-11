namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// A field's cost class: where the engine evaluates it.
/// </summary>
internal enum FieldCost
{
    /// <summary>Pushed down into SQL only.</summary>
    Sql,

    /// <summary>Item fields, evaluated in memory over each candidate.</summary>
    Row,

    /// <summary>A per-query id set (a resolver).</summary>
    Set,

    /// <summary>Per-candidate facts fetched in batches (streams, ratings).</summary>
    Facts,

    /// <summary>The user's data (watch state).</summary>
    User
}
