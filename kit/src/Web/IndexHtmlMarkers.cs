using System;
using System.Globalization;
using System.Net;
using System.Reflection;
using Jellyfin.Plugin.Kit.Hosting;

namespace Jellyfin.Plugin.Kit.Web;

/// <summary>
/// The markers and the script tag a plugin adds to jellyfin-web's <c>index.html</c>.
/// </summary>
internal static class IndexHtmlMarkers
{
    /// <summary>
    /// Gets the idempotency marker of the script tag, <c>data-&lt;prefix&gt;-plugin</c> (for example <c>data-jfbm-plugin</c>).
    /// </summary>
    /// <param name="prefix">The plugin's DOM prefix (<see cref="KitOptions.Prefix"/>).</param>
    /// <returns>The attribute name.</returns>
    public static string Plugin(string prefix) => "data-" + prefix + "-plugin";

    /// <summary>
    /// Gets the idempotency marker of the head fragment, <c>data-&lt;prefix&gt;-head</c>, which the fragment itself must carry.
    /// </summary>
    /// <param name="prefix">The plugin's DOM prefix.</param>
    /// <returns>The attribute name.</returns>
    public static string Head(string prefix) => "data-" + prefix + "-head";

    /// <summary>
    /// Gets the page-root marker, <c>data-&lt;prefix&gt;-page</c>, that the fallback route's pre-hide rule keeps
    /// visible.
    /// </summary>
    /// <param name="prefix">The plugin's DOM prefix.</param>
    /// <returns>The attribute name.</returns>
    public static string Page(string prefix) => "data-" + prefix + "-page";

    /// <summary>
    /// Builds the script tag, for example
    /// <c>&lt;script data-jfbm-plugin="1.0.0.0" src="../Bookmarks/ClientScript?v=1.0.0.0-3f2a9c1e" defer&gt;&lt;/script&gt;</c>.
    /// The relative <c>../</c> URL works under any BaseUrl, because <c>index.html</c> is only served under <c>…/web/</c> and
    /// jellyfin-web has no <c>&lt;base&gt;</c> tag. The <c>v</c> value ends with the first 8 hex digits of the assembly's
    /// MVID, so every build busts the browser cache.
    /// </summary>
    /// <param name="options">The plugin's kit options.</param>
    /// <returns>The tag.</returns>
    public static string ScriptTag(KitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var version = Version(options.PluginAssembly);
        var source = "../" + options.RoutePrefix + "/ClientScript?v=" + version + "-" + CacheBuster(options.PluginAssembly);
        return "<script " + Plugin(options.Prefix) + "=\"" + WebUtility.HtmlEncode(version) + "\" src=\""
            + WebUtility.HtmlEncode(source) + "\" defer></script>";
    }

    /// <summary>
    /// Gets the assembly's version as four parts, <c>0.0.0.0</c> when it has none.
    /// </summary>
    /// <param name="assembly">The plugin's assembly.</param>
    /// <returns>The version.</returns>
    public static string Version(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.GetName().Version?.ToString() ?? "0.0.0.0";
    }

    /// <summary>
    /// Gets the first 8 hex digits of the assembly's MVID, which changes with every build.
    /// </summary>
    /// <param name="assembly">The plugin's assembly.</param>
    /// <returns>The cache buster.</returns>
    public static string CacheBuster(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        return assembly.ManifestModule.ModuleVersionId.ToString("N", CultureInfo.InvariantCulture)[..8];
    }
}
