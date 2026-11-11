using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// Duplicates' consumer of library changes: the kit's monitor sends it the changes to movies and videos
/// (a local alternate or an owned item is a <c>Video</c>) after its default 60 s quiet period, at the latest
/// 10 min after the first, never while a library scan runs; and every scan's full pass, from
/// <see cref="Library.LibraryMaintenancePostScanTask"/>. Each marks the index out of date, and the index service builds it
/// (in the background: the full pass doesn't wait for the build).
/// </summary>
/// <remarks>
/// A batch with any change counts, removed folders too: a folder's removal takes its movies with it without an event of
/// their own. A full pass counts even when empty, since the scan may have changed what no event says, and the index then
/// is built once after every scan. With Duplicates turned off nothing is built, but the index is marked out of
/// date, so turning Duplicates on never serves an index that missed changes as current.
/// </remarks>
internal sealed class DuplicatesChangeConsumer : ILibraryChangeConsumer
{
    private readonly MovieIndexService _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicatesChangeConsumer"/> class.
    /// </summary>
    /// <param name="index">The movie index.</param>
    public DuplicatesChangeConsumer(MovieIndexService index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    /// <inheritdoc />
    public string Name => "LibraryMaintenance.Duplicates";

    /// <inheritdoc />
    /// <remarks>Movies and videos of the library, with the kit's defaults (60 s quiet, 10 min at most); no identities, since
    /// the index is rebuilt whole.</remarks>
    public LibraryChangeFilter Filter { get; } = new()
    {
        Kinds = new HashSet<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.Video },
        CaptureIdentity = false
    };

    /// <inheritdoc />
    public Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_index.IsEnabled)
        {
            if (batch.FullPass || !batch.IsEmpty)
            {
                _index.MarkDirty();
            }

            return Task.CompletedTask;
        }

        if (batch.FullPass)
        {
            _ = _index.OnLibraryScanned();
        }
        else if (!batch.IsEmpty)
        {
            _ = _index.OnLibraryChanged();
        }

        return Task.CompletedTask;
    }
}
