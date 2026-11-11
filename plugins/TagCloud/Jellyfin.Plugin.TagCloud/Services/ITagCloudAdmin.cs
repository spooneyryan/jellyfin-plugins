using Jellyfin.Plugin.TagCloud.Api.Models;

namespace Jellyfin.Plugin.TagCloud.Services;

/// <summary>
/// The admin's view of the index (<c>/TagCloud/Admin</c>).
/// </summary>
public interface ITagCloudAdmin
{
    /// <summary>
    /// Gets the index's state.
    /// </summary>
    /// <returns>The status.</returns>
    TagCloudStatusDto GetStatus();

    /// <summary>
    /// Asks for a full rebuild of the index; it runs in the background.
    /// </summary>
    /// <returns>The status as the rebuild is asked for.</returns>
    TagCloudStatusDto RequestRebuild();
}
