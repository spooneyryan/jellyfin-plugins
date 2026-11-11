using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A page of the clip list.
/// </summary>
/// <param name="Rows">The clips on the page, in order.</param>
/// <param name="Total">How many clips match the filters.</param>
/// <param name="StartIndex">Where the page starts.</param>
internal sealed record ClipPage(IReadOnlyList<ClipRow> Rows, int Total, int StartIndex);
