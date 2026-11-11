namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// What a move takes.
/// </summary>
internal enum MoveUnitKind
{
    /// <summary>The item's own folder, renamed as one (an own-folder movie, a DVD or Blu-ray folder).</summary>
    Folder,

    /// <summary>The item's files and their sidecars, each renamed on its own (loose files, episodes).</summary>
    FileSet
}
