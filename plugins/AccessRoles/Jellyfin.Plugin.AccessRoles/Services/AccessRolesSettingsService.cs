using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.AccessRoles.Sync;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.AccessRoles.Services;

/// <summary>
/// The Settings tab's API. The settings are read as this build reads them (an unknown value as its
/// default); a change goes through the write pattern of the roles API: a dry run that lists each setting it changes,
/// before and after, with its <c>PreviewToken</c>, and the save checked against that token (409 <c>Changed</c> naming the
/// settings changed meanwhile, nothing saved). A key whose type carries secrets added to the configuration-read
/// allow-list needs the admin's confirmation. A save is the plugin configuration's own (Jellyfin's
/// <c>UpdateConfiguration</c>, so whatever reads it sees it at once), audited <c>SettingsChanged</c>.
/// </summary>
internal sealed partial class AccessRolesSettingsService : IAccessRolesSettings
{
    /// <summary>The most keys the allow-list takes.</summary>
    public const int MaxAllowListKeys = 50;

    /// <summary>The configuration keys whose type carries secrets.</summary>
    public static readonly IReadOnlyList<string> SecretKeys = ["network", "livetv", "database", "subtitles"];

    // The settings in the Settings tab's order: the names of the changes, and the lines of the token.
    private static readonly string[] Names =
    [
        nameof(AccessRolesSettingsDto.EnforcementEnabled), nameof(AccessRolesSettingsDto.LibrarySyncEnabled), nameof(AccessRolesSettingsDto.ReconcileIntervalMinutes),
        nameof(AccessRolesSettingsDto.DriftPolicy), nameof(AccessRolesSettingsDto.LibraryHardening), nameof(AccessRolesSettingsDto.NonAdminConfigurationReads),
        nameof(AccessRolesSettingsDto.ConfigurationReadAllowList), nameof(AccessRolesSettingsDto.BlockNonAdminRestart), nameof(AccessRolesSettingsDto.TrimPublicUsers),
        nameof(AccessRolesSettingsDto.MirrorPreferenceLockToPolicy),
        nameof(AccessRolesSettingsDto.FirstLoadMode), nameof(AccessRolesSettingsDto.BlockedNavigationNotice), nameof(AccessRolesSettingsDto.ShowManagedNoticeOnUserPages),
        nameof(AccessRolesSettingsDto.EnableWebUi), nameof(AccessRolesSettingsDto.ReportWebAnchorProblems), nameof(AccessRolesSettingsDto.SnapshotsToKeep),
        nameof(AccessRolesSettingsDto.AuditRetentionMonths)
    ];

    private readonly IPluginConfigurationProvider _configuration;
    private readonly IServerUsers _users;
    private readonly AccessRolesAuditLog _audit;
    private readonly object _save = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesSettingsService"/> class.
    /// </summary>
    /// <param name="configuration">The plugin configuration.</param>
    /// <param name="users">The server's users, for the audit line's actor.</param>
    /// <param name="audit">The audit log.</param>
    public AccessRolesSettingsService(IPluginConfigurationProvider configuration, IServerUsers users, AccessRolesAuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(audit);
        _configuration = configuration;
        _users = users;
        _audit = audit;
    }

