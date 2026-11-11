using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// The post-scan pattern: Jellyfin creates a plugin's post-scan task once and runs the same
/// instance inside every scan, while <c>IsScanRunning</c> is true. The kit's base flushes the monitor as one full pass per
/// scan, keeps no state of its own, and returns at once for an inert build or without a monitor. Jellyfin's own post-scan
/// tasks run after it, so their changes come in the batch right after the scan (<c>AfterFullPass</c>).
/// </summary>
public sealed class KitPostScanTaskBaseTests
{
    private readonly FakeLibrary _library = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSameInstance_FlushesExactlyOneFullPassPerScan_WhileTheScanRuns()
    {
        var consumer = new RecordingConsumer("Consumer", library: _library.Object);
        using var provider = Services(consumer, active: true);
        var task = new FxPostScanTask(provider);

        // Scan 1: a movie added while the scan runs, then the post-scan tasks, inside the scan.
        _library.IsScanRunning = true;
        var first = _library.AddMovie("First", 2001, TimeSpan.FromMinutes(90));
        _library.RaiseAdded(first);
        await task.Run(new ListProgress(), Ct);
        _library.IsScanRunning = false;

        // Scan 2 with the same instance: nothing changed, still one full pass.
        _library.IsScanRunning = true;
        await task.Run(new ListProgress(), Ct);
        _library.IsScanRunning = false;

        // Scan 3: a removal.
        _library.IsScanRunning = true;
        _library.Remove(first);
        await task.Run(new ListProgress(), Ct);
        _library.IsScanRunning = false;

        Assert.Equal(3, consumer.Received.Count);
        Assert.All(consumer.Received, r => Assert.True(r.Batch.FullPass && r.Batch.DuringPostScan && r.ScanRunning));
        Assert.Equal([first.Id], consumer.Batches[0].Added.Select(c => c.ItemId));
        Assert.True(consumer.Batches[1].IsEmpty);
        Assert.Equal([first.Id], consumer.Batches[2].Removed.Select(c => c.ItemId));
        Assert.Equal(3, provider.GetRequiredService<LibraryChangeMonitor>().Queues[0].FullPasses);
    }

    [Fact]
    public async Task JellyfinsOwnPostScanTasksRunAfterOurs_SoTheirChangesComeAsTheBatchRightAfterTheScan()
    {
        // Jellyfin 10.11.5 lists the plugins' post-scan tasks first (ApplicationHost.cs:822-826, LibraryManager.cs:263) and
        // runs them in that order inside the scan (LibraryManager.cs:1220-1265): ours, then Artists, Genres, MusicGenres,
        // Studios, Collection, CleanDatabase and Splashscreen.
        var consumer = new RecordingConsumer("Consumer", library: _library.Object);
        using var provider = Services(consumer, active: true);
        var monitor = provider.GetRequiredService<LibraryChangeMonitor>();
        var movie = _library.AddMovie("Scanned", 2001, TimeSpan.FromMinutes(90));
        var artist = _library.Add(new MusicArtist { Name = "Dead Artist" });
        var genre = _library.Add(new Genre { Name = "New Genre" });
        ILibraryPostScanTask[] postScanTasks =
        [
            new FxPostScanTask(provider),
            new CoreLikePostScanTask(() =>
            {
                _library.Remove(artist);        // ArtistsValidator deletes a dead artist by name (no folder)
                _library.RaiseAdded(genre);     // GenresValidator creates a genre
            }),
        ];

        _library.IsScanRunning = true;
        _library.RaiseAdded(movie);
        foreach (var task in postScanTasks)
        {
            await task.Run(new ListProgress(), Ct);
        }

        // The full pass has only what the scan's validation did; the rest waits while the scan runs.
        var full = Assert.Single(consumer.Batches);
        Assert.True(full.FullPass);
        Assert.Equal([movie.Id], full.Added.Select(c => c.ItemId));
        Assert.Empty(full.RemovedFolders);
        Assert.Equal(LibraryChangeMonitor.ScanEndRecheckInterval, await monitor.DispatchDueAsync(Ct));
        Assert.Single(consumer.Batches);

        _library.IsScanRunning = false;
        await monitor.DispatchDueAsync(Ct);

        var after = consumer.Received[1];
        Assert.True(after.Batch.AfterFullPass && !after.Batch.FullPass && !after.Batch.DuringPostScan && !after.ScanRunning);
        Assert.Equal([artist.Id], after.Batch.Removed.Select(c => c.ItemId));
        Assert.Empty(after.Batch.RemovedFolders);
        Assert.Equal([genre.Id], after.Batch.Added.Select(c => c.ItemId));
    }

