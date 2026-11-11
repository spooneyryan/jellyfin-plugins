using Jellyfin.Plugin.TagCloud.Api.Models;
using Jellyfin.Plugin.TagCloud.Index;

namespace Jellyfin.Plugin.TagCloud.Services;

/// <summary>
/// The admin's view of the index over <see cref="TagCloudIndex"/>.
/// </summary>
internal sealed class TagCloudAdminService : ITagCloudAdmin
{
    private readonly TagCloudIndex _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudAdminService"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    public TagCloudAdminService(TagCloudIndex index)
    {
        _index = index;
    }

    /// <inheritdoc />
    public TagCloudStatusDto GetStatus() => _index.Describe();

    /// <inheritdoc />
    public TagCloudStatusDto RequestRebuild()
    {
        _index.RequestRebuild();
        return _index.Describe();
    }
}
