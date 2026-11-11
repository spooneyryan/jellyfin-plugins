using System;
using Jellyfin.Plugin.SmartSearch.Query.Execution;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>What a run shares with the pass it belongs to, and what it knows about the server when it starts.</summary>
/// <param name="ItemCache">The candidates already loaded in the pass, or <c>null</c>.</param>
/// <param name="PassUtc">The pass's time, which relative dates are measured from, or <c>null</c> for the run's own.</param>
/// <param name="ScanRunning">Whether a library scan was running when the run (or its pass) started.</param>
/// <param name="Audience">The mixed-audience answers kept for the pass, or <c>null</c>.</param>
/// <param name="StartupPass">Whether the run belongs to the pass after the server started, whatever else that pass also serves.</param>
internal sealed record AutoCollectionRunConditions(PassItemCache? ItemCache = null, DateTime? PassUtc = null, bool ScanRunning = false, AudienceMemo? Audience = null, bool StartupPass = false)
{
    /// <summary>Gets the conditions of a run on its own, outside a pass, with no scan running.</summary>
    public static AutoCollectionRunConditions None { get; } = new();
}
