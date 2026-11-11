using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What the mass-removal guard decided about a run's removals.</summary>
/// <param name="Reasons">Why they are held (<see cref="GuardReasons"/>); empty when they may go.</param>
/// <param name="Removals">The removals counted: members removed plus dead links.</param>
/// <param name="Locations">The library folders that couldn't be reached.</param>
internal sealed record GuardVerdict(IReadOnlyList<string> Reasons, int Removals, IReadOnlyList<string> Locations)
{
    /// <summary>Gets a verdict that holds nothing.</summary>
    public static GuardVerdict Pass { get; } = new([], 0, []);

    /// <summary>Gets a value indicating whether removals are held.</summary>
    public bool Tripped => Reasons.Count > 0;

    /// <summary>Gets a value indicating whether the whole write is held: the collection would be emptied.</summary>
    public bool Empty => Reasons.Contains(GuardReasons.Empty);
}
