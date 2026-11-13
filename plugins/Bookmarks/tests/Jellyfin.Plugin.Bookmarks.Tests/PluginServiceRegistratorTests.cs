using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Bookmarks.Tests;

/// <summary>
/// The kit's ABI guard in Bookmarks' registrator.
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
        Assert.Same(BookmarksKit.Options, Assert.Single(services, d => d.ServiceType == typeof(KitOptions)).ImplementationInstance);
        Assert.Single(services, d => d.ServiceType == typeof(KitPaths));
        Assert.Single(services, d => d.ServiceType == typeof(BookmarkPaths));
        Assert.Single(services, d => d.ServiceType == typeof(IBookmarkManager));

        // The gate every automatic writer of bookmarks.json asks while the store is read-only: one per server.
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(BackgroundWriteGate)).Lifetime);

        // The clip file operations and the janitor: one each per server.
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(ClipFileOps)).Lifetime);
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(PartialFileJanitor)).Lifetime);

        // The My Clips page's service with its list and the two caches, one each per server (the caches keep their
        // answers 30 s and 60 s for every request).
        Assert.Equal(typeof(ClipService), Assert.Single(services, d => d.ServiceType == typeof(IBookmarkClips)).ImplementationType);
        Assert.All(
            new[] { typeof(IBookmarkClips), typeof(ClipQueryService), typeof(ClipStateCache), typeof(ClipLibraryLocations) },
            type => Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == type).Lifetime));

        // The missing-clip sweep, one per server (it keeps its last run for Diagnostics' ClipSweep entry), and its setting's
        // default (30 days).
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(MissingClipSweep)).Lifetime);
        Assert.Equal(30, new Configuration.PluginConfiguration().MissingClipRetentionDays);

        // The kit's users services: the deleted-user consumer (scoped, as Jellyfin's EventManager resolves it), the
        // reconciler, and its owners, each the kit's KitComponent (created on its own since 1.1): the bookmark store's and
        // the developer user data demo's.
        Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, d => d.ServiceType == typeof(MediaBrowser.Controller.Events.IEventConsumer<Jellyfin.Data.Events.Users.UserDeletedEventArgs>)).Lifetime);
        Assert.Single(services, d => d.ServiceType == typeof(Kit.Users.UserReconciler));
        Assert.Equal(
            [typeof(BookmarkUserDataOwner).FullName, typeof(UserDataDemoOwner).FullName],
            services.Where(d => d.ServiceType == typeof(KitComponent<Kit.Users.IUserDataOwner>)).Select(d => ((KitComponent<Kit.Users.IUserDataOwner>)d.ImplementationInstance!).Name));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Kit.Users.IUserDataOwner));
        Assert.Single(services, d => d.ServiceType == typeof(IBookmarkUserDataDemo));
        Assert.Single(services, d => d.ServiceType == typeof(IBookmarkRecordsAdmin));
        Assert.Equal(Guid.Parse(Plugin.PluginId), BookmarksKit.Options.PluginId);
        Assert.Equal(Kit.Users.DeletedUserPolicy.KeepAsOrphan, BookmarksKit.Options.DeletedUserPolicy(new ServiceCollection().AddSingleton(Bookmarks.Tests.TestSupport.TestData.ConfigurationProvider()).BuildServiceProvider()));
        Assert.Equal("KeepAsOrphan", new Configuration.PluginConfiguration().DeletedUserPolicy);
        Assert.False(new Configuration.PluginConfiguration().EnableUserDataDemo);

        // The kit's item identity, used by the identity service and the developer identity demo.
        Assert.Single(services, d => d.ServiceType == typeof(Kit.Library.MoveJournalReader));
        Assert.Single(services, d => d.ServiceType == typeof(Kit.Library.IdentityResolver));
        Assert.Single(services, d => d.ServiceType == typeof(IBookmarkIdentityDemo));
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(BookmarkIdentityService)).Lifetime);

        // The kit's library-change monitor, with its consumers, each the kit's KitComponent: the developer
        // library change demo, the library demo and, after them, the identity service.
        Assert.Equal(ServiceLifetime.Singleton, Assert.Single(services, d => d.ServiceType == typeof(Kit.Library.LibraryChangeMonitor)).Lifetime);
        Assert.Equal(
            [typeof(BookmarkLibraryChangeDemo).FullName, typeof(LibraryDemoRecords).FullName, typeof(BookmarkIdentityService).FullName],
            services.Where(d => d.ServiceType == typeof(KitComponent<Kit.Library.ILibraryChangeConsumer>)).Select(d => ((KitComponent<Kit.Library.ILibraryChangeConsumer>)d.ImplementationInstance!).Name));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Kit.Library.ILibraryChangeConsumer));
        Assert.Single(services, d => d.ServiceType == typeof(IBookmarkLibraryChangeDemo));
        Assert.False(new Configuration.PluginConfiguration().EnableLibraryChangeDemo);

        // The kit's record lifecycle and library reads: the verify sweep, the detached records, LibraryAccess,
        // FreshUserData and the daily maintenance, with the bookmarks and the developer library demo as owners; the
        // settings' defaults: the demo off, detached records kept for good, no developer clock offset, 7 snapshots.
        Assert.Single(services, d => d.ServiceType == typeof(Kit.Library.AttachedRecordVerifier));
        Assert.Single(services, d => d.ServiceType == typeof(Kit.Library.DetachedRecords));
        Assert.Single(services, d => d.ServiceType == typeof(Kit.Library.LibraryAccess));
        Assert.Single(services, d => d.ServiceType == typeof(Kit.Library.FreshUserData));
        Assert.Single(services, d => d.ServiceType == typeof(Kit.Hosting.KitMaintenance));
        // Each owner and step is the kit's KitComponent, created on its own: the demo, if it couldn't be
        // created, would cost only itself.
        Assert.Equal(
            [typeof(LibraryDemoRecords).FullName, typeof(BookmarkIdentityService).FullName],
            services.Where(d => d.ServiceType == typeof(KitComponent<Kit.Library.IAttachedRecordOwner>)).Select(d => ((KitComponent<Kit.Library.IAttachedRecordOwner>)d.ImplementationInstance!).Name));
        Assert.Equal(
            [typeof(LibraryDemoRecords).FullName, typeof(BookmarkIdentityService).FullName],
            services.Where(d => d.ServiceType == typeof(KitComponent<Kit.Library.IDetachedRecordOwner>)).Select(d => ((KitComponent<Kit.Library.IDetachedRecordOwner>)d.ImplementationInstance!).Name));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(Kit.Library.IAttachedRecordOwner) || d.ServiceType == typeof(Kit.Library.IDetachedRecordOwner) || d.ServiceType == typeof(IKitMaintenanceStep));
        Assert.Single(services, d => d.ServiceType == typeof(IBookmarkLibraryDemo));
        Assert.Equal(
            ["Archive", "ClipFiles", "MissingClips", "Reattach", "ResolveDetached", "Snapshots", "Users", "VerifySweep"],
            services.Where(d => d.ServiceType == typeof(KitComponent<IKitMaintenanceStep>)).Select(d => ((KitComponent<IKitMaintenanceStep>)d.ImplementationInstance!).Name).Order(StringComparer.Ordinal));
        Assert.False(new Configuration.PluginConfiguration().EnableLibraryDemo);
        Assert.Equal(0, new Configuration.PluginConfiguration().DetachedRetentionDays);
        Assert.Equal(0, new Configuration.PluginConfiguration().DeveloperClockOffsetDays);
        Assert.Equal(7, new Configuration.PluginConfiguration().SnapshotsToKeep);

        // The clip worker, the library-change monitor, the verify sweep, the identity service, the source of the clip
        // folders' facade ClipRootsV1, and the kit's Plugin Pages registration, which registers nothing unless
        // the page demo is on and PageHostMode is Auto or PluginPagesOnly. Factories are told apart by what they return.
        Assert.Equal(
            [
                typeof(ClipExtractionService).FullName, typeof(Jellyfin.Plugin.Bookmarks.Integration.ClipRootsFacadeService).FullName, typeof(BookmarkIdentityService).FullName,
                typeof(Kit.Library.AttachedRecordVerifier).FullName, typeof(Kit.Library.LibraryChangeMonitor).FullName, typeof(PluginPagesRegistrationService).FullName
            ],
            services.Where(d => d.ServiceType == typeof(IHostedService))
                .Select(d => (d.ImplementationType ?? d.ImplementationFactory?.Method.ReturnType)?.FullName)
                .Order(StringComparer.Ordinal));
        Assert.Single(services, d => d.ServiceType == typeof(IKitPages));
        Assert.Single(services, d => d.ServiceType == typeof(KitPageHosts));

        // The web UI comes from the kit's index.html middleware, the only startup filter, and its script from the kit's
        // EmbeddedScript.
        Assert.Equal(typeof(IndexHtmlInjectionStartupFilter), Assert.Single(services, d => d.ServiceType == typeof(IStartupFilter)).ImplementationType);
        Assert.Single(services, d => d.ServiceType == typeof(EmbeddedScript));
        Assert.Single(services, d => d.ServiceType == typeof(IEmbeddedScript));
    }

    [Fact]
    public void TheClientScript_IsTheKitsEmbeddedScript_ForBookmarksOptions()
    {
        var services = Register(JellyfinLine.ServerVersion);
        services.AddLogging();
        using var provider = services.BuildServiceProvider();

        var scripts = Assert.IsType<EmbeddedScript>(provider.GetRequiredService<IEmbeddedScript>());

        Assert.Same(scripts, provider.GetRequiredService<EmbeddedScript>());
        Assert.Contains("\"prefix\":\"jfbm\"", scripts.ClientScriptText, StringComparison.Ordinal);
        Assert.Null(scripts.AdminScriptText);
    }

    [Fact]
    public void BookmarkStore_IsTheKitStore_OnBookmarksJsonInTheDataFolder_WithTheKitLoggerCategory()
    {
        using var directory = new TempDirectory();
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.DataPath).Returns(directory.Path);
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(NullLogger.Instance);
        var services = Register(JellyfinLine.ServerVersion);
        services.AddSingleton(paths.Object).AddSingleton(loggers.Object);
        using var provider = services.BuildServiceProvider();

        var store = Assert.IsType<BookmarkStore>(provider.GetRequiredService<IBookmarkStore>());

        Assert.Equal(Path.Combine(directory.Path, "bookmarks", "bookmarks.json"), store.Documents.FilePath);
        loggers.Verify(f => f.CreateLogger("Jellyfin.Plugin.Bookmarks.Kit.Storage"), Times.Once);
        Assert.Empty(store.GetAll());
        Assert.False(store.IsReadOnly);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "bookmarks")), "Reading created the data folder.");
    }

    [Fact]
    public void OnTheOtherLine_RegistersNothingButTheAbiStatus()
    {
        var services = Register(JellyfinLine.OtherLineVersion);

        var descriptor = Assert.Single(services);
        var status = Assert.IsType<KitAbiStatus>(descriptor.ImplementationInstance);
        Assert.False(status.IsActive);
        Assert.Equal($"Bookmarks is built for Jellyfin {JellyfinLine.Target}, but the server is Jellyfin {JellyfinLine.OtherLineVersion}: the plugin is inactive. Install the Bookmarks build for Jellyfin {JellyfinLine.OtherTarget}.", status.Message);
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

    private static ServiceCollection Register(string version)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(h => h.ApplicationVersion).Returns(Version.Parse(version));
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, host.Object);
        return services;
    }
}
