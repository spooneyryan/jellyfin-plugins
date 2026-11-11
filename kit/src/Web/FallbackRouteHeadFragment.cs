using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Web.Pages;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// Builds the head fragment of a plugin with fallback routes.
/// jellyfin-web's router doesn't wait for deferred plugin scripts, so on a cold load of <c>#/&lt;pfx&gt;-&lt;key&gt;</c> it
/// shows "Page not found" until the client script arrives (131 frames with the script delayed 1.5 s, measured in a
/// sandbox). The fragment
/// runs while <c>index.html</c> is parsed and removes that race:
/// <list type="bullet">
/// <item><description>a style rule that hides every child of <c>#fallbackPage</c> except the plugin's own page root while
/// <c>&lt;html&gt;</c> has the class <c>&lt;pfx&gt;-route</c>;</description></item>
/// <item><description>a script that sets that class when <c>location.hash</c> is one of the plugin's routes, and a
/// capture-phase <c>viewbeforeshow</c> listener that sets <c>#fallbackPage</c>'s <c>data-title</c> from a static
/// key-to-title map, before libraryMenu reads it. It first keeps the page's own title ("Page not found") in
/// <see cref="OriginalTitleAttribute"/>, unless that is already set: the listener runs before every kit's, so a kit
/// would otherwise record our title as the original and its hand-back would restore that.</description></item>
/// </list>
/// Later route changes are the client script's job. The fragment never touches <c>window.ApiClient</c>, which doesn't
/// exist yet at that point, and holds nothing but the route keys and titles: <c>index.html</c> is anonymous.
/// <para><see cref="Render"/> adds the page-host state the client script reads before it can call the API: a JSON data
/// block <c>&lt;script type="application/json" data-&lt;pfx&gt;-head data-&lt;pfx&gt;-pages&gt;</c> with the known page
/// keys, the keys the admin turned off, <c>PageHostMode</c> and <c>ShowFallbackNav</c>. These are server-wide settings
/// that say nothing about a user or an item, the only kind of value a head fragment may hold.</para>
/// </summary>
internal static partial class FallbackRouteHeadFragment
{
    /// <summary>
    /// The attribute of <c>#fallbackPage</c> that keeps its own <c>data-title</c> while one of our plugins claims it, shared
    /// by every plugin's kit (<c>SHARED.originalTitle</c> in <c>kit/web/kit.js</c>, kept equal by a test).
    /// </summary>
    public const string OriginalTitleAttribute = "data-jfkit-orig-title";

