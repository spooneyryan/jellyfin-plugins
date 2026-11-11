using System;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// Reads the server's libraries: what exists now, from <c>GetVirtualFolders()</c> only
/// (the only list without ghosts). The lifecycle over time (renames, deletions, re-created libraries, <c>KnownLibraries</c>) is
/// <see cref="LibraryLifecycle"/>'s, run by the library sync.
/// </summary>
internal interface ILibraryCatalogSource
{
    /// <summary>
    /// Reads the libraries now.
    /// </summary>
    /// <returns>The catalog.</returns>
    LibraryCatalog Read();

    /// <summary>
    /// Gets the id the core gives a library of this name: <c>GetNewItemId(&lt;DefaultUserViewsPath&gt;\&lt;name&gt;,
    /// typeof(CollectionFolder))</c>, whether it exists or not, as the library structure calls need
    /// it before and after they run.
    /// </summary>
    /// <param name="name">The library's name (its folder's).</param>
    /// <returns>The id, or <see cref="Guid.Empty"/> for an empty name.</returns>
    Guid IdOf(string name);
}
