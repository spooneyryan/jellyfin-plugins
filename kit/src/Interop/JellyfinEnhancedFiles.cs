using System;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Where Jellyfin Enhanced (JE), Custom Tabs and Plugin Pages keep what the kit reads. The kit was tested with JE 12.8,
/// 12.11 and 12.12, whose names, formats and defaults are the same (read from their DLLs). Every file is only read: the
/// kit never writes, moves or deletes one of them, and never changes their settings.
/// </summary>
internal static class JellyfinEnhancedFiles
{
    /// <summary>
    /// JE's configuration name: its settings are <c>&lt;PluginConfigurationsPath&gt;/Jellyfin.Plugin.JellyfinEnhanced.xml</c>
    /// and its data the folder of the same name (<c>reviews.json</c>, <c>&lt;userIdN&gt;/bookmark.json</c>,
    /// <c>&lt;userIdN&gt;/shortcuts.json</c>).
    /// </summary>
    public const string ConfigurationName = "Jellyfin.Plugin.JellyfinEnhanced";

    /// <summary>JE's settings file, in the plugin configurations folder.</summary>
    public const string SettingsFile = ConfigurationName + ".xml";

    /// <summary>Custom Tabs' configuration name (<c>Jellyfin.Plugin.CustomTabs.xml</c>).</summary>
    public const string CustomTabsConfigurationName = "Jellyfin.Plugin.CustomTabs";

    /// <summary>JE's server-wide reviews (User Reviews).</summary>
    public const string ReviewsFile = "reviews.json";

    /// <summary>A user's JE bookmarks, in the user's folder.</summary>
    public const string BookmarksFile = "bookmark.json";

    /// <summary>A user's personal JE shortcuts, in the user's folder; they override the server's (JE 12.8 to 12.12 <c>config.js</c>, <c>initializeShortcuts</c>).</summary>
    public const string ShortcutsFile = "shortcuts.json";

    /// <summary>The largest JE, Custom Tabs or Plugin Pages file read: 20 MB.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>The most user folders looked at in JE's data folder.</summary>
    public const int MaxUserFolders = 10_000;

    /// <summary>The Plugin Pages id of JE's bookmarks page.</summary>
    public const string BookmarksPageId = "Jellyfin.Plugin.JellyfinEnhanced.BookmarksPage";

    /// <summary>The Plugin Pages URL of JE's bookmarks page.</summary>
    public const string BookmarksPageUrl = "/JellyfinEnhanced/bookmarksPage";

    /// <summary>
    /// The content of the Custom Tab JE creates for its bookmarks and renders them into (JE 12.8 to 12.12
    /// <c>configPage.html</c>, <c>CUSTOM_TAB_MANAGED_ENTRIES</c>, which recognises its own tab by this exact text).
    /// </summary>
    public const string BookmarksTabHtml = "<div class=\"sections bookmarks\"></div>";

    /// <summary>The JE shortcut whose key opens JE's bookmark dialog in the player.</summary>
    public const string BookmarkShortcut = "BookmarkCurrentTime";

    /// <summary>The JE shortcut whose key (any value but <c>""</c>) makes the digit keys jump to a percentage.</summary>
    public const string PercentageShortcut = "JumpToPercentage";

    /// <summary>Jellyfin Enhanced's plugin id (<c>JellyfinEnhanced.Id</c>, the same in every version).</summary>
    public static readonly Guid PluginId = new("f69e946a-4b3c-4e9a-8f0a-8d7c1b2c4d9b");

    /// <summary>Custom Tabs' plugin id.</summary>
    public static readonly Guid CustomTabsPluginId = new("fbacd0b6-fd46-4a05-b0a4-2045d6a135b0");
}
