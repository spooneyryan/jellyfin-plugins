namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// A poster badge's corner in logical terms (1.1; <see cref="PluginConfiguration.PosterBadgePosition"/>, whose
/// values are <c>top-start</c>, <c>top-end</c>, <c>bottom-start</c> and <c>bottom-end</c>): start is left in LTR, right in
/// RTL.
/// </summary>
internal enum PosterBadgePosition
{
    /// <summary><c>top-start</c> (the default): clear of the native indicators in both directions.</summary>
    TopStart,

    /// <summary><c>top-end</c>.</summary>
    TopEnd,

    /// <summary><c>bottom-start</c>.</summary>
    BottomStart,

    /// <summary><c>bottom-end</c>.</summary>
    BottomEnd
}