    [Fact]
    public void TheTask_KeepsNoStateOfItsOwn()
    {
        // Only the service provider: the monitor is resolved on every run.
        var fields = typeof(KitPostScanTaskBase).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Concat(typeof(FxPostScanTask).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));

        var field = Assert.Single(fields);
        Assert.Equal(typeof(IServiceProvider), field.FieldType);
        Assert.True(field.IsInitOnly);
    }

    [Fact]
    public async Task ItReportsProgress_ConsumerByConsumer()
    {
        using var provider = Services([new RecordingConsumer("One"), new RecordingConsumer("Two")], active: true);
        var progress = new ListProgress();

        await new FxPostScanTask(provider).Run(progress, Ct);

        Assert.Equal([50.0, 100.0], progress.Values);
    }

    [Fact]
    public async Task ACancelledScan_ReachesTheConsumer()
    {
        var consumer = new RecordingConsumer("Slow") { OnHandle = (_, token) => Task.Delay(Timeout.Infinite, token) };
        using var provider = Services(consumer, active: true);
        using var scan = new CancellationTokenSource();

        var run = new FxPostScanTask(provider).Run(new ListProgress(), scan.Token);
        await consumer.WaitForBatchAsync(1);
        await scan.CancelAsync();

        // LibraryManager.RunPostScanTasks logs "Post-scan task cancelled" and rethrows: the scan ends cancelled.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AnInertBuild_ReturnsAtOnce_WithoutResolvingTheMonitor(bool? active)
    {
        var resolved = false;
        var services = new ServiceCollection().AddSingleton<LibraryChangeMonitor>(_ =>
        {
            resolved = true;
            throw new InvalidOperationException("An inert build registers no monitor.");
        });
        if (active is { } isActive)
        {
            services.AddSingleton(new KitAbiStatus("Kit Test", "10.11", "12.1.0", isActive, "Inactive."));
        }

        using var provider = services.BuildServiceProvider();
        var progress = new ListProgress();

        await new FxPostScanTask(provider).Run(progress, Ct);

        Assert.False(resolved);
        Assert.Equal([100.0], progress.Values);
    }

    [Fact]
    public async Task WithoutAMonitor_ItReturnsAtOnce()
    {
        using var provider = new ServiceCollection().AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", true, "Active.")).BuildServiceProvider();

        await new FxPostScanTask(provider).Run(new ListProgress(), Ct);
    }

    [Fact]
    public void TheBase_IsAPublicAbstractPostScanTask_ThatAPluginSealsAndJellyfinCreatesWithTheServiceProvider()
    {
        Assert.True(typeof(KitPostScanTaskBase).IsPublic);
        Assert.True(typeof(KitPostScanTaskBase).IsAbstract);
        Assert.True(typeof(ILibraryPostScanTask).IsAssignableFrom(typeof(KitPostScanTaskBase)));
        var constructor = Assert.Single(typeof(KitPostScanTaskBase).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
        Assert.True(constructor.IsFamily);
        Assert.Equal([typeof(IServiceProvider)], constructor.GetParameters().Select(p => p.ParameterType));
        Assert.Throws<ArgumentNullException>(() => new FxPostScanTask(null!));

        // Jellyfin creates it with ActivatorUtilities over its root provider (ApplicationHost.CreateInstanceSafe).
        using var provider = new ServiceCollection().BuildServiceProvider();
        Assert.IsType<FxPostScanTask>(ActivatorUtilities.CreateInstance(provider, typeof(FxPostScanTask)));
    }

    private ServiceProvider Services(RecordingConsumer consumer, bool active) => Services([consumer], active);

    private ServiceProvider Services(RecordingConsumer[] consumers, bool active)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var services = new ServiceCollection()
            .AddSingleton(host.Object)
            .AddSingleton(_library.Object)
            .AddSingleton(Mock.Of<IUserDataManager>())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(new KitAbiStatus("Kit Test", "10.11", "10.11.5", active, "Active."))
            .AddKit(TestKit.Options)
            .AddKitLibraryChanges();
        foreach (var consumer in consumers)
        {
            // Each consumer is the kit's KitComponent, as AddKitLibraryConsumer adds one.
            services.AddSingleton(new KitComponent<ILibraryChangeConsumer>(consumer.Name, _ => consumer));
        }

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<LibraryChangeMonitor>().Subscribe();
        return provider;
    }

    /// <summary>A plugin's post-scan task: public, sealed, the service provider only.</summary>
    public sealed class FxPostScanTask(IServiceProvider services) : KitPostScanTaskBase(services);

    /// <summary>One of Jellyfin's own post-scan tasks, which run after the plugins'.</summary>
    private sealed class CoreLikePostScanTask(Action run) : ILibraryPostScanTask
    {
        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            run();
            return Task.CompletedTask;
        }
    }
}
