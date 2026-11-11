namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// Where a plugin's user pages live (<c>PageHostMode</c>). The plugin configuration keeps it as a string
/// (the kit's types are internal); <see cref="PageHostModes.Parse"/> reads it.
/// </summary>
internal enum PageHostMode
{
    /// <summary>
    /// The default: fallback routes <c>#/&lt;pfx&gt;-&lt;key&gt;</c> linked from the "Library tools"
    /// drawer section. Nothing is registered with Plugin Pages.
    /// </summary>
    FallbackOnly,

    /// <summary>
    /// Pages are registered with Plugin Pages at startup; links use its route when it lists the page, and "Library tools"
    /// adds our link whenever the drawer lacks Plugin Pages' (it loses its section after a sign-in without a reload).
    /// Pages Plugin Pages doesn't list use their fallback routes.
    /// </summary>
    Auto,

    /// <summary>
    /// Like <see cref="Auto"/>, but "Library tools" never adds a link for a page Plugin Pages lists, even when its drawer
    /// section is missing. Pages Plugin Pages doesn't list (it isn't installed) still get their fallback route and
    /// link.
    /// </summary>
    PluginPagesOnly,
}
