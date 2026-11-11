using System;
using System.Linq;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.Kit.Web;
using Jellyfin.Plugin.Kit.Web.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Bookmarks;

/// <summary>
/// Bookmarks' identity for the shared kit.
/// </summary>
internal static class BookmarksKit
{
    /// <summary>The key of the first page of the page-hosting demo (<c>#/jfbm-demo-a</c>).</summary>
    public const string DemoPageA = "demo-a";

    /// <summary>The key of the second page of the page-hosting demo (<c>#/jfbm-demo-b</c>).</summary>
    public const string DemoPageB = "demo-b";

    /// <summary>
    /// The key of the My Bookmarks page (<c>#/jfbm-bookmarks</c>, <c>/Bookmarks/Pages/bookmarks</c>; since 1.1.0):
    /// the first of <see cref="KitOptions.Pages"/>, known for good, on while the setting <c>EnableBookmarksPage</c> is;
    /// the capabilities report it as <c>Pages.Bookmarks</c> (<see cref="HasPage"/>).
    /// </summary>
    public const string BookmarksPage = "bookmarks";

    /// <summary>The label of the My Bookmarks page (never "Bookmarks", which Jellyfin Enhanced uses).</summary>
    public const string BookmarksPageTitle = "My Bookmarks";

    /// <summary>The Material icon of the My Bookmarks page's nav link.</summary>
    public const string BookmarksPageIcon = "collections_bookmark";

    /// <summary>The suffix of the My Bookmarks page's Plugin Pages Id, <c>Jellyfin.Plugin.Bookmarks.BookmarksPage</c>.</summary>
    public const string BookmarksPageIdSuffix = "BookmarksPage";

    /// <summary>
    /// The key of the My Clips page (<c>#/jfbm-clips</c>, <c>/Bookmarks/Pages/clips</c>; since 1.2.0): the second of
    /// <see cref="KitOptions.Pages"/>, known for good, on while the setting <c>EnableClipsPage</c> is; the
    /// capabilities report it as <c>Pages.Clips</c> (<see cref="HasPage"/>).
    /// </summary>
    public const string ClipsPage = "clips";

    /// <summary>The label of the My Clips page.</summary>
    public const string ClipsPageTitle = "My Clips";

    /// <summary>The Material icon of the My Clips page's nav link.</summary>
    public const string ClipsPageIcon = "video_library";

    /// <summary>The suffix of the My Clips page's Plugin Pages Id, <c>Jellyfin.Plugin.Bookmarks.ClipsPage</c>.</summary>
    public const string ClipsPageIdSuffix = "ClipsPage";

    /// <summary>
    /// The name of the bookmark shortcut setting (<c>BookmarkNowShortcut</c>) for the kit's shortcut policy: the Diagnostics
    /// entry's <c>OurShortcuts</c> and the settings page's check.
    /// </summary>
    public const string BookmarkNowShortcutName = "BookmarkNow";

    /// <summary>
    /// Gets the kit options. The tests check them with <see cref="KitOptions.Validate"/>.
    /// </summary>
    public static KitOptions Options { get; } = new()
    {
        PluginName = "Bookmarks",
        RoutePrefix = "Bookmarks",
        Prefix = "jfbm",
        SchemaPrefix = "Bookmark",
        DataFolder = "bookmarks",
        PluginAssembly = typeof(BookmarksKit).Assembly,
        PluginId = Guid.Parse(Plugin.PluginId),

        // What happens to a deleted user's data: KeepAsOrphan by default. Read each time a user is deleted or an admin
        // applies it.
        DeletedUserPolicy = services => DeletedUserPolicies.Parse(Configuration(services).DeletedUserPolicy, DeletedUserPolicy.KeepAsOrphan),

        // The settings page's Data tab offers Start empty for kit-users.json, the list of users deleted on this server, while
        // it can't be read (since Bookmarks 1.2).
        UsersRecoveryPage = true,

        // The clock of the kit's record lifecycle: the system clock, which a Debug build moves on by the developer setting
        // DeveloperClockOffsetDays so a sandbox can check the archive after retention.
        Clock = DeveloperClock.For,

        // Read on every index.html request, so the setting applies without a restart.
        WebUiEnabled = services => Configuration(services).EnableWebUi,

        // The kit's page hosting. The user pages: My Bookmarks (on while EnableBookmarksPage is, read per request for its
        // shell and index.html and once at startup for the Plugin Pages registration), My Clips (on while EnableClipsPage
        // is, read the same ways), and the two pages of
        // the developer demo, on only while EnablePageDemo is (off by default; the sandbox checks of the page hosting use
        // them). Every page here is known for good: a page that is off keeps its key, its shell answers 200 with the
        // "turned off" note and its route shows that note, and it has no nav link and no Plugin Pages registration.
        Pages =
        [
            new KitPage(BookmarksPage, BookmarksPageTitle, BookmarksPageIcon, BookmarksPageIdSuffix) { IsEnabled = services => Configuration(services).EnableBookmarksPage },
            new KitPage(ClipsPage, ClipsPageTitle, ClipsPageIcon, ClipsPageIdSuffix) { IsEnabled = services => Configuration(services).EnableClipsPage },
            new KitPage(DemoPageA, "Demo page A", "video_library", "DemoAPage") { IsEnabled = services => Configuration(services).EnablePageDemo },
            new KitPage(DemoPageB, "Demo page B", "collections_bookmark", "DemoBPage")
            {
                IsEnabled = services => Configuration(services) is { EnablePageDemo: true, EnableDemoPageB: true }
            },
        ],
        RegisterPluginPages = services => Configuration(services).RegisterPluginPages,
        PageHostMode = services => Configuration(services).PageHostMode,
        ShowFallbackNav = services => Configuration(services).ShowFallbackNav,

        // Whether the ClientHealth endpoint records reports of missing jellyfin-web anchors.
        ReportWebAnchorProblems = services => Configuration(services).ReportWebAnchorProblems,

        // Jellyfin Enhanced interop: Bookmarks replaces JE's Bookmarks (the overlap banner and the switch-off checklist come
        // from the kit), and its BookmarkNowShortcut setting is checked against jellyfin-web's and JE's keys on every
        // Diagnostics request, and as the settings page's field is typed (GET /Bookmarks/Admin/Shortcuts/Check). The banner
        // follows ShowJellyfinEnhancedBanner.
        JellyfinEnhanced = new JeInteropOptions
        {
            Overlap = JeFeature.Bookmarks,
            Shortcuts = services => [new JeShortcutSetting(BookmarkNowShortcutName, "Bookmark this moment", Configuration(services).BookmarkNowShortcut)],
            ShowBanner = services => Configuration(services).ShowJellyfinEnhancedBanner
        },

        // The fallback routes' head fragment and page-host state of every known page, on every index.html request since
        // the My Bookmarks page: the known keys and titles, the keys turned off, the host mode and the nav setting.
        HeadFragment = context => FallbackRouteHeadFragment.Render(Options!, context.RequestServices)
    };

    /// <summary>
    /// Gets a value indicating whether the build has the user page with this key (a known page, turned on or not).
    /// </summary>
    /// <param name="key">The page key.</param>
    /// <returns>Whether <see cref="KitOptions.Pages"/> lists it.</returns>
    public static bool HasPage(string key) => Options.Pages.Any(page => string.Equals(page.Key, key, StringComparison.Ordinal));

    private static PluginConfiguration Configuration(IServiceProvider services)
        => services.GetRequiredService<IPluginConfigurationProvider>().GetConfiguration();
}
