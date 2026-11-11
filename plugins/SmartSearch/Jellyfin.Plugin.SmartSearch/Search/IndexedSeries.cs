using System;

namespace Jellyfin.Plugin.SmartSearch.Search;

/// <summary>A series as the value index keeps it, for the series picker.</summary>
/// <param name="Id">The series' id.</param>
/// <param name="Name">Its name.</param>
/// <param name="SortName">Its sort name.</param>
/// <param name="ProductionYear">Its year.</param>
/// <param name="Folded">Its name folded as the rules compare text (case and accents ignored).</param>
internal sealed record IndexedSeries(Guid Id, string? Name, string? SortName, int? ProductionYear, string Folded);
