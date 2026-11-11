using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <c>AddKitIdentity</c>: the move-journal reader for the server's journal, with the kit's logger category, and the
/// resolver, both singletons. <c>AddKitLibraryChanges</c>: the monitor, once, as a singleton, a hosted service and
/// the Diagnostics entry <c>LibraryChanges</c>, with the plugin's consumers resolved when first needed. The record lifecycle:
/// <c>AddKitLibraryAccess</c>, <c>AddKitVerifier</c> (hosted, Diagnostics <c>Verifier</c>, a daily step) and
/// <c>AddKitDetachedRecords</c> (Diagnostics <c>Detached</c>, three daily steps, the archive in the data folder).
/// </summary>
public sealed class KitLibraryServiceCollectionExtensionsTests
{
    [Fact]
    public void AddKitIdentity_RegistersTheReaderAndTheResolver()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "jellyfin-plugin-tests", "data");
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(dataPath);
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(Mock.Of<ILogger>());
        var services = new ServiceCollection()
            .AddSingleton(paths.Object)
            .AddSingleton(Mock.Of<ILibraryManager>())
            .AddSingleton(loggerFactory.Object)
            .AddKit(TestKit.Options)
            .AddKitIdentity()
            .AddKitIdentity();

        using var provider = services.BuildServiceProvider();

        var reader = provider.GetRequiredService<MoveJournalReader>();
        Assert.Equal(Path.Combine(dataPath, "librarymaintenance", "moves.jsonl"), reader.FilePath);
        Assert.Same(reader, provider.GetRequiredService<MoveJournalReader>());
        Assert.Same(provider.GetRequiredService<IdentityResolver>(), provider.GetRequiredService<IdentityResolver>());
        loggerFactory.Verify(f => f.CreateLogger("Jellyfin.Plugin.KitTest.Kit.MoveJournal"), Times.Once);
        Assert.Single(services, d => d.ServiceType == typeof(IdentityResolver));
    }

    [Fact]
    public void AddKitIdentity_ChecksItsArgument()
        => Assert.Throws<ArgumentNullException>(() => KitLibraryServiceCollectionExtensions.AddKitIdentity(null!));

    [Fact]
    public void AddKitLibraryChanges_RegistersTheMonitorOnce_AsAHostedService_WithItsDiagnostics()
    {
        var created = 0;
        var host = new Mock<IServerApplicationHost>();
        var services = new ServiceCollection()
            .AddSingleton(host.Object)
            .AddSingleton(Mock.Of<ILibraryManager>())
            .AddSingleton(Mock.Of<IUserDataManager>())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddKit(TestKit.Options)
            .AddKitLibraryChanges()
            .AddKitLibraryChanges()
            .AddSingleton(_ =>
            {
                created++;
                return new RecordingConsumer("Fx");
            })
            .AddKitLibraryConsumer<RecordingConsumer>()
            .AddKitLibraryConsumer<RecordingConsumer>();

        Assert.Single(services, d => d.ServiceType == typeof(LibraryChangeMonitor));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Single(services, d => d.ServiceType == typeof(IKitDiagnosticsSource));
        Assert.Equal(typeof(RecordingConsumer).FullName, Assert.IsType<KitComponent<ILibraryChangeConsumer>>(Assert.Single(services, d => d.ServiceType == typeof(KitComponent<ILibraryChangeConsumer>)).ImplementationInstance).Name);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ILibraryChangeConsumer));

        using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<LibraryChangeMonitor>();
        Assert.Same(monitor, Assert.Single(provider.GetServices<IHostedService>()));
        Assert.Equal(0, created);

        var source = Assert.Single(provider.GetServices<IKitDiagnosticsSource>());
        Assert.Equal(KitLibraryServiceCollectionExtensions.LibraryChangesDiagnosticsKey, source.Key);
        var described = (IDictionary<string, object?>)source.Describe()!;
        Assert.Equal(false, described["Watching"]);
        Assert.Equal("Fx", ((List<IDictionary<string, object?>>)described["Consumers"]!).Single()["Name"]);
        Assert.Empty((IEnumerable<IDictionary<string, object?>>)described["Unavailable"]!);
        Assert.Equal(1, created);
    }

    [Fact]
    public async Task AddKitLibraryConsumer_AConsumerThatCantBeCreated_CostsOnlyItself_AndIsTriedAgain()
    {
        // The monitor used to take its consumers from one GetServices call, so one that couldn't be created left every
        // consumer without changes.
        var library = new FakeLibrary();
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        var logger = new ListLogger();
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(logger);
        var fails = true;
        var working = new RecordingConsumer("Working");
        var late = new RecordingConsumer("Late");
        var services = new ServiceCollection()
            .AddSingleton(host.Object)
            .AddSingleton(library.Object)
            .AddSingleton(loggers.Object)
            .AddKit(TestKit.Options)
            .AddKitLibraryChanges()
            .AddSingleton(new KitComponent<ILibraryChangeConsumer>("Late", _ => fails ? throw new InvalidOperationException("no data folder") : late))
            .AddSingleton(new KitComponent<ILibraryChangeConsumer>("Working", _ => working));
        await using var provider = services.BuildServiceProvider();
        var monitor = provider.GetRequiredService<LibraryChangeMonitor>();
        monitor.Subscribe();

        Assert.Equal(["Working"], monitor.Queues.Select(q => q.Name));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Late couldn't be created", StringComparison.Ordinal));
        var described = (IDictionary<string, object?>)Assert.Single(provider.GetServices<IKitDiagnosticsSource>()).Describe()!;
        Assert.Equal("no data folder", ((IEnumerable<IDictionary<string, object?>>)described["Unavailable"]!).Single()["Problem"]);

        var movie = library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));
        library.RaiseAdded(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, CancellationToken.None);
        Assert.Equal([movie.Id], working.Batches.Single().Added.Select(c => c.ItemId));

        // Still failing: tried again, still left out, a Debug line only.
        Assert.False(monitor.TryAddUnavailableConsumers(force: true));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);

        fails = false;
        Assert.True(monitor.TryAddUnavailableConsumers(force: true));
        Assert.Equal(["Working", "Late"], monitor.Queues.Select(q => q.Name));
        described = (IDictionary<string, object?>)Assert.Single(provider.GetServices<IKitDiagnosticsSource>()).Describe()!;
        Assert.Empty((IEnumerable<IDictionary<string, object?>>)described["Unavailable"]!);

        library.RaiseUpdated(movie);
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, CancellationToken.None);
        Assert.Equal([movie.Id], late.Batches.Single().Updated.Select(c => c.ItemId));
        Assert.Equal(2, working.Batches.Count);
        Assert.False(monitor.TryAddUnavailableConsumers(force: true));
    }

    [Fact]
    public void AddKitLibraryChanges_ChecksItsArgument()
        => Assert.Throws<ArgumentNullException>(() => KitLibraryServiceCollectionExtensions.AddKitLibraryChanges(null!));

    [Fact]
    public void AddKitLibraryAccess_RegistersLibraryAccessAndFreshUserData_WithTheRepositoryFallback()
    {
        using var provider = new ServiceCollection()
            .AddSingleton(Mock.Of<ILibraryManager>())
            .AddSingleton(Mock.Of<IUserDataManager>())
            .AddSingleton(Mock.Of<IItemRepository>())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddKit(TestKit.Options)
            .AddKitLibraryAccess()
            .AddKitLibraryAccess()
            .BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<LibraryAccess>(), provider.GetRequiredService<LibraryAccess>());
        var fresh = provider.GetRequiredService<FreshUserData>();
        Assert.Equal(FreshUserDataReadPath.Query, fresh.ReadPath);
    }

    [Fact]
    public async Task AddKitVerifier_RegistersTheSweepOnce_AsAHostedService_WithItsDiagnosticsAndDailyStep()
    {
        var owner = new FakeRecordOwner { Name = "fx" };
        var services = Services()
            .AddKitVerifier()
            .AddKitVerifier()
            .AddSingleton(owner)
            .AddKitRecordOwner<FakeRecordOwner>();

        Assert.Single(services, d => d.ServiceType == typeof(AttachedRecordVerifier));
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));

        using var provider = services.BuildServiceProvider();
        var verifier = provider.GetRequiredService<AttachedRecordVerifier>();
        Assert.Same(verifier, Assert.Single(provider.GetServices<IHostedService>()));
        var source = Assert.Single(provider.GetServices<IKitDiagnosticsSource>());
        Assert.Equal(KitLibraryServiceCollectionExtensions.VerifierDiagnosticsKey, source.Key);

        var step = Assert.Single(KitComponents.CreateAll<IKitMaintenanceStep>(provider, NullLogger.Instance));
        Assert.Equal(("VerifySweep", KitMaintenanceOrder.VerifySweep), (step.Name, step.Order));
        var result = await step.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "Daily" }, (IEnumerable<string>)result!["Reasons"]!);
        Assert.Equal(new[] { "fx" }, (IEnumerable<string>)((IDictionary<string, object?>)source.Describe()!)["Owners"]!);
    }

    [Fact]
    public async Task AddKitDetachedRecords_RegistersTheArchivesInTheDataFolder_TheirDiagnostics_AndThreeDailySteps()
    {
        var owner = new FakeRecordOwner { Name = "fx", DetachedRetentionDays = 1 };
        owner.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" }, detachedUtc: DateTime.UtcNow.AddDays(-3));
        var services = Services()
            .AddKitDetachedRecords()
            .AddKitDetachedRecords()
            .AddSingleton(owner)
            .AddKitRecordOwner<FakeRecordOwner>();
        Assert.Single(services, d => d.ServiceType == typeof(DetachedRecords));

        using var provider = services.BuildServiceProvider();
        var records = provider.GetRequiredService<DetachedRecords>();
        Assert.Equal(Path.Combine(DataPath, "kittest", "archive", "fx-archived.jsonl"), records.ArchiveOf(owner).FilePath);
        Assert.Equal(KitLibraryServiceCollectionExtensions.DetachedDiagnosticsKey, Assert.Single(provider.GetServices<IKitDiagnosticsSource>()).Key);
        var steps = KitComponents.CreateAll<IKitMaintenanceStep>(provider, NullLogger.Instance).OrderBy(s => s.Order).ToList();
        Assert.Equal(new[] { "Archive", "ResolveDetached", "Reattach" }, steps.Select(s => s.Name));

        try
        {
            var archive = await steps[0].RunAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, ((IDictionary<string, object?>)((List<object?>)archive!["Owners"]!).Single()!)["Archived"]);
            Assert.Empty(owner.Records);
        }
        finally
        {
            Directory.Delete(Path.Combine(DataPath, "kittest"), recursive: true);
        }
    }

    [Fact]
    public async Task AnOwnerThatCantBeCreated_CostsOnlyItself_AndIsTriedAgain()
    {
        // Every owner used to come from one GetServices call cached in a Lazy, so one owner whose creation threw
        // once (the developer demo, registered in every build) stopped the sweep, the detached records and the maintenance of
        // every owner (Bookmarks' too) until a restart, and the startup sweep failed again after every backoff.
        var logger = new ListLogger();
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(logger);
        var good = new FakeRecordOwner { Name = "good" };
        good.Add(new IdentitySnapshot { ItemId = Guid.NewGuid(), Kind = "Movie" });
        var broken = true;
        var services = Services()
            .AddSingleton(loggerFactory.Object)
            .AddKitVerifier()
            .AddKitDetachedRecords()
            .AddKitMaintenance()
            .AddSingleton(_ => broken ? throw new InvalidOperationException("no data folder") : new FlakyOwner { Name = "flaky" })
            .AddKitRecordOwner<FlakyOwner>()
            .AddSingleton(good)
            .AddKitRecordOwner<FakeRecordOwner>()
            .AddKitRecordOwner<FakeRecordOwner>();
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(KitComponent<IAttachedRecordOwner>)));
        Assert.Equal(2, services.Count(d => d.ServiceType == typeof(KitComponent<IDetachedRecordOwner>)));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAttachedRecordOwner) || d.ServiceType == typeof(IDetachedRecordOwner));
        var ct = TestContext.Current.CancellationToken;

        try
        {
            using var provider = services.BuildServiceProvider();
            var verifier = provider.GetRequiredService<AttachedRecordVerifier>();
            var records = provider.GetRequiredService<DetachedRecords>();

            var sweep = await verifier.SweepAsync(VerifySweepReason.Requested, ct);
            Assert.Equal(new[] { "good" }, sweep.Owners.Select(o => o.Owner));
            Assert.Equal(1, sweep.Detached);
            Assert.Equal(new[] { "good" }, records.Owners.Select(o => o.Name));
            var run = await provider.GetRequiredService<KitMaintenance>().RunAsync(null, ct);
            Assert.Equal(0, run["Failed"]);
            var unavailable = Assert.Single((IEnumerable<IDictionary<string, object?>>)Diagnostics(provider)["Verifier"]["Unavailable"]!);
            Assert.Equal((typeof(FlakyOwner).FullName, "no data folder"), ((string?)unavailable["Name"], (string?)unavailable["Problem"]));

            // One Error per registration (the sweep's and the detached records'), however often it was asked for.
            Assert.Equal(2, logger.At(LogLevel.Error).Count(e => e.Message.Contains(typeof(FlakyOwner).FullName!, StringComparison.Ordinal)));

            broken = false;
            Assert.Equal(new[] { "flaky", "good" }, records.Owners.Select(o => o.Name));
            Assert.Equal(new[] { "flaky", "good" }, (await verifier.SweepAsync(VerifySweepReason.Requested, ct)).Owners.Select(o => o.Owner));
            Assert.Empty((IEnumerable<IDictionary<string, object?>>)Diagnostics(provider)["Verifier"]["Unavailable"]!);
        }
        finally
        {
            if (Directory.Exists(Path.Combine(DataPath, "kittest")))
            {
                Directory.Delete(Path.Combine(DataPath, "kittest"), recursive: true);
            }
        }
    }

    private static string DataPath { get; } = Path.Combine(Path.GetTempPath(), "jellyfin-plugin-tests", "kit-library-" + Guid.NewGuid().ToString("N"));

    private static Dictionary<string, IDictionary<string, object?>> Diagnostics(IServiceProvider provider)
        => provider.GetServices<IKitDiagnosticsSource>().ToDictionary(s => s.Key, s => (IDictionary<string, object?>)s.Describe()!);

    private static IServiceCollection Services()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(DataPath);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        return new ServiceCollection()
            .AddSingleton(paths.Object)
            .AddSingleton(host.Object)
            .AddSingleton(new Jellyfin.Plugin.Kit.TestSupport.Library.FakeLibrary().Object)
            .AddSingleton(Mock.Of<IUserManager>())
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddKit(TestKit.Options);
    }
}

/// <summary>A second owner type (the kit adds each owner type once).</summary>
internal sealed class FlakyOwner : FakeRecordOwner
{
}
