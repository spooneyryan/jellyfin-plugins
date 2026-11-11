using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The server's libraries at one moment: what <c>All</c> and <c>AllExcept</c> expand against, what library
/// references are checked against, and where names come from. Existence comes from <c>GetVirtualFolders()</c> only
/// because the user root's children and <c>GetItemById</c> keep deleted libraries.
/// </summary>
internal sealed class LibraryCatalog
{
    private readonly Dictionary<Guid, CatalogLibrary> _byId;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryCatalog"/> class.
    /// </summary>
    /// <param name="libraries">The libraries; a repeated id keeps its first entry, an empty id is left out.</param>
    public LibraryCatalog(IEnumerable<CatalogLibrary> libraries)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        _byId = [];
        foreach (var library in libraries)
        {
            if (library is not null && !library.Id.Equals(Guid.Empty))
            {
                _byId.TryAdd(library.Id, library);
            }
        }

        Libraries = [.. _byId.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Id)];
    }

    /// <summary>Gets an empty catalog.</summary>
    public static LibraryCatalog Empty { get; } = new([]);

    /// <summary>Gets the libraries, by name.</summary>
    public IReadOnlyList<CatalogLibrary> Libraries { get; }

    /// <summary>Gets the libraries' ids.</summary>
    public IEnumerable<Guid> Ids => Libraries.Select(l => l.Id);

    /// <summary>
    /// Gets whether a library exists.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <returns><c>true</c> when the server lists it.</returns>
    public bool Contains(Guid id) => _byId.ContainsKey(id);

    /// <summary>
    /// Finds a library.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <returns>The library, or <c>null</c>.</returns>
    public CatalogLibrary? Find(Guid id) => _byId.TryGetValue(id, out var library) ? library : null;

    /// <summary>
    /// Expands a native access to the existing libraries it opens now.
    /// </summary>
    /// <param name="enableAllFolders">Whether every library is granted.</param>
    /// <param name="enabledFolders">The granted libraries otherwise.</param>
    /// <returns>The ids.</returns>
    public IReadOnlySet<Guid> Expand(bool enableAllFolders, IEnumerable<Guid> enabledFolders)
    {
        ArgumentNullException.ThrowIfNull(enabledFolders);
        return enableAllFolders ? Ids.ToHashSet() : enabledFolders.Where(Contains).ToHashSet();
    }

    /// <summary>
    /// Orders library ids by the libraries' names, ids the catalog doesn't know last.
    /// </summary>
    /// <param name="ids">The ids.</param>
    /// <returns>The ordered ids.</returns>
    public IReadOnlyList<Guid> Order(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return [.. ids.Distinct().OrderBy(id => Find(id) is null).ThenBy(id => Find(id)?.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(id => id)];
    }
}
