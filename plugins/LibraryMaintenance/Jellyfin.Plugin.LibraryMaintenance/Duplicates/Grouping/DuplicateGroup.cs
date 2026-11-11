using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

/// <summary>
/// How sure a group is: a display and sort aid that never drives an action. The grouper takes the
/// first rule that matches, in the order Conflict, the Possible cap, Confirmed, Likely, Possible.
/// </summary>
internal enum GroupConfidence
{
    /// <summary>At least two entries share a TMDb or IMDb id, every id is the same, and nothing capped the group.</summary>
    Confirmed = 0,

    /// <summary>The same title key and the same known year.</summary>
    Likely,

    /// <summary>Capped (years or runtimes differ, an unknown-year match), or anything else (unknown years).</summary>
    Possible,

    /// <summary>Two entries carry different TMDb ids, or different IMDb ids.</summary>
    Conflict,
}

/// <summary>
/// A group's flags. <c>ToleranceChain</c> comes with a later year tolerance and <c>NewSinceIgnored</c>
/// with the ignore marks; their bits are kept so that the names don't move.
/// </summary>
[Flags]
internal enum GroupFlags
{
    /// <summary>No flag.</summary>
    None = 0,

    /// <summary>The entries' known years differ.</summary>
    YearsDiffer = 1 << 0,

    /// <summary>An entry has no year.</summary>
    YearUnknown = 1 << 1,

    /// <summary>A chain of years within the tolerance.</summary>
    ToleranceChain = 1 << 2,

    /// <summary>Two entries carry different TMDb ids, or different IMDb ids: the group is a <see cref="GroupConfidence.Conflict"/>.</summary>
    DifferentProviderIds = 1 << 3,

    /// <summary>Entries share an id but their known years differ: "possibly a misidentification" (a remake TMDb resolved
    /// to the same film).</summary>
    SameIdYearsDiffer = 1 << 4,

    /// <summary>The runtimes differ by more than 15 % of the longer and more than 10 minutes, and the editions don't.</summary>
    RuntimesDiffer = 1 << 5,

    /// <summary>The entries' editions differ (one may have none).</summary>
    EditionsDiffer = 1 << 6,

    /// <summary>No one library holds every entry.</summary>
    CrossLibrary = 1 << 7,

    /// <summary>An entry's version set is broken.</summary>
    BrokenMerge = 1 << 8,

    /// <summary>A member is a disc rip or an ISO, not a video file.</summary>
    ContainsDiscRip = 1 << 9,

    /// <summary>A member is a shortcut (<c>.strm</c>).</summary>
    ContainsShortcut = 1 << 10,

    /// <summary>A member is a placeholder (<c>.disc</c>).</summary>
    ContainsPlaceholder = 1 << 11,

    /// <summary>The movies with no year of a title with several years: they group only among themselves.</summary>
    Ambiguous = 1 << 12,

    /// <summary>A movie joined a group after it was ignored.</summary>
    NewSinceIgnored = 1 << 13,
}

/// <summary>
/// Why a group formed (the groups' <c>Reasons</c>): the mode's rule, the ids its entries share, and an unknown-year match.
/// </summary>
[Flags]
internal enum GroupReasons
{
    /// <summary>No reason.</summary>
    None = 0,

    /// <summary>The same title key and the same year.</summary>
    TitleAndYear = 1 << 0,

    /// <summary>The same title key.</summary>
    Title = 1 << 1,

    /// <summary>At least two entries share a TMDb id.</summary>
    SameTmdbId = 1 << 2,

    /// <summary>At least two entries share an IMDb id.</summary>
    SameImdbId = 1 << 3,

    /// <summary>Movies with no year joined the title's only year (<see cref="UnknownYearPolicy.MatchAny"/>).</summary>
    UnknownYearMatch = 1 << 4,
}

/// <summary>
/// A group of duplicate movies: at least two index entries, the suggested keep first.
/// </summary>
/// <param name="Id">The group id: the first 16 hex digits of SHA-256 over the mode and the sorted representative ids.</param>
/// <param name="Mode">The mode it was grouped in.</param>
/// <param name="Key">The title key, with <c>|</c> and the year (or <c>?</c>) in Title + Year mode.</param>
/// <param name="DisplayTitle">The suggested keep's name.</param>
/// <param name="Entries">The entries, the suggested keep first, then the others in the keep's order.</param>
/// <param name="Confidence">How sure the group is.</param>
/// <param name="Flags">Its flags.</param>
/// <param name="Reasons">Why it formed.</param>
/// <param name="Years">The entries' known years, ascending.</param>
/// <param name="ReclaimableBytes">The bytes of every entry but the suggested keep (each set's indexed members).</param>
/// <param name="ReclaimableIsLowerBound">Whether those entries hold bytes the index doesn't know (the page shows "≥").</param>
internal sealed record DuplicateGroup(
    string Id,
    MatchMode Mode,
    string Key,
    string DisplayTitle,
    IReadOnlyList<GroupedEntry> Entries,
    GroupConfidence Confidence,
    GroupFlags Flags,
    GroupReasons Reasons,
    IReadOnlyList<int> Years,
    long ReclaimableBytes,
    bool ReclaimableIsLowerBound)
{
    /// <summary>Gets the suggested keep: advisory only.</summary>
    public GroupedEntry SuggestedKeep => Entries[0];
}