    /// <summary>
    /// Reads the settings as this build reads them: an unknown value as its default, a number within its range,
    /// the allow-list's keys lower case and once each.
    /// </summary>
    /// <param name="configuration">The configuration.</param>
    /// <returns>The settings.</returns>
    public static AccessRolesSettingsDto Read(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new AccessRolesSettingsDto
        {
            EnableWebUi = configuration.EnableWebUi,
            ReportWebAnchorProblems = configuration.ReportWebAnchorProblems,
            SnapshotsToKeep = Math.Max(0, configuration.SnapshotsToKeep),
            EnforcementEnabled = configuration.EnforcementEnabled,
            LibrarySyncEnabled = configuration.LibrarySyncEnabled,
            ReconcileIntervalMinutes = Math.Clamp(configuration.ReconcileIntervalMinutes, 1, 60),
            DriftPolicy = LibrarySync.DriftPolicy(configuration),
            LibraryHardening = Is(configuration.LibraryHardening, "Off") ? "Off" : Is(configuration.LibraryHardening, "Strict") ? "Strict" : "Standard",
            NonAdminConfigurationReads = Is(configuration.NonAdminConfigurationReads, "Redact") ? "Redact" : "AllowList",
            ConfigurationReadAllowList = [.. (configuration.ConfigurationReadAllowList ?? []).Where(k => !string.IsNullOrWhiteSpace(k)).Select(ConfigurationReadGuard.Normalize).Distinct(StringComparer.Ordinal)],
            BlockNonAdminRestart = configuration.BlockNonAdminRestart,
            TrimPublicUsers = configuration.TrimPublicUsers,
            MirrorPreferenceLockToPolicy = configuration.MirrorPreferenceLockToPolicy,
            FirstLoadMode = string.Equals(configuration.FirstLoadMode, "ShowUntilKnown", StringComparison.Ordinal) ? "ShowUntilKnown" : "HideUntilKnown",
            BlockedNavigationNotice = string.Equals(configuration.BlockedNavigationNotice, "Silent", StringComparison.Ordinal) ? "Silent" : "Toast",
            ShowManagedNoticeOnUserPages = configuration.ShowManagedNoticeOnUserPages,
            AuditRetentionMonths = Math.Max(0, configuration.AuditRetentionMonths)
        };
    }

    /// <inheritdoc />
    public AccessRolesSettingsDto Get() => Read(_configuration.GetConfiguration());

