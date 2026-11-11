using System;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// A title the index holds: a movie or a series, with what the titles list shows of it.
/// </summary>
/// <param name="Id">The item id.</param>
/// <param name="Name">The display name.</param>
/// <param name="SortName">The name it sorts by.</param>
/// <param name="Year">The production year, or null.</param>
/// <param name="Kind">Movie or Series.</param>
internal sealed record TitleRecord(Guid Id, string Name, string SortName, int? Year, BaseItemKind Kind);
