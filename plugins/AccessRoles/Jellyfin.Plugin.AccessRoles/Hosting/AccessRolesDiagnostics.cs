using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Rules;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AccessRoles.Hosting;

/// <summary>
/// Access Roles' own entries of <c>GET /AccessRoles/Admin/Diagnostics</c>: what enforces (the library
/// sync since 0.9.3, the settings locks since 0.9.5, the admin surfaces and the server-wide hardening since 0.9.7), the
/// library sync, the action manifest and the rule
/// table against the running server, the server's users as the kit lists them, and the store's records counted. Every
/// value is a count, a name of ours, a time or a server path; no user name and no setting of another plugin.
/// </summary>
internal static class AccessRolesDiagnostics
{
    /// <summary>The assembly the manifest reviews.</summary>
    public const string JellyfinApiAssembly = "Jellyfin.Api";

    /// <summary>
    /// Describes what is enforced: since 0.9.3 the library sync writes library roles to users' native
    /// library access unless it is paused; since 0.9.5 the settings locks, and since 0.9.7 the admin surfaces
    /// and the server-wide rules, are enforced unless enforcement is switched off or the SAFE-MODE file exists. With the
    /// server-wide settings as they apply (<c>ServerWide</c>), the filter's counters (refused, reverted, failed closed,
    /// hidden), its last error, the lock snapshot's state and the hardening rules' own times (<c>Cost</c>).
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <returns>The entry.</returns>
    public static IDictionary<string, object?> DescribeEnforcement(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var configuration = services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
        var paths = services.GetRequiredService<AccessRolesPaths>();
        var paused = LibrarySync.PauseReason(configuration, paths, services.GetRequiredService<AccessRolesStore>());
        var settingsOff = File.Exists(paths.SafeModeFile) ? SyncReasons.SafeMode : !configuration.EnforcementEnabled ? SyncReasons.EnforcementDisabled : null;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Active"] = paused is null || settingsOff is null,
            ["LibraryAccess"] = paused is null ? "Synced" : "Paused",
            ["PausedBecause"] = paused,
            ["SettingsLocks"] = settingsOff is null,
            ["SettingsLocksOffBecause"] = settingsOff,
            ["Hardening"] = settingsOff is null,
            ["ServerWide"] = DescribeServerWide(configuration),
            ["NotEnforcedLocks"] = SettingsSections.NotEnforcedYet.Select(l => l.ToString()).ToList(),
            ["Message"] = (settingsOff, paused) switch
            {
                (null, null) => "Settings locks, the admin surfaces and the server-wide hardening are enforced, and library roles are written to users' library access.",
                (null, _) => "Settings locks, the admin surfaces and the server-wide hardening are enforced; the library sync is paused (" + paused + "), so no library access is written.",
                _ => "Nothing is enforced (" + settingsOff + "): settings locks, the admin surfaces and the hardening pass everything and the library sync writes nothing."
            }
        };

        try
        {
            var snapshot = services.GetRequiredService<EffectiveRulesCache>().Current();
            result["Snapshot"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["ManagedUsers"] = snapshot.Managed,
                ["LockedUsers"] = snapshot.Locked,
                ["DegradedBecause"] = snapshot.DegradedBecause
            };

            // Degraded mode: whom it locks, read from where, since when, and whether an admin can end it.
            result["Degraded"] = services.GetRequiredService<AccessRolesStore>().Degraded.Describe();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result["Snapshot"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Error"] = ex.Message };
        }

        result["Blocked"] = services.GetRequiredService<BlockCounter>().Describe();
        result["Filter"] = services.GetRequiredService<EnforcementStatus>().Describe();
        result["Cost"] = services.GetService<HardeningCost>()?.Describe();
        return result;
    }

