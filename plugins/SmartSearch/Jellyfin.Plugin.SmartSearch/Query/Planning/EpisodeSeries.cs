using System;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>A series id and the series name episodes store with it (<c>Episode.SeriesName</c>, which can lag a rename).</summary>
/// <param name="SeriesId">The episodes' <c>SeriesId</c>, <see cref="Guid.Empty"/> when they have none.</param>
/// <param name="SeriesName">The episodes' stored <c>SeriesName</c>.</param>
internal sealed record EpisodeSeries(Guid SeriesId, string? SeriesName);
