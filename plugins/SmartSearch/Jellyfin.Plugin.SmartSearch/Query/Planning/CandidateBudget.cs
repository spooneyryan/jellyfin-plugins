using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Query.Execution;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// One evaluation's share of <c>MaxCandidates</c>: every id an evaluation loads counts against it,
/// the Residual path's candidate id list and the item sets of <c>Library</c> rules alike. Each load asks for at most
/// <see cref="Remaining"/> + 1 ids, so a load past the cap is seen without reading the rest, and refused.
/// </summary>
internal sealed class CandidateBudget
{
    private int _used;

    /// <summary>
    /// Initializes a new instance of the <see cref="CandidateBudget"/> class.
    /// </summary>
    /// <param name="max">The most ids the evaluation may load.</param>
    /// <param name="kinds">The query's kinds, for the refusal's hint.</param>
    public CandidateBudget(int max, IReadOnlyCollection<BaseItemKind> kinds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        ArgumentNullException.ThrowIfNull(kinds);
        Max = max;
        Kinds = kinds;
    }

    /// <summary>Gets the cap.</summary>
    public int Max { get; }

    /// <summary>Gets the query's kinds.</summary>
    public IReadOnlyCollection<BaseItemKind> Kinds { get; }

    /// <summary>Gets how many ids were loaded.</summary>
    public int Used => Volatile.Read(ref _used);

    /// <summary>Gets how many more ids may be loaded.</summary>
    public int Remaining => Math.Max(0, Max - Used);

    /// <summary>Gets the <c>Limit</c> a load asks for: one more than <see cref="Remaining"/>, so a load past the cap shows.</summary>
    public int NextLimit => Remaining + 1;

    /// <summary>Counts a load's ids, and refuses when the evaluation is past the cap.</summary>
    /// <param name="count">The ids loaded.</param>
    /// <param name="exactCount">When past the cap, how many ids the load would have had without its limit (a count
    /// query), or <c>null</c> to report what was counted.</param>
    /// <exception cref="SmartSearchRefusedException">The evaluation is past the cap (<c>QueryTooBroad</c>).</exception>
    public void Take(int count, Func<int>? exactCount = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var before = Interlocked.Add(ref _used, count) - count;
        if (before + count > Max)
        {
            var total = exactCount is null ? before + count : before + Math.Max(count, exactCount());
            throw SmartSearchRefusedException.QueryTooBroad(total, Max, Kinds);
        }
    }
}
