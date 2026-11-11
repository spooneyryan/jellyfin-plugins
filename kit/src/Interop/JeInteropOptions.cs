using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// What a plugin tells the kit's Jellyfin Enhanced interop (<c>KitOptions.JellyfinEnhanced</c>): which
/// JE feature it replaces, for the overlap banner and the switch-off checklist, and which of its own shortcut settings the
/// shortcut policy checks against the reserved keys.
/// </summary>
internal sealed class JeInteropOptions
{
    /// <summary>
    /// Gets the Jellyfin Enhanced feature the plugin replaces: <see cref="JeFeature.Bookmarks"/> for Bookmarks,
    /// <see cref="JeFeature.Reviews"/> for User Ratings, <see cref="JeFeature.None"/> for the others. With a feature, the
    /// Diagnostics entry carries the banner while JE's feature is still on and the checklist of what to switch off.
    /// </summary>
    public JeFeature Overlap { get; init; } = JeFeature.None;

    /// <summary>
    /// Gets the plugin's shortcut settings, read with the request's services on every Diagnostics request: each one's
    /// name, label and the combination the admin set (<c>null</c> or empty for off). The policy reports each that is
    /// invalid or reserved by jellyfin-web or Jellyfin Enhanced, for the settings page's warning.
    /// </summary>
    public Func<IServiceProvider, IReadOnlyList<JeShortcutSetting>> Shortcuts { get; init; } = static _ => [];

    /// <summary>
    /// Gets whether the settings page shows the overlap banner while JE's feature is on (the kit's common setting
    /// <c>ShowJellyfinEnhancedBanner</c>, on by default; since Bookmarks 1.1), read with the request's services on
    /// every Diagnostics request. Off, the entry's <c>Overlap.Banner</c> is <c>null</c> and <c>Overlap.BannerHidden</c>
    /// says a banner was left out; the checklist and "Use B" stay.
    /// </summary>
    public Func<IServiceProvider, bool> ShowBanner { get; init; } = static _ => true;
}
