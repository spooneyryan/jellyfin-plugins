using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// A hold and its approval: the version follows the held set, an approval covers that set or less, never more dead links
/// and never an emptying run unless the approved hold was one; a set too large to list is covered only as a whole. Also
/// the previews a save can name (one definition's only) and the pass cache's limit (the candidate lists count).
/// </summary>
public sealed class AutoCollectionPendingGuardTests
{
    private static readonly DateTime Then = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly GuardVerdict Scan = new([GuardReasons.ScanRunning], 3, []);
    private static readonly GuardVerdict Emptying = new([GuardReasons.Empty], 3, []);

    [Fact]
    public void TheVersion_StaysForTheSameSet_AndMovesOnForAnyOther()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var first = AutoCollectionPendingGuard.Of(null, [a, b], 1, Scan, Then);
        Assert.Equal(Then, first.DetectedUtc);

        Assert.Equal(Then, AutoCollectionPendingGuard.Of(first, [b, a], 1, Scan, Then.AddMinutes(5)).DetectedUtc);
        Assert.Equal(Then.AddMinutes(5), AutoCollectionPendingGuard.Of(first, [a, b, Guid.NewGuid()], 1, Scan, Then.AddMinutes(5)).DetectedUtc);
        Assert.Equal(Then.AddMinutes(5), AutoCollectionPendingGuard.Of(first, [a, b], 2, Scan, Then.AddMinutes(5)).DetectedUtc);
        Assert.Equal(Then.AddMinutes(5), AutoCollectionPendingGuard.Of(first, [a, b], 1, Emptying, Then.AddMinutes(5)).DetectedUtc);

        // A clock that didn't move still makes a later version.
        Assert.True(AutoCollectionPendingGuard.Of(first, [a], 1, Scan, Then).DetectedUtc > Then);
    }

    [Fact]
    public void AnApproval_CoversTheApprovedSetOrLess_AndNothingMore()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var hold = AutoCollectionPendingGuard.Of(null, [a, b], 2, Scan, Then);
        Assert.False(hold.Covers([a, b], 2, false));

        var approved = hold with { ApprovedUtc = Then };
        Assert.True(approved.Covers([a, b], 2, false));
        Assert.True(approved.Covers([b], 0, false));
        Assert.False(approved.Covers([a, b, Guid.NewGuid()], 2, false));
        Assert.False(approved.Covers([a], 3, false));
        Assert.False(approved.Covers([a, b], 2, true));
    }

    [Fact]
    public void AnApprovedEmptyHold_CoversTheEmptyingRun()
    {
        var a = Guid.NewGuid();
        var approved = AutoCollectionPendingGuard.Of(null, [a], 0, Emptying, Then) with { ApprovedUtc = Then };
        Assert.True(approved.Covers([a], 0, true));
        Assert.False(approved.Covers([a, Guid.NewGuid()], 0, true));
    }

    [Fact]
    public void AHoldTooLargeToList_IsCoveredOnlyAsAWhole()
    {
        var ids = Enumerable.Range(0, AutoCollectionPendingGuard.MaxHeldIds + 5).Select(_ => Guid.NewGuid()).ToList();
        var approved = AutoCollectionPendingGuard.Of(null, ids, 0, Scan, Then) with { ApprovedUtc = Then };
        Assert.False(approved.Complete);
        Assert.Equal(AutoCollectionPendingGuard.MaxHeldIds, approved.WouldRemove.Count);
        Assert.Equal(ids.Count, approved.HeldMembers);

        Assert.True(approved.Covers(ids, 0, false));
        Assert.False(approved.Covers(ids.Skip(1).ToList(), 0, false));
    }

    [Fact]
    public void AHoldAnOlderBuildStored_IsCoveredByNoApproval()
    {
        var a = Guid.NewGuid();
        var old = new AutoCollectionPendingGuard { DetectedUtc = Then, WouldRemove = [a], WouldRemoveCount = 1, Reasons = [GuardReasons.ScanRunning], ApprovedUtc = Then };
        Assert.False(old.Covers([a], 0, false));
    }

    [Fact]
    public void APreview_IsFoundOnlyForTheDefinitionItWasOf()
    {
        var ledger = new PreviewLedger(TimeProvider.System);
        var mine = Guid.NewGuid();
        IReadOnlyList<Guid> ids = [Guid.NewGuid(), Guid.NewGuid()];
        var hash = MembershipHash.Of(ids);
        ledger.Record(hash, ids, 3, mine);

        Assert.Equal(3, ledger.Find(hash, mine)!.Value.WouldRemove);
        Assert.Null(ledger.Find(hash, Guid.NewGuid()));
        Assert.Null(ledger.Find(hash, null));
        Assert.False(ledger.Matches(hash, [.. ids, Guid.NewGuid()], Guid.NewGuid()));
        Assert.True(ledger.Matches(hash, [.. ids, Guid.NewGuid()], mine));
    }

    [Fact]
    public void ThePassCache_CountsTheCandidateLists_AgainstItsLimit()
    {
        var cache = new PassItemCache(10);
        var loads = 0;
        IReadOnlyList<Guid> Eight() { loads++; return Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList(); }

        cache.Candidates("a", Eight);
        cache.Candidates("a", Eight);
        Assert.Equal(1, loads);
        Assert.Equal(8, cache.Count);

        // The second list doesn't fit: it is read each time, never kept.
        cache.Candidates("b", Eight);
        cache.Candidates("b", Eight);
        Assert.Equal(3, loads);
        Assert.Equal(8, cache.Count);
        Assert.False(cache.CanHold("scope", [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]));
    }
}
