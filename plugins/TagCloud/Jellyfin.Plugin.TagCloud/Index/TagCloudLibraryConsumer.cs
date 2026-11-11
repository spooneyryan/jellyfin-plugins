using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.TagCloud.Configuration;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// Keeps the index current as the library changes: the kit's monitor batches Jellyfin's item events for movies
/// and series, and each batch patches the titles it names, or, past <c>FullRebuildAfterChanges</c> titles or with a
/// removed folder, asks for a full rebuild. The post-scan task flushes the monitor inside every scan.
/// </summary>
internal sealed class TagCloudLibraryConsumer : ILibraryChangeConsumer
{
    private readonly TagCloudIndex _index;
    private readonly IPluginConfigurationProvider _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudLibraryConsumer"/> class.
    /// </summary>
    /// <param name="index">The index.</param>
    /// <param name="configuration">The settings.</param>
    public TagCloudLibraryConsumer(TagCloudIndex index, IPluginConfigurationProvider configuration)
    {
        _index = index;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public string Name => "TagCloudIndex";

    /// <inheritdoc />
    public LibraryChangeFilter Filter { get; } = new()
    {
        Kinds = new HashSet<BaseItemKind>(TitleReader.Kinds),
        CaptureIdentity = false
    };

    /// <inheritdoc />
    public async Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        if (batch is null || batch.IsEmpty)
        {
            return;
        }

        var removed = batch.Removed.Select(c => c.ItemId).ToHashSet();
        var changed = batch.Added.Concat(batch.Updated).Select(c => c.ItemId).Where(id => !removed.Contains(id)).ToHashSet();
        var threshold = System.Math.Max(1, _configuration.GetConfiguration().FullRebuildAfterChanges);
        if (batch.RemovedFolders.Count > 0 || changed.Count + removed.Count > threshold)
        {
            _index.RequestRebuild();
            return;
        }

        await _index.PatchAsync(changed, removed, cancellationToken).ConfigureAwait(false);
    }
}
