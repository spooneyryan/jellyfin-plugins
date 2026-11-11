using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Plugin.Kit.Tests.Library.MoveJournalTests;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// The parent-only-event test: removing a series deletes its seasons and episodes but raises <c>ItemRemoved</c> once,
/// for the series (<c>LibraryManager.cs:409-466</c>). Episode records detach through the container ids of their
/// snapshots (<see cref="ItemIdentity.IsRemovedBy"/>); a record without them is detached by the queued verify sweep
/// (<see cref="AttachedRecordVerifier"/>), which leaves a pending journal <c>NewId</c> attached and detaches it after
/// seven days (<see cref="MoveJournal.IsPendingRelink"/>).
/// </summary>
/// <remarks>
/// The removals reach the consumer through the kit's <see cref="LibraryChangeMonitor"/>, flushed as the post-scan task does:
/// the removed series in <c>RemovedFolders</c>, its seasons and episodes nowhere, and the consumer handles the batch
/// in the kit's order, container ids first, then the verify sweep because a folder was removed. The sweep is the kit's
/// own, over the records as an <see cref="IAttachedRecordOwner"/>.
/// </remarks>
public sealed class CascadingRemovalTests : IDisposable
{
    private static readonly DateTime Monday = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeLibrary _library = new();
    private readonly FakeRecordOwner _owner = new() { Name = "episodes" };
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(Monday));
    private readonly TempDirectory _directory = new();
    private readonly Mock<IServerApplicationHost> _host = new();
    private readonly List<IDisposable> _disposables = [];

    public CascadingRemovalTests()
    {
        _host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _directory.Dispose();
    }

    [Fact]
    public async Task RemovingASeries_RaisesOneEvent_AndDetachesItsEpisodesThroughTheirContainerIds()
    {
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var seasons = new[] { _library.AddSeason(series, 1), _library.AddSeason(series, 2) };
        var episodes = seasons.SelectMany(s => new[] { _library.AddEpisode(series, s, 1), _library.AddEpisode(series, s, 2) }).ToList();
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        var records = episodes.Cast<BaseItem>().Append(movie).Select(i => _owner.Add(ItemIdentity.Capture(i, _library.Object))).ToList();

        // A record written before its snapshot had container ids (a 1.0 bookmark before the backfill).
        var legacy = _owner.Add(ItemIdentity.Capture(episodes[3], _library.Object) with { SeriesId = null, SeasonId = null });
        var consumer = new Consumer(_owner, Verifier());
        var monitor = Monitor(consumer);

        _library.Remove(series);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, TestContext.Current.CancellationToken);

        // One event, for the series only; the consumer takes episodes and movies, and the series comes as a removed folder.
        var batch = Assert.Single(consumer.Batches);
        Assert.Empty(batch.Removed);
        Assert.Equal(new[] { series.Id }, batch.RemovedFolders.Select(c => c.ItemId));
        Assert.All(episodes, e => Assert.Null(_library.Object.GetItemById(e.Id)));
        Assert.All(seasons, s => Assert.Null(_library.Object.GetItemById(s.Id)));
        Assert.All(records.Take(episodes.Count), r => Assert.Equal("Container", r.DetachedBy));
        Assert.Null(records[^1].DetachedUtc);

        // The container ids miss the legacy record; the verify sweep the removed folder triggers finds it, in the same batch.
        Assert.Equal("Sweep", legacy.DetachedBy);
        var sweep = Assert.Single(consumer.Sweeps);
        Assert.Equal(new[] { VerifySweepReason.RemovedFolders }, sweep.Reasons);
        Assert.Equal(1, sweep.Detached);
    }

    [Fact]
    public async Task RemovingASeason_DetachesOnlyItsEpisodes()
    {
        var series = _library.AddSeries("Test Series", null, ("Tvdb", "900401"));
        var one = _library.AddSeason(series, 1);
        var two = _library.AddSeason(series, 2);
        var inOne = _owner.Add(ItemIdentity.Capture(_library.AddEpisode(series, one, 1), _library.Object));
        var inTwo = _owner.Add(ItemIdentity.Capture(_library.AddEpisode(series, two, 1), _library.Object));
        var consumer = new Consumer(_owner, Verifier());
        var monitor = Monitor(consumer);

        _library.Remove(two);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { two.Id }, consumer.Batches.Single().RemovedFolders.Select(c => c.ItemId));
        Assert.Null(inOne.DetachedUtc);
        Assert.Equal("Container", inTwo.DetachedBy);
    }

    [Fact]
    public async Task TheSweep_SkipsAPendingJournalNewId_AndDetachesItAfterSevenDays()
    {
        // A record relinked from the journal to a predicted id that no scan has picked up yet.
        File.WriteAllLines(_directory.Combine("moves.jsonl"), [Header(), Line(1, A, B, utc: Monday)]);
        var pending = _owner.Add(new IdentitySnapshot { ItemId = B, Kind = "Movie", Name = "Heat" });
        var missing = _owner.Add(new IdentitySnapshot { ItemId = C, Kind = "Movie", Name = "Gone" });
        var verifier = Verifier();

        _clock.Set(new DateTimeOffset(Monday.AddDays(6)));
        await verifier.SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);
        Assert.Null(pending.DetachedUtc);
        Assert.NotNull(missing.DetachedUtc);

        _clock.Set(new DateTimeOffset(Monday.AddDays(7)));
        await verifier.SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);
        Assert.NotNull(pending.DetachedUtc);
    }

    [Fact]
    public async Task TheSweep_LeavesAPendingRelinkAttached_OnceTheScanCreatesTheItem()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        File.WriteAllLines(_directory.Combine("moves.jsonl"), [Header(), Line(1, A, movie.Id, utc: Monday)]);
        var relinked = _owner.Add(new IdentitySnapshot { ItemId = movie.Id, Kind = "Movie", Name = "Heat" });
        _clock.Set(new DateTimeOffset(Monday.AddDays(30)));

        await Verifier().SweepAsync(VerifySweepReason.Daily, TestContext.Current.CancellationToken);

        Assert.Null(relinked.DetachedUtc);
    }

    private AttachedRecordVerifier Verifier()
    {
        var verifier = new AttachedRecordVerifier(
            _host.Object,
            _library.Object,
            new MoveJournalReader(_directory.Combine("moves.jsonl"), NullLogger.Instance),
            () => [_owner],
            NullLogger.Instance,
            _clock);
        _disposables.Add(verifier);
        return verifier;
    }

    private LibraryChangeMonitor Monitor(Consumer consumer)
    {
        var monitor = new LibraryChangeMonitor(_host.Object, _library.Object, null, () => [consumer], TestKit.Options, NullLoggerFactory.Instance);
        _disposables.Add(monitor);
        monitor.Subscribe();
        return monitor;
    }

    // A library-change consumer of episode and movie records, reduced to the kit's order: detach every record a removed
    // item takes with it (its item or a container), then await the verify sweep when a folder was removed.
    private sealed class Consumer(FakeRecordOwner owner, AttachedRecordVerifier verifier) : ILibraryChangeConsumer
    {
        public string Name => "Records";

        public LibraryChangeFilter Filter { get; } = new() { Kinds = new HashSet<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.Episode } };

        public List<LibraryChangeBatch> Batches { get; } = [];

        public List<VerifySweepResult> Sweeps { get; } = [];

        public async Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
        {
            Batches.Add(batch);
            foreach (var removed in batch.Removed.Concat(batch.RemovedFolders).Select(c => c.ItemId).Distinct())
            {
                foreach (var record in owner.Records.Where(r => r.DetachedUtc is null && ItemIdentity.IsRemovedBy(r.Identity, removed)))
                {
                    record.DetachedUtc = Monday;
                    record.DetachedBy = "Container";
                }
            }

            if (batch.RemovedFolders.Count > 0)
            {
                Sweeps.Add(await verifier.SweepAsync(VerifySweepReason.RemovedFolders, cancellationToken).ConfigureAwait(false));
            }
        }
    }
}
