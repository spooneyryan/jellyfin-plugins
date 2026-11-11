using System.Linq;
using Emby.Naming.Common;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using Jellyfin.Plugin.LibraryMaintenance.Move.Preflight;
using Jellyfin.Plugin.LibraryMaintenance.Operations;
using Jellyfin.Plugin.LibraryMaintenance.Settings;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using ItemVisibility = Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups.ItemVisibility;

namespace Jellyfin.Plugin.LibraryMaintenance;

/// <summary>
/// Registers the plugin's services with the server.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // First statement: on a Jellyfin line this build isn't for, register nothing but the status.
        if (!KitAbiGuard.Register(serviceCollection, applicationHost, LibraryMaintenanceKit.Options))
        {
            return;
        }

        serviceCollection.AddKit(LibraryMaintenanceKit.Options);

        // The Dashboard page's module: the kit builds GET /LibraryMaintenance/AdminScript from kit.js,
        // Web/admin/js and Web/admin/css. Since 1.0.2 also the web client's script, GET /LibraryMaintenance/ClientScript
        // from kit.js, Web/js and Web/css (Move's item-menu entry and dialog), which the kit's middleware, the only startup
        // filter, adds to index.html while EnableWebUi is on.
        serviceCollection.AddEmbeddedScript();
        serviceCollection.AddIndexHtmlInjection();

        // Diagnostics: GET /LibraryMaintenance/Admin/Diagnostics, the kit's body with the plugin's own sources,
        // and since 1.0.2 POST /LibraryMaintenance/Diagnostics/ClientHealth, the web client's anchor reports.
        serviceCollection.AddKitDiagnostics();

        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();

        // Duplicates' movie index: held in memory, never written to disk, built on request and kept fresh.
        serviceCollection.AddSingleton<MovieIndexBuilder>();
        serviceCollection.AddSingleton<MovieIndexService>();
        serviceCollection.AddSingleton<IDuplicatesIndex>(static provider => provider.GetRequiredService<MovieIndexService>());
        serviceCollection.AddKitDiagnosticsSource("DuplicatesIndex", static provider => provider.GetRequiredService<MovieIndexService>().Describe());

        // Its freshness: the kit's monitor batches Jellyfin's movie and video events for
        // Duplicates' consumer (60 s quiet, 10 min at most, never during a scan), and Library/LibraryMaintenancePostScanTask
        // flushes it as a full pass inside every scan, after which the index is built once.
        serviceCollection.AddKitLibraryChanges();
        serviceCollection.AddSingleton<DuplicatesChangeConsumer>();
        serviceCollection.AddKitLibraryConsumer<DuplicatesChangeConsumer>();

        // The groups: GET /Groups groups the index for the libraries the caller can see (the kit's
        // LibraryAccess; admins get no bypass), through an LRU of 8 results per snapshot, options and library set.
        serviceCollection.AddKitLibraryAccess();
        serviceCollection.AddSingleton<ItemVisibility>();
        serviceCollection.AddSingleton<DuplicateGroupsService>();
        serviceCollection.AddSingleton<IDuplicateGroups>(static provider => provider.GetRequiredService<DuplicateGroupsService>());
        serviceCollection.AddKitDiagnosticsSource("DuplicateGroups", static provider => provider.GetRequiredService<DuplicateGroupsService>().Describe());

        // The settings: the Settings tab's data, a save previewed when it changes the index's scope, and the
        // settings' export and import in the kit's envelope.
        serviceCollection.AddSingleton<SettingsService>();
        serviceCollection.AddSingleton<ILibraryMaintenanceSettings>(static provider => provider.GetRequiredService<SettingsService>());

        // Move media (Library Maintenance 1.1, in development): the gate (MoveEnabled, a Windows host) and the
        // service behind GET /LibraryMaintenance/Capabilities and the /LibraryMaintenance/Move routes.
        serviceCollection.AddSingleton(static provider => new MoveFeature(provider.GetRequiredService<IPluginConfigurationProvider>()));
        serviceCollection.AddSingleton<MoveMedia>();
        serviceCollection.AddSingleton<IMoveMedia>(static provider => provider.GetRequiredService<MoveMedia>());

        // The operation gate Move shares with Duplicates: one file- or version-changing operation at a
        // time and the per-item busy set. Move's item checks and preflight read it.
        serviceCollection.AddSingleton<LibraryOperationGate>();
        serviceCollection.AddSingleton(static provider => new MoveEligibility(
            provider.GetRequiredService<LibraryAccess>(),
            provider.GetRequiredService<LibraryLocationCatalog>(),
            provider.GetRequiredService<LibraryOperationGate>(),
            provider.GetRequiredService<MoveFeature>()));
        serviceCollection.AddSingleton(static _ => new FileLockProbe());
        serviceCollection.AddSingleton<IFolderWriteAccess>(static _ => new FolderWriteAccess());
        serviceCollection.AddSingleton<MovePreflight>();

        // Move's paths: the locations of the libraries the caller can open, the volume a path is on (its
        // GUID, never the drive letter), and a client's destination resolved to a folder inside one, never an arbitrary path.
        serviceCollection.AddSingleton<LibraryLocationCatalog>();
        serviceCollection.AddSingleton<IVolumeInfoProvider>(static _ => new VolumeInfoProvider());
        serviceCollection.AddSingleton(static provider => new DestinationResolver(
            null,
            () => provider.GetRequiredService<ILibraryManager>().GetVirtualFolders().SelectMany(static f => f.Locations ?? [])));

        // The unit planner: what moves, where to, how, its predicted ids and its guards. Jellyfin's own
        // NamingOptions when the host registers them, and IFileSystem.GetValidFilename for wrap names.
        serviceCollection.AddSingleton(static provider => new UnitPlanner(
            provider.GetRequiredService<ILibraryManager>(),
            provider.GetRequiredService<IVolumeInfoProvider>(),
            provider.GetRequiredService<IFileSystem>().GetValidFilename,
            provider.GetService<NamingOptions>()));

        // Planning (MovePlanService): an item's source, its destinations and their folders, and a plan kept 15
        // minutes under an id and a hash; the "Kept" summary reads the users' data as a preview (the job snapshots it).
        serviceCollection.AddSingleton<MoveDestinations>();
        serviceCollection.AddSingleton<MovePreservedSummary>();
        serviceCollection.AddSingleton<MovePlanService>();
    }
}
