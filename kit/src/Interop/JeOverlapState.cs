using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// The overlap of one Jellyfin Enhanced feature with the plugin that replaces it (<see cref="JeSwitchOff.Describe"/>).
/// </summary>
/// <param name="Feature">The feature.</param>
/// <param name="JeInstalled">Whether Jellyfin Enhanced is installed.</param>
/// <param name="JeActive">Whether it is loaded in this process.</param>
/// <param name="FeatureOn">Whether JE's feature is still on (the banner's condition).</param>
/// <param name="Banner">The settings page's banner while the feature is on, or <c>null</c>.</param>
/// <param name="Checklist">The switch-off checklist, each item ticked as detected.</param>
/// <param name="Complete">Whether every item (but the optional ones) is done.</param>
/// <param name="CanUseB">Bookmarks only: whether <c>B</c> is durably free in Jellyfin Enhanced, so "Use B for bookmarks" may be offered:
/// its <c>BookmarkCurrentTime</c> is off or another key for the server default in a way its next start keeps (not a cleared key, and not only
/// Disable Keyboard Shortcuts), no user's own list sets <c>B</c>, and every user's list could be read.</param>
/// <param name="UseBNote">Why "Use B" is withheld although the items look done (the admin's key is only cleared, or only Disable Keyboard Shortcuts
/// frees it, or a user's list couldn't be read), for the disabled button; <c>null</c> when it is offered, or when the checklist items say it plainly.</param>
internal sealed record JeOverlapState(
    JeFeature Feature,
    bool JeInstalled,
    bool JeActive,
    bool FeatureOn,
    string? Banner,
    IReadOnlyList<JeChecklistItem> Checklist,
    bool Complete,
    bool CanUseB,
    string? UseBNote)
{
    /// <summary>Gets the state of a plugin that replaces nothing.</summary>
    public static JeOverlapState None { get; } = new(JeFeature.None, false, false, false, null, [], true, false, null);
}
