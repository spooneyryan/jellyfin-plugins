namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// What a poster badge shows (1.1; <see cref="PluginConfiguration.PosterBadgeContent"/>).
/// </summary>
internal enum PosterBadgeContent
{
    /// <summary>The member average (the default).</summary>
    Average,

    /// <summary>The viewer's own stars.</summary>
    Mine,

    /// <summary>Both.</summary>
    Both
}
