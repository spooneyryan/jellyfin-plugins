using System.IO;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Jellyfin.Plugin.TagCloud.Clouds;
using Jellyfin.Plugin.TagCloud.Configuration;
using Jellyfin.Plugin.TagCloud.Index;
using Jellyfin.Plugin.TagCloud.Services;
using Jellyfin.Plugin.TagCloud.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagCloud;

/// <summary>
/// Registers the plugin's services with the server.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // First statement: on a Jellyfin line this build isn't for, register nothing but the status.
        if (!KitAbiGuard.Register(serviceCollection, applicationHost, TagCloudKit.Options))
        {
            return;
        }

        serviceCollection.AddKit(TagCloudKit.Options);

        // The web UI: the kit's middleware adds the script tag to index.html on every request, and the kit
        // builds the script it loads from kit.js and Web/js.
        serviceCollection.AddIndexHtmlInjection();
        serviceCollection.AddEmbeddedScript();

        // Page hosting: the shell of GET /TagCloud/Pages/cloud and the Plugin Pages registration in
        // Auto/PluginPagesOnly mode.
        serviceCollection.AddKitPages();

        // Diagnostics: the ClientHealth reports, and the admin Diagnostics with the saved
        // clouds' store and the index.
        serviceCollection.AddKitDiagnostics();
        serviceCollection.AddKitDiagnosticsSource("Store", services => KitDiagnostics.DescribeStore(services.GetRequiredService<SavedCloudOwner>().Store));
        serviceCollection.AddKitDiagnosticsSource("Index", services => services.GetRequiredService<TagCloudIndex>().Describe());

        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();

        // The saved clouds: data/tagcloud/clouds.json, snapshots in data/tagcloud/snapshots.
        serviceCollection.AddSingleton(services =>
        {
            var paths = services.GetRequiredService<KitPaths>();
            return new SavedCloudOwner(
                Path.Combine(paths.Root, SavedCloudOwner.FileName),
                paths.Snapshots,
                TagCloudKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Storage"),
                TagCloudKit.Options.Clock(services));
        });

        // The index: the titles and their tags, genres and studios from the library, the people in one grouped
        // read of the server's database (the per-title fallback when that can't run), built after the startup gate by the
        // worker and kept current by the library consumer below.
        serviceCollection.AddSingleton(services => new TitleReader(services.GetRequiredService<ILibraryManager>()));
        serviceCollection.AddSingleton(services => new PeopleReader(
            services.GetRequiredService<ILibraryManager>(),
            () => services.GetService<IDbContextFactory<JellyfinDbContext>>(),
            TagCloudKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "People")));
        serviceCollection.AddSingleton(services => new TagCloudIndex(
            services.GetRequiredService<TitleReader>(),
            services.GetRequiredService<PeopleReader>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            TagCloudKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Index"),
            TagCloudKit.Options.Clock(services)));
        serviceCollection.AddHostedService(services => new TagCloudIndexWorker(
            applicationHost,
            services.GetRequiredService<TagCloudIndex>(),
            TagCloudKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "IndexWorker")));

        // Library changes: the kit's monitor batches Jellyfin's item events for the index's consumer, and
        // Library/TagCloudPostScanTask flushes it as a full pass inside every scan.
        serviceCollection.AddKitLibraryChanges();
        serviceCollection.AddSingleton(services => new TagCloudLibraryConsumer(
            services.GetRequiredService<TagCloudIndex>(),
            services.GetRequiredService<IPluginConfigurationProvider>()));
        serviceCollection.AddKitLibraryConsumer<TagCloudLibraryConsumer>();

        // Users and data: a deleted user's clouds, the reconciler, and Diagnostics' Users entry.
        serviceCollection.AddKitUsers();
        serviceCollection.AddKitUserDataOwner<SavedCloudOwner>();

        // Library reads: LibraryAccess, the InternalItemsQuery(user) factory every count starts from.
        serviceCollection.AddKitLibraryAccess();

        // The daily maintenance that Tasks/TagCloudMaintenanceTask runs at 03:40: the snapshot of clouds.json, the kit's
        // steps (the user reconciliation among them) and a full rebuild of the index.
        serviceCollection.AddKitMaintenance();
        serviceCollection.AddTagCloudMaintenanceSteps();

        // The endpoints' services.
        serviceCollection.AddSingleton<ITagCloudQuery>(services => new TagCloudQueryService(
            services.GetRequiredService<TagCloudIndex>(),
            services.GetRequiredService<LibraryAccess>(),
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IPluginConfigurationProvider>()));
        serviceCollection.AddSingleton<ITagCloudClouds>(services => new TagCloudCloudService(
            services.GetRequiredService<SavedCloudOwner>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            TagCloudKit.Options.Clock(services)));
        serviceCollection.AddSingleton<ITagCloudAdmin>(services => new TagCloudAdminService(services.GetRequiredService<TagCloudIndex>()));
    }
}
