using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Jellyfin Enhanced's defaults in the versions the kit was tested with, 12.8, 12.11 and 12.12 (the same in all three),
/// read from its <c>Configuration.PluginConfiguration</c> constructor: the
/// settings the kit reads, and the 20 shortcuts JE puts back at every start for any action its settings leave without a
/// key (<c>JellyfinEnhanced.BackfillMissingDefaultShortcuts</c>, called from JE's constructor).
/// </summary>
internal static class JellyfinEnhancedDefaults
{
    /// <summary>JE's default shortcuts, in its order.</summary>
    public static readonly IReadOnlyList<JeShortcut> Shortcuts =
    [
        new("OpenSearch", "/", "Open Search", "Global"),
        new("GoToHome", "Shift+H", "Go to Home", "Global"),
        new("GoToDashboard", "D", "Go to Dashboard", "Global"),
        new("QuickConnect", "Q", "Quick Connect", "Global"),
        new("PlayRandomItem", "R", "Play Random Item", "Global"),
        new("CycleAspectRatio", "A", "Cycle Aspect Ratio", "Player"),
        new("ShowPlaybackInfo", "I", "Show Playback Info", "Player"),
        new("SubtitleMenu", "S", "Subtitle Menu", "Player"),
        new("CycleSubtitleTracks", "C", "Cycle Subtitle Tracks", "Player"),
        new("CycleAudioTracks", "V", "Cycle Audio Tracks", "Player"),
        new("IncreasePlaybackSpeed", "+", "Increase Playback Speed", "Player"),
        new("DecreasePlaybackSpeed", "-", "Decrease Playback Speed", "Player"),
        new("ResetPlaybackSpeed", "R", "Reset Playback Speed", "Player"),
        new(JellyfinEnhancedFiles.BookmarkShortcut, "B", "Bookmark Current Time", "Player"),
        new("OpenEpisodePreview", "P", "Open Episode Preview", "Player"),
        new("SkipIntroOutro", "O", "Skip Intro/Outro", "Player"),
        new("FrameStepBack", ",", "Step Back One Frame", "Player"),
        new("FrameStepForward", ".", "Step Forward One Frame", "Player"),
        new("JumpToLastPosition", "Z", "Jump to Last Position", "Player"),
        new(JellyfinEnhancedFiles.PercentageShortcut, "0-9", "Jump to % of video", "Player")
    ];

    /// <summary>
    /// JE's own key that no setting changes: <c>?</c> opens its panel (a separate listener, <c>events.js:11-22</c>, which
    /// checks <c>event.key</c> only, so with or without Shift).
    /// </summary>
    public static readonly IReadOnlyList<string> PanelCombos = ["?", "Shift+?"];

    /// <summary>
    /// The defaults of the settings the kit reads, where JE's constructor sets them (<c>MirrorReviewRatingsToJellyfin</c>
    /// is 12.11's and 12.12's; 12.8 has no such element, which reads as its default). Only a setting listed here is read from the XML.
    /// The review settings after <c>MirrorReviewRatingsToJellyfin</c> are read for User Ratings' switch-over checklist and
    /// import report: <c>ShowAnilistReviews</c> and <c>ReviewTranslationEnabled</c>
    /// are 12.14's (an older JE reads their default).
    /// </summary>
    public static readonly FrozenDictionary<string, bool> Flags = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["ShowUserReviews"] = false,
        ["ShowUserRatingOnPosters"] = false,
        ["MirrorReviewRatingsToJellyfin"] = false,
        ["ShowReviews"] = false,
        ["ShowAnilistReviews"] = false,
        ["ReviewTranslationEnabled"] = false,
        ["ReviewsExpandedByDefault"] = false,
        ["HideReviewsFromHiddenUsers"] = true,
        ["HideReviewsFromDisabledUsers"] = true,
        ["ActivityFeedEnabled"] = false,
        ["ActivityFeedShowReviewed"] = true,
        ["SpoilerStripReviews"] = true,
        ["DisableAllShortcuts"] = false,
        ["BookmarksEnabled"] = true,
        ["BookmarksUsePluginPages"] = false,
        ["BookmarksUseCustomTabs"] = false,
        ["BookmarksUseNativeTab"] = false,
        ["BookmarksAutoCreateCustomTab"] = false,
        ["BookmarksCustomTabJeOwned"] = false
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// JE's text settings the kit reads only for whether they are set (12.14's review translation service, for User
    /// Ratings' switch-over checklist): never their values, which name an outside service and its key. Empty by
    /// default.
    /// </summary>
    public static readonly IReadOnlyList<string> PresenceOnly = ["ReviewTranslationUrl", "ReviewTranslationApiKey"];

    /// <summary>
    /// Gets a JE default shortcut's label.
    /// </summary>
    /// <param name="name">The action.</param>
    /// <returns>The label, or the action's name for one JE doesn't have.</returns>
    public static string LabelOf(string name)
    {
        foreach (var shortcut in Shortcuts)
        {
            if (string.Equals(shortcut.Name, name, StringComparison.Ordinal))
            {
                return shortcut.Label ?? name;
            }
        }

        return name;
    }
}
