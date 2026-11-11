using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A duplicate group: two or more entries with the same title key (and year, in Title + Year), the keep
/// first.
/// </summary>
public sealed class LibraryMaintenanceDuplicateGroupDto
{
    /// <summary>
    /// Gets or sets the group's id: 16 hex digits from the mode and the entries; it changes when the members change.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the title shown: the suggested keep's name.
    /// </summary>
    public string DisplayTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the key the entries share: the title key, then <c>|</c> and the year (<c>?</c> when unknown) in
    /// Title + Year.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the confidence: <c>Confirmed</c>, <c>Likely</c>, <c>Possible</c> or <c>Conflict</c>; a display and
    /// sort aid only.
    /// </summary>
    public string Confidence { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets why the entries are a group: <c>TitleAndYear</c> or <c>Title</c>, and <c>SameTmdbId</c>,
    /// <c>SameImdbId</c>, <c>UnknownYearMatch</c>.
    /// </summary>
    public IReadOnlyList<string> Reasons { get; set; } = [];

    /// <summary>
    /// Gets or sets what differs or needs a look, such as <c>YearsDiffer</c> or <c>CrossLibrary</c>.
    /// </summary>
    public IReadOnlyList<string> Flags { get; set; } = [];

    /// <summary>
    /// Gets or sets the entries' known years, ascending.
    /// </summary>
    public IReadOnlyList<int> Years { get; set; } = [];

    /// <summary>
    /// Gets or sets the suggested keep's item id (advisory only): the first entry's.
    /// </summary>
    public Guid SuggestedKeepId { get; set; }

    /// <summary>
    /// Gets or sets the bytes of every entry but the suggested keep.
    /// </summary>
    public long ReclaimableBytes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="ReclaimableBytes"/> is a lower bound: a size is unknown, or an
    /// entry has local alternates, stacked parts or versions the index doesn't hold.
    /// </summary>
    public bool ReclaimableIsLowerBound { get; set; }

    /// <summary>
    /// Gets or sets the group's ignore state: <c>None</c> before 1.0.x's ignore marks.
    /// </summary>
    public string IgnoreState { get; set; } = "None";

    /// <summary>
    /// Gets or sets the entries, the suggested keep first, then in the keep's order.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateGroupEntryDto> Entries { get; set; } = [];
}
