using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Model;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The <c>UserConfiguration</c> merge (<c>UserConfigurationMerge</c>, and <c>UserUpdateMerge</c>'s
/// <c>updateUser.Configuration</c>): every settings page of jellyfin-web saves the whole configuration, so a 403 would also
/// lose the unlocked sections' changes; instead the stored values of the locked sections' fields
/// (<see cref="SettingsSections.ConfigurationFields"/>) are copied into the posted configuration and the save goes through.
/// </summary>
internal static class UserConfigurationMerger
{
    private static readonly IReadOnlyDictionary<string, Field> _fields = new Dictionary<string, Field>(StringComparer.Ordinal)
    {
        [nameof(UserConfiguration.EnableLocalPassword)] = new((c, s) => c.EnableLocalPassword = s.EnableLocalPassword, (a, b) => a.EnableLocalPassword == b.EnableLocalPassword),
        [nameof(UserConfiguration.DisplayMissingEpisodes)] = new((c, s) => c.DisplayMissingEpisodes = s.DisplayMissingEpisodes, (a, b) => a.DisplayMissingEpisodes == b.DisplayMissingEpisodes),
        [nameof(UserConfiguration.DisplayCollectionsView)] = new((c, s) => c.DisplayCollectionsView = s.DisplayCollectionsView, (a, b) => a.DisplayCollectionsView == b.DisplayCollectionsView),
        [nameof(UserConfiguration.OrderedViews)] = new((c, s) => c.OrderedViews = [.. s.OrderedViews ?? []], (a, b) => SameIds(a.OrderedViews, b.OrderedViews)),
        [nameof(UserConfiguration.LatestItemsExcludes)] = new((c, s) => c.LatestItemsExcludes = [.. s.LatestItemsExcludes ?? []], (a, b) => SameIds(a.LatestItemsExcludes, b.LatestItemsExcludes)),
        [nameof(UserConfiguration.MyMediaExcludes)] = new((c, s) => c.MyMediaExcludes = [.. s.MyMediaExcludes ?? []], (a, b) => SameIds(a.MyMediaExcludes, b.MyMediaExcludes)),
        [nameof(UserConfiguration.GroupedFolders)] = new((c, s) => c.GroupedFolders = [.. s.GroupedFolders ?? []], (a, b) => SameIds(a.GroupedFolders, b.GroupedFolders)),
        [nameof(UserConfiguration.HidePlayedInLatest)] = new((c, s) => c.HidePlayedInLatest = s.HidePlayedInLatest, (a, b) => a.HidePlayedInLatest == b.HidePlayedInLatest),
        [nameof(UserConfiguration.AudioLanguagePreference)] = new((c, s) => c.AudioLanguagePreference = s.AudioLanguagePreference, (a, b) => SameText(a.AudioLanguagePreference, b.AudioLanguagePreference)),
        [nameof(UserConfiguration.PlayDefaultAudioTrack)] = new((c, s) => c.PlayDefaultAudioTrack = s.PlayDefaultAudioTrack, (a, b) => a.PlayDefaultAudioTrack == b.PlayDefaultAudioTrack),
        [nameof(UserConfiguration.EnableNextEpisodeAutoPlay)] = new((c, s) => c.EnableNextEpisodeAutoPlay = s.EnableNextEpisodeAutoPlay, (a, b) => a.EnableNextEpisodeAutoPlay == b.EnableNextEpisodeAutoPlay),
        [nameof(UserConfiguration.RememberAudioSelections)] = new((c, s) => c.RememberAudioSelections = s.RememberAudioSelections, (a, b) => a.RememberAudioSelections == b.RememberAudioSelections),
        [nameof(UserConfiguration.RememberSubtitleSelections)] = new((c, s) => c.RememberSubtitleSelections = s.RememberSubtitleSelections, (a, b) => a.RememberSubtitleSelections == b.RememberSubtitleSelections),
        [nameof(UserConfiguration.CastReceiverId)] = new((c, s) => c.CastReceiverId = s.CastReceiverId, (a, b) => SameText(a.CastReceiverId, b.CastReceiverId)),
        [nameof(UserConfiguration.SubtitleLanguagePreference)] = new((c, s) => c.SubtitleLanguagePreference = s.SubtitleLanguagePreference, (a, b) => SameText(a.SubtitleLanguagePreference, b.SubtitleLanguagePreference)),
        [nameof(UserConfiguration.SubtitleMode)] = new((c, s) => c.SubtitleMode = s.SubtitleMode, (a, b) => a.SubtitleMode == b.SubtitleMode)
    };

    /// <summary>Gets the names of the fields the merger knows: every field of every section.</summary>
    public static IReadOnlyCollection<string> KnownFields => [.. _fields.Keys];

    /// <summary>
    /// Gets whether a merge has anything to do: one of the sections with configuration fields is locked.
    /// </summary>
    /// <param name="locked">The caller's locks.</param>
    /// <returns><c>true</c> when a merge applies.</returns>
    public static bool Applies(IReadOnlySet<AccessRolesLock> locked)
    {
        ArgumentNullException.ThrowIfNull(locked);
        return SettingsSections.ConfigurationFields.Keys.Any(locked.Contains);
    }

    /// <summary>
    /// Copies the stored values of the locked sections' fields into the posted configuration, in place (the bound argument
    /// belongs to this request only), and says which fields the post had changed.
    /// </summary>
    /// <param name="posted">The bound request body.</param>
    /// <param name="stored">The stored configuration (<c>GetUserDto(user).Configuration</c>).</param>
    /// <param name="locked">The caller's locks.</param>
    /// <returns>The reverted fields, by section; empty when the post changed nothing locked.</returns>
    public static IReadOnlyDictionary<AccessRolesLock, IReadOnlyList<string>> Merge(UserConfiguration posted, UserConfiguration stored, IReadOnlySet<AccessRolesLock> locked)
    {
        ArgumentNullException.ThrowIfNull(posted);
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(locked);
        var reverted = new Dictionary<AccessRolesLock, IReadOnlyList<string>>();
        foreach (var (section, names) in SettingsSections.ConfigurationFields)
        {
            if (!locked.Contains(section))
            {
                continue;
            }

            var changed = new List<string>();
            foreach (var name in names)
            {
                var field = _fields[name];
                if (!field.Same(posted, stored))
                {
                    changed.Add(name);
                }

                field.Copy(posted, stored);
            }

            if (changed.Count > 0)
            {
                reverted[section] = changed;
            }
        }

        return reverted;
    }

    private static bool SameIds(Guid[]? a, Guid[]? b) => (a ?? []).SequenceEqual(b ?? []);

    // A text field never set (null) and one saved empty are the same choice: jellyfin-web's Playback and Subtitles forms post
    // "" for a stored null (a freshly seeded user's AudioLanguagePreference), which isn't a change.
    private static bool SameText(string? a, string? b) => string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);

    private sealed record Field(Action<UserConfiguration, UserConfiguration> Copy, Func<UserConfiguration, UserConfiguration, bool> Same);
}
