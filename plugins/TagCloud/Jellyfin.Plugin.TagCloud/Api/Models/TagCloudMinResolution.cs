namespace Jellyfin.Plugin.TagCloud.Api.Models;

/// <summary>
/// The least sharp picture <c>GET /TagCloud/Titles</c> keeps (<c>minResolution=</c>), for movies only: a series has no
/// picture of its own (its episodes are the videos), so it is never left out by this. A movie whose size isn't known
/// is left out by anything but <see cref="Any"/>. The steps are the server's own labels: a film cropped wide, 1920×800, is
/// 1080p.
/// </summary>
public enum TagCloudMinResolution
{
    /// <summary>Every title.</summary>
    Any,

    /// <summary>480p and up (wider than 720 or taller than 404 pixels).</summary>
    P480,

    /// <summary>720p and up (wider than 1024 or taller than 576 pixels).</summary>
    P720,

    /// <summary>1080p and up (wider than 1280 or taller than 962 pixels).</summary>
    P1080,

    /// <summary>4K (wider than 2560 or taller than 1440 pixels).</summary>
    P4K
}
