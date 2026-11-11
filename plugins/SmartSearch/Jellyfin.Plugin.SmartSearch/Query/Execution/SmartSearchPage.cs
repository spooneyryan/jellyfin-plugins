using System;
using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>A page of a search.</summary>
/// <param name="Items">The page's items, as <c>/Items</c> returns them.</param>
/// <param name="TotalRecordCount">The total: the Residual path's result count, or the Fast path's SQL count (as
/// <c>/Items</c>' <c>TotalRecordCount</c> is, so the pages can hold fewer items when <c>IsVisible</c> refuses some);
/// <c>null</c> when not asked for.</param>
/// <param name="StartIndex">The page's start.</param>
/// <param name="ResultToken">The token for the next pages.</param>
/// <param name="Path">The plan's path.</param>
/// <param name="Warnings"><c>Reevaluated</c> (the token had expired, the result was evaluated again) and
/// <c>ItemsChanged</c> (a cached id no longer loads).</param>
/// <param name="Elapsed">The time the call took.</param>
/// <param name="HasMore">Whether a later page has results.</param>
/// <param name="Dropped">The page's ids the hydration left out (gone, owned, or refused by <c>IsVisible</c>).</param>
/// <param name="CandidateCount">The candidates the Residual path checked; 0 on the others.</param>
internal sealed record SmartSearchPage(
    IReadOnlyList<BaseItemDto> Items,
    int? TotalRecordCount,
    int StartIndex,
    string ResultToken,
    QueryPlanPath Path,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed,
    bool HasMore,
    int Dropped,
    int CandidateCount);
