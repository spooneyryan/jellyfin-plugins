using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// What each section of a user's own Settings menu covers on the server:
/// the <see cref="UserConfiguration"/> fields and the <c>usersettings</c> display-preference keys the mergers keep at their
/// stored values, and the sections that can only be hidden.
/// </summary>
/// <remarks>
/// <para>Every public property of 10.11.5's <see cref="UserConfiguration"/> belongs to exactly one section (a test pins it,
/// so a field a later Jellyfin adds needs a decision). The field lists follow jellyfin-web 10.11.5's pages
/// (<c>displaySettings.js</c> L146, <c>playbackSettings.js</c> L299-309, <c>subtitlesettings.js</c> L88-89,
/// <c>homeScreenSettings.js</c> L356-378 and L380-396).</para>
/// <para>The two Playback overlay keys (<c>enableNextVideoInfoOverlay</c>, <c>enableVideoRemainingTime</c>) are not merged:
/// jellyfin-web reads them back from the browser, and whether a native client reads the server's copy was not
/// checked. <c>chromecastVersion</c> belongs to no section.</para>
/// </remarks>
internal static class SettingsSections
{
    /// <summary>The audit and counter rule name's prefix: <c>Settings.Profile</c>.</summary>
    public const string RulePrefix = "Settings.";

    /// <summary>The <c>homesection</c> key prefix the core parses (any letter case, any index; <c>DisplayPreferencesController.cs</c> L178-190).</summary>
    public const string HomeSectionPrefix = "homesection";

    /// <summary>The <c>landing-</c> key prefix (the landing view of a library; L192-199).</summary>
    public const string LandingPrefix = "landing-";

    /// <summary>Gets the fields of <see cref="UserConfiguration"/> per section.</summary>
    public static IReadOnlyDictionary<AccessRolesLock, IReadOnlyList<string>> ConfigurationFields { get; } = new Dictionary<AccessRolesLock, IReadOnlyList<string>>
    {
        [AccessRolesLock.Profile] = [nameof(UserConfiguration.EnableLocalPassword)],
        [AccessRolesLock.DisplayAccount] = [nameof(UserConfiguration.DisplayMissingEpisodes), nameof(UserConfiguration.DisplayCollectionsView)],
        [AccessRolesLock.Home] =
        [
            nameof(UserConfiguration.OrderedViews), nameof(UserConfiguration.LatestItemsExcludes), nameof(UserConfiguration.MyMediaExcludes),
            nameof(UserConfiguration.GroupedFolders), nameof(UserConfiguration.HidePlayedInLatest)
        ],
        [AccessRolesLock.Playback] =
        [
            nameof(UserConfiguration.AudioLanguagePreference), nameof(UserConfiguration.PlayDefaultAudioTrack), nameof(UserConfiguration.EnableNextEpisodeAutoPlay),
            nameof(UserConfiguration.RememberAudioSelections), nameof(UserConfiguration.RememberSubtitleSelections), nameof(UserConfiguration.CastReceiverId)
        ],
        [AccessRolesLock.Subtitles] = [nameof(UserConfiguration.SubtitleLanguagePreference), nameof(UserConfiguration.SubtitleMode)]
    };

    /// <summary>
    /// Gets the <c>usersettings</c> document's keys per section, matched in any letter case; Home's
    /// <c>homesection*</c> and <c>landing-*</c> are prefixes (<see cref="IsHomeKey"/>).
    /// </summary>
    public static IReadOnlyDictionary<AccessRolesLock, IReadOnlyList<string>> DisplayPreferenceKeys { get; } = new Dictionary<AccessRolesLock, IReadOnlyList<string>>
    {
        [AccessRolesLock.DisplayAccount] = ["useEpisodeImagesInNextUpAndResume", "dashboardTheme"],
        [AccessRolesLock.Home] = ["homesection*", "tvhome", "landing-*"],
        [AccessRolesLock.Playback] = ["skipBackLength", "skipForwardLength"]
    };

    /// <summary>
    /// Gets the sections whose settings live only in the browser or the app, so a lock hides them in the web and the
    /// server has nothing to enforce ("hidden only").
    /// </summary>
    public static IReadOnlySet<AccessRolesLock> HiddenOnly { get; } = new HashSet<AccessRolesLock> { AccessRolesLock.DisplayDevice, AccessRolesLock.Controls };

    /// <summary>
    /// Gets the settings sections the server enforces: every settings section but the hidden-only ones. What a store
    /// that can't be trusted locks for every non-admin.
    /// </summary>
    public static IReadOnlyList<AccessRolesLock> ServerEnforced { get; } = [.. AccessPresets.SettingsSections.Where(s => !HiddenOnly.Contains(s))];

    /// <summary>
    /// Gets the locks this build stores and reports but the server doesn't enforce yet: none since
    /// 0.9.7, which enforces <c>AdminSurfaces</c> and <c>UserDirectory</c>. <c>/Me</c> names them
    /// (<c>NotEnforced</c>) and the role surfaces say "not enforced yet" for them, so a later build's new lock can use it.
    /// </summary>
    public static IReadOnlyList<AccessRolesLock> NotEnforcedYet { get; } = [];

    /// <summary>
    /// Gets the locks besides the settings sections that the server enforces since 0.9.7: what a store that can't
    /// be trusted locks for every non-admin too (fail closed).
    /// </summary>
    public static IReadOnlyList<AccessRolesLock> ServerEnforcedSurfaces { get; } = [AccessRolesLock.AdminSurfaces, AccessRolesLock.UserDirectory];

    /// <summary>
    /// Gets whether the server enforces a lock in this build: not one of <see cref="NotEnforcedYet"/> (none since 0.9.7), and
    /// never the hidden-only sections (<see cref="HiddenOnly"/>: their settings live in the browser or the app, so the web
    /// hides them and the server has nothing to enforce).
    /// </summary>
    /// <param name="lock">The lock.</param>
    /// <returns><c>false</c> for one of <see cref="NotEnforcedYet"/> or <see cref="HiddenOnly"/>.</returns>
    public static bool IsEnforced(AccessRolesLock @lock) => !NotEnforcedYet.Contains(@lock) && !HiddenOnly.Contains(@lock);

    /// <summary>
    /// Gets the rule name of a section: <c>Settings.&lt;Lock&gt;</c> (the <c>RequestsBlocked</c> line's).
    /// </summary>
    /// <param name="section">The section.</param>
    /// <returns>The rule name.</returns>
    public static string RuleName(AccessRolesLock section) => RulePrefix + section;

    /// <summary>
    /// Gets whether a display-preference key is a Home key: one the core parses as a home section (<c>homesection</c> in any
    /// letter case, any index), <c>tvhome</c>, or a <c>landing-</c> key, each in any letter case.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns><c>true</c> for a Home key.</returns>
    public static bool IsHomeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith(HomeSectionPrefix, StringComparison.OrdinalIgnoreCase)
            || key.Equals("tvhome", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(LandingPrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the section a display-preference key belongs to, in any letter case, or <c>null</c>.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The section, or <c>null</c> for a key no section covers.</returns>
    public static AccessRolesLock? SectionOfKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (IsHomeKey(key))
        {
            return AccessRolesLock.Home;
        }

        foreach (var (section, keys) in DisplayPreferenceKeys)
        {
            if (section != AccessRolesLock.Home && keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                return section;
            }
        }

        return null;
    }
}