    /// <inheritdoc />
    public async Task<AccessRolesSettingsChangeDto> UpdateAsync(AccessRolesSettingsDto body, AccessRolesSettingsOptions options, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(caller);
        if (body.UnknownKeys is { Count: > 0 } unknown)
        {
            // A key that names no setting (misspelt, or of a newer build) is refused, dry run or not.
            var names = unknown.Keys.Order(StringComparer.Ordinal).Take(10).Select(k => k.Length > 64 ? k[..64] + "…" : k);
            throw PluginException.Invalid("No such setting: " + string.Join(", ", names) + (unknown.Count > 10 ? " and " + (unknown.Count - 10).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty) + " (the names are as GET Settings answers them). Nothing was saved.");
        }

        // Read, checked and saved under one lock, so two saves can't both pass the same token; the audit line after it.
        AccessRolesSettingsChangeDto answer;
        List<string> secrets;
        lock (_save)
        {
            var current = _configuration.GetConfiguration();
            var before = Read(current);
            var after = Merge(before, body);
            var changes = Changes(before, after);
            secrets = [.. (after.ConfigurationReadAllowList ?? []).Where(k => SecretKeys.Contains(k, StringComparer.Ordinal) && !(before.ConfigurationReadAllowList ?? []).Contains(k, StringComparer.Ordinal))];
            var lines = Lines(changes);
            answer = new AccessRolesSettingsChangeDto
            {
                DryRun = options.DryRun,
                Settings = after,
                Changes = changes,
                SecretKeysAdded = secrets,
                Warnings = Warnings(before, after),
                PreviewToken = AccessRolesTokens.Of(lines)
            };
            if (options.DryRun)
            {
                return answer;
            }

            if (options.IfPreview is { } confirmed && !AccessRolesTokens.Same(answer.PreviewToken, confirmed))
            {
                var differing = AccessRolesTokens.Differing(confirmed, lines);
                var named = differing is null ? changes.Select(c => c.Name) : Names.Where(differing.Contains);
                throw new AccessRolesException(
                    PluginErrorKind.Conflict,
                    AccessRolesException.Changed,
                    "The settings changed since the preview you saw (" + (string.Join(", ", named) is { Length: > 0 } list ? list : "what the change does") + "); nothing was saved. Look at the preview again.");
            }

            if (secrets.Count > 0 && !options.ConfirmSecretKeys)
            {
                throw new AccessRolesException(
                    PluginErrorKind.Invalid,
                    AccessRolesException.SecretKeys,
                    "Adding " + string.Join(", ", secrets) + " to the configuration-read allow-list needs your confirmation (confirmSecretKeys=true): their secrets are still stripped from what non-admins read, but the rest is the server's configuration. Nothing was saved.");
            }

            if (changes.Count == 0)
            {
                return answer;
            }

            _configuration.Save(Apply(Copy(current), after, changes.Select(c => c.Name).ToHashSet(StringComparer.Ordinal)));
        }

        var actor = AuditActor.Of(caller, id => _users.Find(id)?.Name);
        await _audit.AppendAsync(
            [
                new AuditRecord(actor, AuditActions.SettingsChanged, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["Changes"] = answer.Changes.Select(c => new Dictionary<string, object?>(StringComparer.Ordinal) { ["Name"] = c.Name, ["Before"] = c.Before, ["After"] = c.After }).ToList(),
                    ["SecretKeysAdded"] = secrets
                })
            ],
            CancellationToken.None).ConfigureAwait(false);
        return answer;
    }

    private static bool Is(string? value, string expected) => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    // The body over the settings read, every value checked; a value the body leaves out stays.
    private static AccessRolesSettingsDto Merge(AccessRolesSettingsDto before, AccessRolesSettingsDto body) => new()
    {
        EnableWebUi = body.EnableWebUi ?? before.EnableWebUi,
        ReportWebAnchorProblems = body.ReportWebAnchorProblems ?? before.ReportWebAnchorProblems,
        SnapshotsToKeep = Range(body.SnapshotsToKeep, before.SnapshotsToKeep, 0, 100, nameof(body.SnapshotsToKeep)),
        EnforcementEnabled = body.EnforcementEnabled ?? before.EnforcementEnabled,
        LibrarySyncEnabled = body.LibrarySyncEnabled ?? before.LibrarySyncEnabled,
        ReconcileIntervalMinutes = Range(body.ReconcileIntervalMinutes, before.ReconcileIntervalMinutes, 1, 60, nameof(body.ReconcileIntervalMinutes)),
        DriftPolicy = OneOf(body.DriftPolicy, before.DriftPolicy, nameof(body.DriftPolicy), "Reapply", "ReportOnly"),
        LibraryHardening = Hardening(body.LibraryHardening, before.LibraryHardening),
        NonAdminConfigurationReads = OneOf(body.NonAdminConfigurationReads, before.NonAdminConfigurationReads, nameof(body.NonAdminConfigurationReads), "AllowList", "Redact"),
        ConfigurationReadAllowList = body.ConfigurationReadAllowList is { } keys ? AllowList(keys) : before.ConfigurationReadAllowList,
        BlockNonAdminRestart = body.BlockNonAdminRestart ?? before.BlockNonAdminRestart,
        TrimPublicUsers = body.TrimPublicUsers ?? before.TrimPublicUsers,
        MirrorPreferenceLockToPolicy = body.MirrorPreferenceLockToPolicy ?? before.MirrorPreferenceLockToPolicy,
        FirstLoadMode = OneOf(body.FirstLoadMode, before.FirstLoadMode, nameof(body.FirstLoadMode), "HideUntilKnown", "ShowUntilKnown"),
        BlockedNavigationNotice = OneOf(body.BlockedNavigationNotice, before.BlockedNavigationNotice, nameof(body.BlockedNavigationNotice), "Toast", "Silent"),
        ShowManagedNoticeOnUserPages = body.ShowManagedNoticeOnUserPages ?? before.ShowManagedNoticeOnUserPages,
        AuditRetentionMonths = Range(body.AuditRetentionMonths, before.AuditRetentionMonths, 0, 120, nameof(body.AuditRetentionMonths))
    };

    private static int? Range(int? value, int? current, int min, int max, string name)
        => value is not { } given ? current
            : given >= min && given <= max ? given
            : throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "{0} must be {1} to {2}.", name, min, max));

    private static string? OneOf(string? value, string? current, string name, params string[] allowed)
    {
        if (value is null)
        {
            return current;
        }

        return allowed.FirstOrDefault(a => Is(value, a))
            ?? throw PluginException.Invalid(name + " must be " + string.Join(" or ", allowed) + ".");
    }

    // Off or Standard; Strict (1.1) only as it is stored already.
    private static string? Hardening(string? value, string? current)
    {
        if (value is null)
        {
            return current;
        }

        if (Is(value, "Strict"))
        {
            return string.Equals(current, "Strict", StringComparison.Ordinal)
                ? current
                : throw PluginException.Invalid("LibraryHardening Strict arrives in 1.1: choose Off or Standard.");
        }

        return OneOf(value, current, nameof(AccessRolesSettingsDto.LibraryHardening), "Off", "Standard");
    }

    private static List<string> AllowList(IReadOnlyList<string> keys)
    {
        var normalized = new List<string>();
        foreach (var key in keys)
        {
            if (key is null)
            {
                throw PluginException.Invalid("ConfigurationReadAllowList can't have an empty key.");
            }

            var value = ConfigurationReadGuard.Normalize(key);
            if (!KeyPattern().IsMatch(value))
            {
                throw PluginException.Invalid("\"" + key + "\" isn't a configuration key: letters, digits, '.', '_' and '-', at most 64.");
            }

            if (!normalized.Contains(value, StringComparer.Ordinal))
            {
                normalized.Add(value);
            }
        }

        return normalized.Count <= MaxAllowListKeys
            ? normalized
            : throw PluginException.Invalid(string.Format(CultureInfo.InvariantCulture, "ConfigurationReadAllowList takes at most {0} keys.", MaxAllowListKeys));
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    private static string Text(AccessRolesSettingsDto settings, string name) => name switch
    {
        nameof(AccessRolesSettingsDto.ConfigurationReadAllowList) => string.Join(", ", settings.ConfigurationReadAllowList ?? []),
        _ => typeof(AccessRolesSettingsDto).GetProperty(name)!.GetValue(settings) switch
        {
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            var other => other?.ToString() ?? string.Empty
        }
    };

    private static List<AccessRolesSettingChangeDto> Changes(AccessRolesSettingsDto before, AccessRolesSettingsDto after)
        => [.. Names.Select(n => new AccessRolesSettingChangeDto { Name = n, Before = Text(before, n), After = Text(after, n) }).Where(c => !string.Equals(c.Before, c.After, StringComparison.Ordinal))];

    private static Dictionary<string, string> Lines(IReadOnlyList<AccessRolesSettingChangeDto> changes)
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = "settings" };
        foreach (var change in changes)
        {
            lines[change.Name] = change.Before + "→" + change.After;
        }

        return lines;
    }

    private static List<string> Warnings(AccessRolesSettingsDto before, AccessRolesSettingsDto after)
    {
        var warnings = new List<string>();
        if (before.EnforcementEnabled == true && after.EnforcementEnabled == false)
        {
            warnings.Add("Nothing is enforced: settings locks, the admin surfaces and the server-wide hardening pass everything, and the library sync writes nothing. Restrictions are only stored. From the next server start the plugin is inert: only its admin page loads, and offers to turn enforcement back on.");
        }

        if (before.LibrarySyncEnabled == true && after.LibrarySyncEnabled == false)
        {
            warnings.Add("Library roles are stored and previewed but never written to users' library access.");
        }

        if (!Is(before.LibraryHardening, "Off") && Is(after.LibraryHardening, "Off"))
        {
            warnings.Add("Library leak hardening is off: a non-admin can find items of libraries they can't open through search, people, suggestions and the other answers the hardening covers.");
        }

        if (Is(before.NonAdminConfigurationReads, "AllowList") && Is(after.NonAdminConfigurationReads, "Redact"))
        {
            warnings.Add("Non-admins can read every configuration key, with its known secrets stripped; a key of another plugin's type is refused.");
        }

        if (before.BlockNonAdminRestart == true && after.BlockNonAdminRestart == false)
        {
            warnings.Add("Anyone who can reach the server can restart it, without signing in, as on a stock 10.11.5.");
        }

        if (before.TrimPublicUsers == true && after.TrimPublicUsers == false)
        {
            warnings.Add("The public user list shows every field again, other users' library ids included, to anyone who can reach the server.");
        }

        if (before.MirrorPreferenceLockToPolicy == false && after.MirrorPreferenceLockToPolicy == true)
        {
            warnings.Add("The users whose roles lock all five server-stored settings sections also lose native preference access, which stays after the plugin is disabled or deleted (release it first: Overview, Release all locks), has no checkbox on the Dashboard, and blocks their own watched states and favourites through /UserItems/{id}/UserData.");
        }

        if (Is(before.BlockedNavigationNotice, "Toast") && Is(after.BlockedNavigationNotice, "Silent"))
        {
            warnings.Add("Web users get no feedback at all when a navigation is blocked: the stock pages show nothing on a 403.");
        }

        if (Is(before.FirstLoadMode, "HideUntilKnown") && Is(after.FirstLoadMode, "ShowUntilKnown"))
        {
            warnings.Add("In a browser that doesn't know a managed user's locks yet (a new browser, a cleared cache), the web client shows their locked Settings entries until the server answers; the server still refuses what is locked.");
        }

        if (before.EnableWebUi == true && after.EnableWebUi == false)
        {
            warnings.Add("The web client's script and head fragment stop: nothing is hidden in the web, while the server still enforces.");
        }

        return warnings;
    }

    // A copy of the configuration (the instance Jellyfin holds is replaced, never changed in place).
    private static PluginConfiguration Copy(PluginConfiguration configuration)
        => JsonSerializer.Deserialize<PluginConfiguration>(JsonSerializer.Serialize(configuration)) ?? new PluginConfiguration();

    // Only the settings the change changes: the others stay as they are stored, an unknown value included.
    private static PluginConfiguration Apply(PluginConfiguration configuration, AccessRolesSettingsDto settings, IReadOnlySet<string> changed)
    {
        bool Changed(string name) => changed.Contains(name);
        if (Changed(nameof(settings.EnableWebUi)))
        {
            configuration.EnableWebUi = settings.EnableWebUi!.Value;
        }

        if (Changed(nameof(settings.ReportWebAnchorProblems)))
        {
            configuration.ReportWebAnchorProblems = settings.ReportWebAnchorProblems!.Value;
        }

        if (Changed(nameof(settings.SnapshotsToKeep)))
        {
            configuration.SnapshotsToKeep = settings.SnapshotsToKeep!.Value;
        }

        if (Changed(nameof(settings.EnforcementEnabled)))
        {
            configuration.EnforcementEnabled = settings.EnforcementEnabled!.Value;
        }

        if (Changed(nameof(settings.LibrarySyncEnabled)))
        {
            configuration.LibrarySyncEnabled = settings.LibrarySyncEnabled!.Value;
        }

        if (Changed(nameof(settings.ReconcileIntervalMinutes)))
        {
            configuration.ReconcileIntervalMinutes = settings.ReconcileIntervalMinutes!.Value;
        }

        if (Changed(nameof(settings.DriftPolicy)))
        {
            configuration.DriftPolicy = settings.DriftPolicy!;
        }

        if (Changed(nameof(settings.LibraryHardening)))
        {
            configuration.LibraryHardening = settings.LibraryHardening!;
        }

        if (Changed(nameof(settings.NonAdminConfigurationReads)))
        {
            configuration.NonAdminConfigurationReads = settings.NonAdminConfigurationReads!;
        }

        if (Changed(nameof(settings.ConfigurationReadAllowList)))
        {
            configuration.ConfigurationReadAllowList = [.. settings.ConfigurationReadAllowList ?? []];
        }

        if (Changed(nameof(settings.BlockNonAdminRestart)))
        {
            configuration.BlockNonAdminRestart = settings.BlockNonAdminRestart!.Value;
        }

        if (Changed(nameof(settings.TrimPublicUsers)))
        {
            configuration.TrimPublicUsers = settings.TrimPublicUsers!.Value;
        }

        if (Changed(nameof(settings.MirrorPreferenceLockToPolicy)))
        {
            configuration.MirrorPreferenceLockToPolicy = settings.MirrorPreferenceLockToPolicy!.Value;
        }

        if (Changed(nameof(settings.FirstLoadMode)))
        {
            configuration.FirstLoadMode = settings.FirstLoadMode!;
        }

        if (Changed(nameof(settings.BlockedNavigationNotice)))
        {
            configuration.BlockedNavigationNotice = settings.BlockedNavigationNotice!;
        }

        if (Changed(nameof(settings.ShowManagedNoticeOnUserPages)))
        {
            configuration.ShowManagedNoticeOnUserPages = settings.ShowManagedNoticeOnUserPages!.Value;
        }

        if (Changed(nameof(settings.AuditRetentionMonths)))
        {
            configuration.AuditRetentionMonths = settings.AuditRetentionMonths!.Value;
        }

        return configuration;
    }
}
