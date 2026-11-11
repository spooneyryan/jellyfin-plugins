namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// Where the server put one user page at startup.
/// </summary>
/// <param name="Key">The page key.</param>
/// <param name="PluginPagesId">The page's Plugin Pages Id.</param>
/// <param name="Host">The host.</param>
/// <param name="Detail">Why: the Plugin Pages API and version, the mode, or the failure.</param>
internal sealed record KitPageHostEntry(string Key, string PluginPagesId, KitPageHost Host, string Detail);
