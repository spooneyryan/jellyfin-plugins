namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>Whether the plugin writes the collection's overview.</summary>
internal enum SmartSearchOverviewMode
{
    /// <summary>A value this version doesn't know.</summary>
    Unknown,

    /// <summary>The overview is never touched.</summary>
    LeaveAsIs,

    /// <summary>The definition's overview is written whenever it differs.</summary>
    Managed
}
