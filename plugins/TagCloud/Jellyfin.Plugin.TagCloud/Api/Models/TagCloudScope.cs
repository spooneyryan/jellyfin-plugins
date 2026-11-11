namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// Which titles a cloud counts over. TV counts series, never episodes.
/// </summary>
public enum TagCloudScope
{
    /// <summary>Movies.</summary>
    Movies,

    /// <summary>TV series.</summary>
    Shows,

    /// <summary>Movies and TV series.</summary>
    Both
}
