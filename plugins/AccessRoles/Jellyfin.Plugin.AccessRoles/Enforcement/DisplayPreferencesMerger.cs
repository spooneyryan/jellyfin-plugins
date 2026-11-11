using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AccessRoles.Model;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The <c>usersettings</c> display-preferences merge (<c>DisplayPreferencesMerge</c>): jellyfin-web sends the
/// whole document, so the locked sections' keys (<see cref="SettingsSections.DisplayPreferenceKeys"/>) are taken out of the
/// post in any letter case, every key the core would parse as a home section among them (<c>homesection</c> in any case,
/// any index, also past the stored layout), and exactly the stored values are added back, so a key the post left out is
/// written back too (the core clears the home sections and resets the skip lengths and the theme to defaults for keys that
/// are absent, <c>v10.11.5:Jellyfin.Api/Controllers/DisplayPreferencesController.cs:139-190</c>).
/// </summary>
internal static class DisplayPreferencesMerger
{
    /// <summary>The web client's document: <c>usersettings</c>.</summary>
    public const string UserSettingsDocument = "usersettings";

    /// <summary>The web client's (and Android TV's) <c>client</c> value: <c>emby</c>, compared ignoring case.</summary>
    public const string UserSettingsClient = "emby";

    /// <summary>Gets the <c>usersettings</c> document's id: <c>"usersettings".GetMD5()</c>, <c>3ce5b65d-e116-d731-65d1-efc4a30ec35c</c>.</summary>
    public static Guid UserSettingsDocumentId { get; } = UserSettingsDocument.GetMD5();

    /// <summary>
    /// Gets the id of the document a request names, as the action derives it (<c>Guid.TryParse</c>, else <c>GetMD5()</c>,
    /// L134-137), so every GUID form of the <c>usersettings</c> id (N, D, B, P, upper case) is the same document.
    /// </summary>
    /// <param name="displayPreferencesId">The route's <c>displayPreferencesId</c>.</param>
    /// <returns>The document id.</returns>
    public static Guid DocumentId(string displayPreferencesId)
    {
        ArgumentNullException.ThrowIfNull(displayPreferencesId);
        return Guid.TryParse(displayPreferencesId, out var id) ? id : displayPreferencesId.GetMD5();
    }

