using System;
using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>A hydrated page.</summary>
/// <param name="Items">The DTOs, in the page's order.</param>
/// <param name="Dropped">The page's ids left out: gone, owned, or refused by <c>IsVisible</c>.</param>
internal sealed record HydratedPage(IReadOnlyList<BaseItemDto> Items, IReadOnlyList<Guid> Dropped);
