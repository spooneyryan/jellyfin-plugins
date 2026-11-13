using Jellyfin.Plugin.Kit.TestSupport;
using System;
using System.Linq;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests;

/// <summary>
/// The kit's ABI guard in Library Maintenance's registrator, and what 1.0 registers.
/// </summary>
public sealed class PluginServiceRegistratorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnItsLine_RegistersTheKitAndTheServices(bool laterPatch)
    {
        var version = laterPatch ? JellyfinLine.LaterPatchVersion : JellyfinLine.ServerVersion;
        var services = Register(version);

        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services, d => d.ServiceType == typeof(KitAbiStatus)).ImplementationInstance);
        Assert.True(status.IsActive);
        Assert.Same(LibraryMaintenanceKit.Options, Assert.Single(services, d => d.ServiceType == typeof(KitOptions)).ImplementationInstance);
        Assert.Single(services, d => d.ServiceType == typeof(KitPaths));
        Assert.Single(services, d => d.ServiceType == typeof(IEmbeddedScript));
        Assert.Single(services, d => d.ServiceType == typeof(IKitDiagnostics));
        Assert.Equal(typeof(PluginConfigurationProvider), Assert.Single(services, d => d.ServiceType == typeof(IPluginConfigurationProvider)).ImplementationType);
        Assert.Equal(Guid.Parse(Plugin.PluginId), LibraryMaintenanceKit.Options.PluginId);
    }

    [Fact]
    public void OnItsLine_RegistersTheDuplicatesIndex_OnceForBothItsNames()
    {
        // One index per server, behind the controllers' public interface, with its Diagnostics entry.
        var registered = Register(JellyfinLine.ServerVersion);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registered, d => d.ServiceType == typeof(MovieIndexBuilder)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registered, d => d.ServiceType == typeof(MovieIndexService)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registered, d => d.ServiceType == typeof(IDuplicatesIndex)).Lifetime);

        var library = new DuplicatesLibrary();
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in registered.Where(d => d.ServiceType != typeof(IPluginConfigurationProvider)))
        {
            services.Add(descriptor);
        }

        services.AddLogging();
        services.AddSingleton(library.Fake.Object);
        services.AddSingleton(library.ConfigurationProvider);
        var provider = services.BuildServiceProvider();

        var index = provider.GetRequiredService<MovieIndexService>();
        Assert.Same(index, provider.GetRequiredService<IDuplicatesIndex>());
        var source = Assert.Single(provider.GetServices<IKitDiagnosticsSource>(), s => s.Key == "DuplicatesIndex");
        Assert.Equal(0L, Assert.IsType<System.Collections.Generic.IDictionary<string, object?>>(source.Describe(), exactMatch: false)["Version"]);

        // The container disposes the one instance under both names; the second is a no-op.
        provider.Dispose();
        Assert.Same(System.Threading.Tasks.Task.CompletedTask, index.Rebuild());
        Assert.Empty(library.IdQueries);
    }

    [Fact]
    public void OnItsLine_RegistersTheGroups_OnceForBothNames_WithTheKitsLibraryAccess()
    {
        // One groups service (its cache is per server), the kit's LibraryAccess, a Diagnostics entry.
        var registered = Register(JellyfinLine.ServerVersion);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registered, d => d.ServiceType == typeof(DuplicateGroupsService)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registered, d => d.ServiceType == typeof(IDuplicateGroups)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registered, d => d.ServiceType == typeof(LibraryAccess)).Lifetime);

        var library = new DuplicatesLibrary();
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in registered.Where(d => d.ServiceType != typeof(IPluginConfigurationProvider)))
        {
            services.Add(descriptor);
        }

        services.AddLogging();
        services.AddSingleton(library.Fake.Object);
        services.AddSingleton(library.ConfigurationProvider);
        using var provider = services.BuildServiceProvider();

        var groups = provider.GetRequiredService<DuplicateGroupsService>();
        Assert.Same(groups, provider.GetRequiredService<IDuplicateGroups>());
        var source = Assert.Single(provider.GetServices<IKitDiagnosticsSource>(), s => s.Key == "DuplicateGroups");
        var described = Assert.IsType<System.Collections.Generic.IDictionary<string, object?>>(source.Describe(), exactMatch: false);
        Assert.Equal(0, described["CachedResults"]);
        Assert.Null(described["LastGroupingMs"]);
        Assert.Empty(library.IdQueries);
    }

    [Fact]
    public async System.Threading.Tasks.Task On1011_RegistersTheIndexsFreshness_TheKitsMonitorWithDuplicatesConsumer()
    {
        // The kit's library-change monitor, its only hosted service here, with Duplicates' consumer as
        // the kit's KitComponent, and its Diagnostics entry.
        var registered = Register(JellyfinLine.ServerVersion);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registered, d => d.ServiceType == typeof(LibraryChangeMonitor)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(registered, d => d.ServiceType == typeof(DuplicatesChangeConsumer)).Lifetime);
        Assert.Equal(
            [typeof(DuplicatesChangeConsumer).FullName],
            registered.Where(d => d.ServiceType == typeof(KitComponent<ILibraryChangeConsumer>)).Select(d => ((KitComponent<ILibraryChangeConsumer>)d.ImplementationInstance!).Name));
        Assert.DoesNotContain(registered, d => d.ServiceType == typeof(ILibraryChangeConsumer));
        Assert.Equal(
            [typeof(LibraryChangeMonitor).FullName],
            registered.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => (d.ImplementationType ?? d.ImplementationFactory?.Method.ReturnType)?.FullName));

        // Wired up: a scan's full pass, as the post-scan task flushes it, builds the index the controllers read.
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.CoreStartupHasCompleted).Returns(true);
        IServiceCollection services = new ServiceCollection();
        foreach (var descriptor in registered.Where(d => d.ServiceType != typeof(IPluginConfigurationProvider) && d.ServiceType != typeof(MovieIndexBuilder)))
        {
            services.Add(descriptor);
        }

        services.AddLogging();
        services.AddSingleton(host.Object);
        services.AddSingleton(library.Fake.Object);
        services.AddSingleton(library.ConfigurationProvider);
        services.AddSingleton(library.Builder());
        await using var provider = services.BuildServiceProvider();
        Assert.Contains(provider.GetServices<IKitDiagnosticsSource>(), s => s.Key == "LibraryChanges");

        var monitor = provider.GetRequiredService<LibraryChangeMonitor>();
        monitor.Subscribe();
        library.Fake.IsScanRunning = true;
        await monitor.FlushAsync(fullPass: true, duringPostScan: true, null, TestContext.Current.CancellationToken);
        var index = provider.GetRequiredService<IDuplicatesIndex>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (index.GetStatus(null) is { Version: 0 } or { Rebuilding: true })
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await System.Threading.Tasks.Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, index.GetStatus(null).Movies);
        Assert.False(index.GetStatus(null).BuiltDuringScan);
    }

    [Fact]
    public void OnItsLine_AddsTheIndexHtmlInjection_AndNoPageHosting()
    {
        // 1.0 had no ClientScript and no index.html injection; Move's item-menu entry added them:
        // the kit's index.html middleware is the only startup filter. The Dashboard page is jellyfin-web's own
        // (IHasWebPages), so the kit's page shells and Plugin Pages registration aren't added.
        var services = Register(JellyfinLine.ServerVersion);

        Assert.Equal(typeof(IndexHtmlInjectionStartupFilter), Assert.Single(services, d => d.ServiceType == typeof(IStartupFilter)).ImplementationType);
        Assert.DoesNotContain(services, d => d.ServiceType.FullName == "Jellyfin.Plugin.Kit.Web.Pages.IKitPages");
        Assert.Empty(LibraryMaintenanceKit.Options.Pages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnAnotherLine_RegistersOnlyTheInactiveStatus(bool older)
    {
        var services = Register(older ? "10.10.7" : JellyfinLine.OtherLineVersion);

        var status = Assert.IsType<KitAbiStatus>(Assert.Single(services).ImplementationInstance);
        Assert.False(status.IsActive);
    }

    [Fact]
    public void TheDefaults_AreTheDocumentedOnes()
    {
        // The 1.0 fields and their defaults.
        var configuration = new PluginConfiguration();

        Assert.True(configuration.DuplicatesEnabled);
        Assert.Equal("TitleAndYear", configuration.DefaultMatchMode);
        Assert.Equal("MatchUnknownOnly", configuration.UnknownYearPolicy);
        Assert.Empty(configuration.IncludedLibraryIds);
        Assert.Empty(configuration.ExcludedPathPrefixes);
        Assert.True(configuration.StripEditionSuffixes);
        Assert.Equal(24, configuration.EditionPhrases.Length);
        Assert.Contains("director's cut", configuration.EditionPhrases);
        Assert.Equal(["En"], configuration.ArticleLanguages);
        Assert.True(configuration.WarmIndexAfterScan);
        Assert.Equal(5, configuration.RequestWaitSeconds);

        // The ClientScript's common fields.
        Assert.True(configuration.EnableWebUi);
        Assert.True(configuration.ReportWebAnchorProblems);
    }

    private static IServiceCollection Register(string version)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version));
        IServiceCollection services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        return services;
    }
}
