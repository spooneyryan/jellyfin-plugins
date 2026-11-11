using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// A library as the server lists it now: a folder under the default user views
/// path, with the id the core gives its <c>CollectionFolder</c>.
/// </summary>
/// <param name="Id">The library's id: <c>GetNewItemId(&lt;root\default\Name&gt;, typeof(CollectionFolder))</c>, never
/// <c>GetVirtualFolders().ItemId</c>, which reports the old id after a case-only rename on Windows (seen on a 10.11.5 sandbox).</param>
/// <param name="Name">The library's name.</param>
/// <param name="Locations">The library's folders.</param>
/// <param name="Enabled">Whether the native "Enable the library" switch is on (off hides it from everyone).</param>
/// <param name="CollectionType">The library's type (<c>movies</c>, <c>tvshows</c>, <c>boxsets</c>…), or <c>null</c>.</param>
/// <param name="CreatedUtc">The creation time of the library's folder (what tells a re-created library), or
/// <c>null</c> when unknown.</param>
/// <param name="HasItem">Whether the core has the library's item: <c>GetVirtualFolders()</c> gives an <c>ItemId</c>. False
/// in the "folder without item" state (on 10.11.5 about half a second during an add, and after a rename sent
/// with <c>refreshLibrary=false</c> until the next scan), in which the catalog takes no lifecycle action (the
/// lifecycle's ambiguous states).</param>
internal sealed record CatalogLibrary(Guid Id, string Name, IReadOnlyList<string> Locations, bool Enabled, string? CollectionType, DateTime? CreatedUtc, bool HasItem = true);
