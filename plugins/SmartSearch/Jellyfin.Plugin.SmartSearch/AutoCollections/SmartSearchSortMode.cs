namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The collection's sort (<c>BoxSet.DisplayOrder</c>).</summary>
internal enum SmartSearchSortMode
{
    /// <summary>A value this version doesn't know.</summary>
    Unknown,

    /// <summary>The query's order, kept as the order of the links (<c>Default</c>); needs the lock.</summary>
    QueryOrder,

    /// <summary>Release date (<c>PremiereDate</c>), the only sort an unlocked collection keeps.</summary>
    PremiereDate,

    /// <summary>Sort name (<c>SortName</c>); needs the lock.</summary>
    SortName
}
