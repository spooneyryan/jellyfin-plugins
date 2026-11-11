using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.Kit.Tests.Library.MoveJournalTests;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="AttachedRecordVerifier"/>: each distinct attached id looked up once, in
/// batches of 500 with a yield between them; misses detached with their snapshots kept; pending journal relinks left alone
/// for seven days; read-only and failing owners and failing lookups never detach anything; sweeps queued and joined; the
/// startup full pass after the gate.
/// </summary>
public sealed class AttachedRecordVerifierTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly FakeLibrary _library = new();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Monday.AddHours(1)));
    private readonly TempDirectory _directory = new();
    private readonly ListLogger _logger = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly List<AttachedRecordVerifier> _verifiers = [];

    public AttachedRecordVerifierTests()
    {
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
    }

    private string JournalFile => _directory.Combine("moves.jsonl");

    public void Dispose()
    {
        foreach (var verifier in _verifiers)
        {
            verifier.Dispose();
        }

        _directory.Dispose();
    }

    [Fact]
    public async Task ASweep_DetachesTheRecordsOfMissingItems_AndKeepsTheirSnapshots()
    {
        var kept = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var gone = _library.AddMovie("Ronin", 1998, TimeSpan.FromMinutes(122));
        var owner = new FakeRecordOwner();
        var onKept = owner.Add(ItemIdentity.Capture(kept, _library.Object));
        var onGone = owner.Add(ItemIdentity.Capture(gone, _library.Object));
        var alsoOnGone = owner.Add(ItemIdentity.Capture(gone, _library.Object));
        var snapshot = onGone.Identity;
        _library.Remove(gone, raiseEvent: false);

        var result = await Verifier(owner).SweepAsync(VerifySweepReason.Requested, TestContext.Current.CancellationToken);

        Assert.Null(onKept.DetachedUtc);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime, onGone.DetachedUtc);
        Assert.NotNull(alsoOnGone.DetachedUtc);
        Assert.Same(snapshot, onGone.Identity);
        var one = Assert.Single(result.Owners);
        Assert.Equal((2, 1, 2, 0), (one.Checked, one.Missing, one.Detached, one.Pending));
        Assert.Equal(new[] { VerifySweepReason.Requested }, result.Reasons);
        Assert.Equal(new[] { gone.Id }, Assert.Single(owner.DetachCalls));
        Assert.Contains(_logger.At(LogLevel.Information), e => e.Message.Contains("2 records detached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachDistinctIdIsLookedUpOnce_AndNothingIsDetachedWhenAllAreThere()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var owner = new FakeRecordOwner();
        owner.Add(ItemIdentity.Capture(movie, _library.Object));
        owner.Add(ItemIdentity.Capture(movie, _library.Object));
        owner.Add(new IdentitySnapshot { ItemId = Guid.Empty, Kind = "Video", Name = "adopted clip" });

        var result = await Verifier(owner).SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { movie.Id }, _library.IdLookups);
        Assert.Empty(owner.DetachCalls);
        Assert.Equal(0, result.Detached);
        Assert.Empty(_logger.At(LogLevel.Information));
    }

    [Fact]
    public async Task AnAlbumAndTrackStillInTheItemCache_AfterTheirArtistFolderWent_AreMissing_AndTheirLiveNeighboursStay()
    {
        // 10.11.5 deletes a music artist folder's albums and tracks with it, but its item cache still answers GetItemById
        // for them, with their parent links to the artist, which it no longer has (seen in a sandbox run).
        var library = _library.Add(new CollectionFolder { Name = "Music", Path = @"C:\media\Music" });
        var artist = _library.Add(new MusicArtist { Name = "Artist A", Path = @"C:\media\Music\Artist A" }, library);
        var album = _library.Add(new MusicAlbum { Name = "Album A", Path = @"C:\media\Music\Artist A\Album A" }, artist);
        var track = _library.Add(new Audio { Name = "Track 1", Path = @"C:\media\Music\Artist A\Album A\01.flac" }, album);
        var other = _library.Add(new MusicArtist { Name = "Artist B", Path = @"C:\media\Music\Artist B" }, library);
        var kept = _library.Add(new MusicAlbum { Name = "Album B", Path = @"C:\media\Music\Artist B\Album B" }, other);
        var keptTrack = _library.Add(new Audio { Name = "Track 1", Path = @"C:\media\Music\Artist B\Album B\01.flac" }, kept);
        var owner = new FakeRecordOwner();
        var onAlbum = owner.Add(ItemIdentity.Capture(album, _library.Object));
        var onTrack = owner.Add(ItemIdentity.Capture(track, _library.Object));
        var onKept = owner.Add(ItemIdentity.Capture(kept, _library.Object));
        var onKeptTrack = owner.Add(ItemIdentity.Capture(keptTrack, _library.Object));
        _library.Remove(artist, raiseEvent: false, cacheKeepsDescendants: true);
        Assert.Same(album, _library.Object.GetItemById(album.Id));
        Assert.Empty(_library.Object.GetItemList(new() { ItemIds = [album.Id, track.Id] }));
        _library.IdLookups.Clear();

        var result = await Verifier(owner).SweepAsync(VerifySweepReason.RemovedFolders, TestContext.Current.CancellationToken);

        Assert.NotNull(onAlbum.DetachedUtc);
        Assert.NotNull(onTrack.DetachedUtc);
        Assert.Null(onKept.DetachedUtc);
        Assert.Null(onKeptTrack.DetachedUtc);
        Assert.Equal(new[] { album.Id, track.Id }.Order(), Assert.Single(owner.DetachCalls).Order());
        Assert.Equal((4, 2, 2), (result.Owners[0].Checked, result.Owners[0].Missing, result.Detached));

        // Each id once, the library's own folder the last one asked: the walk stops there.
        Assert.Equal(_library.IdLookups.Distinct().Count(), _library.IdLookups.Count);
        Assert.Equal(new[] { album.Id, artist.Id, track.Id, kept.Id, other.Id, library.Id, keptTrack.Id }.Order(), _library.IdLookups.Order());
    }

    [Fact]
    public async Task TheSweepYields_AfterEachBatchOf500()
    {
        // 1,200 ids: the caller gets its task back after the first batch; the 501st lookup waits until it has.
        var owner = new FakeRecordOwner();
        for (var i = 0; i < 1200; i++)
        {
            owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie", Name = "m" + i });
        }

        var returned = new ManualResetEventSlim();
        var returnedBefore501 = false;
        var lookups = 0;
        _library.Mock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid _) =>
        {
            if (Interlocked.Increment(ref lookups) == AttachedRecordVerifier.BatchSize + 1)
            {
                returnedBefore501 = returned.Wait(Wait);
            }

            return null;
        });

        var sweep = Verifier(owner).SweepAsync(VerifySweepReason.Requested, TestContext.Current.CancellationToken);
        returned.Set();
        var result = await sweep.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.True(returnedBefore501, "The sweep didn't yield after its first batch of 500.");
        Assert.Equal(1200, lookups);
        Assert.Equal(1200, result.Detached);
        Assert.Single(owner.DetachCalls);
    }

    [Fact]
    public async Task APendingJournalRelink_StaysAttachedForSevenDays_ThenDetaches()
    {
        // A record relinked from the journal to a predicted id no scan has picked up yet.
        File.WriteAllLines(JournalFile, [Header(), Line(1, A, B, utc: Monday)]);
        var owner = new FakeRecordOwner();
        var pending = owner.Add(new IdentitySnapshot { ItemId = B, Kind = "Movie", Name = "Heat" });
        var gone = owner.Add(new IdentitySnapshot { ItemId = C, Kind = "Movie", Name = "Gone" });
        var verifier = Verifier(owner);

        _clock.Set(new DateTimeOffset(Monday.AddDays(6)));
        var first = await verifier.SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);
        Assert.Null(pending.DetachedUtc);
        Assert.NotNull(gone.DetachedUtc);
        Assert.Equal((1, 1), (first.Owners[0].Pending, first.Owners[0].Detached));
        Assert.Equal(MoveJournalStatus.Ok, first.JournalStatus);

        _clock.Set(new DateTimeOffset(Monday.AddDays(7)));
        var second = await verifier.SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);
        Assert.NotNull(pending.DetachedUtc);
        Assert.Equal(0, second.Owners[0].Pending);
    }

    [Fact]
    public async Task APendingRelinkThatTheScanPickedUp_IsSimplyThere()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        File.WriteAllLines(JournalFile, [Header(), Line(1, A, movie.Id, utc: Monday)]);
        var owner = new FakeRecordOwner();
        var record = owner.Add(ItemIdentity.Capture(movie, _library.Object));
        _clock.Set(new DateTimeOffset(Monday.AddDays(30)));

        await Verifier(owner).SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);

        Assert.Null(record.DetachedUtc);
    }

    [Fact]
    public async Task AReadOnlyOwner_IsLeftAlone_AndAFailingOne_DoesntStopTheOthers()
    {
        var readOnly = new FakeRecordOwner { Name = "readonly", IsReadOnly = true };
        readOnly.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        var failing = new FailingOwner();
        var fine = new FakeRecordOwner { Name = "fine" };
        var record = fine.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });

        var result = await Verifier(readOnly, failing, fine).SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "ReadOnly", "Failed", null }, result.Owners.Select(o => o.Skipped));
        Assert.Empty(readOnly.DetachCalls);
        Assert.Null(readOnly.Records[0].DetachedUtc);
        Assert.NotNull(record.DetachedUtc);
        Assert.Single(_logger.At(LogLevel.Error));
    }

    [Fact]
    public async Task ALookupThatThrows_DetachesNothing_AndWarnsOnce()
    {
        var owner = new FakeRecordOwner();
        var records = Enumerable.Range(0, 3).Select(_ => owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" })).ToList();
        _library.Mock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Throws(new InvalidOperationException("database is locked"));

        var result = await Verifier(owner).SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);

        Assert.All(records, r => Assert.Null(r.DetachedUtc));
        Assert.Equal(3, result.Owners[0].LookupErrors);
        Assert.Empty(owner.DetachCalls);
        Assert.Single(_logger.At(LogLevel.Warning));
    }

    [Fact]
    public async Task RequestsMadeWhileASweepRuns_JoinTheNextSweep_AndNeverGetTheRunningOnesResult()
    {
        var owner = new FakeRecordOwner { DetachGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        var verifier = Verifier(owner);

        var running = verifier.SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => owner.DetachCalls.Count == 1);

        // A folder was removed after the daily sweep had looked: its batch needs a sweep of its own.
        var removedFolders = verifier.SweepAsync(VerifySweepReason.RemovedFolders, TestContext.Current.CancellationToken);
        var requested = verifier.SweepAsync(VerifySweepReason.Requested, TestContext.Current.CancellationToken);
        Assert.Equal(true, verifier.Describe()["Queued"]);
        owner.DetachGate.SetResult();

        var first = await running.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var second = await removedFolders.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Same(second, await requested.WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { VerifySweepReason.Daily }, first.Reasons);
        Assert.Equal(new[] { VerifySweepReason.RemovedFolders, VerifySweepReason.Requested }, second.Reasons);
        Assert.Equal(2L, verifier.Describe()["Sweeps"]);
    }

    [Fact]
    public async Task TheStartupSweep_RunsOnceAfterTheGate_AfterTheWorkBeforeIt_AndTakesWhatWasQueuedBefore()
    {
        var owner = new FakeRecordOwner();
        var record = owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        var order = new List<string>();
        var verifier = Verifier([owner], _ =>
        {
            order.Add("before");
            return Task.CompletedTask;
        });

        verifier.Queue(VerifySweepReason.RemovedFolders);
        await verifier.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Equals(verifier.Describe()["StartupSweepDone"], true) && Equals(verifier.Describe()["Sweeping"], false));
        await verifier.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "before" }, order);
        Assert.NotNull(record.DetachedUtc);
        var sweeps = (IList<IDictionary<string, object?>>)verifier.Describe()["Recent"]!;
        Assert.Contains(sweeps, s => ((IEnumerable<string>)s["Reasons"]!).Contains("Startup"));
        Assert.Contains(sweeps, s => ((IEnumerable<string>)s["Reasons"]!).Contains("RemovedFolders"));
        Assert.Empty(_logger.At(LogLevel.Error));
    }

    [Fact]
    public async Task WithoutOwners_ThereIsNoStartupSweep()
    {
        var verifier = Verifier();

        await verifier.StartAsync(TestContext.Current.CancellationToken);
        await verifier.ExecuteTask!.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(0L, verifier.Describe()["Sweeps"]);
    }

    [Fact]
    public async Task Describe_ListsTheOwnersAndTheSweepsNewestFirst()
    {
        var owner = new FakeRecordOwner { Name = "bookmarks" };
        var verifier = Verifier(owner);

        await verifier.SweepAsync(VerifySweepReason.Startup, TestContext.Current.CancellationToken);
        await verifier.SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);
        var described = verifier.Describe();

        Assert.Equal(new[] { "bookmarks" }, (IEnumerable<string>)described["Owners"]!);
        Assert.Equal(true, described["StartupSweepDone"]);
        var recent = (IList<IDictionary<string, object?>>)described["Recent"]!;
        Assert.Equal(new[] { "Daily" }, (IEnumerable<string>)recent[0]["Reasons"]!);
        Assert.Equal(AttachedRecordVerifier.BatchSize, described["BatchSize"]);
        Assert.Equal(VerifySweepReason.Daily, verifier.LastSweep!.Reasons.Single());
    }

    private sealed class FailingOwner : IAttachedRecordOwner
    {
        public string Name => "failing";

        public bool IsReadOnly => false;

        public IReadOnlyCollection<Guid> GetAttachedItemIds() => throw new InvalidOperationException("broken");

        public Task<int> DetachAsync(IReadOnlyCollection<Guid> itemIds, DateTime detachedUtc, CancellationToken cancellationToken) => throw new InvalidOperationException("broken");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The condition wasn't met in time.");
            }

            await Task.Delay(10);
        }
    }

    private AttachedRecordVerifier Verifier(params IAttachedRecordOwner[] owners) => Verifier(owners, null);

    private AttachedRecordVerifier Verifier(IAttachedRecordOwner[] owners, Func<CancellationToken, Task>? beforeStartup)
    {
        var verifier = new AttachedRecordVerifier(_host.Object, _library.Object, new MoveJournalReader(JournalFile, _logger), () => owners, _logger, _clock, beforeStartup);
        _verifiers.Add(verifier);
        return verifier;
    }
}
