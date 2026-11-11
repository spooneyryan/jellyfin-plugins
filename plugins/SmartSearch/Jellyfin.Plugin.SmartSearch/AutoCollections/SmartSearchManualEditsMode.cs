namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What happens to manual adds and removes made in Jellyfin's own collection screens.</summary>
internal enum SmartSearchManualEditsMode
{
    /// <summary>A value this version doesn't know.</summary>
    Unknown,

    /// <summary>They are kept, as pins and excludes.</summary>
    Respect,

    /// <summary>They are reverted on the next run.</summary>
    Overwrite
}
