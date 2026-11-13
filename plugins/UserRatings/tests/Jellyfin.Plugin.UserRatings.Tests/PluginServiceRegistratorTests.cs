using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests;

/// <summary>
/// User Ratings' registrator: the kit's ABI guard first, then the kit,
/// the web client's script and its <c>index.html</c> tag, Diagnostics, the SQLite store with its hosted service,
/// the daily maintenance (integrity check, snapshot) and Diagnostics' <c>Store</c> and <c>Records</c>, the
/// services, singletons built only when first used, the library lifecycle (the kit's library changes, verify
/// sweep and detached records, with the ratings' consumer and owner as a hosted service) and the admin endpoints' service,
/// and the maintenance's MergedVersions step and the kit's users with the ratings store as their owner.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class PluginServiceRegistratorTests : IDisposable
{
    private readonly TempDirectory _data = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnItsLine_RegistersTheKit_TheWebClient_Diagnostics_TheStore_AndTheMaintenance(bool laterPatch)
    {
        var version = laterPatch ? JellyfinLine.LaterPatchVersion : JellyfinLine.ServerVersion;
        var services = Register(version);

        Assert.True(Assert.IsType<KitAbiStatus>(Assert.Single(services, d => d.ServiceType == typeof(KitAbiStatus)).ImplementationInstance).IsActive);
        Assert.Same(UserRatingsKit.Options, Assert.Single(services, d => d.ServiceType == typeof(KitOptions)).ImplementationInstance);
        Assert.Single(services, d => d.ServiceType == typeof(KitPaths));
        Assert.Equal(typeof(PluginConfigurationProvider), Assert.Single(services, d => d.ServiceType == typeof(IPluginConfigurationProvider)).ImplementationType);

        // The web client through the kit: the index.html middleware, the only startup filter, and the script.
        Assert.Equal(typeof(IndexHtmlInjectionStartupFilter), Assert.Single(services, d => d.ServiceType == typeof(IStartupFilter)).ImplementationType);
        Assert.Single(services, d => d.ServiceType == typeof(IEmbeddedScript));
        Assert.Single(services, d => d.ServiceType == typeof(IKitDiagnostics));

        // The store: the kit's database, the store as a singleton. Five hosted services: the database's, the kit's
        // library-change monitor and verify sweep and the ratings' lifecycle (the move-journal catch-up), and the data
        // jobs' tmp/ sweep at the start.
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(SqliteDatabase)).Lifetime);
        Assert.Equal(typeof(SqliteRatingsStore), Assert.Single(services, d => d.ServiceType == typeof(IRatingsStore)).ImplementationType);
        Assert.Equal(5, services.Count(d => d.ServiceType == typeof(IHostedService)));
        Assert.Single(services, d => d.ServiceType == typeof(KitMaintenance));
        string[] steps =
        [
            KitSqliteServiceCollectionExtensions.IntegrityCheckStep,
            KitSqliteServiceCollectionExtensions.SnapshotsStep,
            KitSqliteServiceCollectionExtensions.CheckpointStep,
            "VerifySweep",
            "Archive",
            "ResolveDetached",
            "Reattach",
            RatingsLibraryConsumer.MergedVersionsStep,
            "Users",
            RatingsUserDataOwner.ArchivePurgesStep,
            ModerationLogRetention.StepName,
            RatingsData.TempStep,
        ];
        Assert.Equal(steps, services.Where(d => d.ServiceType == typeof(KitComponent<IKitMaintenanceStep>)).Select(d => ((KitComponent<IKitMaintenanceStep>)d.ImplementationInstance!).Name));
        Assert.Equal(
            ["Detached", "JellyfinEnhanced", "LibraryChanges", "Lifecycle", "Maintenance", "Records", "Store", "Users", "Verifier"],
            services.Where(d => d.ServiceType == typeof(IKitDiagnosticsSource)).Select(DescribeKey).Order(StringComparer.Ordinal));

        // The lifecycle: one consumer, record owner and detached-record owner, the ratings' lifecycle, each created by
        // the kit on its own.
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(RatingsLibraryConsumer)).Lifetime);
        var name = typeof(RatingsLibraryConsumer).FullName;
        Assert.Equal(name, ((KitComponent<ILibraryChangeConsumer>)Assert.Single(services, d => d.ServiceType == typeof(KitComponent<ILibraryChangeConsumer>)).ImplementationInstance!).Name);
        Assert.Equal(name, ((KitComponent<IAttachedRecordOwner>)Assert.Single(services, d => d.ServiceType == typeof(KitComponent<IAttachedRecordOwner>)).ImplementationInstance!).Name);
        Assert.Equal(name, ((KitComponent<IDetachedRecordOwner>)Assert.Single(services, d => d.ServiceType == typeof(KitComponent<IDetachedRecordOwner>)).ImplementationInstance!).Name);
        foreach (var type in new[] { typeof(LibraryChangeMonitor), typeof(AttachedRecordVerifier), typeof(DetachedRecords), typeof(MoveJournalReader), typeof(IdentityResolver), typeof(IUserRatingsAdminApi) })
        {
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == type).Lifetime);
        }

        // The users: the kit's reconciler and deleted-user consumer, with the ratings store as their one owner.
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(UserReconciler)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(RatingsUserDataOwner)).Lifetime);
        Assert.Equal(typeof(KitUserDeletedConsumer), Assert.Single(services, d => d.ServiceType == typeof(MediaBrowser.Controller.Events.IEventConsumer<Jellyfin.Data.Events.Users.UserDeletedEventArgs>)).ImplementationType);
        Assert.Equal(typeof(RatingsUserDataOwner).FullName, ((KitComponent<IUserDataOwner>)Assert.Single(services, d => d.ServiceType == typeof(KitComponent<IUserDataOwner>)).ImplementationInstance!).Name);

        // The services and the member endpoints' service, each one singleton.
        foreach (var type in new[] { typeof(RatingsSettingsReader), typeof(LibraryAccess), typeof(FreshUserData), typeof(ItemAccess), typeof(PlayedCheck), typeof(SummaryCache), typeof(CopySiblings), typeof(RatingsService), typeof(IUserRatingsApi) })
        {
            Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == type).Lifetime);
        }
    }

    [Fact]
    public async Task TheServices_Resolve_WithoutOpeningTheDatabase_AndTheCacheFollowsTheStore()
    {
        await using var provider = Build(withServer: true);

        var service = provider.GetRequiredService<RatingsService>();
        Assert.IsType<UserRatingsApi>(provider.GetRequiredService<IUserRatingsApi>());
        var cache = provider.GetRequiredService<SummaryCache>();
        Assert.NotNull(service);
        Assert.False(File.Exists(provider.GetRequiredService<SqliteDatabase>().FilePath), "Resolving the services opened the database.");

        // The lifecycle and the admin endpoints' service resolve without opening it too.
        Assert.NotNull(provider.GetRequiredService<RatingsLibraryConsumer>());
        Assert.IsType<UserRatingsAdminApi>(provider.GetRequiredService<IUserRatingsAdminApi>());
        Assert.False(File.Exists(provider.GetRequiredService<SqliteDatabase>().FilePath), "Resolving the lifecycle opened the database.");

        // The users too: the owner and the kit's reconciler, which finds it.
        Assert.Equal("userratings", provider.GetRequiredService<RatingsUserDataOwner>().Name);
        Assert.NotNull(provider.GetRequiredService<UserReconciler>());
        Assert.False(File.Exists(provider.GetRequiredService<SqliteDatabase>().FilePath), "Resolving the users' owner opened the database.");

        var store = provider.GetRequiredService<IRatingsStore>();
        var item = Guid.NewGuid();
        Assert.Empty(cache.Get(item));
        await store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), item, 6), TestContext.Current.CancellationToken);
        Assert.Single(cache.Get(item));
    }

    [Fact]
    public void OnTheOtherLine_RegistersNothingButTheAbiStatus()
    {
        var services = Register(JellyfinLine.OtherLineVersion);

        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance);
        Assert.False(status.IsActive);
        Assert.Equal($"User Ratings is built for Jellyfin {JellyfinLine.Target}, but the server is Jellyfin {JellyfinLine.OtherLineVersion}: the plugin is inactive. Install the User Ratings build for Jellyfin {JellyfinLine.OtherTarget}.", status.Message);
    }

    [Fact]
    public void WhenTheVersionCheckThrows_RegistersNothingButTheAbiStatus()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Throws<MissingMethodException>();
        var services = new ServiceCollection();

        new PluginServiceRegistrator().RegisterServices(services, host.Object);

        Assert.False(Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance).IsActive);
    }

    [Fact]
    public void TheWebUiSettings_AreReadPerRequest_FromTheConfiguration()
    {
        var configuration = new PluginConfiguration { EnableWebUi = false, ReportWebAnchorProblems = false };
        var provider = new Mock<IPluginConfigurationProvider>();
        provider.Setup(p => p.GetConfiguration()).Returns(() => configuration);
        using var services = new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider();

        Assert.False(UserRatingsKit.Options.WebUiEnabled!(services));
        Assert.False(UserRatingsKit.Options.ReportWebAnchorProblems!(services));

        configuration = new PluginConfiguration();
        Assert.True(UserRatingsKit.Options.WebUiEnabled!(services));
        Assert.True(UserRatingsKit.Options.ReportWebAnchorProblems!(services));
        Assert.Equal(Jellyfin.Plugin.Kit.Users.DeletedUserPolicy.Delete, UserRatingsKit.Options.DeletedUserPolicy!(services));
    }

    [Fact]
    public async Task TheStore_IsUserratingsDb_InTheDataFolder_CreatedByTheHostedServiceOrFirstUse_NotByResolving()
    {
        // data/userratings/userratings.db; snapshots in data/userratings/snapshots (as the sandbox showed).
        await using var provider = Build(withServer: true);
        var database = provider.GetRequiredService<SqliteDatabase>();
        var store = provider.GetRequiredService<IRatingsStore>();

        Assert.Equal(Path.Combine(_data.Path, "userratings", "userratings.db"), database.FilePath);
        Assert.Equal(Path.Combine(_data.Path, "userratings", "snapshots"), database.SnapshotDirectory);
        Assert.False(File.Exists(database.FilePath), "Resolving the store opened the database.");

        // Since 0.3.14: secure_delete, and the pre-operation snapshots and the archive's copies kept 7 days, none older.
        Assert.Equal((true, TimeSpan.FromDays(7), "userratings-archived.jsonl"), (database.SecureDelete, database.PreOpKeepWithin, Assert.Single(database.PreOpCopies)));

        var service = provider.GetServices<IHostedService>().Single(s => s is SqliteDatabaseService);
        await service.StartAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 200 && !File.Exists(database.FilePath); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(database.FilePath), "The hosted service didn't create the database after the startup gate.");
        Assert.Equal(RatingsSchema.CurrentVersion, database.SchemaVersion);
        Assert.False(store.IsReadOnly);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheMaintenance_ChecksAndSnapshotsTheDatabase_AndDiagnosticsDescribeItsStoreAndRecords()
    {
        await using var provider = Build();
        var store = provider.GetRequiredService<IRatingsStore>();
        await store.UpsertAsync(StoreFixture.Movie(Guid.NewGuid(), Guid.NewGuid(), 7, "Words."), TestContext.Current.CancellationToken);

        var run = await provider.GetRequiredService<KitMaintenance>().RunAsync(null, TestContext.Current.CancellationToken);
        Assert.NotNull(run);

        var database = provider.GetRequiredService<SqliteDatabase>();
        Assert.True(database.LastQuickCheck!.Ok);
        Assert.Single(database.ListSnapshots(), s => s.Kind == Jellyfin.Plugin.Kit.Storage.SnapshotKind.Daily);

        var diagnostics = provider.GetRequiredService<IKitDiagnostics>().Describe();
        var described = Assert.IsAssignableFrom<IDictionary<string, object?>>(diagnostics["Store"]);
        Assert.Equal(("SQLite", "userratings", RatingsSchema.CurrentVersion, "Ok"), (described["Engine"], described["Name"], described["Version"], described["Status"]));
        var records = Assert.IsAssignableFrom<IDictionary<string, object?>>(diagnostics[PluginServiceRegistrator.RecordsSource]);
        Assert.Equal((1L, 0L, 1L, 1L), (records["Attached"], records["Detached"], records["Reviews"], records["Users"]));
    }

    [Fact]
    public async Task TheMaintenance_WaitsForTheDataJobsRunner_ThenHoldsItThroughItsSteps()
    {
        // Since 0.3.20: a restore or an import writes the database, then the archive; the maintenance's
        // Archive step mustn't run between the two. A probe step, last, sees the runner held.
        string? seen = null;
        await using var provider = Build(configure: services => services.AddKitMaintenanceStep("Probe", int.MaxValue, (p, _) =>
        {
            seen = Assert.Throws<Jellyfin.Plugin.Kit.Api.PluginException>(() => p.GetRequiredService<AdminJobRunner>().ThrowIfBusy()).Message;
            return Task.FromResult<IDictionary<string, object?>?>(null);
        }));
        var runner = provider.GetRequiredService<AdminJobRunner>();
        var maintenance = provider.GetRequiredService<KitMaintenance>();
        Task<IDictionary<string, object?>> run;
        using (runner.Hold("restore"))
        {
            run = maintenance.RunAsync(null, TestContext.Current.CancellationToken);
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.False(run.IsCompleted);
            Assert.Null(seen);
        }

        var result = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        Assert.False(result.ContainsKey("Skipped"));
        Assert.Equal("A daily maintenance is running. Wait for it to finish.", seen);
        Assert.Equal(new[] { PluginServiceRegistrator.MaintenanceGate }, (List<string>)maintenance.Describe()["Gates"]!);
        runner.Hold("restore").Dispose();
    }

    public void Dispose() => _data.Dispose();

    private static ServiceCollection Register(string version)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version));
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        return services;
    }

    private static string DescribeKey(ServiceDescriptor descriptor)
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        return ((IKitDiagnosticsSource)descriptor.ImplementationFactory!(provider)).Key;
    }

    private ServiceProvider Build(bool withServer = false, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        if (withServer)
        {
            services.AddSingleton(new Mock<ILibraryManager>().Object);
            services.AddSingleton(new Mock<IUserManager>().Object);
            services.AddSingleton(new Mock<IUserDataManager>().Object);
        }

        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(JellyfinLine.Server);
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(_data.Path);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(_data.Path, "configurations"));
        services.AddSingleton(paths.Object);
        services.AddSingleton(host.Object);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
