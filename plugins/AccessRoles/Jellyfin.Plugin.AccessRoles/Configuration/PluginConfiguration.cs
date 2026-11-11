using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AccessRoles.Configuration;

/// <summary>
/// The plugin's settings (with the kit's common fields that apply to it), in Jellyfin's XML file
/// <c>plugins/configurations/Jellyfin.Plugin.AccessRoles.xml</c>. Roles and assignments are not settings: they live in
/// <c>data/accessroles/accessroles.json</c>, because the generic <c>POST /Plugins/{id}/Configuration</c> would bypass the
/// lockout checks.
/// </summary>
/// <remarks>
/// Development builds 0.9.1 to 0.9.4 store these settings and show none of them on the settings page
/// yet; since 0.9.3 the library sync reads <see cref="EnforcementEnabled"/>, <see cref="LibrarySyncEnabled"/>,
/// <see cref="ReconcileIntervalMinutes"/> and <see cref="DriftPolicy"/> at every run, and since 0.9.4 so do the pre-merge of
/// an admin's policy save and the sign-in hook; the other enforcement settings wait
/// for their builds (0.9.5 settings locks, 0.9.7 admin surfaces and hardening, 0.9.13 recovery). Settings whose values are names (<see cref="DriftPolicy"/> and the like) are strings, so a
/// value a newer build wrote never stops the file from loading; each reader takes them with its default for anything it
/// doesn't know.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether the plugin's web script and head fragment are added to the web client
    /// Off: nothing is hidden in the web, while the server keeps enforcing. Read on every page load.
    /// </summary>
    public bool EnableWebUi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the server records the reports browsers send when a jellyfin-web element
    /// the web script relies on is missing.
    /// </summary>
    public bool ReportWebAnchorProblems { get; set; } = true;

    /// <summary>
    /// Gets or sets how many daily snapshots of <c>accessroles.json</c> the maintenance task keeps; 0 takes none.
    /// </summary>
    public int SnapshotsToKeep { get; set; } = 7;

    /// <summary>
    /// Gets or sets a value indicating whether anything is enforced: the master switch. Off: the filters pass
    /// everything, library sync stops and the web script only shows the admin banner.
    /// </summary>
    public bool EnforcementEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether library roles are written to users' native library access. Off:
    /// they are stored and previewed but never written.
    /// </summary>
    public bool LibrarySyncEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the minutes between backstop reconciles of library access, 1 to 60; library
    /// structure calls reconcile at once.
    /// </summary>
    public int ReconcileIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets what happens when an admin edits a managed user's library access on the stock Dashboard page:
    /// <c>Reapply</c> (the role result wins) or <c>ReportOnly</c>.
    /// </summary>
    public string DriftPolicy { get; set; } = "Reapply";

    /// <summary>
    /// Gets or sets the server-wide library leak hardening: <c>Off</c>, <c>Standard</c> or <c>Strict</c>
    /// (Strict arrives in 1.1).
    /// </summary>
    public string LibraryHardening { get; set; } = "Standard";

    /// <summary>
    /// Gets or sets how non-admins' configuration reads are answered, server-wide: <c>AllowList</c>
    /// (only <see cref="ConfigurationReadAllowList"/>) or <c>Redact</c> (every key, secrets stripped from a copy).
    /// </summary>
    public string NonAdminConfigurationReads { get; set; } = "AllowList";

    /// <summary>
    /// Gets or sets the named configuration keys non-admins may read in <c>AllowList</c> mode: by default
    /// <c>encoding</c>, which the video player reads. Their secrets are stripped all the same.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML serializer writes the settings file; an array round-trips there.")]
    public string[] ConfigurationReadAllowList { get; set; } = ["encoding"];

    /// <summary>
    /// Gets or sets a value indicating whether <c>POST /System/Restart</c> is refused to everyone but administrators and API
    /// keys, anonymous LAN callers included.
    /// </summary>
    public bool BlockNonAdminRestart { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether <c>GET /Users/Public</c> is trimmed to the login fields.
    /// </summary>
    public bool TrimPublicUsers { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the native <c>EnableUserPreferenceAccess</c> is cleared when every
    /// server-stored settings section is locked. Off by default.
    /// </summary>
    public bool MirrorPreferenceLockToPolicy { get; set; }

    /// <summary>
    /// Gets or sets the first-load rule of the web script: <c>HideUntilKnown</c> or <c>ShowUntilKnown</c>.
    /// </summary>
    public string FirstLoadMode { get; set; } = "HideUntilKnown";

    /// <summary>
    /// Gets or sets what a user sees when a locked page is blocked: <c>Toast</c> or <c>Silent</c>.
    /// </summary>
    public string BlockedNavigationNotice { get; set; } = "Toast";

    /// <summary>
    /// Gets or sets a value indicating whether the stock Dashboard user pages of managed users show a banner.
    /// </summary>
    public bool ShowManagedNoticeOnUserPages { get; set; } = true;

    /// <summary>
    /// Gets or sets the months of audit log kept, next to a 50 MB cap; 0 or less keeps every month (the cap still
    /// applies). Read since 0.9.2 by the daily maintenance's <c>Audit</c> step.
    /// </summary>
    public int AuditRetentionMonths { get; set; } = 12;
}
