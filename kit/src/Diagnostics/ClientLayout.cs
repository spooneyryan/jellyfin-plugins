namespace Jellyfin.Plugin.Kit.Diagnostics;

/// <summary>
/// The web client's layout in a health report (<c>html.layout-tv</c>, <c>html.layout-mobile</c>, else desktop).
/// </summary>
internal enum ClientLayout
{
    /// <summary>The desktop layout (also the experimental layout, which keeps <c>html.layout-desktop</c>).</summary>
    Desktop,

    /// <summary>The mobile layout.</summary>
    Mobile,

    /// <summary>The TV layout.</summary>
    Tv
}