    /// <summary>
    /// Builds the fragment. Keys and titles are fixed per build, so a plugin builds it once and returns the same text on
    /// every request (<c>KitOptions.HeadFragment = _ =&gt; fragment</c>).
    /// </summary>
    /// <param name="prefix">The plugin's DOM prefix, for example <c>jfbm</c>.</param>
    /// <param name="titles">The route keys (the <c>clips</c> of <c>#/jfbm-clips</c>) and their page titles.</param>
    /// <returns>The fragment: one <c>&lt;style&gt;</c> and one <c>&lt;script&gt;</c>, both with the head marker.</returns>
    /// <exception cref="ArgumentException">The prefix, a key or a title is invalid, or there are no routes.</exception>
    public static string Build(string prefix, IEnumerable<KeyValuePair<string, string>> titles)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(titles);
        if (!Prefix().IsMatch(prefix))
        {
            throw new ArgumentException($"'{prefix}' is not a DOM prefix such as 'jfbm'.", nameof(prefix));
        }

        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, title) in titles)
        {
            if (key is null || !Key().IsMatch(key))
            {
                throw new ArgumentException($"'{key}' is not a route key: lower-case letters, digits and '-', such as 'clips'.", nameof(titles));
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                throw new ArgumentException($"The route '{key}' has no title.", nameof(titles));
            }

            if (!map.TryAdd(key, title))
            {
                throw new ArgumentException($"The route '{key}' is listed twice.", nameof(titles));
            }
        }

        if (map.Count == 0)
        {
            throw new ArgumentException("There are no routes.", nameof(titles));
        }

        // The default encoder escapes <, >, &, quotes and non-ASCII, so no title can end the script element.
        var titleMap = JsonSerializer.Serialize(map);
        var marker = IndexHtmlMarkers.Head(prefix);
        var routeClass = prefix + "-route";
        return "<style " + marker + ">html." + routeClass + " #fallbackPage > :not([" + IndexHtmlMarkers.Page(prefix)
            + "]) { display:none !important }</style>"
            + "<script " + marker + ">(function () {"
            + "var t = " + titleMap + ";"
            + "function k() {"
            + "var m = /^\\/" + prefix + "-([a-z0-9-]+)$/.exec(location.hash.replace(/^#!?/, '').split('?')[0]);"
            + "return m && Object.prototype.hasOwnProperty.call(t, m[1]) ? m[1] : null;"
            + "}"
            + "if (k()) { document.documentElement.classList.add('" + routeClass + "'); }"
            + "document.addEventListener('viewbeforeshow', function (e) {"
            + "var x = k();"
            + "var p = e.target;"
            + "if (x && p && p.id === 'fallbackPage') {"
            + "if (!p.hasAttribute('" + OriginalTitleAttribute + "')) { p.setAttribute('" + OriginalTitleAttribute + "', p.getAttribute('data-title') || ''); }"
            + "p.setAttribute('data-title', t[x]);"
            + "}"
            + "}, true);"
            + "})();</script>";
    }

    /// <summary>
    /// Renders the fragment of a plugin's user pages (<c>KitOptions.Pages</c>) for one <c>index.html</c> request: the
    /// <see cref="Build(string, IEnumerable{KeyValuePair{string, string}})"/> fragment for every known page (turned-off
    /// pages included, so their fallback route still shows our "turned off" note under the right title), followed by
    /// the page-host state. A setting that can't be read takes its default (page on, <c>FallbackOnly</c>, nav on).
    /// Plugins set <c>KitOptions.HeadFragment = context =&gt; FallbackRouteHeadFragment.Render(Options, context.RequestServices)</c>.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="services">The request's services, for the settings.</param>
    /// <returns>The fragment, or <c>null</c> when the plugin has no user pages.</returns>
    public static string? Render(KitOptions options, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(services);
        var pages = options.Pages.Where(p => p is not null).ToList();
        if (pages.Count == 0)
        {
            return null;
        }

        var state = new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["pages"] = pages.Select(p => p.Key).Order(StringComparer.Ordinal).ToArray(),
            ["off"] = pages.Where(p => !Read(() => p.IsEnabled(services), true)).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray(),
            ["mode"] = PageHostModes.Parse(Read(() => options.PageHostMode(services), null)).ToString(),
            ["nav"] = Read(() => options.ShowFallbackNav(services), true),
        };

        // The default encoder escapes <, >, &, quotes and non-ASCII, so nothing can end the script element.
        return Build(options.Prefix, pages.Select(p => KeyValuePair.Create(p.Key, p.Title)))
            + "<script type=\"application/json\" " + IndexHtmlMarkers.Head(options.Prefix) + " " + StateAttribute(options.Prefix) + ">"
            + JsonSerializer.Serialize(state) + "</script>";
    }

    /// <summary>
    /// Gets the attribute that marks the page-host state element, <c>data-&lt;prefix&gt;-pages</c>.
    /// </summary>
    /// <param name="prefix">The plugin's DOM prefix.</param>
    /// <returns>The attribute name.</returns>
    public static string StateAttribute(string prefix) => "data-" + prefix + "-pages";

    /// <summary>
    /// Builds the fragment for routes given as key and title pairs.
    /// </summary>
    /// <param name="prefix">The plugin's DOM prefix.</param>
    /// <param name="routes">The route keys and titles.</param>
    /// <returns>The fragment.</returns>
    public static string Build(string prefix, params (string Key, string Title)[] routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return Build(prefix, routes.Select(r => KeyValuePair.Create(r.Key, r.Title)));
    }

    private static T Read<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    [GeneratedRegex("^jf[a-z0-9]{2,8}$")]
    private static partial Regex Prefix();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex Key();
}
