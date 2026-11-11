using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Tests.TestHosting;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kit.Tests.Web.Pages;

/// <summary>
/// <see cref="PluginPagesRegistrationService"/>: registration only in <c>Auto</c>/<c>PluginPagesOnly</c> with
/// <c>RegisterPluginPages</c> on, only enabled pages, the host of each page recorded, and never an exception.
/// </summary>
public sealed class PluginPagesRegistrationServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FallbackOnly")]
    [InlineData("fallbackonly")]
    public async Task FallbackOnly_RegistersNothing(string? mode)
    {
        var kit = new PageTestKit();
        kit.Settings.Mode = mode;
        using var fake = FakePluginPages.Version24();
        var run = await RunAsync(kit, fake);

        Assert.Empty(FakePluginPages.Registered(run.Manager));
        Assert.All(run.Hosts.Entries, e => Assert.Equal(KitPageHost.Fallback, e.Host));
        var line = Assert.Single(run.Logger.At(LogLevel.Information));
        Assert.Contains("2 page(s) not registered with Plugin Pages (PageHostMode=FallbackOnly, RegisterPluginPages=True)", line.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Auto")]
    [InlineData("PluginPagesOnly")]
    [InlineData("auto")]
    public async Task AutoAndPluginPagesOnly_RegisterTheEnabledPages(string mode)
    {
        var kit = new PageTestKit();
        kit.Settings.Mode = mode;
        kit.Settings.Enabled.Remove("beta");
        using var fake = FakePluginPages.Version24();
        var run = await RunAsync(kit, fake);

        Assert.Equal([("Jellyfin.Plugin.Fx.AlphaPage", "/Fx/Pages/alpha", "Alpha", "video_library")], FakePluginPages.Registered(run.Manager));
        Assert.Equal(
            [("alpha", KitPageHost.PluginPages), ("beta", KitPageHost.Disabled)],
            run.Hosts.Entries.Select(e => (e.Key, e.Host)));
        Assert.StartsWith(PluginPagesBridge.Api24, run.Hosts.Entries[0].Detail, StringComparison.Ordinal);
        Assert.Contains(run.Logger.At(LogLevel.Information), e => e.Message.Contains("registered page Jellyfin.Plugin.Fx.AlphaPage (/Fx/Pages/alpha) with Plugin Pages", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RegisterPluginPagesOff_RegistersNothing_EvenInAuto()
    {
        var kit = new PageTestKit();
        kit.Settings.Mode = "Auto";
        kit.Settings.Register = false;
        using var fake = FakePluginPages.Version24();
        var run = await RunAsync(kit, fake);

        Assert.Empty(FakePluginPages.Registered(run.Manager));
        Assert.All(run.Hosts.Entries, e => Assert.Equal(KitPageHost.Fallback, e.Host));
    }

    [Fact]
    public async Task WithoutPluginPages_ThePagesStayOnTheirFallbackRoutes()
    {
        var kit = new PageTestKit();
        kit.Settings.Mode = "Auto";
        var run = await RunAsync(kit, null);

        Assert.All(run.Hosts.Entries, e => Assert.Equal(KitPageHost.Fallback, e.Host));
        Assert.All(run.Hosts.Entries, e => Assert.StartsWith(PluginPagesBridge.NotInstalled, e.Detail, StringComparison.Ordinal));
        Assert.Equal(2, run.Logger.At(LogLevel.Information).Count(e => e.Message.Contains("uses #/jfkt-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task BrokenSettings_NeverThrow_AndFallBackSafely()
    {
        var kit = new PageTestKit();
        kit.Settings.ThrowOnMode = true;
        kit.Settings.ThrowOnEnabled = true;
        using var fake = FakePluginPages.Version24();
        var run = await RunAsync(kit, fake);

        Assert.Empty(FakePluginPages.Registered(run.Manager));
        Assert.Equal([("alpha", KitPageHost.Fallback), ("beta", KitPageHost.Disabled)], run.Hosts.Entries.Select(e => (e.Key, e.Host)));
        Assert.Equal(2, run.Logger.At(LogLevel.Warning).Count());
    }

    [Fact]
    public async Task AnUnknownMode_IsFallbackOnly_WithAWarning()
    {
        var kit = new PageTestKit();
        kit.Settings.Mode = "Sometimes";
        using var fake = FakePluginPages.Version24();
        var run = await RunAsync(kit, fake);

        Assert.Empty(FakePluginPages.Registered(run.Manager));
        Assert.Contains(run.Logger.At(LogLevel.Warning), e => e.Message.Contains("Sometimes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BeforeStart_EveryPageIsNotStarted_AndStopDoesNothing()
    {
        var kit = new PageTestKit();
        var hosts = new KitPageHosts(kit.Options);

        Assert.All(hosts.Entries, e => Assert.Equal(KitPageHost.NotStarted, e.Host));
        Assert.Equal(["Jellyfin.Plugin.Fx.AlphaPage", "Jellyfin.Plugin.Fx.BetaPage"], hosts.Entries.Select(e => e.PluginPagesId));
        var service = new PluginPagesRegistrationService(kit.Options, new ServiceCollection().BuildServiceProvider(), kit.CreatePages(), hosts, WebKit.LoggerFactory(new ListLogger()), () => []);
        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void AddKitPages_RegistersTheShells_TheHosts_AndOneHostedService()
    {
        var services = new ServiceCollection().AddSingleton(new PageTestKit().Options).AddLogging();

        services.AddKitPages().AddKitPages();

        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<KitPages>(), provider.GetRequiredService<IKitPages>());
        Assert.NotNull(provider.GetRequiredService<KitPageHosts>());
        Assert.IsType<PluginPagesRegistrationService>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    private static async Task<Run> RunAsync(PageTestKit kit, FakePluginPages? fake)
    {
        var services = new ServiceCollection();
        object manager = new object();
        if (fake is not null)
        {
            manager = fake.CreateManager();
            services.AddSingleton(fake.ManagerInterface, manager);
        }

        var provider = services.BuildServiceProvider();
        var logger = new ListLogger();
        var hosts = new KitPageHosts(kit.Options);
        var assemblies = fake is null ? Array.Empty<Assembly>() : [fake.Assembly];
        var service = new PluginPagesRegistrationService(kit.Options, provider, kit.CreatePages(logger), hosts, WebKit.LoggerFactory(logger), () => assemblies);
        await service.StartAsync(TestContext.Current.CancellationToken);
        return new Run(manager, hosts, logger);
    }

    private sealed record Run(object Manager, KitPageHosts Hosts, ListLogger Logger);
}
