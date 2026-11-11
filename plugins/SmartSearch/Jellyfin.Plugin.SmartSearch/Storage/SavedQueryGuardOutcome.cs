using System.Collections.Generic;
using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>What a guarded change did or why it was refused.</summary>
/// <param name="Status">The outcome.</param>
/// <param name="References">The references concerned: those the write affects, or those that refuse it.</param>
internal sealed record SavedQueryGuardOutcome(SavedQueryGuardStatus Status, IReadOnlyList<SmartSearchAutoCollectionRefDto> References)
{
    /// <summary>Gets the outcome of a write that touched no reference.</summary>
    public static SavedQueryGuardOutcome Done { get; } = new(SavedQueryGuardStatus.Done, []);
}
