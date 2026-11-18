using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A playlist as an export reads it: its id, name and entries in playlist order.
/// </summary>
/// <param name="Id">The playlist's id.</param>
/// <param name="Name">The playlist's name.</param>
/// <param name="Entries">Its entries in order: each one's item, or <c>null</c> for one no longer in the library.</param>
internal sealed record PlaylistExportSource(Guid Id, string Name, IReadOnlyList<BaseItem?> Entries);
