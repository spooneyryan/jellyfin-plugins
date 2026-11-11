namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// One rename of a unit's commit: the unit folder, or one member of a file set.
/// </summary>
/// <param name="From">The path now.</param>
/// <param name="To">The path after.</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
internal sealed record UnitRename(string From, string To, bool IsDirectory);
