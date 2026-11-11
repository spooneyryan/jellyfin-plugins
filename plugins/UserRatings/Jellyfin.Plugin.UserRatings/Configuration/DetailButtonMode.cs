namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// Where the details page's Rate button shows (<see cref="PluginConfiguration.ShowDetailButton"/>).
/// </summary>
internal enum DetailButtonMode
{
    /// <summary>On Series, Season and MusicAlbum pages, whose child list sits above the section (the default).</summary>
    Auto,

    /// <summary>On every rateable page.</summary>
    Always,

    /// <summary>Nowhere.</summary>
    Never
}
