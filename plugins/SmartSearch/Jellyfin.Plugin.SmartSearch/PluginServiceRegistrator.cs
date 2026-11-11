using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using Jellyfin.Plugin.SmartSearch.Configuration;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Fields;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using Jellyfin.Plugin.SmartSearch.Query.Validation;
using Jellyfin.Plugin.SmartSearch.Search;
using Jellyfin.Plugin.SmartSearch.Services;
using Jellyfin.Plugin.SmartSearch.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch;

/// <summary>
/// Registers the plugin's services with the server. Never throws.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // First statement: on a Jellyfin line this build isn't for, register nothing but the status.
        if (!KitAbiGuard.Register(serviceCollection, applicationHost, SmartSearchKit.Options))
        {
            return;
        }

        serviceCollection.AddKit(SmartSearchKit.Options);

        // The web UI: the kit's middleware adds the script tag to index.html on every request, and the kit
        // builds the script it loads from kit.js and Web/js.
        serviceCollection.AddIndexHtmlInjection();
        serviceCollection.AddEmbeddedScript();

        // Page hosting: the shell of GET /SmartSearch/Pages/search and the Plugin Pages registration in
        // Auto/PluginPagesOnly mode.
        serviceCollection.AddKitPages();

        // Diagnostics: the ClientHealth reports and the admin Diagnostics.
        serviceCollection.AddKitDiagnostics();

        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();

        // The field registry: one immutable instance for the validator, the canonicalizer and /Fields.
        serviceCollection.AddSingleton<FieldRegistry>();
        serviceCollection.AddSingleton<IFieldCatalog>(services => services.GetRequiredService<FieldRegistry>());
        serviceCollection.AddSingleton<ISmartSearchFields>(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new SmartSearchFields(
                services.GetRequiredService<IFieldCatalog>(),
                () => EngineSettings.AllowedItemKinds(configuration.GetConfiguration()),
                () => configuration.GetConfiguration().EnableSearchPage,
                () => configuration.GetConfiguration().AllowQuerySharing,
                () => configuration.GetConfiguration().EnableAutoCollections);
        });

        // The planner: the kit's library access, and what it
        // reads from core's database to decide what is safe to push down. The clean-form probe runs after startup and in
        // every scan (the kit's library-change monitor, flushed by Query/Planning/SmartSearchPostScanTask).
        serviceCollection.AddKitLibraryAccess();
        serviceCollection.AddKitLibraryChanges();
        serviceCollection.AddSingleton(services => new DatabasePushdownFacts(
            services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>(),
            SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Planner"),
            SmartSearchKit.Options.Clock(services),
            services.GetService<IUserDataManager>(),
            services.GetService<ILibraryManager>()));
        serviceCollection.AddSingleton<IPushdownFacts>(services => services.GetRequiredService<DatabasePushdownFacts>());
        serviceCollection.AddSingleton(services => new CleanFormProbe(
            services.GetRequiredService<IServerApplicationHost>(),
            services.GetRequiredService<DatabasePushdownFacts>(),
            SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "CleanFormProbe")));
        serviceCollection.AddHostedService(services => services.GetRequiredService<CleanFormProbe>());
        serviceCollection.AddKitLibraryConsumer<CleanFormProbe>();
        serviceCollection.AddSingleton(services => new QueryPlanner(
            services.GetRequiredService<FieldRegistry>(),
            services.GetRequiredService<LibraryAccess>(),
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IPushdownFacts>(),
            services.GetRequiredService<ValueIndexService>()));

        // The engine, behind the search endpoint (and the auto-collections later). No facts source
        // yet (no field of this version needs facts). MaxConcurrentSearches is read once, here; the other settings on each search.
        serviceCollection.AddSingleton(services => new QueryCanonicalizer(services.GetRequiredService<IFieldCatalog>()));
        serviceCollection.AddSingleton(services => new ResidualEvaluator(
            services.GetRequiredService<FieldRegistry>(),
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IUserDataManager>(),
            services.GetServices<IItemFactsSource>()));
        serviceCollection.AddSingleton(services => new PageHydrator(
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IDtoService>(),
            services.GetRequiredService<LibraryAccess>()));
        serviceCollection.AddSingleton(services => new ResultCache(
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IUserDataManager>(),
            services.GetRequiredService<IUserManager>(),
            SmartSearchKit.Options.Clock(services),
            () => TimeSpan.FromSeconds(EngineSettings.From(services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration()).ResultCacheSeconds),
            () => EngineSettings.From(services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration()).MaxCachedResultIds));
        serviceCollection.AddSingleton(services => new EngineSlots(
            EngineSettings.SearchSlots(services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration()),
            EngineSlots.DefaultWait));
        serviceCollection.AddSingleton<ISmartSearchEngine>(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            var localization = services.GetRequiredService<ILocalizationManager>();
            var clock = SmartSearchKit.Options.Clock(services);
            return new SmartSearchEngine(
                services.GetRequiredService<QueryPlanner>(),
                services.GetRequiredService<QueryCanonicalizer>(),
                services.GetRequiredService<LibraryAccess>(),
                services.GetRequiredService<ILibraryManager>(),
                services.GetRequiredService<ResidualEvaluator>(),
                services.GetRequiredService<PageHydrator>(),
                services.GetRequiredService<ResultCache>(),
                services.GetRequiredService<EngineSlots>(),
                () => EngineSettings.From(configuration.GetConfiguration()),
                () => EvaluationEnvironment.For(localization, clock),
                clock);
        });

        // The endpoints' services: the strict validator, the search, and the lookups (the value index per folder and
        // parental profile, the series picker), each read as the signed-in user.
        serviceCollection.AddSingleton(services => new QueryValidator(services.GetRequiredService<IFieldCatalog>()));
        serviceCollection.AddSingleton<ISmartSearchService>(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new SmartSearchService(
                services.GetRequiredService<ISmartSearchEngine>(),
                services.GetRequiredService<QueryValidator>(),
                services.GetRequiredService<LibraryAccess>(),
                services.GetRequiredService<ILibraryManager>(),
                () => EngineSettings.AllowedItemKinds(configuration.GetConfiguration()),
                () => configuration.GetConfiguration().EnableSearchPage);
        });
        // The value index also answers the planner's lookups in the caller's scope (series names, studios).
        serviceCollection.AddSingleton(services => new ValueIndexService(
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<LibraryAccess>(),
            SmartSearchKit.Options.Clock(services)));
        serviceCollection.AddSingleton(services => new SeriesTypeahead(services.GetRequiredService<ValueIndexService>(), services.GetRequiredService<LibraryAccess>()));
        serviceCollection.AddSingleton<ISmartSearchLookups>(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new SmartSearchLookups(
                services.GetRequiredService<LibraryAccess>(),
                services.GetRequiredService<IFieldCatalog>(),
                services.GetRequiredService<ValueIndexService>(),
                services.GetRequiredService<SeriesTypeahead>(),
                () => EngineSettings.AllowedItemKinds(configuration.GetConfiguration()),
                () => configuration.GetConfiguration().EnableSearchPage);
        });
        serviceCollection.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new SearchPageSwitch(() => configuration.GetConfiguration().EnableSearchPage);
        });

        // The saved queries: the kit's JSON store (data/smartsearch/saved-queries.json, its snapshots beside it) as the
        // owner of its users' data (the deleted-user policy, the reconciliation, remap by name), the reference guard
        // (the auto-collections that follow saved queries), and the endpoints' services.
        serviceCollection.AddKitUsers();
        // The auto-collections' definitions (data/smartsearch/auto-collections.json, an owner of its users' data that never
        // deletes a definition with a user), registered before the saved-query store: the reconciler visits its owners in
        // this order, which is the lock order (reconciler, then the definitions, then the saved queries).
        serviceCollection.AddSingleton(services =>
        {
            var paths = services.GetRequiredService<KitPaths>();
            return new AutoCollectionStore(
                Path.Combine(paths.Root, AutoCollectionStore.FileName),
                paths.Snapshots,
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Storage"),
                SmartSearchKit.Options.Clock(services));
        });
        serviceCollection.AddKitUserDataOwner<AutoCollectionStore>();
        serviceCollection.AddKitDiagnosticsSource("AutoCollections", services => KitDiagnostics.DescribeStore(services.GetRequiredService<AutoCollectionStore>().Documents));
        // The saved queries' reference guard: the auto-collection definitions that follow a saved query take part in its
        // every change (the guard's lock is the definitions store's gate, taken before the saved queries' lock).
        serviceCollection.AddSingleton<ISavedQueryReferenceGuard>(services => new DefinitionReferenceGuard(
            services.GetRequiredService<AutoCollectionStore>(),
            services.GetRequiredService<SavedQueryStore>(),
            services.GetRequiredService<AutoCollectionChecks>(),
            services.GetRequiredService<AutoCollectionCheckContextSource>(),
            SmartSearchKit.Options.Clock(services),
            SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections")));
        serviceCollection.AddSingleton(services =>
        {
            var paths = services.GetRequiredService<KitPaths>();
            return new SavedQueryStore(
                Path.Combine(paths.Root, SavedQueryStore.FileName),
                paths.Snapshots,
                () => services.GetRequiredService<ISavedQueryReferenceGuard>(),
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Storage"),
                SmartSearchKit.Options.Clock(services));
        });
        serviceCollection.AddKitUserDataOwner<SavedQueryStore>();
        serviceCollection.AddKitDiagnosticsSource("SavedQueries", services => KitDiagnostics.DescribeStore(services.GetRequiredService<SavedQueryStore>().Documents));
        serviceCollection.AddSingleton(services => new SavedQueryReader(services.GetRequiredService<QueryValidator>(), services.GetRequiredService<QueryCanonicalizer>()));
        serviceCollection.AddSingleton<ISmartSearchSavedQueries>(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new SavedQueryService(
                services.GetRequiredService<SavedQueryStore>(),
                services.GetRequiredService<SavedQueryReader>(),
                () => services.GetRequiredService<ISavedQueryReferenceGuard>(),
                services.GetRequiredService<QueryValidator>(),
                services.GetRequiredService<ISmartSearchService>(),
                services.GetRequiredService<IUserManager>(),
                () => SavedQuerySettings.From(configuration.GetConfiguration()),
                SmartSearchKit.Options.Clock(services),
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "SavedQueries"));
        });
        serviceCollection.AddSingleton<ISmartSearchSavedQueriesAdmin>(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new SavedQueryAdmin(
                services.GetRequiredService<SavedQueryStore>(),
                services.GetRequiredService<SavedQueryReader>(),
                () => services.GetRequiredService<ISavedQueryReferenceGuard>(),
                services.GetRequiredService<UserReconciler>(),
                services.GetRequiredService<IUserManager>(),
                services.GetRequiredService<IServerApplicationHost>(),
                () => configuration.GetConfiguration(),
                () => SavedQuerySettings.From(configuration.GetConfiguration()),
                SmartSearchKit.Options.Clock(services),
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "SavedQueries"),
                () => KitComponents.CreateAll<IUserDataOwner>(services, SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Users")),
                () => services.GetRequiredService<AutoCollectionTransfer>());
        });
        serviceCollection.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new AutoCollectionTransfer(
                services.GetRequiredService<AutoCollectionStore>(),
                () => AutoCollectionSettings.From(configuration.GetConfiguration()),
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections"),
                services.GetRequiredService<SavedQueryStore>());
        });

        // The auto-collections' rebuildable run state, and the checks every save and run passes.
        serviceCollection.AddSingleton(services =>
        {
            var paths = services.GetRequiredService<KitPaths>();
            return new AutoCollectionStateStore(
                Path.Combine(paths.Root, AutoCollectionStateStore.FileName),
                paths.Snapshots,
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Storage"),
                SmartSearchKit.Options.Clock(services));
        });
        serviceCollection.AddSingleton(services => new AutoCollectionChecks(
            services.GetRequiredService<QueryValidator>(),
            services.GetRequiredService<QueryCanonicalizer>(),
            services.GetRequiredService<IFieldCatalog>()));
        serviceCollection.AddSingleton(services => new CollectionNameChecker(
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<ICollectionManager>(),
            services.GetRequiredService<IFileSystem>(),
            services.GetRequiredService<IServerApplicationPaths>(),
            services.GetRequiredService<AutoCollectionStore>(),
            SmartSearchKit.Options.Clock(services)));
        serviceCollection.AddSingleton(services => new MembershipPlanner(services.GetRequiredService<ISmartSearchEngine>(), services.GetRequiredService<LibraryAccess>(), services.GetRequiredService<ILibraryManager>()));
        serviceCollection.AddSingleton(services => new MixedAudienceCheck(services.GetRequiredService<LibraryAccess>(), services.GetRequiredService<IUserManager>(), services.GetRequiredService<ILibraryManager>()));
        serviceCollection.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new AutoCollectionCheckContextSource(
                services.GetRequiredService<AutoCollectionStore>(),
                services.GetRequiredService<SavedQueryStore>(),
                services.GetRequiredService<IUserManager>(),
                () => EngineSettings.AllowedItemKinds(configuration.GetConfiguration()),
                () => AutoCollectionSettings.From(configuration.GetConfiguration()));
        });

        // The collection writes: the server's own collection calls, one save and one refresh per changed collection.
        serviceCollection.AddSingleton<ICollectionPersistence>(services => new CollectionPersistence(
            services.GetRequiredService<ICollectionManager>(),
            services.GetRequiredService<IProviderManager>(),
            services.GetRequiredService<IFileSystem>(),
            services.GetRequiredService<ILibraryManager>()));
        serviceCollection.AddSingleton(services => new LinkResolver(services.GetRequiredService<ILibraryManager>(), services.GetRequiredService<LibraryAccess>()));
        serviceCollection.AddSingleton(services => new CollectionLocator(services.GetRequiredService<ILibraryManager>(), services.GetRequiredService<LibraryAccess>()));
        serviceCollection.AddSingleton(services => new AutoCollectionDiagnostics(
            services.GetRequiredService<AutoCollectionStore>(),
            services.GetRequiredService<AutoCollectionStateStore>(),
            services.GetRequiredService<CollectionLocator>()));
        serviceCollection.AddKitDiagnosticsSource("AutoCollectionRuns", services => services.GetRequiredService<AutoCollectionDiagnostics>().Describe());
        serviceCollection.AddSingleton(services => new DirectReplaceWriter(
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<ICollectionPersistence>(),
            services.GetRequiredService<CollectionLocator>(),
            SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections")));
        serviceCollection.AddSingleton(services => new MassRemovalGuard(services.GetRequiredService<ILibraryManager>()));

        // Pins and excludes: edits made outside the plugin, moved items (Library Maintenance's journal) and gone ones.
        serviceCollection.AddKitIdentity();
        serviceCollection.AddSingleton<CollectionWriteSuppression>();
        serviceCollection.AddSingleton(services => new ManagerApiWriter(
            services.GetRequiredService<ICollectionPersistence>(),
            services.GetRequiredService<DirectReplaceWriter>(),
            services.GetRequiredService<CollectionWriteSuppression>(),
            SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections")));
        serviceCollection.AddSingleton(services => new ManualEditKeeper(
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IdentityResolver>(),
            services.GetRequiredService<MoveJournalReader>()));
        serviceCollection.AddSingleton<ICollectionImages>(services => new CollectionImages(
            services.GetRequiredService<IProviderManager>(),
            services.GetRequiredService<ILibraryManager>()));
        serviceCollection.AddSingleton(services => new AutoCollectionDefaultImage(
            services.GetRequiredService<ICollectionImages>(),
            SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections")));
        serviceCollection.AddSingleton(services => new DefinitionRunner(
            services.GetRequiredService<AutoCollectionStore>(),
            services.GetRequiredService<AutoCollectionStateStore>(),
            services.GetRequiredService<AutoCollectionChecks>(),
            services.GetRequiredService<AutoCollectionCheckContextSource>(),
            services.GetRequiredService<CollectionLocator>(),
            services.GetRequiredService<LinkResolver>(),
            services.GetRequiredService<MembershipPlanner>(),
            services.GetRequiredService<MixedAudienceCheck>(),
            services.GetRequiredService<CollectionNameChecker>(),
            services.GetRequiredService<DirectReplaceWriter>(),
            services.GetRequiredService<MassRemovalGuard>(),
            SmartSearchKit.Options.Clock(services),
            SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections"),
            services.GetRequiredService<ManualEditKeeper>(),
            services.GetRequiredService<ManagerApiWriter>(),
            services.GetRequiredService<AutoCollectionDefaultImage>()));
        // The worker: every run and pass, one at a time, after the server's startup.
        serviceCollection.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            var library = services.GetRequiredService<ILibraryManager>();
            return new AutoCollectionWorker(
                services.GetRequiredService<IServerApplicationHost>(),
                services.GetRequiredService<DefinitionRunner>(),
                services.GetRequiredService<AutoCollectionStore>(),
                services.GetRequiredService<AutoCollectionStateStore>(),
                () => AutoCollectionSettings.From(configuration.GetConfiguration()),
                () => library.IsScanRunning,
                SmartSearchKit.Options.Clock(services),
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections"));
        });
        serviceCollection.AddSingleton<IAutoCollectionQueue>(services => services.GetRequiredService<AutoCollectionWorker>());
        serviceCollection.AddHostedService(services => services.GetRequiredService<AutoCollectionWorker>());

        // The triggers: library changes (the monitor's batches, and the full pass inside every scan, which only queues a
        // pass), the startup pass and changed saved queries. The daily task is AutoCollections/RefreshAutoCollectionsTask.
        serviceCollection.AddSingleton(services => new AutoCollectionsChangeConsumer(
            services.GetRequiredService<AutoCollectionStore>(),
            services.GetRequiredService<AutoCollectionChecks>(),
            services.GetRequiredService<AutoCollectionCheckContextSource>(),
            services.GetRequiredService<IAutoCollectionQueue>(),
            SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections"),
            id => services.GetRequiredService<AutoCollectionStateStore>().Find(id)?.CreatePending == true));
        serviceCollection.AddKitLibraryConsumer<AutoCollectionsChangeConsumer>();
        serviceCollection.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            return new AutoCollectionTriggers(
                services.GetRequiredService<IServerApplicationHost>(),
                services.GetRequiredService<IAutoCollectionQueue>(),
                services.GetRequiredService<AutoCollectionStore>(),
                services.GetRequiredService<SavedQueryStore>(),
                () => AutoCollectionSettings.From(configuration.GetConfiguration()),
                SmartSearchKit.Options.Clock(services),
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections"),
                id => services.GetRequiredService<AutoCollectionStateStore>().Find(id)?.CreatePending == true);
        });
        serviceCollection.AddHostedService(services => services.GetRequiredService<AutoCollectionTriggers>());
        serviceCollection.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            var library = services.GetRequiredService<ILibraryManager>();
            return new CollectionEditListener(
                services.GetRequiredService<IServerApplicationHost>(),
                services.GetRequiredService<ICollectionManager>(),
                library,
                services.GetRequiredService<AutoCollectionStore>(),
                services.GetRequiredService<AutoCollectionStateStore>(),
                services.GetRequiredService<IAutoCollectionQueue>(),
                services.GetRequiredService<ManualEditKeeper>(),
                services.GetRequiredService<CollectionWriteSuppression>(),
                () => AutoCollectionSettings.From(configuration.GetConfiguration()),
                () => library.IsScanRunning,
                SmartSearchKit.Options.Clock(services),
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections"));
        });
        serviceCollection.AddHostedService(services => services.GetRequiredService<CollectionEditListener>());
        serviceCollection.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IPluginConfigurationProvider>();
            var library = services.GetRequiredService<ILibraryManager>();
            return new UserDataTrigger(
                services.GetRequiredService<IServerApplicationHost>(),
                services.GetRequiredService<IUserDataManager>(),
                services.GetRequiredService<AutoCollectionStore>(),
                services.GetRequiredService<IAutoCollectionQueue>(),
                () => AutoCollectionSettings.From(configuration.GetConfiguration()),
                () => library.IsScanRunning,
                SmartSearchKit.Options.Clock(services),
                SmartSearchKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "AutoCollections"));
        });
        serviceCollection.AddHostedService(services => services.GetRequiredService<UserDataTrigger>());
        serviceCollection.AddSingleton(services => new PreviewLedger(SmartSearchKit.Options.Clock(services)));
        serviceCollection.AddSingleton<ISmartSearchAutoCollections>(services => new AutoCollectionService(new AutoCollectionServiceParts
        {
            Names = services.GetRequiredService<CollectionNameChecker>(),
            Store = services.GetRequiredService<AutoCollectionStore>(),
            State = services.GetRequiredService<AutoCollectionStateStore>(),
            Checks = services.GetRequiredService<AutoCollectionChecks>(),
            Context = services.GetRequiredService<AutoCollectionCheckContextSource>(),
            Planner = services.GetRequiredService<MembershipPlanner>(),
            MixedAudience = services.GetRequiredService<MixedAudienceCheck>(),
            Hydrator = services.GetRequiredService<PageHydrator>(),
            Access = services.GetRequiredService<LibraryAccess>(),
            Runner = services.GetRequiredService<DefinitionRunner>(),
            Queue = services.GetRequiredService<IAutoCollectionQueue>(),
            Locator = services.GetRequiredService<CollectionLocator>(),
            Links = services.GetRequiredService<LinkResolver>(),
            SavedQueries = services.GetRequiredService<SavedQueryStore>(),
            SavedQueryService = () => services.GetRequiredService<ISmartSearchSavedQueries>(),
            Ledger = services.GetRequiredService<PreviewLedger>(),
            Time = SmartSearchKit.Options.Clock(services),
            Guard = services.GetRequiredService<MassRemovalGuard>(),
            ScanRunning = () => services.GetRequiredService<ILibraryManager>().IsScanRunning,
            Keeper = services.GetRequiredService<ManualEditKeeper>(),
            MaxSavedQueriesPerUser = () => SavedQuerySettings.From(services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration()).MaxPerUser
        }));

        // The daily maintenance (Storage/SmartSearchMaintenanceTask): the snapshot of the saved queries, then the kit's
        // user reconciliation.
        serviceCollection.AddKitMaintenance();
        serviceCollection.AddKitMaintenanceStep("Snapshots", KitMaintenanceOrder.Snapshots, SnapshotAsync);
    }

    // The daily snapshot of saved-queries.json: SnapshotsToKeep of them, none while the store is read-only or with 0.
    private static async Task<IDictionary<string, object?>?> SnapshotAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var keep = Math.Max(0, services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration().SnapshotsToKeep);
        var store = services.GetRequiredService<SavedQueryStore>();
        var path = await store.Documents.SnapshotAsync(store.SnapshotDirectory, keep, cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Keep"] = keep,
            ["Snapshot"] = path is null ? null : Path.GetFileName(path),
            ["Skipped"] = path is not null ? null : store.IsReadOnly ? "ReadOnly" : keep == 0 ? "SnapshotsToKeep is 0" : "NotWritten"
        };
    }
}
