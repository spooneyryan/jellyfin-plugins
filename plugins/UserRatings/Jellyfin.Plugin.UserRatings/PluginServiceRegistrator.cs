using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.UserRatings;

/// <summary>
/// Registers the plugin's services with the server. Never throws:
/// it only adds descriptors; every service is built when first used.
/// </summary>
/// <remarks>
/// Development build 0.3.3 registers the kit, the web client's script and its <c>index.html</c> tag, Diagnostics,
/// the SQLite store: <c>data/userratings/userratings.db</c> through the kit's <c>SqliteDatabase</c> (opened by its
/// hosted service after the startup gate, released at shutdown), the daily maintenance with its integrity check and
/// snapshot, and <see cref="IRatingsStore"/>; and the services: the settings reader, the kit's <c>LibraryAccess</c>
/// and <c>FreshUserData</c>, <see cref="ItemAccess"/>, <see cref="PlayedCheck"/>, <see cref="SummaryCache"/>,
/// <see cref="CopySiblings"/> and <see cref="RatingsService"/>; and the member endpoints' <see cref="IUserRatingsApi"/>;
/// and the library lifecycle: the kit's library changes, verify sweep and detached records with
/// <see cref="RatingsLibraryConsumer"/> as their consumer and owner, and the admin endpoints' <see cref="IUserRatingsAdminApi"/>.
/// Later builds add the archive (the kit's Archive and Reattach steps), the maintenance's MergedVersions step and the users: the
/// kit's users services with <see cref="RatingsUserDataOwner"/> as their owner. Moderation and the Top Rated page come
/// later. No registration names a Microsoft.Data.Sqlite type (kit README "SQLite", Isolation).
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>The Diagnostics entry with the store's counts.</summary>
    internal const string RecordsSource = "Records";

    /// <summary>The Diagnostics entry of the ratings lifecycle.</summary>
    internal const string LifecycleSource = "Lifecycle";

    /// <summary>The users' logger component: <c>Jellyfin.Plugin.UserRatings.Users</c>.</summary>
    internal const string UsersComponent = "Users";

    /// <summary>The moderation's logger component: <c>Jellyfin.Plugin.UserRatings.Moderation</c>.</summary>
    internal const string ModerationComponent = "Moderation";

    /// <summary>The log component of the Settings tab's saves.</summary>
    internal const string SettingsComponent = "Settings";

    /// <summary>The Data tab's log component: the jobs, the export, the import, Restore, Relink and Purge.</summary>
    internal const string DataComponent = "Data";

    /// <summary>The daily maintenance's gate: the data jobs' runner (since the 0.3.19 verification).</summary>
    internal const string MaintenanceGate = "DataJobs";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // First statement: on a Jellyfin line this build isn't for, register nothing but the status.
        if (!KitAbiGuard.Register(serviceCollection, applicationHost, UserRatingsKit.Options))
        {
            return;
        }

        serviceCollection.AddKit(UserRatingsKit.Options);
        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();
        serviceCollection.AddSingleton<RatingsSettingsReader>();

        // The web client: the kit's index.html middleware adds the ClientScript's tag
        // (data-jfur-plugin), and the kit builds the script from kit.js and Web/js.
        serviceCollection.AddIndexHtmlInjection();
        serviceCollection.AddEmbeddedScript();

        // Diagnostics: the browsers' ClientHealth reports and GET /UserRatings/Admin/Diagnostics.
        serviceCollection.AddKitDiagnostics();

        // Jellyfin Enhanced: the kit's read-only reader of JE's files and settings, and its Diagnostics
        // entry with the overlap banner and the switch-over checklist (UserRatingsKit names the feature, JeFeature.Reviews).
        serviceCollection.AddKitJellyfinEnhanced();

        // The daily "User Ratings: Maintenance" at 03:20, and the store:
        // userratings.db with its Diagnostics "Store", the maintenance's IntegrityCheck and Snapshots steps (SnapshotsToKeep,
        // read at each run), and the counts as Diagnostics "Records".
        serviceCollection.AddKitMaintenance();
        // The pre-operation snapshots and the archive's copies are kept 7 days, none older, deleted by the daily snapshot too;
        // every writable connection has the kit's secure_delete, so a deleted review leaves no bytes.
        serviceCollection.AddKitSqliteDatabase(RatingsSchema.DatabaseFile, RatingsSchema.Migrations, SnapshotsToKeep, preOpKeepWithin: RatingsUserDataOwner.PreOpKeepWithin, preOpCopies: [RatingsLibraryConsumer.StoreName + DetachedArchive.FileSuffix]);
        serviceCollection.AddSingleton<IRatingsStore, SqliteRatingsStore>();
        serviceCollection.AddKitDiagnosticsSource(RecordsSource, DescribeRecords);

        // The services: item access through the kit's LibraryAccess (one id per item, lists through its
        // EligibleIds), RequirePlayedToRate through the kit's FreshUserData, the summary
        // cache following the store's writes, the "rated on another copy" hint, and the member's reads and writes.
        serviceCollection.AddKitLibraryAccess();
        serviceCollection.AddSingleton<ItemAccess>();
        serviceCollection.AddSingleton<PlayedCheck>();
        serviceCollection.AddSingleton<SummaryCache>();
        serviceCollection.AddSingleton<CopySiblings>();

        // The archive's earlier lines of a rating whose review a moderator or its member removes: taken out right
        // after the commit, with no copy; one that fails waits for the daily ArchivePurges step below.
        serviceCollection.AddSingleton(static services => new ArchivedLinePurges(
            services.GetRequiredService<IRatingsStore>(),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), LifecycleSource),
            () => services.GetRequiredService<RatingsLibraryConsumer>().Archive));
        serviceCollection.AddSingleton(static services => new RatingsService(
            services.GetRequiredService<IRatingsStore>(),
            services.GetRequiredService<SummaryCache>(),
            services.GetRequiredService<ItemAccess>(),
            services.GetRequiredService<CopySiblings>(),
            services.GetRequiredService<PlayedCheck>(),
            services.GetRequiredService<RatingsSettingsReader>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<ILibraryManager>(),
            UserRatingsKit.Options.Clock(services),
            services.GetService<ILogger<RatingsService>>(),
            services.GetRequiredService<ArchivedLinePurges>()));

        // The member endpoints: the controller's one service, which maps RatingsService's records to the
        // DTOs and reads the authors' names and avatars per request.
        serviceCollection.AddSingleton<IUserRatingsApi>(static services => new UserRatingsApi(
            services.GetRequiredService<RatingsService>(),
            services.GetRequiredService<RatingsSettingsReader>(),
            services.GetRequiredService<IRatingsStore>(),
            services.GetRequiredService<IUserManager>(),
            services.GetService<IImageProcessor>(),
            services.GetService<ILogger<UserRatingsApi>>()));

        // The ratings lifecycle: the kit's library-change monitor, flushed as a full pass inside every
        // scan by Lifecycle/RatingsPostScanTask (Jellyfin's own post-scan changes follow in the batch after it); the move
        // journal and the resolver; the verify sweep (once after the startup gate, after a batch with removed folders, and
        // daily as the maintenance's VerifySweep step) and the detached records (the daily ResolveDetached step, and
        // the Archive and Reattach steps: ratings detached longer than the setting DetachedRetentionDays, read at every run,
        // move to the kit's archive and come back from it when their item does). RatingsLibraryConsumer is their consumer and owner,
        // and a hosted service for the move-journal catch-up (after the gate, and when Library Maintenance calls
        // Integration/ItemMovedHookV1), with Diagnostics "Lifecycle" and the Ambiguous banner's "seen" (the admin endpoints).
        // Each is created on its own by the kit, so a lifecycle that can't be created never stops ratings from working.
        serviceCollection.AddKitLibraryChanges();
        serviceCollection.AddKitVerifier();
        serviceCollection.AddKitDetachedRecords();
        serviceCollection.AddSingleton(static services => new RatingsLibraryConsumer(
            services.GetRequiredService<IServerApplicationHost>(),
            services.GetRequiredService<IRatingsStore>(),
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<MoveJournalReader>(),
            () => services.GetRequiredService<AttachedRecordVerifier>(),
            () => services.GetRequiredService<DetachedRecords>(),
            services.GetService<IActivityManager>(),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), LifecycleSource),
            UserRatingsKit.Options.Clock(services),
            () => services.GetRequiredService<RatingsSettingsReader>().Read().DetachedRetentionDays));
        serviceCollection.AddHostedService(static services => services.GetRequiredService<RatingsLibraryConsumer>());
        serviceCollection.AddKitRecordOwner<RatingsLibraryConsumer>();
        serviceCollection.AddKitLibraryConsumer<RatingsLibraryConsumer>();
        serviceCollection.AddKitDiagnosticsSource(LifecycleSource, static services => services.GetRequiredService<RatingsLibraryConsumer>().Describe());
        serviceCollection.AddSingleton<IUserRatingsAdminApi>(static services => new UserRatingsAdminApi(
            services.GetRequiredService<RatingsLibraryConsumer>(),
            () => services.GetRequiredService<UserReconciler>(),
            () => services.GetRequiredService<RatingsUserDataOwner>(),
            () => services.GetRequiredService<RatingsModeration>(),
            () => services.GetRequiredService<RatingsSettingsService>(),
            () => services.GetRequiredService<RatingsData>(),
            id => services.GetRequiredService<RatingsData>().HasAllLibraries(id)));

        // The Data tab: one data job at a time (the export and the envelope import, a dry run
        // first, its PlanToken, the import snapshot), the snapshots and Restore with the archive's copy, the detached list
        // with Relink and Purge. The runner stops with the server (a running job is cancelled; its write is one transaction).
        serviceCollection.AddSingleton(static services => new AdminJobRunner(
            UserRatingsKit.Options.Clock(services),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), DataComponent)));
        serviceCollection.AddSingleton(static services => new RatingsEnvelope(
            services.GetRequiredService<IRatingsStore>(),
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IUserManager>(),
            () => services.GetRequiredService<IdentityResolver>(),
            () => services.GetRequiredService<DetachedRecords>(),
            () => services.GetRequiredService<RatingsLibraryConsumer>(),
            () => services.GetRequiredService<RatingsUserDataOwner>().KnownUsers,
            () => services.GetRequiredService<UserReconciler>().DeletedUserIds,
            services.GetRequiredService<ArchivedLinePurges>(),
            services.GetService<IServerApplicationHost>()?.SystemId,
            UserRatingsKit.Options.Clock(services),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), DataComponent)));
        serviceCollection.AddSingleton(static services => new JellyfinEnhancedImport(
            services.GetRequiredService<IRatingsStore>(),
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IUserManager>(),
            () => services.GetRequiredService<JellyfinEnhancedReader>(),
            () => services.GetRequiredService<DetachedRecords>(),
            () => services.GetRequiredService<RatingsLibraryConsumer>(),
            services.GetRequiredService<ArchivedLinePurges>(),
            UserRatingsKit.Options.Clock(services),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), DataComponent)));
        serviceCollection.AddSingleton(static services => new RatingsData(
            services.GetRequiredService<IRatingsStore>(),
            services.GetRequiredService<RatingsEnvelope>(),
            services.GetRequiredService<AdminJobRunner>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<ILibraryManager>(),
            () => services.GetRequiredService<IdentityResolver>(),
            () => services.GetRequiredService<DetachedRecords>(),
            () => services.GetRequiredService<RatingsLibraryConsumer>(),
            () => services.GetRequiredService<AttachedRecordVerifier>(),
            services.GetRequiredService<ArchivedLinePurges>(),
            () => services.GetRequiredService<RatingsSettingsReader>().Read(),
            UserRatingsKit.Options.Clock(services),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), DataComponent),
            services.GetRequiredService<JellyfinEnhancedImport>(),
            services.GetRequiredService<ItemAccess>()));

        // The data jobs' files in tmp/: an export's file holds every review, so it is deleted an hour
        // after its job (or on DELETE Jobs/{id}); what a kill left goes at the next start (this hosted service) and in the daily
        // maintenance's DataTemp step (at the end of this method).
        serviceCollection.AddHostedService(static services => new DataTempSweep(
            () => services.GetRequiredService<RatingsData>(),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), DataComponent)));

        // The Settings tab: every setting checked on the server, a version and a 409 against a lost save, the
        // dry run's consequences; saved through Plugin.UpdateConfiguration, read at each use.
        serviceCollection.AddSingleton(static services => new RatingsSettingsService(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<IRatingsStore>(),
            UserRatingsKit.Options.Clock(services),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), SettingsComponent)));

        // Moderation: the Moderation list, Remove text only and Remove rating and review (a dry
        // run first, its token, a pre-operation snapshot, a moderation-log row, the archive's lines and a checkpoint), the log.
        serviceCollection.AddSingleton(static services => new RatingsModeration(
            services.GetRequiredService<IRatingsStore>(),
            services.GetRequiredService<ItemAccess>(),
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<ArchivedLinePurges>(),
            UserRatingsKit.Options.Clock(services),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), ModerationComponent)));

        // Versions merged while the server was off: the maintenance's own step, after the kit's, moves the
        // ratings on items that are now a merged version to the primary, as a batch's Updated step does.
        serviceCollection.AddKitMaintenanceStep(
            RatingsLibraryConsumer.MergedVersionsStep,
            KitMaintenanceOrder.Plugin,
            static async (services, cancellationToken) => await services.GetRequiredService<RatingsLibraryConsumer>().MergeVersionsAsync(cancellationToken).ConfigureAwait(false));

        // Users: the deleted-user policy on Jellyfin's UserDeletedEventArgs (DeletedUserPolicy,
        // Delete by default: the ratings deleted with tombstones; Anonymize: a former member's; KeepAsOrphan: kept, counted
        // nowhere), after a pre-operation snapshot; the daily maintenance's Users step, which marks unknown ids as orphans and
        // never changes a rating (former members are no unknown ids); Remap by name; and Diagnostics "Users".
        serviceCollection.AddKitUsers();
        serviceCollection.AddSingleton(static services => new RatingsUserDataOwner(
            services.GetRequiredService<IRatingsStore>(),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), UsersComponent),
            UserRatingsKit.Options.Clock(services),
            () => services.GetRequiredService<RatingsLibraryConsumer>()));
        serviceCollection.AddKitUserDataOwner<RatingsUserDataOwner>();

        // The archive rewrites of deleted users that failed after their policy's commit, and
        // those of ratings whose review was removed (Ids), run again.
        serviceCollection.AddKitMaintenanceStep(
            RatingsUserDataOwner.ArchivePurgesStep,
            KitMaintenanceOrder.Plugin,
            static async (services, cancellationToken) =>
            {
                var report = await services.GetRequiredService<RatingsUserDataOwner>().RunPendingPurgesAsync(cancellationToken).ConfigureAwait(false);
                report["Ids"] = await services.GetRequiredService<ArchivedLinePurges>().RunAsync(cancellationToken).ConfigureAwait(false);
                return report;
            });

        // The moderation log's retention: rows older than ModerationLogDays (365; 0 keeps every row,
        // read at each run) are deleted, then a checkpoint is asked for, before the kit's own Checkpoint step ends the run.
        serviceCollection.AddSingleton(static services => new ModerationLogRetention(
            services.GetRequiredService<IRatingsStore>(),
            () => services.GetRequiredService<RatingsSettingsReader>().Read().ModerationLogDays,
            UserRatingsKit.Options.Clock(services),
            UserRatingsKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), ModerationComponent)));
        serviceCollection.AddKitMaintenanceStep(
            ModerationLogRetention.StepName,
            KitMaintenanceOrder.Plugin,
            static (services, cancellationToken) => services.GetRequiredService<ModerationLogRetention>().RunAsync(cancellationToken));

        // The data jobs' files left in tmp/ (above, with the Data tab).
        serviceCollection.AddKitMaintenanceStep(
            RatingsData.TempStep,
            KitMaintenanceOrder.Plugin,
            static (services, _) => Task.FromResult<IDictionary<string, object?>?>(services.GetRequiredService<RatingsData>().SweepTemp()));

        // The maintenance holds the data jobs' runner (since the 0.3.19 verification): a restore or an import writes the
        // database, then the archive, and the Archive step mustn't run between the two. It waits up to 10 minutes for one in
        // progress, then skips its run (Diagnostics Maintenance: LastRun.Skipped); while it runs, they are refused with 409.
        serviceCollection.AddKitMaintenanceGate(
            MaintenanceGate,
            static (services, cancellationToken) => services.GetRequiredService<AdminJobRunner>().HoldWhenIdleAsync("daily maintenance", AdminJobRunner.MaintenanceWait, cancellationToken));
    }

    /// <summary>
    /// The store's counts for Diagnostics <c>Records</c> (Rows, Users, Detached).
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <returns>The counts.</returns>
    internal static IDictionary<string, object?> DescribeRecords(IServiceProvider services)
    {
        var counts = services.GetRequiredService<IRatingsStore>().Count();
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Attached"] = counts.Attached,
            ["Detached"] = counts.Detached,
            ["Reviews"] = counts.Reviews,
            ["Users"] = counts.Users,
            ["Tombstones"] = counts.Tombstones,
            ["ModerationLog"] = counts.ModerationLog,
            ["ImportLedger"] = counts.ImportLedger,
            // The 0.3.21 sanity check: detached JE ratings whose TMDb id still ends in :sN or :sN:eM never attach; 0 expected.
            ["DetachedJeWithKeySuffix"] = counts.DetachedJeWithKeySuffix
        };
    }

    // Held to 0 to 365 as the Settings tab holds it (since 0.3.18: a value saved through
    // POST /Plugins/{id}/Configuration was taken above 365), a value out of range named in GET Admin/Settings' Problems.
    internal static int SnapshotsToKeep(IServiceProvider services)
        => services.GetRequiredService<RatingsSettingsReader>().Read().SnapshotsToKeep;
}
