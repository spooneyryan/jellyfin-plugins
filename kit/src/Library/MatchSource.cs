namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Which step of <see cref="IdentityResolver"/> found a candidate, in the order the steps run.
/// </summary>
internal enum MatchSource
{
    /// <summary>No step found it.</summary>
    None,

    /// <summary><c>GetItemById</c> of the snapshot's own id: the item is still there (<see cref="MatchStrength.Exact"/>).</summary>
    Id,

    /// <summary>
    /// The move journal: the chain of Committed lines from the snapshot's id led to it, by id or by the last line's
    /// <c>NewPath</c> (<see cref="MatchStrength.Exact"/>).
    /// </summary>
    Journal,

    /// <summary><c>FindByPath</c> of the snapshot's path (<see cref="MatchStrength.Exact"/>).</summary>
    Path,

    /// <summary>The item's own provider ids (<see cref="MatchStrength.Strong"/>, or weak on a conflicting id).</summary>
    ProviderIds,

    /// <summary>
    /// For episodes and seasons: the series' provider ids plus the season and episode numbers
    /// (<see cref="MatchStrength.Strong"/>).
    /// </summary>
    SeriesNumbers,

    /// <summary>The same kind, name and year, or series name and numbers (<see cref="MatchStrength.Weak"/>).</summary>
    Name
}
