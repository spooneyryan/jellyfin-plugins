using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TagCloud.Index;

/// <summary>
/// Builds the index once the server has started (the kit's startup gate), then again whenever a full rebuild is asked for:
/// a large batch of library changes, the daily maintenance, an admin's Rebuild, a source turned on.
/// </summary>
internal sealed class TagCloudIndexWorker : KitBackgroundService
{
    private readonly TagCloudIndex _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="TagCloudIndexWorker"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="index">The index.</param>
    /// <param name="logger">The logger.</param>
    public TagCloudIndexWorker(IServerApplicationHost applicationHost, TagCloudIndex index, ILogger logger)
        : base(applicationHost, logger)
    {
        _index = index;
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        // A failed build throws; the kit's base logs it and runs this again after a pause, so the first build is retried.
        await _index.BuildAsync(stoppingToken).ConfigureAwait(false);
        while (true)
        {
            await _index.WaitForRebuildRequestAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                await _index.BuildAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The last snapshot stays; the next ask (or the daily maintenance) tries again.
                Logger.LogError(ex, "Rebuilding the tag cloud's index failed");
            }
        }
    }
}
