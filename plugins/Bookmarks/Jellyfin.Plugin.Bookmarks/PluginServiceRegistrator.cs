using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Integration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Storage.Migrations;
using Jellyfin.Plugin.Bookmarks.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks;

/// <summary>
/// Registers the plugin's services with the server.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // First statement: on a Jellyfin line this build isn't for, register nothing but the status.
        if (!KitAbiGuard.Register(serviceCollection, applicationHost, BookmarksKit.Options))
        {
            return;
        }

        serviceCollection.AddKit(BookmarksKit.Options);

        // The web UI: the kit's middleware adds the script tag to index.html on every request, and the kit builds the
        // script it loads from kit.js and Web/js.
        serviceCollection.AddIndexHtmlInjection();
        serviceCollection.AddEmbeddedScript();

        // Page hosting: the shells of GET /Bookmarks/Pages/{key}, and the Plugin Pages registration in
        // Auto/PluginPagesOnly mode, for My Bookmarks, My Clips and the developer demo's pages (BookmarksKit).
        serviceCollection.AddKitPages();

        // Diagnostics: the ClientHealth reports browsers send when a jellyfin-web anchor is
        // missing, and the admin Diagnostics body with the store's status.
        serviceCollection.AddKitDiagnostics();
        serviceCollection.AddKitDiagnosticsSource("Store", services => KitDiagnostics.DescribeStore(services.GetRequiredService<IBookmarkStore>().Documents));

        // Jellyfin Enhanced interop: the reader of JE's settings and files (read-only), the shortcut policy
        // the capabilities' ReservedShortcuts and the settings page's warning come from, and the Diagnostics entry
        // JellyfinEnhanced with the overlap banner, the switch-off checklist and "Use B" (BookmarksKit names the feature and
        // the shortcut setting).
        serviceCollection.AddKitJellyfinEnhanced();
        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();
        serviceCollection.AddSingleton<BookmarkPaths>();
        // Schema 2: a 1.0 file is upgraded on first use, with the clip folders and user names seeded from the plugin's
        // paths, its settings and the server's users.
        serviceCollection.AddSingleton<IBookmarkStore>(services =>
        {
            var paths = services.GetRequiredService<BookmarkPaths>();
            var logger = BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Storage");
            var migration = BookmarkStoreMigration1To2.Create(
                paths,
                services.GetRequiredService<IPluginConfigurationProvider>(),
                () => services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
                logger);
            return BookmarkStore.Create(paths.StoreFile, BookmarkStore.Migrations(migration), logger);
        });

        // While the store is read-only (a newer Bookmarks' file, or an upgrade that failed), every
        // automatic writer stays idle: the clip worker, the janitor, the identity service, the deleted-user policy and the
        // maintenance's own steps.
        serviceCollection.AddSingleton<BackgroundWriteGate>();

        // Clip files: every delete, move and serve goes through ClipFileOps and its guard, which tells the My Clips list's
        // ClipStateCache (below) about every file it moves into place or deletes. When the clip worker starts, the janitor
        // deletes the stale .partial files of jobs an earlier run didn't finish and retries the files an automatic delete
        // couldn't delete. Since 1.2.0 it also confirms a clip folder (an admin's, or the default folder made again after it
        // was deleted), marking its clips missing on the lifecycle's clock.
        serviceCollection.AddSingleton(services => new ClipFileOps(
            services.GetRequiredService<IBookmarkStore>(),
            services.GetRequiredService<BackgroundWriteGate>(),
            services.GetRequiredService<ClipStateCache>(),
            services.GetRequiredService<BookmarkPaths>(),
            services.GetRequiredService<ILogger<ClipFileOps>>(),
            BookmarksKit.Options.Clock(services)));
        serviceCollection.AddSingleton<PartialFileJanitor>();

        // The free-space guard: the probe (GetDiskFreeSpaceExW on the nearest existing folder), the checks when a clip is
        // asked for and when it starts, the watchdog, the disk-full wording, and Diagnostics' ClipStorage entry for the
        // current clip folder, with its free-space line and warnings.
        serviceCollection.AddSingleton<IDriveSpaceProbe, DriveSpaceProbe>();
        serviceCollection.AddSingleton<ClipStorageGuard>();
        serviceCollection.AddKitDiagnosticsSource("ClipStorage", services => ClipFolderWarnings(services, services.GetRequiredService<ClipStorageGuard>().DescribeCurrentRoot()));

        // The clip file renames not finished: how many wait, the journal entries that can't be read (an
        // admin looks at those), the clips they hold; the renames' history kept for a restore.
        serviceCollection.AddKitDiagnosticsSource("ClipRenames", services => services.GetRequiredService<ClipFileOps>().DescribeRenames());

        serviceCollection.AddSingleton<IFfmpegRunner, FfmpegRunner>();
        // A new clip's name follows the renames' rule of taken names (any record's path, a file waiting to be deleted, a
        // rename's paths, besides a file there and a .partial).
        serviceCollection.AddSingleton<IClipPlanner>(services => new ClipPlanner(
            services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.IMediaSourceManager>(),
            services.GetRequiredService<MediaBrowser.Controller.MediaEncoding.IMediaEncoder>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<BookmarkPaths>(),
            services.GetRequiredService<ClipFileOps>()));
        serviceCollection.AddSingleton<ClipExtractionService>();
        serviceCollection.AddSingleton<IClipExtractionService>(services => services.GetRequiredService<ClipExtractionService>());
        serviceCollection.AddHostedService(services => services.GetRequiredService<ClipExtractionService>());

        serviceCollection.AddSingleton<IBookmarkManager, BookmarkManager>();

        // The My Bookmarks page's lists (filtered and ordered in memory, the library asked only for the page's
        // ItemAvailable, as the caller) and the users' own naming preferences.
        serviceCollection.AddSingleton<IBookmarkQuery, BookmarkQueryService>();
        serviceCollection.AddSingleton<IBookmarkPreferences, BookmarkPreferencesService>();

        // The My Clips page's endpoints: the user's clips, live bookmarks' and those kept when
        // a bookmark was deleted, with what each is right now (the clip files' states kept 30 s, the libraries' folders 60 s),
        // their numbers, the state of the clips being made, and a delete of several, file first. LibraryAccess (the kit's,
        // below) gives the libraries the caller may open, for a library's name.
        serviceCollection.AddSingleton<ClipStateCache>();
        serviceCollection.AddSingleton<ClipLibraryLocations>();

        // The warning that a clip folder is in a Movies or TV library (since 1.2.1): the settings page's check as the admin
        // types, and Diagnostics' ClipStorage.
        serviceCollection.AddSingleton(services => new ClipFolderInLibrary(
            services.GetRequiredService<ClipLibraryLocations>(),
            services.GetRequiredService<LibraryAccess>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            services.GetRequiredService<BookmarkPaths>(),
            BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Clips")));
        serviceCollection.AddSingleton<ClipQueryService>();
        serviceCollection.AddSingleton<IBookmarkClips, ClipService>();

        // The admins' "All users" scope of My Clips: every user's clips, gated by
        // AdminsCanManageAllClips, over the same list, caches and clip file operations.
        serviceCollection.AddSingleton<IBookmarkAdminClips>(services => new AdminClipService(
            services.GetRequiredService<IBookmarkStore>(),
            services.GetRequiredService<ClipQueryService>(),
            services.GetRequiredService<ClipFileOps>(),
            services.GetRequiredService<IClipExtractionService>(),
            services.GetRequiredService<ClipLibraryLocations>(),
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<LibraryAccess>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<ILogger<AdminClipService>>(),
            BookmarksKit.Options.Clock(services)));

        // The missing-clip sweep, the daily maintenance's last step (AddBookmarkMaintenanceSteps, below):
        // clip files gone from a clip folder that is there marked missing, the marks of files that are back cleared, clip
        // folders that aren't there left out whole, and after MissingClipRetentionDays a deleted bookmark's kept clip moved
        // into the bookmarks' archive (the identity service owns it) and a bookmark's own clip entry cleared; never a file
        // deleted. Its last run is in Diagnostics' ClipSweep entry.
        serviceCollection.AddSingleton(services => new MissingClipSweep(
            services.GetRequiredService<IBookmarkStore>(),
            services.GetRequiredService<BackgroundWriteGate>(),
            services.GetRequiredService<ClipFileOps>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            () => services.GetRequiredService<DetachedRecords>(),
            () => services.GetRequiredService<BookmarkIdentityService>(),
            services.GetRequiredService<ILogger<MissingClipSweep>>(),
            BookmarksKit.Options.Clock(services)));
        serviceCollection.AddKitDiagnosticsSource("ClipSweep", services => services.GetRequiredService<MissingClipSweep>().Describe());

        // The clip folders for other plugins: Library Maintenance's Duplicates asks
        // Integration/ClipRootsV1, whose source this hosted service is after the startup gate, also while the data is
        // read-only; Diagnostics' ClipRoots entry shows what it answers.
        serviceCollection.AddHostedService<ClipRootsFacadeService>();
        serviceCollection.AddKitDiagnosticsSource("ClipRoots", _ => ClipRootsFacadeService.Describe());

        // Item identity: the move-journal reader and the resolver, which Bookmarks' identity service
        // (below) and the developer identity demo behind EnableIdentityDemo (off by default) use.
        serviceCollection.AddKitIdentity();
        serviceCollection.AddSingleton<IBookmarkIdentityDemo>(services => new BookmarkIdentityDemo(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            services.GetRequiredService<IdentityResolver>(),
            services.GetRequiredService<MoveJournalReader>()));

        // Library changes: the kit's monitor batches Jellyfin's item events for the consumers below, each
        // created on its own (one that can't be created costs only itself, and is tried again every minute), and
        // Library/BookmarksPostScanTask flushes it as a full pass inside every scan (before Jellyfin's own post-scan tasks,
        // whose changes come in the batch right after the scan). The consumers: Bookmarks' identity service (below), and the
        // developer demos behind EnableLibraryChangeDemo and EnableLibraryDemo (off by default, taking no change then).
        serviceCollection.AddKitLibraryChanges();
        serviceCollection.AddSingleton(services => new BookmarkLibraryChangeDemo(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>()));
        serviceCollection.AddKitLibraryConsumer<BookmarkLibraryChangeDemo>();
        serviceCollection.AddSingleton<IBookmarkLibraryChangeDemo>(services => services.GetRequiredService<BookmarkLibraryChangeDemo>());

        // Users and data: the deleted-user policy on Jellyfin's UserDeletedEventArgs (KeepAsOrphan by
        // default: bookmarks without a clip file deleted, those with one kept as tombstones), the reconciler (orphans only,
        // never a deletion), and the Users entry of Diagnostics. The owners, each created on its own: the bookmark store
        // and the developer user data demo behind EnableUserDataDemo (off by default, holding nothing then).
        serviceCollection.AddKitUsers();
        serviceCollection.AddSingleton(services => new BookmarkUserDataOwner(
            services.GetRequiredService<IBookmarkStore>(),
            services.GetRequiredService<BackgroundWriteGate>(),
            services.GetRequiredService<ClipFileOps>(),
            () => services.GetRequiredService<IClipExtractionService>(),
            services.GetRequiredService<BookmarkPaths>().SnapshotsDirectory,
            BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Users"),
            BookmarksKit.Options.Clock(services),
            () => services.GetRequiredService<DetachedRecords>(),
            () => services.GetRequiredService<BookmarkIdentityService>()));
        serviceCollection.AddKitUserDataOwner<BookmarkUserDataOwner>();
        serviceCollection.AddSingleton(services => new UserDataDemoOwner(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<BookmarkPaths>().UserDataDemoFile,
            services.GetRequiredService<BookmarkPaths>().SnapshotsDirectory,
            BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Storage")));
        serviceCollection.AddKitUserDataOwner<UserDataDemoOwner>();
        serviceCollection.AddSingleton<IBookmarkUserDataDemo>(services => new BookmarkUserDataDemo(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<UserDataDemoOwner>(),
            services.GetRequiredService<UserReconciler>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            applicationHost,
            BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "UserDataDemo")));

        // The record lifecycle and library reads: the verify sweep (at startup after the gate, after library changes with
        // removed folders, daily), the detached records with their archive in data/bookmarks/archive, LibraryAccess and
        // FreshUserData, and the daily maintenance that Tasks/BookmarksMaintenanceTask runs at 03:10: the snapshot of
        // bookmarks.json, the kit's steps (the user reconciliation among them), the clip-file retries and the missing-clip
        // sweep.
        serviceCollection.AddKitLibraryAccess();
        serviceCollection.AddKitVerifier();
        serviceCollection.AddKitDetachedRecords();
        serviceCollection.AddKitMaintenance();
        serviceCollection.AddBookmarkMaintenanceSteps();

        serviceCollection.AddSingleton(services => new LibraryDemoRecords(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            () => services.GetRequiredService<AttachedRecordVerifier>(),
            () => services.GetRequiredService<DetachedRecords>(),
            services.GetRequiredService<BookmarkPaths>().LibraryDemoFile,
            services.GetRequiredService<BookmarkPaths>().SnapshotsDirectory,
            BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "LibraryDemo"),
            BookmarksKit.Options.Clock(services)));
        // The kit creates each record owner and consumer on its own, so a demo that can't be created never stops the bookmarks'.
        serviceCollection.AddKitRecordOwner<LibraryDemoRecords>();
        serviceCollection.AddKitLibraryConsumer<LibraryDemoRecords>();
        serviceCollection.AddSingleton<IBookmarkLibraryDemo>(services => new BookmarkLibraryDemo(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.IUserDataManager>(),
            services.GetRequiredService<LibraryDemoRecords>(),
            services.GetRequiredService<AttachedRecordVerifier>(),
            services.GetRequiredService<DetachedRecords>(),
            services.GetRequiredService<LibraryAccess>(),
            services.GetRequiredService<FreshUserData>(),
            applicationHost,
            BookmarksKit.Options.Clock(services)));

        // Bookmarks' identity service: the identity backfill and the move-journal catch-up after the
        // startup gate (hosted), the library-change consumer (detach, the verify sweep after removed folders, refresh,
        // reattach with the runtime-checked relink policy) and the bookmark store as the kit's record owner; idle while the
        // data is read-only. Library Maintenance reaches it through Integration/ItemMovedHookV1. It comes after the developer
        // demos: while a demo is on, the demo is the first owner and consumer, so its sandbox checks see its records detached
        // by its own consumer before the verify sweep; while it is off (always, outside development) it takes no change and
        // the kit leaves it alone, so the order changes nothing.
        serviceCollection.AddSingleton(services => new BookmarkIdentityService(
            applicationHost,
            services.GetRequiredService<IBookmarkStore>(),
            services.GetRequiredService<BackgroundWriteGate>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            services.GetRequiredService<MoveJournalReader>(),
            () => services.GetRequiredService<AttachedRecordVerifier>(),
            () => services.GetRequiredService<DetachedRecords>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<BookmarkPaths>().SnapshotsDirectory,
            BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Identity"),
            BookmarksKit.Options.Clock(services)));
        serviceCollection.AddHostedService(services => services.GetRequiredService<BookmarkIdentityService>());
        serviceCollection.AddKitRecordOwner<BookmarkIdentityService>();
        serviceCollection.AddKitLibraryConsumer<BookmarkIdentityService>();
        serviceCollection.AddKitDiagnosticsSource("Identity", services => services.GetRequiredService<BookmarkIdentityService>().Describe());
        serviceCollection.AddSingleton<IBookmarkRecordsAdmin>(services => new BookmarkRecordsAdmin(
            services.GetRequiredService<UserReconciler>(),
            services.GetRequiredService<DetachedRecords>(),
            services.GetRequiredService<BookmarkIdentityService>()));

        // The Jellyfin Enhanced bookmark import: every user's JE bookmark.json through the kit's
        // reader (read-only), matched as each bookmark's owner through the kit's resolver, a dry run first, then one write of
        // the bookmarks with the ledger; Diagnostics' JeImport entry carries the settings page's gate for the switch-off
        // checklist (an applied import, not a dry run), and the capabilities tell an admin when JE bookmarks wait to be imported.
        serviceCollection.AddSingleton(services => new JellyfinEnhancedBookmarkImporter(
            services.GetRequiredService<IBookmarkStore>(),
            services.GetRequiredService<JellyfinEnhancedReader>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.ILibraryManager>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            services.GetRequiredService<IdentityResolver>(),
            services.GetRequiredService<MoveJournalReader>(),
            services.GetRequiredService<BookmarkPaths>().SnapshotsDirectory,
            BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "JeImport"),
            BookmarksKit.Options.Clock(services)));
        serviceCollection.AddSingleton<IBookmarkJeImport>(services => services.GetRequiredService<JellyfinEnhancedBookmarkImporter>());
        serviceCollection.AddKitDiagnosticsSource("JeImport", services => services.GetRequiredService<JellyfinEnhancedBookmarkImporter>().Describe());

        // Each user's bookmarks counted for the admin: counts and sizes, never a bookmark's text, and bookmarks.json's size
        // with a warning above 3 MB.
        serviceCollection.AddKitDiagnosticsSource("BookmarkCounts", services => BookmarkCounts.Describe(
            services.GetRequiredService<IBookmarkStore>(),
            services.GetService<MediaBrowser.Controller.Library.IUserManager>()));

        // The settings page's tools: export and import of the bookmark data with its archive, the snapshots and Restore
        // snapshot, the daily maintenance run now, and the bookmark shortcut's check through the kit's shortcut policy;
        // since 1.2.0 an admin's confirmation of a clip folder the
        // missing-clip sweep left out ("This is the clip folder; its clips are gone").
        serviceCollection.AddSingleton<IBookmarkAdminTools>(services => new BookmarkAdminTools(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<IBookmarkStore>(),
            services.GetRequiredService<BookmarkPaths>().SnapshotsDirectory,
            services.GetRequiredService<UserReconciler>(),
            services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>(),
            services.GetRequiredService<DetachedRecords>(),
            services.GetRequiredService<BookmarkIdentityService>(),
            () => services.GetService<AttachedRecordVerifier>(),
            services.GetRequiredService<JellyfinEnhancedDiagnostics>(),
            () => services.GetService<MediaBrowser.Model.Tasks.ITaskManager>(),
            applicationHost.SystemId,
            BookmarksKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Data"),
            BookmarksKit.Options.Clock(services),
            () => services.GetService<MissingClipSweep>(),
            services.GetRequiredService<ClipFileOps>(),
            () => services.GetService<ClipFolderInLibrary>()));
    }

    // Diagnostics' ClipStorage with the warning that the clip folder is in a Movies or TV library (since 1.2.1), when the
    // libraries' folders can be read here.
    private static IDictionary<string, object?> ClipFolderWarnings(IServiceProvider services, IDictionary<string, object?> clipStorage)
        => services.GetService<ClipFolderInLibrary>()?.AddTo(clipStorage) ?? clipStorage;
}
