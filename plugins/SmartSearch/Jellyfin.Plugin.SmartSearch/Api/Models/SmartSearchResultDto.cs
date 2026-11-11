using System.Collections.Generic;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// A page of a search, as the signed-in user may see it: every item passes their library access, parental limit and
/// tags, and merged versions show one row per version set and presentation key, as the library views do.
/// </summary>
public sealed class SmartSearchResultDto
{
    /// <summary>Gets or sets the page's items, as <c>/Items</c> returns them (images, user data).</summary>
    public IReadOnlyList<BaseItemDto> Items { get; set; } = [];

    /// <summary>
    /// Gets or sets the total, or <c>null</c> when it wasn't asked for. For a search the database answers alone it is the
    /// database's count, as <c>/Items</c>' is, so the pages can hold fewer items when the server hides some of them from
    /// the user.
    /// </summary>
    public int? TotalRecordCount { get; set; }

    /// <summary>Gets or sets the index of the page's first result.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the token for the next pages of this search.</summary>
    public string ResultToken { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a later page has results.</summary>
    public bool HasMore { get; set; }

    /// <summary>Gets or sets a value indicating whether a library scan is running (results may miss new items).</summary>
    public bool ScanInProgress { get; set; }

    /// <summary>
    /// Gets or sets the warnings: <c>Reevaluated</c> (the token had expired, the search ran again), <c>ItemsChanged</c>
    /// (an item of the page is gone) and <c>LibrariesUnavailable</c> (a library the query names isn't one the user can
    /// open, or no longer exists).
    /// </summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];

    /// <summary>Gets or sets how long the call took, in milliseconds.</summary>
    public long ElapsedMs { get; set; }
}
