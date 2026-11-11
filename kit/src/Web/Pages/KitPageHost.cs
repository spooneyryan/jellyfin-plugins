namespace Jellyfin.Plugin.Kit.Web.Pages;

/// <summary>
/// Where the server put a user page at startup.
/// </summary>
internal enum KitPageHost
{
    /// <summary>The registration service hasn't run yet.</summary>
    NotStarted,

    /// <summary>The page was turned off at startup, so it wasn't registered anywhere.</summary>
    Disabled,

    /// <summary>The page uses its fallback route (<c>FallbackOnly</c>, registration off, or Plugin Pages absent or
    /// refusing).</summary>
    Fallback,

    /// <summary>Plugin Pages accepted the page.</summary>
    PluginPages,
}
