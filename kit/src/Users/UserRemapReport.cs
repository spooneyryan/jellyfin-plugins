using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// The answer of <c>POST /&lt;Route&gt;/Admin/Users/Remap?by=Name&amp;dryRun=true</c>.
/// </summary>
/// <param name="DryRun">Whether nothing was written.</param>
/// <param name="Mapped">The ids given (or to be given) to server users.</param>
/// <param name="Unmatched">The ids left alone.</param>
/// <param name="MovedRecords">How many records moved; 0 in a dry run.</param>
/// <param name="SafetySnapshots">The pre-operation snapshots taken first.</param>
internal sealed record UserRemapReport(bool DryRun, IReadOnlyList<UserRemapEntry> Mapped, IReadOnlyList<UserRemapUnmatched> Unmatched, int MovedRecords, IReadOnlyList<string> SafetySnapshots)
{
    /// <summary>
    /// Gets the plan's token, when the caller asked for a remap bound to its dry run
    /// (<see cref="UserReconciler.RemapByNameAsync(bool, string?, CancellationToken)"/>): a hash of every old id, new
    /// id and record count mapped. The real run is given it back and refuses (409) when the plan is no longer the same.
    /// Null, and left out of <see cref="ToDictionary"/>, otherwise.
    /// </summary>
    public string? PlanToken { get; init; }

    /// <summary>
    /// Describes the report with plain values (for admins only: it holds names).
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary()
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DryRun"] = DryRun,
            ["Mapped"] = Mapped.Select(m => m.ToDictionary()).ToList(),
            ["Unmatched"] = Unmatched.Select(u => u.ToDictionary()).ToList(),
            ["MovedRecords"] = MovedRecords,
            ["SafetySnapshots"] = SafetySnapshots
        };
        if (PlanToken is not null)
        {
            result["PlanToken"] = PlanToken;
        }

        return result;
    }
}