    /// <summary>
    /// Gets whether a request writes the web client's <c>usersettings</c> document: its derived id, and <c>client</c>
    /// <c>emby</c> in any letter case (fail closed: the core keeps <c>EMBY</c> as a document of its own, which jellyfin-web
    /// never reads, and it is merged all the same).
    /// </summary>
    /// <param name="displayPreferencesId">The route's <c>displayPreferencesId</c>.</param>
    /// <param name="client">The query's <c>client</c>.</param>
    /// <returns><c>true</c> for the <c>usersettings</c> document.</returns>
    public static bool IsUserSettings(string displayPreferencesId, string client)
    {
        ArgumentNullException.ThrowIfNull(displayPreferencesId);
        ArgumentNullException.ThrowIfNull(client);
        return DocumentId(displayPreferencesId).Equals(UserSettingsDocumentId) && client.Equals(UserSettingsClient, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets whether a merge has anything to do: one of the sections with display-preference keys is locked.
    /// </summary>
    /// <param name="locked">The caller's locks.</param>
    /// <returns><c>true</c> when a merge applies.</returns>
    public static bool Applies(IReadOnlySet<AccessRolesLock> locked)
    {
        ArgumentNullException.ThrowIfNull(locked);
        return SettingsSections.DisplayPreferenceKeys.Keys.Any(locked.Contains);
    }

    /// <summary>
    /// Gets the stored values of a section's keys, as <c>GET /DisplayPreferences/usersettings</c> answers them
    /// (L82-97): the home sections as <c>homesection&lt;Order&gt;</c>, the entity's columns, then the custom keys.
    /// </summary>
    /// <param name="section">The section.</param>
    /// <param name="stored">The stored document.</param>
    /// <param name="storedCustom">The stored custom keys.</param>
    /// <returns>The keys and their values.</returns>
    public static IReadOnlyDictionary<string, string?> StoredKeys(AccessRolesLock section, DisplayPreferences stored, IReadOnlyDictionary<string, string?> storedCustom)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(storedCustom);
        var keys = new Dictionary<string, string?>(StringComparer.Ordinal);
        switch (section)
        {
            case AccessRolesLock.Home:
                foreach (var homeSection in stored.HomeSections.OrderBy(s => s.Order))
                {
                    keys[SettingsSections.HomeSectionPrefix + homeSection.Order.ToString(CultureInfo.InvariantCulture)] = homeSection.Type.ToString().ToLowerInvariant();
                }

                keys["tvhome"] = stored.TvHome;
                foreach (var (key, value) in storedCustom.Where(kv => kv.Key.StartsWith(SettingsSections.LandingPrefix, StringComparison.OrdinalIgnoreCase)))
                {
                    keys.TryAdd(key, value);
                }

                break;
            case AccessRolesLock.Playback:
                keys["skipBackLength"] = stored.SkipBackwardLength.ToString(CultureInfo.InvariantCulture);
                keys["skipForwardLength"] = stored.SkipForwardLength.ToString(CultureInfo.InvariantCulture);
                break;
            case AccessRolesLock.DisplayAccount:
                keys["dashboardTheme"] = stored.DashboardTheme;
                if (storedCustom.TryGetValue("useEpisodeImagesInNextUpAndResume", out var episodeImages))
                {
                    keys["useEpisodeImagesInNextUpAndResume"] = episodeImages;
                }

                break;
        }

        return keys;
    }

    /// <summary>
    /// Merges in place (the bound <see cref="DisplayPreferencesDto"/> belongs to this request only): every posted key of a
    /// locked section goes, in any letter case, and the stored ones are added back.
    /// </summary>
    /// <param name="posted">The bound request body.</param>
    /// <param name="stored">The stored document (<c>IDisplayPreferencesManager.GetDisplayPreferences</c>).</param>
    /// <param name="storedCustom">The stored custom keys (<c>ListCustomItemDisplayPreferences</c>).</param>
    /// <param name="locked">The caller's locks.</param>
    /// <param name="defaults">What jellyfin-web takes for a key with no value (<see cref="WebClientDefaults"/>); the stock web
    /// client's when <c>null</c>.</param>
    /// <returns>
    /// The sections whose keys the post had changed, with those keys: a key whose posted value differs from the stored one,
    /// a value that is absent or empty counting as the web client's default for the key (none for Home's keys) on either side
    /// (jellyfin-web posts <c>useEpisodeImagesInNextUpAndResume: "false"</c> and its default theme on every
    /// Display save, stored or not).
    /// </returns>
    public static IReadOnlyDictionary<AccessRolesLock, IReadOnlyList<string>> Merge(DisplayPreferencesDto posted, DisplayPreferences stored, IReadOnlyDictionary<string, string?> storedCustom, IReadOnlySet<AccessRolesLock> locked, IReadOnlyDictionary<string, string>? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(posted);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(storedCustom);
        ArgumentNullException.ThrowIfNull(locked);
        defaults ??= WebClientDefaults.Stock.Defaults;
        posted.CustomPrefs ??= [];
        var prefs = posted.CustomPrefs;
        var reverted = new Dictionary<AccessRolesLock, IReadOnlyList<string>>();
        foreach (var section in SettingsSections.DisplayPreferenceKeys.Keys.Where(locked.Contains))
        {
            var postedKeys = prefs.Keys.Where(k => SettingsSections.SectionOfKey(k) == section).ToList();
            var postedValues = postedKeys.ToDictionary(k => k, k => prefs[k], StringComparer.Ordinal);
            foreach (var key in postedKeys)
            {
                prefs.Remove(key);
            }

            var storedKeys = StoredKeys(section, stored, storedCustom);
            foreach (var (key, value) in storedKeys)
            {
                prefs[key] = value;
            }

            var changed = postedValues.Keys.Union(storedKeys.Keys, StringComparer.Ordinal)
                .Where(k => !string.Equals(Effective(postedValues, k, defaults), Effective(storedKeys, k, defaults), StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (changed.Count > 0)
            {
                reverted[section] = changed;
            }
        }

        return reverted;
    }

    // A key's value as jellyfin-web reads it: absent, null or empty is the web's default for the key, if it has one, else "".
    // jellyfin-web echoes an unset column as an empty string, which the core stores as it came; neither is a change.
    private static string Effective(IReadOnlyDictionary<string, string?> values, string key, IReadOnlyDictionary<string, string> defaults)
        => values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : defaults.GetValueOrDefault(key) ?? string.Empty;
}
