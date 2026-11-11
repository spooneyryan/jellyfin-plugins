using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>
/// The guard's matrix: emptying is held on every run unless allowed; a large removal is held only with a health signal
/// (a run started during a scan, a library folder missing, a candidate drop, the startup pass); thresholds, the
/// definition's own percent, the switch, the just-edited relaxation and a manager's approval.
/// </summary>
public sealed class MassRemovalGuardTests
{
    private static readonly Guid Movies = Guid.NewGuid();
    private static readonly Guid Shows = Guid.NewGuid();
    private const string Hash = "sha256:query";
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly MassRemovalGuard _guard;

    public MassRemovalGuardTests()
    {
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { Name = "Movies", ItemId = Movies.ToString("N"), Locations = ["/media/movies-a", "/media/movies-b"] },
            new VirtualFolderInfo { Name = "Shows", ItemId = Shows.ToString("N"), Locations = ["/media/shows"] }
        ]);
        _guard = new MassRemovalGuard(library.Object, path => !_missing.Contains(path));
    }

    // Current members, removals, and whether that is large: at least max(5, 50% of current).
    public static TheoryData<int, int, bool> Sizes => new()
    {
        { 10, 4, false },
        { 10, 5, true },
        { 10, 6, true },
        { 100, 49, false },
        { 100, 50, true },
        { 3, 3, false },
        { 8, 5, true }
    };

    [Fact]
    public void NoRemovals_PassWhateverTheSignals()
    {
        var verdict = Check(current: 10, removals: 0, desired: 10, conditions: new(ScanRunning: true), reason: SmartSearchAutoCollectionRunReason.Startup);
        Assert.False(verdict.Tripped);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Emptying_IsHeld_UnlessTheDefinitionAllowsIt_OrTheSettingIsOff(bool allowEmpty, bool blockEmpty, bool held)
    {
        var definition = Definition(allowEmpty: allowEmpty);
        var verdict = Check(current: 2, removals: 2, desired: 0, definition: definition, settings: new AutoCollectionSettings { GuardBlockEmpty = blockEmpty });
        Assert.Equal(held, verdict.Tripped);
        Assert.Equal(held, verdict.Empty);
    }

    [Fact]
    public void Emptying_IsHeld_EvenWithTheGuardOff_JustEdited_OrApproved_TheApprovedHoldDecidesWhatGoes()
    {
        var edited = new AutoCollectionState { AcceptedRemovals = 3 };
        Assert.True(Check(current: 3, removals: 3, desired: 0, previous: edited, settings: new AutoCollectionSettings { GuardEnabled = false }).Empty);

        var approved = new AutoCollectionState { PendingGuard = new AutoCollectionPendingGuard { ApprovedUtc = DateTime.UtcNow } };
        Assert.True(Check(current: 3, removals: 3, desired: 0, previous: approved).Empty);
    }

    [Fact]
    public void TheFirstRunAfterASaveFromItsPreview_IsHeld_WhenItFindsOtherMembers()
    {
        var previous = new AutoCollectionState { AcceptedRemovals = 20, AcceptedResultHash = MembershipHash.Of([Guid.NewGuid()]) };
        Assert.True(Check(30, 20, 10, previous: previous, conditions: new(ScanRunning: true)).Tripped);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void ALargeRemoval_IsHeld_OnlyWithASignal(int current, int removals, bool large)
    {
        Assert.False(Check(current, removals, desired: current - removals + 1).Tripped);
        var withScan = Check(current, removals, desired: current - removals + 1, conditions: new(ScanRunning: true));
        Assert.Equal(large, withScan.Tripped);
        if (large)
        {
            Assert.Equal([GuardReasons.ScanRunning], withScan.Reasons);
            Assert.Equal(removals, withScan.Removals);
        }
    }

    [Fact]
    public void ALegitimateLargeRemoval_AfterAScan_IsApplied()
    {
        // 6 of 10 go, the candidates hold steady, the pass after the scan never sees one running.
        var previous = new AutoCollectionState { LastCandidateCount = 1000, LastQueryHash = Hash };
        var verdict = Check(current: 10, removals: 6, desired: 4, candidates: 994, previous: previous, reason: SmartSearchAutoCollectionRunReason.PostScan);
        Assert.False(verdict.Tripped);
    }

    [Fact]
    public void EachSignal_HoldsALargeRemoval()
    {
        Assert.Equal([GuardReasons.ScanRunning], Check(10, 6, 4, conditions: new(ScanRunning: true)).Reasons);
        Assert.Equal([GuardReasons.StartupPass], Check(10, 6, 4, reason: SmartSearchAutoCollectionRunReason.Startup).Reasons);
        Assert.Equal([GuardReasons.CandidateDrop], Check(10, 6, 4, candidates: 499, previous: new AutoCollectionState { LastCandidateCount = 1000, LastQueryHash = Hash }).Reasons);
        Assert.False(Check(10, 6, 4, candidates: 500, previous: new AutoCollectionState { LastCandidateCount = 1000, LastQueryHash = Hash }).Tripped);
        Assert.False(Check(10, 6, 4, candidates: 499, previous: new AutoCollectionState { LastCandidateCount = 1000, LastQueryHash = Hash }, queryHash: "sha256:changed").Tripped);

        _missing.Add("/media/movies-b");
        var offline = Check(10, 6, 4);
        Assert.Equal([GuardReasons.LocationUnreachable], offline.Reasons);
        Assert.Equal(["/media/movies-b"], offline.Locations);

        // Another library's missing folder isn't this definition's concern.
        _missing.Clear();
        _missing.Add("/media/shows");
        Assert.False(Check(10, 6, 4).Tripped);

        // Several at once.
        Assert.Equal([GuardReasons.ScanRunning, GuardReasons.StartupPass], Check(10, 6, 4, conditions: new(ScanRunning: true), reason: SmartSearchAutoCollectionRunReason.Startup).Reasons);
    }

    [Fact]
    public void TheDefinitionsOwnPercent_AndTheSwitch_Apply()
    {
        var strict = Definition(maxRemovalPercent: 10);
        Assert.True(Check(100, 10, 90, definition: strict, conditions: new(ScanRunning: true)).Tripped);
        Assert.False(Check(100, 9, 91, definition: strict, conditions: new(ScanRunning: true)).Tripped);
        Assert.Equal(10, MassRemovalGuard.Threshold(strict, new AutoCollectionSettings(), 100));
        Assert.Equal(5, MassRemovalGuard.Threshold(strict, new AutoCollectionSettings(), 20));

        Assert.False(Check(10, 6, 4, conditions: new(ScanRunning: true), settings: new AutoCollectionSettings { GuardEnabled = false }).Tripped);
    }

    [Theory]
    [InlineData(20, 20, false)]
    [InlineData(20, 21, false)]
    [InlineData(20, 22, true)]
    [InlineData(100, 105, false)]
    [InlineData(100, 106, true)]
    public void TheFirstRunAfterASaveFromItsPreview_IsntHeld_WhenItRemovesAboutAsMany(int accepted, int removals, bool held)
    {
        var previous = new AutoCollectionState { AcceptedRemovals = accepted };
        Assert.Equal(held, Check(removals + 10, removals, 10, previous: previous, conditions: new(ScanRunning: true)).Tripped);
    }

    private static AutoCollectionDefinition Definition(bool allowEmpty = false, int? maxRemovalPercent = null)
        => new() { Id = Guid.NewGuid(), Name = "Test", Safety = new AutoCollectionSafety { AllowEmpty = allowEmpty, MaxRemovalPercent = maxRemovalPercent } };

    private GuardVerdict Check(
        int current,
        int removals,
        int desired,
        int candidates = 1000,
        AutoCollectionDefinition? definition = null,
        AutoCollectionState? previous = null,
        AutoCollectionSettings? settings = null,
        AutoCollectionRunConditions? conditions = null,
        SmartSearchAutoCollectionRunReason reason = SmartSearchAutoCollectionRunReason.Manual,
        string queryHash = Hash)
    {
        var members = new DesiredMembership(Enumerable.Range(0, desired).Select(_ => Guid.NewGuid()).ToList(), desired, candidates, 0, 0, 0, [Movies], null, null!);
        return _guard.Check(definition ?? Definition(), previous ?? new AutoCollectionState { LastCandidateCount = 1000, LastQueryHash = Hash }, current, removals, members, queryHash, settings ?? new AutoCollectionSettings(), conditions ?? AutoCollectionRunConditions.None, reason);
    }
}
