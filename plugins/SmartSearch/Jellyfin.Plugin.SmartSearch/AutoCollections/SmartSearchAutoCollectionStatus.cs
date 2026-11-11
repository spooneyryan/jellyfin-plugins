namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>An auto-collection's state, as the tab shows it.</summary>
internal enum SmartSearchAutoCollectionStatus
{
    /// <summary>A value this version doesn't know.</summary>
    Unknown,

    /// <summary>No collection yet (new, or imported from another server).</summary>
    NotCreated,

    /// <summary>A run was asked for and hasn't started.</summary>
    Queued,

    /// <summary>A run is going on.</summary>
    Running,

    /// <summary>The collection matches the query.</summary>
    Ok,

    /// <summary>Removals are held back for a manager's review.</summary>
    Guarded,

    /// <summary>The collection found by its id no longer carries the plugin's marker: nothing is written until a manager decides.</summary>
    TetherMissing,

    /// <summary>The collection was deleted; nothing is recreated on its own.</summary>
    Orphaned,

    /// <summary>The saved query is gone.</summary>
    QueryMissing,

    /// <summary>The query can't be used for the collection (with a reason).</summary>
    QueryInvalid,

    /// <summary>The query reads watch state and no evaluation user is set, or that user was deleted.</summary>
    EvaluationUserMissing,

    /// <summary>The user who made the definition was deleted: their libraries are its scope, so it waits for an administrator.</summary>
    CreatorMissing,

    /// <summary>The definition (or the whole file) was written by a newer Smart Search: never run or written.</summary>
    NeedsNewerVersion,

    /// <summary>The run failed (with a reason).</summary>
    Error,

    /// <summary>The definition is turned off.</summary>
    Disabled
}
