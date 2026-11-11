using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// The libraries and their locations (<c>LibraryLocationCatalog</c>): Jellyfin's virtual folders, limited to the
/// libraries the caller can open (the kit's <see cref="LibraryAccess"/>, as Duplicates does), and
/// the library an item is in.
/// </summary>
internal sealed class LibraryLocationCatalog
{
    /// <summary>The library types Move works in.</summary>
    public static readonly IReadOnlyList<CollectionTypeOptions> MoveTypes = [CollectionTypeOptions.movies, CollectionTypeOptions.tvshows, CollectionTypeOptions.musicvideos];

    private readonly ILibraryManager _library;
    private readonly LibraryAccess _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryLocationCatalog"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="access">Which libraries a user can open.</param>
    public LibraryLocationCatalog(ILibraryManager library, LibraryAccess access)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(access);
        _library = library;
        _access = access;
    }

    /// <summary>
    /// Gets the libraries a caller can open, in Jellyfin's order, each with its locations as configured.
    /// </summary>
    /// <param name="user">The caller, or null for an API key (every library).</param>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<CatalogLibrary> GetLibraries(User? user)
    {
        var visible = _access.VisibleLibraries(user).Select(l => l.Id).ToHashSet();
        var result = new List<CatalogLibrary>();
        foreach (var folder in _library.GetVirtualFolders() ?? [])
        {
            if (folder is null || !Guid.TryParse(folder.ItemId, CultureInfo.InvariantCulture, out var id) || !visible.Contains(id))
            {
                continue;
            }

            var locations = (folder.Locations ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            result.Add(new CatalogLibrary(id, folder.Name ?? string.Empty, folder.CollectionType, locations, folder.LibraryOptions));
        }

        return result;
    }

    /// <summary>
    /// Gets one library the caller can open.
    /// </summary>
    /// <param name="id">The library id.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The library, or null when there is none or the caller can't open it.</returns>
    public CatalogLibrary? FindLibrary(Guid id, User? user) => id == Guid.Empty ? null : GetLibraries(user).FirstOrDefault(l => l.Id == id);

    /// <summary>
    /// Gets the library and location an item is in: one of its collection folders the caller can open
    /// (<c>GetCollectionFolders</c>) whose location holds the item's path, the longest such location; else the longest
    /// location of any library the caller can open.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The library and the location's index, or null when no location holds the item.</returns>
    public (CatalogLibrary Library, int LocationIndex)? FindSource(BaseItem item, User? user)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var libraries = GetLibraries(user);
        var own = (_library.GetCollectionFolders(item) ?? []).Select(f => f.Id).ToHashSet();
        foreach (var candidates in new[] { libraries.Where(l => own.Contains(l.Id)), libraries })
        {
            (CatalogLibrary Library, int LocationIndex)? best = null;
            foreach (var library in candidates)
            {
                var index = library.LocationOf(item.Path);
                if (index >= 0 && (best is null || library.Locations[index].Length > best.Value.Library.Locations[best.Value.LocationIndex].Length))
                {
                    best = (library, index);
                }
            }

            if (best is not null)
            {
                return best;
            }
        }

        return null;
    }
}
