using System;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Operations;

/// <summary>
/// The gate Move and Duplicates share: one operation at a time,
/// refused while a scan runs except inside the post-scan task, and the per-item busy set, all or none.
/// </summary>
public sealed class LibraryOperationGateTests
{
    private static readonly Guid _a = new("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid _b = new("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid _c = new("a0000000-0000-0000-0000-000000000003");

    [Fact]
    public void TheOperationLock_IsOneAtATime_AndReleasedOnce()
    {
        using var gate = new LibraryOperationGate(new FakeLibrary().Object);
        var first = gate.TryEnter("job 1 Committing");
        Assert.NotNull(first);
        Assert.Equal("job 1 Committing", gate.CurrentOperation);
        Assert.Null(gate.TryEnter("duplicates merge"));

        first.Dispose();
        first.Dispose();
        Assert.Null(gate.CurrentOperation);
        using var second = gate.TryEnter("duplicates merge");
        Assert.NotNull(second);
        Assert.Null(gate.TryEnter("job 2"));
    }

    [Fact]
    public void TheOperationLock_IsRefusedWhileAScanRuns_ExceptInThePostScanTask()
    {
        var library = new FakeLibrary { IsScanRunning = true };
        using var gate = new LibraryOperationGate(library.Object);
        Assert.True(gate.IsScanRunning);
        Assert.Null(gate.TryEnter("job 1"));
        using var lease = gate.TryEnter("post-scan", duringPostScan: true);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task EnterAsync_WaitsForTheLock_OrTimesOut()
    {
        using var gate = new LibraryOperationGate(new FakeLibrary().Object);
        var held = gate.TryEnter("job 1")!;
        Assert.Null(await gate.EnterAsync("job 2", TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken));

        var waiting = gate.EnterAsync("job 2", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        held.Dispose();
        using var lease = await waiting;
        Assert.Equal("job 2", lease?.Operation);
    }

    [Fact]
    public async Task EnterAsync_DoesNotLookAtAScan_SoTheMoveJobMustWaitForOrRefuseOne()
    {
        // Only TryEnter refuses during a scan. The worker's EnterAsync takes the lock while a scan
        // runs, so the move job waits a scan out itself.
        var library = new FakeLibrary { IsScanRunning = true };
        using var gate = new LibraryOperationGate(library.Object);
        Assert.Null(gate.TryEnter("duplicates merge"));
        using var lease = await gate.EnterAsync("job 1", TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        Assert.NotNull(lease);
    }

    [Fact]
    public void TheBusySet_IsAllOrNone_PerOperation()
    {
        using var gate = new LibraryOperationGate(new FakeLibrary().Object);
        Assert.True(gate.TryMarkBusy(OperationOwner.Duplicates, "merge-1", [_a, _b, Guid.Empty], out var none));
        Assert.Null(none);

        // Another operation may not take any of them, and gets none of the rest.
        Assert.False(gate.TryMarkBusy(OperationOwner.Move, "job-1", [_c, _b], out var conflict));
        Assert.Equal(new BusyItem(_b, OperationOwner.Duplicates, "merge-1"), conflict);
        Assert.Null(gate.FindBusy([_c]));

        // The same operation may mark again; its own entries don't count for it.
        Assert.True(gate.TryMarkBusy(OperationOwner.Duplicates, "merge-1", [_a, _c], out _));
        Assert.Null(gate.FindBusy([_a, _b, _c], exceptOperationId: "merge-1"));
        Assert.Equal(OperationOwner.Duplicates, gate.FindBusy([Guid.NewGuid(), _c])?.Owner);
        Assert.Equal(3, gate.Busy().Count);

        Assert.Equal(3, gate.Release("merge-1"));
        Assert.Empty(gate.Busy());
        Assert.True(gate.TryMarkBusy(OperationOwner.Move, "job-1", [_c, _b], out _));
    }

    [Fact]
    public void BusyMessages_NameTheFeatureHoldingTheItem()
    {
        Assert.Equal("This item is being moved.", LibraryOperationGate.BusyMessage(OperationOwner.Move));
        Assert.Equal("This item is being changed on the Duplicates tab.", LibraryOperationGate.BusyMessage(OperationOwner.Duplicates));
    }
}
