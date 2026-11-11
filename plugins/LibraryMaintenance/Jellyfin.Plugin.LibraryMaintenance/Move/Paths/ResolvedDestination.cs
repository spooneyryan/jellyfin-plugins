using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// A destination folder, resolved and checked.
/// </summary>
/// <param name="Library">The destination library.</param>
/// <param name="LocationIndex">The location's index in the library.</param>
/// <param name="LocationPath">The location, spelled exactly as configured (ids are case-sensitive).</param>
/// <param name="Segments">The existing folders below the location, each in its on-disk spelling.</param>
/// <param name="NewFolderName">A new folder to create in them, or null.</param>
internal sealed record ResolvedDestination(CatalogLibrary Library, int LocationIndex, string LocationPath, IReadOnlyList<string> Segments, string? NewFolderName)
{
    /// <summary>Gets the existing folders below the location, joined with <c>\</c> (empty for the location itself).</summary>
    public string RelativePath => string.Join('\\', Segments);

    /// <summary>Gets the deepest existing folder: the location and <see cref="Segments"/>.</summary>
    public string ExistingDirectory => Segments.Count == 0 ? LocationPath : Path.Join(PathRules.TrimEnd(LocationPath), RelativePath);

    /// <summary>Gets the destination folder: <see cref="ExistingDirectory"/>, and the new folder when there is one.</summary>
    public string Directory => NewFolderName is null ? ExistingDirectory : Path.Join(ExistingDirectory, NewFolderName);

    /// <summary>Gets a value indicating whether the location is inside another library's location (or another of its own),
    /// so isn't a top-level folder to Jellyfin.</summary>
    public bool LocationIsNested { get; init; }

    /// <summary>
    /// Gets whether a folder made or moved into <paramref name="parent"/> sits directly in a top-level location, where
    /// Jellyfin reads an extras-named folder as an ordinary one (<see cref="PathRules.IsIgnoredExtrasFolder"/>).
    /// </summary>
    /// <param name="parent">The folder it goes into.</param>
    /// <returns>True for the top-level location itself.</returns>
    public bool IsTopLevelLocation(string parent) => !LocationIsNested && PathRules.SamePath(parent, LocationPath);
}
