namespace Jellyfin.Plugin.LibraryMaintenance.Operations;

/// <summary>
/// The feature that holds an item in <see cref="LibraryOperationGate"/>'s busy set.
/// </summary>
internal enum OperationOwner
{
    /// <summary>A move job.</summary>
    Move,

    /// <summary>A Duplicates merge, delete or undo.</summary>
    Duplicates
}
