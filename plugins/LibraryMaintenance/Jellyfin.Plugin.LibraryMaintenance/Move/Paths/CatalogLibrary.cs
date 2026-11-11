using System;
using System.Collections.Generic;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Paths;

/// <summary>
/// One library as Move sees it: its id, name, type and locations in the order configured.
/// </summary>
/// <param name="Id">The library's id (its <c>CollectionFolder</c>).</param>
/// <param name="Name">Its name.</param>
/// <param name="CollectionType">Its type, or null for a mixed library (never a destination).</param>
/// <param name="Locations">Its folders, as configured.</param>
/// <param name="Options">Its options (for <c>EnableAutomaticSeriesGrouping</c>), or null.</param>
internal sealed record CatalogLibrary(Guid Id, string Name, CollectionTypeOptions? CollectionType, IReadOnlyList<string> Locations, LibraryOptions? Options)
{
    /// <summary>Gets the library type's name as the API gives it (<c>movies</c>, <c>tvshows</c>, <c>musicvideos</c>), or null.</summary>
    public string? CollectionTypeName => CollectionType?.ToString();

    /// <summary>
    /// Gets the index of the location a path is in: the longest location that holds it, or -1.
    /// </summary>
    /// <param name="path">A full path.</param>
    /// <returns>The index, or -1.</returns>
    public int LocationOf(string path)
    {
        var best = -1;
        for (var i = 0; i < Locations.Count; i++)
        {
            if (PathRules.IsSameOrUnder(path, Locations[i]) && (best < 0 || Locations[i].Length > Locations[best].Length))
            {
                best = i;
            }
        }

        return best;
    }
}
