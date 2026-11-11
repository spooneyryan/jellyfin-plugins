using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The mass-removal guard: decides whether a run's removals (members removed and dead links) may go, or wait for a
/// manager's review.
/// </summary>
/// <remarks>
/// <para><b>Empty</b> (hard, every run): a collection with members or dead links whose result is empty is never written,
/// unless the definition allows being empty or <c>GuardBlockEmpty</c> is off.</para>
/// <para><b>Large removal while unhealthy</b> (soft, unless <c>GuardEnabled</c> is off): removals of at least
/// <c>max(GuardMinRemovals, current × percent)</c> (the definition's own percent, else <c>GuardMaxRemovalPercent</c>),
/// together with a health signal: the run started while a library scan was running (a run by hand; passes after a scan
/// wait for its end and never see one); a folder of a library the definition reads doesn't exist; the same query found
/// less than half the candidates of the last run (after a change of the query the counts aren't compared); or the startup
/// pass.</para>
/// <para><b>Just edited</b>: the soft rule doesn't apply to the first run after a save whose accepted preview removed
/// about as many (within 5%, at least 1) and found the same members. <b>Approved</b> is not decided here: a manager's
/// Apply lets the next run through when its removals are the approved ones or fewer
/// (<see cref="AutoCollectionPendingGuard.Covers"/>).</para>
/// </remarks>
internal sealed class MassRemovalGuard
{
    private readonly ILibraryManager _library;
    private readonly Func<string, bool> _directoryExists;

    /// <summary>
    /// Initializes a new instance of the <see cref="MassRemovalGuard"/> class.
    /// </summary>
    /// <param name="library">The library manager (the libraries' folders).</param>
    /// <param name="directoryExists">Whether a folder exists (<see cref="System.IO.Directory.Exists"/> by default).</param>
    public MassRemovalGuard(ILibraryManager library, Func<string, bool>? directoryExists = null)
    {
        _library = library;
        _directoryExists = directoryExists ?? System.IO.Directory.Exists;
    }

    /// <summary>
    /// Counts a run's scope, the items of its query's kinds in its libraries whatever the rules say: the candidate count
    /// the drop signal compares. A query's own matches would drop with every legitimate removal its rules push into SQL.
    /// </summary>
    /// <param name="desired">The run's desired members.</param>
    /// <returns><paramref name="desired"/> with that count as its <see cref="DesiredMembership.CandidateCount"/>.</returns>
    public DesiredMembership WithScopeCount(DesiredMembership desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var query = desired.Evaluation.Plan.CreateScopeQuery();
        return desired with { CandidateCount = query is null ? 0 : _library.GetCount(query) };
    }

    /// <summary>The fewest removals that count as large for a collection of <paramref name="current"/> members.</summary>
    /// <param name="definition">The definition (its own percent).</param>
    /// <param name="settings">The settings.</param>
    /// <param name="current">The members plus dead links.</param>
    /// <returns>The threshold.</returns>
    public static int Threshold(AutoCollectionDefinition definition, AutoCollectionSettings settings, int current)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(settings);
        var percent = Math.Clamp(definition.Safety.MaxRemovalPercent ?? settings.GuardMaxRemovalPercent, 1, 100);
        return Math.Max(settings.GuardMinRemovals, (int)Math.Ceiling(current * percent / 100.0));
    }

    /// <summary>Decides about a run's removals.</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="previous">Its state before the run.</param>
    /// <param name="current">The collection's members plus dead links now.</param>
    /// <param name="removals">The members the run removes plus the dead links it prunes.</param>
    /// <param name="desired">The run's desired members.</param>
    /// <param name="queryHash">The hash of the query the run evaluated (<see cref="AutoCollectionCheck.QueryHash"/>).</param>
    /// <param name="settings">The settings.</param>
    /// <param name="conditions">What the run knows about the server.</param>
    /// <param name="reason">Why it runs.</param>
    /// <returns>The verdict.</returns>
    public GuardVerdict Check(
        AutoCollectionDefinition definition,
        AutoCollectionState? previous,
        int current,
        int removals,
        DesiredMembership desired,
        string? queryHash,
        AutoCollectionSettings settings,
        AutoCollectionRunConditions conditions,
        SmartSearchAutoCollectionRunReason reason)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(conditions);
        if (removals <= 0)
        {
            return GuardVerdict.Pass;
        }

        if (current > 0 && desired.Ids.Count == 0 && !definition.Safety.AllowEmpty && settings.GuardBlockEmpty)
        {
            return new GuardVerdict([GuardReasons.Empty], removals, []);
        }

        if (!settings.GuardEnabled || removals < Threshold(definition, settings, current) || JustEdited(previous, removals, desired))
        {
            return GuardVerdict.Pass;
        }

        var reasons = new List<string>();
        if (conditions.ScanRunning)
        {
            reasons.Add(GuardReasons.ScanRunning);
        }

        var unreachable = UnreachableLocations(desired.LibraryIds);
        if (unreachable.Count > 0)
        {
            reasons.Add(GuardReasons.LocationUnreachable);
        }

        if (previous?.LastCandidateCount is { } before && before > 0 && desired.CandidateCount * 2L < before
            && queryHash is not null && string.Equals(previous.LastQueryHash, queryHash, StringComparison.Ordinal))
        {
            reasons.Add(GuardReasons.CandidateDrop);
        }

        if (reason == SmartSearchAutoCollectionRunReason.Startup || conditions.StartupPass)
        {
            reasons.Add(GuardReasons.StartupPass);
        }

        return reasons.Count == 0 ? GuardVerdict.Pass : new GuardVerdict(reasons, removals, unreachable);
    }

    // The first run after a save from a preview that removed about as many, and found the same members.
    private static bool JustEdited(AutoCollectionState? previous, int removals, DesiredMembership desired)
        => previous?.AcceptedRemovals is { } accepted
            && Math.Abs(removals - accepted) <= Math.Max(1, (int)Math.Ceiling(accepted * 0.05))
            && (previous.AcceptedResultHash is not { } members || string.Equals(members, MembershipHash.Of(desired.Ids), StringComparison.Ordinal));

    // The folders of the definition's libraries that don't exist (a drive or share gone, a folder renamed).
    private List<string> UnreachableLocations(IReadOnlyList<Guid> libraryIds)
    {
        if (libraryIds.Count == 0)
        {
            return [];
        }

        var wanted = libraryIds.ToHashSet();
        var missing = new List<string>();
        foreach (var folder in _library.GetVirtualFolders() ?? [])
        {
            if (Guid.TryParse(folder.ItemId, out var id) && wanted.Contains(id))
            {
                missing.AddRange((folder.Locations ?? []).Where(l => !string.IsNullOrWhiteSpace(l) && !_directoryExists(l)));
            }
        }

        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
