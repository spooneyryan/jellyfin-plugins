namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// How a unit moves.
/// </summary>
internal enum MoveMethod
{
    /// <summary>An atomic rename on one volume.</summary>
    Rename,

    /// <summary>A staged copy, verify, rename and delete across volumes (a blocker in v1).</summary>
    CopyVerifyDelete
}