    /// <summary>
    /// Describes the server-wide rules as the settings make them apply (the Overview's
    /// <i>Server-wide behaviours</i> box): the configuration reads' mode and allow-list, the restart block, the public user
    /// list's trim and the library hardening's level, each as this build reads it (an unknown value as its default).
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The entry.</returns>
    public static IDictionary<string, object?> DescribeServerWide(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var settings = ServerHardening.Settings(configuration);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ConfigurationReads"] = settings.Redact ? "Redact" : "AllowList",
            ["ConfigurationReadAllowList"] = settings.AllowList.Order(StringComparer.Ordinal).ToList(),
            ["BlockNonAdminRestart"] = settings.BlockRestart,
            ["TrimPublicUsers"] = settings.TrimPublicUsers,
            ["LibraryHardening"] = settings.Library ? "Standard" : "Off",

            // The web client's first load (the Overview's box since 0.9.11), as AccessRolesHead reads it.
            ["FirstLoadMode"] = string.Equals(configuration.FirstLoadMode, "ShowUntilKnown", StringComparison.Ordinal) ? "ShowUntilKnown" : "HideUntilKnown"
        };
    }

    /// <summary>
    /// Describes the rule table (<c>RuleTableSelfCheck</c>): every manifest action classified by the part of the plugin
    /// that applies its rules, and the self-check's result against the running server once the startup gate opened: the
    /// filter registered, the rule rows bound, the unreviewed actions, Jellyfin Enhanced's locked saves and the plugin
    /// controllers.
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <returns>The entry.</returns>
    public static IDictionary<string, object?> DescribeRuleTable(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var table = services.GetRequiredService<ActionRuleTable>();
        if (services.GetRequiredService<RuleTableSelfCheck>().Result is { } result)
        {
            return result;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Pending"] = true,
            ["Classification"] = table.Summary()
        };
    }

    /// <summary>
    /// Describes the library sync: paused or not and why, the drift policy and interval, the last run, the
    /// drift count, the pending releases, renames and post-write checks, the totals and the last error.
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <returns>The entry.</returns>
    public static IDictionary<string, object?> DescribeLibrarySync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var configuration = services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
        var store = services.GetRequiredService<AccessRolesStore>();
        var document = store.Current;
        int verifications;
        int failedSaves;
        try
        {
            var writer = services.GetRequiredService<PolicyWriter>();
            verifications = writer.PendingVerifications;
            failedSaves = writer.FailedSaves.Count;
        }
        catch (InvalidOperationException)
        {
            // No user manager (tests): nothing was written.
            verifications = 0;
            failedSaves = 0;
        }

        return services.GetRequiredService<LibrarySyncStatus>().Describe(
            LibrarySync.PauseReason(configuration, services.GetRequiredService<AccessRolesPaths>(), store),
            LibrarySync.DriftPolicy(configuration),
            (int)LibrarySync.Interval(configuration).TotalMinutes,
            document.Ledger.Values.Count(e => e.Release is not null),
            document.PendingRenames.Count,
            verifications,
            failedSaves);
    }

    /// <summary>
    /// Describes the action manifest of this build's Jellyfin line and compares it with the actions the running server
    /// routes from Jellyfin.Api (the request-time check <c>RuleTableSelfCheck</c> builds on): what the
    /// manifest lists that the server doesn't have (<c>Missing</c>), what the server has that it doesn't list
    /// (<c>Unreviewed</c>), and what differs (<c>Changed</c>), plus every other assembly's action count (plugins).
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <returns>The entry.</returns>
    public static IDictionary<string, object?> DescribeManifest(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ActionManifest manifest;
        try
        {
            manifest = services.GetRequiredService<ActionManifest>();
        }
        catch (InvalidDataException ex)
        {
            // A build defect the manifest tests catch; Diagnostics still answers.
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["Error"] = ex.Message };
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["JellyfinTarget"] = manifest.JellyfinTarget,
            ["JellyfinVersion"] = manifest.JellyfinVersion,
            ["Source"] = manifest.Source,
            ["Actions"] = manifest.Actions.Count,
            ["WithRules"] = manifest.Actions.Count(a => a.Rules.Count > 0),
            ["Rules"] = manifest.Actions.SelectMany(a => a.Rules).GroupBy(r => r.Kind.ToString(), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)
        };

        if (services.GetService<IActionDescriptorCollectionProvider>() is not { } descriptors)
        {
            result["Server"] = null;
            return result;
        }

        var comparison = manifest.Compare(ServerActions.Read(descriptors, JellyfinApiAssembly));
        result["Server"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Matches"] = comparison.Matches,
            ["Actions"] = comparison.Compared,
            ["Missing"] = comparison.Missing,
            ["Unreviewed"] = comparison.Unreviewed,
            ["Changed"] = comparison.Changed,
            ["Duplicates"] = comparison.Duplicates,
            ["ByAssembly"] = ServerActions.CountByAssembly(descriptors)
        };
        return result;
    }

    /// <summary>
    /// Describes the server's users as every user list of the plugin reads them: through the kit's <see cref="KitUsers"/>
    /// which reads <c>IUserManager.Users</c> on 10.11.5 and <c>GetUsers()</c> on 10.11.9 and
    /// later. Counts only.
    /// </summary>
    /// <param name="services">The server's services.</param>
    /// <returns>The entry.</returns>
    public static IDictionary<string, object?> DescribeServerUsers(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var users = KitUsers.GetAll(services.GetRequiredService<IUserManager>());
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ListedThrough"] = KitUsers.MemberUsed<IUserManager>(),
            ["Users"] = users.Count,
            ["Administrators"] = users.Count(u => u.HasPermission(PermissionKind.IsAdministrator)),
            ["Disabled"] = users.Count(u => u.HasPermission(PermissionKind.IsDisabled))
        };
    }

    /// <summary>
    /// Counts the store's records, the ledger entries with a pending release (a <c>Restore</c> or <c>GrantAll</c>
    /// the library sync is still to write), and how many records hold values a newer build wrote.
    /// </summary>
    /// <param name="store">The store.</param>
    /// <returns>The entry.</returns>
    public static IDictionary<string, object?> DescribeRecords(AccessRolesStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var document = store.Current;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Roles"] = document.Roles.Count,
            ["Assignments"] = document.Assignments.Count,
            ["DefaultRoles"] = document.DefaultRoleIds.Count,
            ["KnownLibraries"] = document.KnownLibraries.Count,
            ["PendingRenames"] = document.PendingRenames.Count,
            ["LedgerEntries"] = document.Ledger.Count,
            ["PendingReleases"] = document.Ledger.Values.Count(e => e.Release is not null),
            ["MirroredPreferenceAccess"] = document.PreferenceMirror.Count,
            ["UnknownValues"] = document.Assignments.Count(a => a.HasUnknownValues) + document.Roles.Count(r => r.HasUnknownLibraryMode)
        };
    }
}
