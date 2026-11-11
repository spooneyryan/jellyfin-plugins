using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// What a unit of work touched (<see cref="IRatingsStore.Changed"/>): the items whose ratings it wrote, raised after its
/// commit and the generation's bump (or after its rollback, when nothing changed: dropping a cache entry for nothing costs one
/// refill). The summary cache drops their entries (<c>SummaryCache</c>).
/// </summary>
internal sealed class RatingsChangedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RatingsChangedEventArgs"/> class.
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <param name="committed">Whether the unit of work committed.</param>
    public RatingsChangedEventArgs(IReadOnlyCollection<Guid> itemIds, bool committed)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        ItemIds = itemIds;
        Committed = committed;
    }

    /// <summary>Gets the items whose ratings the unit of work wrote (before and after, for a row that moved).</summary>
    public IReadOnlyCollection<Guid> ItemIds { get; }

    /// <summary>Gets a value indicating whether the unit of work committed.</summary>
    public bool Committed { get; }
}
