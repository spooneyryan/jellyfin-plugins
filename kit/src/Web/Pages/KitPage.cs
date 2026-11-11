using System;

namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// One user page of a plugin: its route key, title and icon, the suffix of its Plugin Pages
/// Id, and whether the admin has it turned on. A plugin lists its pages in <c>KitOptions.Pages</c>; every page listed
/// there is <b>known</b> for good, so its shell answers 200 (with a "turned off" note while it is off) and only keys the
/// plugin never had answer 404.
/// </summary>
/// <remarks>
/// The same page is registered in the plugin's JavaScript with <c>kit.pages.register({ key, title, icon, idSuffix,
/// render })</c>; the key, title and icon must match.
/// </remarks>
internal sealed record KitPage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="KitPage"/> class.
    /// </summary>
    /// <param name="key">The route key: lower-case letters, digits and '-', for example <c>clips</c>.</param>
    /// <param name="title">The label and page title, plain text, for example <c>My Clips</c>.</param>
    /// <param name="icon">The Material icon class of the nav link, for example <c>video_library</c>.</param>
    /// <param name="idSuffix">The last part of the Plugin Pages Id, for example <c>ClipsPage</c>.</param>
    public KitPage(string key, string title, string icon, string idSuffix)
    {
        Key = key;
        Title = title;
        Icon = icon;
        IdSuffix = idSuffix;
    }

    /// <summary>
    /// Gets the route key: the <c>clips</c> of <c>#/jfbm-clips</c> and <c>/Bookmarks/Pages/clips</c>.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// Gets the label and page title (plain text; Plugin Pages 2.4.7 puts its <c>DisplayText</c> into <c>innerHTML</c>
    /// unescaped).
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the Material icon class of the nav link (a class jellyfin-web's 46967.*.css bundle has).
    /// </summary>
    public string Icon { get; }

    /// <summary>
    /// Gets the suffix of the Plugin Pages Id, <c>Jellyfin.Plugin.&lt;RoutePrefix&gt;.&lt;IdSuffix&gt;</c>.
    /// </summary>
    public string IdSuffix { get; }

    /// <summary>
    /// Gets whether the admin has the page turned on, read with the request's services for each shell and each
    /// <c>index.html</c>, and once at startup for the Plugin Pages registration (Plugin Pages 2.4.x can't unregister, so a
    /// page turned off later keeps its link until the next restart). Defaults to always on.
    /// </summary>
    public Func<IServiceProvider, bool> IsEnabled { get; init; } = static _ => true;

    /// <summary>
    /// Gets the Plugin Pages Id of the page for a plugin.
    /// </summary>
    /// <param name="routePrefix">The plugin's route prefix, for example <c>Bookmarks</c>.</param>
    /// <returns>The Id, for example <c>Jellyfin.Plugin.Bookmarks.ClipsPage</c>.</returns>
    public string PluginPagesId(string routePrefix) => "Jellyfin.Plugin." + routePrefix + "." + IdSuffix;

    /// <summary>
    /// Gets the shell URL of the page: root-relative, without the BaseUrl (Plugin Pages adds it), with no <c>&amp;</c>,
    /// <c>?</c> or <c>#</c>.
    /// </summary>
    /// <param name="routePrefix">The plugin's route prefix.</param>
    /// <returns>The URL, for example <c>/Bookmarks/Pages/clips</c>.</returns>
    public string ShellUrl(string routePrefix) => "/" + routePrefix + "/Pages/" + Key;
}
