using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// One file or folder of a unit's manifest (<c>Files</c>, <c>Dirs</c>), relative to the unit's source folder.
/// </summary>
/// <param name="RelativePath">The path below the source folder (the unit folder, or a file set's directory).</param>
/// <param name="IsDirectory">Whether it is a folder.</param>
/// <param name="Length">A file's length in bytes; 0 for a folder.</param>
/// <param name="LastWriteUtc">When it was last written.</param>
internal sealed record UnitEntry(string RelativePath, bool IsDirectory, long Length, DateTime LastWriteUtc);
