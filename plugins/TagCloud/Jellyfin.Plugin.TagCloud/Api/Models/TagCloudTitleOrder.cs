namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// How <c>GET /TagCloud/Titles</c> orders a word's titles.
/// </summary>
public enum TagCloudTitleOrder
{
    /// <summary>By sort name, A–Z (the titles card under the cloud).</summary>
    Name,

    /// <summary>A shuffle fixed by the request's seed (the cloud of a held word's titles, the same each time).</summary>
    Random
}
