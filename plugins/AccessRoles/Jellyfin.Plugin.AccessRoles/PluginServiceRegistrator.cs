using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Hosting;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.AccessRoles;

/// <summary>
/// Registers the plugin's services with the server. Never throws:
/// it only adds descriptors; every service is built when first used.
/// </summary>
/// <remarks>
/// Development builds 0.9.1 to 0.9.5 register the kit, the store, the action
/// manifest, Diagnostics, the roles API (catalog, server users, audit log, the service behind the admin controllers), the
/// user-data owner, the daily maintenance and, since 0.9.3, the library sync: the policy writer, the sync and its background
/// service and the library structure hook; since 0.9.4 the user policy hooks (the pre-merge, the per-user lock and the
/// sign-in hook) and the user-created and user-updated event consumers; since 0.9.5 the settings enforcement: Access Roles'
/// one global MVC filter, <c>AccessRulesActionFilter</c> (<c>services.Configure&lt;MvcOptions&gt;(o =&gt;
/// o.Filters.AddService&lt;AccessRulesActionFilter&gt;(-10000))</c>, the order verified on a 10.11.5 sandbox), which applies the
/// settings rules and runs both hooks, its rule table, lock snapshots, blocked-request counter, the rule table's
/// self-check and <c>/Me</c>; since 0.9.6 the head fragment; since 0.9.7 the admin-surface, server-wide and hardening rules
/// (<c>ServerHardening</c>, inside the same filter) with the kit's <c>LibraryAccess</c>, the People-search query over the
/// server's database, the configuration copies and the rules' times.
/// </remarks>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>The snapshot step's name in the daily maintenance.</summary>
    internal const string SnapshotsStep = "Snapshots";

    /// <summary>The audit retention step's name in the daily maintenance.</summary>
    internal const string AuditStep = "Audit";

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // First statement: on a Jellyfin line this build isn't for, register nothing but the status, so an
        // inert build can't half-enforce on a server it doesn't know.
        if (!KitAbiGuard.Register(serviceCollection, applicationHost, AccessRolesKit.Options))
        {
            return;
        }

        // The emergency stop: data/accessroles/SAFE-MODE, or EnforcementEnabled false in the
        // configuration file, at the start makes the plugin inert: nothing but its admin page, which says which is in effect.
        if (AccessRolesInert.Detect(serviceCollection) is { } inert)
        {
            RegisterInert(serviceCollection, inert);
            return;
        }

        serviceCollection.AddKit(AccessRolesKit.Options);
        serviceCollection.AddSingleton<IPluginConfigurationProvider, PluginConfigurationProvider>();

        // The web client: the kit's index.html middleware adds the ClientScript's tag, and the kit builds
        // the script. 0.9.2 has no Web/js files of its own: the head fragment, the CSS and the route guard came with 0.9.6.
        serviceCollection.AddIndexHtmlInjection();
        serviceCollection.AddEmbeddedScript();

        // The roles and assignments store, data/accessroles/accessroles.json, schema 1, created empty at the first
        // start after the startup gate (AccessRolesStartup).
        serviceCollection.AddSingleton<AccessRolesPaths>();
        serviceCollection.AddSingleton(services => AccessRolesStore.Create(
            services.GetRequiredService<AccessRolesPaths>().StoreFile,
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Storage"),
            AccessRolesKit.Options.Clock(services)));
        serviceCollection.AddHostedService(services => new AccessRolesStartup(
            applicationHost,
            services.GetRequiredService<AccessRolesStore>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Startup"),
            services.GetRequiredService<AccessRolesPaths>(),
            services.GetRequiredService<AccessRolesAuditLog>()));

        // The reviewed list of every Jellyfin.Api action of this build's Jellyfin line, read once.
        serviceCollection.AddSingleton(_ => ActionManifest.ForBuild());

        // Diagnostics: the browsers' ClientHealth reports and GET /AccessRoles/Admin/Diagnostics,
        // with the store, what enforces, the manifest against the running server, the server's users through the kit's
        // KitUsers, and the store's records counted.
        serviceCollection.AddKitDiagnostics();
        serviceCollection.AddKitDiagnosticsSource("Store", services => KitDiagnostics.DescribeStore(services.GetRequiredService<AccessRolesStore>().Documents));
        serviceCollection.AddKitDiagnosticsSource("Records", services => AccessRolesDiagnostics.DescribeRecords(services.GetRequiredService<AccessRolesStore>()));
        serviceCollection.AddKitDiagnosticsSource("Enforcement", AccessRolesDiagnostics.DescribeEnforcement);
        serviceCollection.AddKitDiagnosticsSource("LibrarySync", AccessRolesDiagnostics.DescribeLibrarySync);
        serviceCollection.AddKitDiagnosticsSource("ActionManifest", AccessRolesDiagnostics.DescribeManifest);
        serviceCollection.AddKitDiagnosticsSource("ServerUsers", AccessRolesDiagnostics.DescribeServerUsers);

        // The roles API: the library catalog from GetVirtualFolders(), the server's users through KitUsers, the
        // audit log (audit/audit-<yyyyMM>.jsonl) and the service behind the admin controllers. Nothing of it writes
        // a user's policy (the library sync) or blocks a request (the settings locks, the hardening).
        serviceCollection.AddSingleton<ILibraryCatalogSource>(services => new VirtualFolderCatalogSource(
            services.GetRequiredService<ILibraryManager>(),
            services.GetRequiredService<IServerApplicationPaths>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Catalog")));
        serviceCollection.AddSingleton<IServerUsers>(services => new JellyfinServerUsers(services.GetRequiredService<IUserManager>()));
        serviceCollection.AddSingleton(services => new AccessRolesAuditLog(
            services.GetRequiredService<AccessRolesPaths>().AuditDirectory,
            () => services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration().AuditRetentionMonths,
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Audit"),
            AccessRolesKit.Options.Clock(services)));
        serviceCollection.AddSingleton(services => new AccessRolesAdminService(
            services.GetRequiredService<AccessRolesStore>(),
            services.GetRequiredService<ILibraryCatalogSource>(),
            services.GetRequiredService<IServerUsers>(),
            services.GetRequiredService<AccessRolesAuditLog>(),
            services.GetRequiredService<UserReconciler>,
            services.GetRequiredService<LibrarySync>,
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Api"),
            AccessRolesKit.Options.Clock(services),
            new AccessRolesDataSupport(
                services.GetRequiredService<AccessRolesPaths>(),
                () => services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration(),
                applicationHost.SystemId,
                () => services.GetService<MediaBrowser.Model.Tasks.ITaskManager>())));
        serviceCollection.AddSingleton<IAccessRolesAdmin>(services => services.GetRequiredService<AccessRolesAdminService>());
        serviceCollection.AddSingleton<IAccessRolesAudit>(services => services.GetRequiredService<AccessRolesAdminService>());

        // The Settings tab's API: the plugin configuration through a dry run and its token, audited.
        serviceCollection.AddSingleton(services => new AccessRolesSettingsService(
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<IServerUsers>(),
            services.GetRequiredService<AccessRolesAuditLog>()));
        serviceCollection.AddSingleton<IAccessRolesSettings>(services => services.GetRequiredService<AccessRolesSettingsService>());

        // Lockout and recovery: ending Degraded mode, and releasing the plugin's native locks.
        serviceCollection.AddSingleton(services => new AccessRolesRecoveryService(
            services.GetRequiredService<AccessRolesStore>(),
            services.GetRequiredService<IServerUsers>(),
            services.GetRequiredService<AccessRolesAuditLog>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Api"),
            services.GetRequiredService<ILibraryCatalogSource>(),
            services.GetRequiredService<PolicyWriter>(),
            services.GetRequiredService<PreferenceMirrorSync>(),
            AccessRolesKit.Options.Clock(services)));
        serviceCollection.AddSingleton<IAccessRolesRecovery>(services => services.GetRequiredService<AccessRolesRecoveryService>());
        serviceCollection.AddKitDiagnosticsSource("Audit", services => services.GetRequiredService<AccessRolesAuditLog>().Describe());

        // The library sync: the only writer of users' policies, with its per-user
        // locks, the sync runs, the background service that schedules them after the startup gate, and the library structure
        // filter, a global MVC filter that only watches LibraryStructureController's seven actions.
        // The writer also reads a user's committed row, read only (the server's IDbContextFactory<JellyfinDbContext>,
        // AsNoTracking), to tell what an admin saved from what 10.11.5's cache holds after a failed save.
        serviceCollection.AddSingleton<UserPolicyLocks>();
        serviceCollection.AddSingleton(services => new PolicyWriter(
            services.GetRequiredService<IUserManager>(),
            services.GetRequiredService<UserPolicyLocks>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Policy"),
            AccessRolesKit.Options.Clock(services),
            committed: services.GetService<IDbContextFactory<JellyfinDbContext>>() is { } database
                ? new DatabaseCommittedUsers(database, AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Policy"))
                : null));
        serviceCollection.AddSingleton<LibrarySyncStatus>();
        serviceCollection.AddSingleton(services => new LibrarySync(
            services.GetRequiredService<AccessRolesStore>(),
            services.GetRequiredService<ILibraryCatalogSource>(),
            services.GetRequiredService<IServerUsers>(),
            services.GetRequiredService<PolicyWriter>(),
            services.GetRequiredService<AccessRolesAuditLog>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<AccessRolesPaths>(),
            services.GetRequiredService<LibrarySyncStatus>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Sync"),
            AccessRolesKit.Options.Clock(services),
            services.GetRequiredService<PreferenceMirrorSync>()));

        // The optional native mirror (MirrorPreferenceLockToPolicy, off by default), run after each library sync run.
        serviceCollection.AddSingleton(services => new PreferenceMirrorSync(
            services.GetRequiredService<AccessRolesStore>(),
            services.GetRequiredService<ILibraryCatalogSource>(),
            services.GetRequiredService<IServerUsers>(),
            services.GetRequiredService<PolicyWriter>(),
            services.GetRequiredService<AccessRolesAuditLog>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<AccessRolesPaths>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Mirror"),
            AccessRolesKit.Options.Clock(services)));
        serviceCollection.AddSingleton(services => new LibrarySyncService(
            applicationHost,
            services.GetRequiredService<LibrarySync>(),
            services.GetRequiredService<AccessRolesStore>(),
            services.GetRequiredService<PolicyWriter>(),
            services.GetService<ILibraryManager>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<LibrarySyncStatus>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Sync"),
            AccessRolesKit.Options.Clock(services)));
        serviceCollection.AddHostedService(services => services.GetRequiredService<LibrarySyncService>());
        serviceCollection.AddSingleton(services => new LibraryStructureFilter(
            services.GetRequiredService<ActionManifest>(),
            services.GetRequiredService<LibrarySyncService>(),
            services.GetRequiredService<ILibraryCatalogSource>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Sync")));

        // The user policy hooks: UserController's policy save (the per-user lock
        // and the pre-merge), its user edits and deletes (the lock) and the sign-ins (the correction before the token
        // leaves); and the user-created (default roles) and, on 12.1+, user-updated event consumers. Jellyfin's event
        // manager resolves every consumer from a new scope. Since 0.9.5 both hooks run inside the global filter below.
        serviceCollection.AddSingleton(services => new UserPolicyFilter(
            services.GetRequiredService<ActionManifest>(),
            services.GetRequiredService<UserPolicyLocks>(),
            services.GetRequiredService<LibrarySync>(),
            services.GetRequiredService<LibrarySyncService>(),
            services.GetRequiredService<IServerUsers>(),
            services.GetRequiredService<IUserManager>(),
            services.GetService<ISessionManager>(),
            services.GetRequiredService<LibrarySyncStatus>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Policy")));

        // The settings enforcement: the rule table from the manifest, the lock snapshots built
        // from the store on first use, the blocked-request counter with its audit flush, /Me, and Access Roles' one global
        // MVC filter at −10000 (services.Configure<MvcOptions>(o => o.Filters.AddService<AccessRulesActionFilter>(-10000)),
        // the pattern verified on a 10.11.5 sandbox), which applies the settings rules and runs the two user policy hooks; the rule table's self-check
        // against the running server after the startup gate.
        serviceCollection.AddSingleton(services => new ActionRuleTable(services.GetRequiredService<ActionManifest>()));
        serviceCollection.AddSingleton(services => new EffectiveRulesCache(services.GetRequiredService<AccessRolesStore>()));
        serviceCollection.AddSingleton(services => new BlockCounter(AccessRolesKit.Options.Clock(services)));
        serviceCollection.AddSingleton<EnforcementStatus>();
        serviceCollection.AddSingleton(services => new BlockCounterService(
            applicationHost,
            services.GetRequiredService<BlockCounter>(),
            services.GetRequiredService<AccessRolesAuditLog>(),
            services.GetRequiredService<IServerUsers>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Enforcement"),
            AccessRolesKit.Options.Clock(services)));
        serviceCollection.AddHostedService(services => services.GetRequiredService<BlockCounterService>());
        serviceCollection.AddSingleton(services => new RuleTableSelfCheck(
            applicationHost,
            services.GetRequiredService<ActionRuleTable>(),
            services.GetService<IActionDescriptorCollectionProvider>(),
            services.GetService<IOptions<MvcOptions>>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Enforcement")));
        serviceCollection.AddHostedService(services => services.GetRequiredService<RuleTableSelfCheck>());
        serviceCollection.AddSingleton<IAccessRolesMe>(services => new AccessRolesMe(
            services.GetRequiredService<EffectiveRulesCache>(),
            services.GetRequiredService<IServerUsers>(),
            services.GetRequiredService<ILibraryCatalogSource>(),
            JellyfinEnhancedRouted(services),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Enforcement")));
        // What jellyfin-web takes for a usersettings key with no value: its config.json's default theme, read once;
        // a server that hosts no web client gets the stock defaults.
        serviceCollection.AddSingleton(services => new WebClientDefaults(services.GetService<IServerApplicationPaths>()?.WebPath));

        // The admin-surface, server-wide and hardening rules, through the kit's LibraryAccess
        // (GetItemById<BaseItem>(id, user), views by their display parent), the People search's one query per page over the
        // server's IDbContextFactory<JellyfinDbContext> (read only, AsNoTracking), configuration copies made with MVC's own
        // JSON options, and each rule's own time per request.
        serviceCollection.AddSingleton<HardeningCost>();
        serviceCollection.AddSingleton(services => new ServerHardening(
            services.GetRequiredService<IUserManager>(),
            new LibraryAccess(services.GetRequiredService<ILibraryManager>()),
            services.GetService<IDbContextFactory<JellyfinDbContext>>() is { } database ? new DatabasePeopleLibraries(database) : null,
            new ConfigurationReadGuard(() => services.GetService<IOptions<JsonOptions>>()?.Value.JsonSerializerOptions ?? JsonDefaults.Options),
            services.GetRequiredService<BlockCounter>()));
        serviceCollection.AddSingleton(services => new AccessRulesActionFilter(
            services.GetRequiredService<ActionRuleTable>(),
            services.GetRequiredService<EffectiveRulesCache>(),
            services.GetRequiredService<IPluginConfigurationProvider>(),
            services.GetRequiredService<AccessRolesPaths>(),
            services.GetRequiredService<IUserManager>(),
            services.GetService<IDisplayPreferencesManager>(),
            services.GetRequiredService<BlockCounter>(),
            services.GetRequiredService<EnforcementStatus>(),
            services.GetRequiredService<UserPolicyFilter>(),
            services.GetRequiredService<LibraryStructureFilter>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Enforcement"),
            AccessRolesKit.Options.Clock(services),
            services.GetRequiredService<WebClientDefaults>(),
            services.GetRequiredService<ServerHardening>(),
            services.GetRequiredService<HardeningCost>()));
        serviceCollection.Configure<MvcOptions>(options => options.Filters.AddService<AccessRulesActionFilter>(AccessRulesActionFilter.FilterOrder));
        serviceCollection.AddKitDiagnosticsSource("RuleTable", AccessRolesDiagnostics.DescribeRuleTable);

        serviceCollection.TryAddEnumerable(ServiceDescriptor.Scoped<IEventConsumer<UserCreatedEventArgs>, UserCreatedConsumer>(services => new UserCreatedConsumer(
            services.GetRequiredService<AccessRolesAdminService>(),
            services.GetRequiredService<LibrarySync>(),
            services.GetRequiredService<LibrarySyncService>(),
            services.GetRequiredService<LibrarySyncStatus>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Users"),
            services.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>())));
        serviceCollection.TryAddEnumerable(ServiceDescriptor.Scoped<IEventConsumer<UserUpdatedEventArgs>, UserUpdatedConsumer>(services => new UserUpdatedConsumer(
            applicationHost,
            services.GetRequiredService<LibrarySync>(),
            services.GetRequiredService<PolicyWriter>(),
            services.GetRequiredService<LibrarySyncService>(),
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Users"))));

        // A user's records are their assignment and ledger entry: the kit's deleted-user consumer removes both
        // (DeletedUserPolicy fixed to Delete), its reconciler runs in the daily maintenance and gives Diagnostics its
        // Users entry, Remap by name and Apply deleted-user policy.
        serviceCollection.AddSingleton(services => new AccessRolesUserDataOwner(
            services.GetRequiredService<AccessRolesStore>(),
            services.GetRequiredService<AccessRolesAuditLog>(),
            services.GetRequiredService<AccessRolesPaths>().SnapshotsDirectory,
            AccessRolesKit.Options.CreateLogger(services.GetRequiredService<ILoggerFactory>(), "Users")));
        serviceCollection.AddKitUsers();
        serviceCollection.AddKitUserDataOwner<AccessRolesUserDataOwner>();

        // The daily maintenance, "Access Roles: Maintenance" at 03:50: the snapshot of
        // accessroles.json first (daily snapshots, the kit's rule), the kit's user reconciliation, then the audit
        // log's retention.
        serviceCollection.AddKitMaintenance();
        serviceCollection.AddKitMaintenanceStep(SnapshotsStep, KitMaintenanceOrder.Snapshots, SnapshotAsync);
        serviceCollection.AddKitMaintenanceStep(AuditStep, KitMaintenanceOrder.Plugin, static (services, cancellationToken) => AuditRetentionAsync(services, cancellationToken));
    }

    /// <summary>
    /// The inert registration: the kit's paths and the embedded scripts, so the admin page loads, and the stop
    /// itself, which <c>AccessRolesInertGate</c> answers every other action with (503). No index.html injection, no store,
    /// filter, sync, hosted service or event consumer.
    /// </summary>
    /// <param name="serviceCollection">The server's service collection.</param>
    /// <param name="inert">The stop in effect.</param>
    internal static void RegisterInert(IServiceCollection serviceCollection, AccessRolesInert inert)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(inert);
        serviceCollection.AddKit(AccessRolesKit.Options);
        serviceCollection.AddSingleton(inert);
        serviceCollection.AddEmbeddedScript();
        KitStartupLog.Error(AccessRolesKit.Options.GetLoggerCategory("Startup"), inert.Message + " To turn it back on: " + (inert.SafeMode ? "with the server stopped, delete " + inert.SafeModeFile : string.Empty)
            + (inert.SafeMode && inert.EnforcementDisabled ? "; and " : string.Empty)
            + (inert.EnforcementDisabled ? "set EnforcementEnabled to true (the admin page offers it, or edit " + inert.ConfigurationFile + " with the server stopped)" : string.Empty)
            + ", then start the server.");
    }

    /// <summary>
    /// Gets whether Jellyfin Enhanced's controller is routed (its settings section then counts for <c>/Me</c>'s
    /// <c>AllSettingsLocked</c>), read once from MVC's actions the first time it is asked.
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <returns>The check.</returns>
    internal static Func<bool> JellyfinEnhancedRouted(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var routed = new Lazy<bool>(() => services.GetService<IActionDescriptorCollectionProvider>() is { } descriptors
            && descriptors.ActionDescriptors.Items.OfType<ControllerActionDescriptor>().Any(d => string.Equals(d.ControllerTypeInfo.FullName, ActionRuleTable.JellyfinEnhancedController, StringComparison.Ordinal)));
        return () => routed.Value;
    }

    /// <summary>
    /// The audit log's retention: months older than <c>AuditRetentionMonths</c> go, then the oldest while the
    /// files are over 50 MB.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <param name="cancellationToken">Cancels waiting for a write.</param>
    /// <returns>What was done, for Diagnostics <c>Maintenance</c>.</returns>
    internal static async Task<IDictionary<string, object?>?> AuditRetentionAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        return await services.GetRequiredService<AccessRolesAuditLog>().ApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The daily snapshot of <c>accessroles.json</c>: <c>SnapshotsToKeep</c> of them in
    /// <c>data/accessroles/snapshots</c>, none while the store is read-only (the kit store writes none then) or with the
    /// setting at 0.
    /// </summary>
    /// <param name="services">The server's service provider.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot.</param>
    /// <returns>What was done, for Diagnostics <c>Maintenance</c>.</returns>
    internal static async Task<IDictionary<string, object?>?> SnapshotAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var keep = Math.Max(0, services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration().SnapshotsToKeep);
        var store = services.GetRequiredService<AccessRolesStore>();
        var path = await store.Documents.SnapshotAsync(services.GetRequiredService<AccessRolesPaths>().SnapshotsDirectory, keep, cancellationToken).ConfigureAwait(false);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Keep"] = keep,
            ["Snapshot"] = path is null ? null : Path.GetFileName(path),
            ["Skipped"] = path is not null ? null : store.IsReadOnly ? "ReadOnly" : keep == 0 ? "SnapshotsToKeep is 0" : "NotWritten"
        };
    }
}
