using System;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// One folder of a Jellyfin library (<see cref="ClipLibraryLocations"/>).
/// </summary>
/// <param name="Path">The folder, normalised as <see cref="ClipRootPaths.TryNormalize"/> does.</param>
/// <param name="LibraryName">The library's name.</param>
/// <param name="CollectionType">The library's type (<c>movies</c>, <c>tvshows</c>, <c>homevideos</c>, …), if it has one.</param>
/// <param name="LibraryId">The library's id (its collection folder), when Jellyfin gave one.</param>
internal sealed record ClipLibraryLocation(string Path, string LibraryName, CollectionTypeOptions? CollectionType, Guid? LibraryId);
