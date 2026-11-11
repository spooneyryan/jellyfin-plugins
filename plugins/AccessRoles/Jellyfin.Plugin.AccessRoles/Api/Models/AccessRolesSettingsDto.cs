using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Access Roles' settings as the admin page's Settings tab edits them: <c>GET Settings</c> answers
/// every value as this build reads it, and <c>PUT Settings</c> changes the values its body sets (a value left out, or
/// <c>null</c>, stays as it is). The kit's settings with no effect for this plugin aren't here.
/// </summary>
public sealed class AccessRolesSettingsDto
{
    /// <summary>Gets or sets a value indicating whether the head fragment and the web client's script are served.</summary>
    public bool? EnableWebUi { get; set; }

    /// <summary>Gets or sets a value indicating whether browsers report problems with the web client's anchors.</summary>
    public bool? ReportWebAnchorProblems { get; set; }

    /// <summary>Gets or sets how many daily snapshots of the data file are kept: 0 to 100.</summary>
    public int? SnapshotsToKeep { get; set; }

    /// <summary>Gets or sets a value indicating whether anything is enforced (the master switch): off, nothing is, and the library sync writes nothing.</summary>
    public bool? EnforcementEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether library roles are written to users' library access.</summary>
    public bool? LibrarySyncEnabled { get; set; }

    /// <summary>Gets or sets the library sync's backstop interval: 1 to 60 minutes.</summary>
    public int? ReconcileIntervalMinutes { get; set; }

    /// <summary>Gets or sets what the sync does with an edit of a managed user's libraries: <c>Reapply</c> or <c>ReportOnly</c>.</summary>
    public string? DriftPolicy { get; set; }

    /// <summary>
    /// Gets or sets the library leak hardening for non-admins: <c>Off</c> or <c>Standard</c>; a stored <c>Strict</c> (1.1)
    /// is answered as it is and applies as <c>Standard</c>, and can't be set.
    /// </summary>
    public string? LibraryHardening { get; set; }

    /// <summary>Gets or sets how non-admins' configuration reads are answered: <c>AllowList</c> or <c>Redact</c>.</summary>
    public string? NonAdminConfigurationReads { get; set; }

    /// <summary>
    /// Gets or sets the named configuration keys non-admins may read in <c>AllowList</c> mode (lower case, at most 50):
    /// secrets are still stripped, and adding <c>network</c>, <c>livetv</c>, <c>database</c> or <c>subtitles</c> needs
    /// <c>confirmSecretKeys=true</c>.
    /// </summary>
    public IReadOnlyList<string>? ConfigurationReadAllowList { get; set; }

    /// <summary>Gets or sets a value indicating whether a restart by anyone but an administrator is refused.</summary>
    public bool? BlockNonAdminRestart { get; set; }

    /// <summary>Gets or sets a value indicating whether <c>GET /Users/Public</c> is trimmed to what the login screen needs.</summary>
    public bool? TrimPublicUsers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a managed non-admin whose roles lock Profile, Display (account), Home, Playback
    /// and Subtitles also gets native <c>EnableUserPreferenceAccess=false</c> (since 0.9.13). Off by
    /// default.
    /// </summary>
    public bool? MirrorPreferenceLockToPolicy { get; set; }

    /// <summary>Gets or sets the web client's first load: <c>HideUntilKnown</c> or <c>ShowUntilKnown</c>.</summary>
    public string? FirstLoadMode { get; set; }

    /// <summary>
    /// Gets the keys of a <c>PUT</c> body that name no setting, which it answers 400 with their names (otherwise a
    /// misspelt key was ignored, and the save said nothing changed). Never answered.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; init; }

    /// <summary>Gets or sets what a web user sees on a blocked navigation: <c>Toast</c> or <c>Silent</c>.</summary>
    public string? BlockedNavigationNotice { get; set; }

    /// <summary>Gets or sets a value indicating whether the stock Dashboard user pages show the managed-user banner.</summary>
    public bool? ShowManagedNoticeOnUserPages { get; set; }

    /// <summary>Gets or sets how long audit lines are kept: 0 to 120 months, 0 for every month (the 50 MB cap still applies).</summary>
    public int? AuditRetentionMonths { get; set; }
}
