using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A page of duplicate groups (<c>GET /LibraryMaintenance/Duplicates/Groups</c>): the index it was grouped from,
/// the options and libraries, the totals over every group, and the page of the groups the filters keep.
/// </summary>
public sealed class LibraryMaintenanceDuplicateGroupsResultDto
{
    /// <summary>
    /// Gets or sets the index the groups come from.
    /// </summary>
    public LibraryMaintenanceDuplicateGroupsIndexDto Index { get; set; } = new();

    /// <summary>
    /// Gets or sets the match mode: <c>TitleAndYear</c> or <c>Title</c>.
    /// </summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unknown-year policy: <c>MatchUnknownOnly</c> or <c>MatchAny</c>.
    /// </summary>
    public string UnknownYear { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the hash of the options the groups were made with (the mode, the unknown-year policy, the title rules).
    /// </summary>
    public string OptionsHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the libraries grouped: those asked for (all when none) that the caller can see and the index read.
    /// </summary>
    public IReadOnlyList<Guid> EffectiveLibraryIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the movies of the entries in those libraries that have a title to compare.
    /// </summary>
    public int TotalMovies { get; set; }

    /// <summary>
    /// Gets or sets the entries (version sets) in those libraries that have a title to compare.
    /// </summary>
    public int TotalEntries { get; set; }

    /// <summary>
    /// Gets or sets every group in those libraries, before the confidence and text filters.
    /// </summary>
    public int GroupCount { get; set; }

    /// <summary>
    /// Gets or sets the groups hidden by ignore marks (always 0 before 1.0.x).
    /// </summary>
    public int HiddenByIgnore { get; set; }

    /// <summary>
    /// Gets or sets the number of groups the other match mode gives (the page's "Try Title only" hint).
    /// </summary>
    public int AlternateModeGroupCount { get; set; }

    /// <summary>
    /// Gets or sets the reclaimable bytes of every group, before the filters.
    /// </summary>
    public long ReclaimableBytes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="ReclaimableBytes"/> is a lower bound (the page shows "≥"): some
    /// group holds bytes the index doesn't know.
    /// </summary>
    public bool ReclaimableIsLowerBound { get; set; }

    /// <summary>
    /// Gets or sets the index of the page's first group among the groups the filters keep.
    /// </summary>
    public int StartIndex { get; set; }

    /// <summary>
    /// Gets or sets the groups the filters keep.
    /// </summary>
    public int TotalRecordCount { get; set; }

    /// <summary>
    /// Gets or sets the page's groups.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateGroupDto> Items { get; set; } = [];
}
