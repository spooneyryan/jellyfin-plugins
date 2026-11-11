using System;
using System.Net;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// The page shells and the sentences they hold. The same sentences are in
/// <c>kit/web/kit.js</c> (<c>PAGE_TEXT</c>), which shows them in the fallback host; a test keeps both copies equal.
/// </summary>
internal static class KitPageMarkup
{
    /// <summary>
    /// Gets the note of a known page the admin turned off.
    /// </summary>
    /// <param name="title">The page title.</param>
    /// <returns>For example "My Clips is turned off by your server admin.".</returns>
    public static string TurnedOffText(string title) => title + " is turned off by your server admin.";

    /// <summary>
    /// Gets the note a shell shows when no script of ours runs.
    /// </summary>
    /// <param name="pluginName">The plugin's display name.</param>
    /// <returns>For example "This page needs the Bookmarks web UI, which is turned off or failed to load.".</returns>
    public static string NeedsWebUiText(string pluginName) => "This page needs the " + pluginName + " web UI, which is turned off or failed to load.";

    /// <summary>
    /// Builds the shell of an enabled page: the page root with its marker, holding one plain-text line that
    /// <c>kit.css</c> hides once the script mounted the page (<c>[data-&lt;pfx&gt;-mounted] &gt; .&lt;pfx&gt;-shell-note</c>),
    /// so the line is visible exactly when our script never ran, for example with <c>EnableWebUi</c> off.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="page">The page.</param>
    /// <returns>The HTML.</returns>
    public static string Shell(KitOptions options, KitPage page)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(page);

        return "<div class=\"" + options.Prefix + "-page\" " + IndexHtmlMarkers.Page(options.Prefix) + "=\"" + WebUtility.HtmlEncode(page.Key) + "\">"
            + Note(options, NeedsWebUiText(options.PluginName))
            + "</div>";
    }

    /// <summary>
    /// Builds the shell of a known page the admin turned off: the note and no page marker, so nothing mounts. It answers
    /// 200, because Plugin Pages' host shows a fragment only on success; a 404 would leave a blank page titled "Home"
    /// and an uncaught promise rejection.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="page">The page.</param>
    /// <returns>The HTML.</returns>
    public static string TurnedOff(KitOptions options, KitPage page)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(page);

        return "<div class=\"" + options.Prefix + "-page\">"
            + Note(options, TurnedOffText(page.Title))
            + "</div>";
    }

    private static string Note(KitOptions options, string text)
        => "<p class=\"" + options.Prefix + "-shell-note\">" + WebUtility.HtmlEncode(text) + "</p>";
}
