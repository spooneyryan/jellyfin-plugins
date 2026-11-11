using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Integration;

/// <summary>
/// The source of <see cref="ClipRootsV1"/>: while the host runs, after Jellyfin's startup gate, the facade answers the
/// recorded clip folders and the current one from here; when the host stops, it answers nothing again.
/// </summary>
/// <remarks>
/// <para>A hosted service of its own, since the facade answers also while the bookmark data is read-only (it only reads the
/// recorded folders, and a newer version's file still has them), when the identity service and the clip worker stay idle.
/// It writes nothing (Storage <c>BackgroundWriterCoverageTests</c> pins that), so it never asks the background-write
/// gate.</para>
/// <para>After a failed upgrade the store serves an empty stand-in, so the facade answers only the current clip folder then;
/// a store that can't be read at all leaves the recorded folders out the same way, for that call.</para>
/// </remarks>
internal sealed class ClipRootsFacadeService : KitBackgroundService
{
    private readonly IBookmarkStore _store;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly BookmarkPaths _paths;
    private readonly Func<string[]> _source;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipRootsFacadeService"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="store">The bookmark store, for the recorded clip folders.</param>
    /// <param name="configuration">The settings, for the current clip folder.</param>
    /// <param name="paths">The plugin's paths, for the default clip folder.</param>
    /// <param name="logger">The logger.</param>
    public ClipRootsFacadeService(
        IServerApplicationHost applicationHost,
        IBookmarkStore store,
        IPluginConfigurationProvider configuration,
        BookmarkPaths paths,
        ILogger<ClipRootsFacadeService> logger)
        : base(applicationHost, logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(paths);
        _store = store;
        _configuration = configuration;
        _paths = paths;
        _source = CurrentFolders;
    }

    /// <inheritdoc />
    protected override string ServiceName => "The clip folders' facade (ClipRootsV1)";

    /// <summary>
    /// Describes the facade for Diagnostics (<c>ClipRoots</c>): whether it answers, how often other plugins asked, and what it
    /// answers now.
    /// </summary>
    /// <returns>A plain dictionary (paths: admins only).</returns>
    public static IDictionary<string, object?> Describe() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Facade"] = typeof(ClipRootsV1).FullName + "." + nameof(ClipRootsV1.Get),
        ["Answering"] = ClipRootsV1.IsAttached,
        ["Calls"] = ClipRootsV1.Calls,
        ["Folders"] = ClipRootsV1.Answer()
    };

    /// <summary>
    /// The folders the facade answers: the recorded clip folders, then the current one, each once, normalised.
    /// </summary>
    /// <returns>A new array.</returns>
    public string[] CurrentFolders()
    {
        var folders = new List<string>();
        try
        {
            foreach (var root in _store.ClipRoots)
            {
                Add(root);
            }
        }
        catch (Exception ex)
        {
            // The store couldn't be read for this call (locked): the current folder still answers.
            Logger.LogDebug(ex, "The recorded clip folders couldn't be read for ClipRootsV1");
        }

        try
        {
            if (ClipRootPaths.TryGetCurrent(_configuration.GetConfiguration(), _paths, out var current))
            {
                Add(current);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "The current clip folder couldn't be read for ClipRootsV1");
        }

        return folders.ToArray();

        void Add(string folder)
        {
            if (ClipRootPaths.TryNormalize(folder, out var normalized) && !folders.Contains(normalized, ClipRootPaths.Comparer))
            {
                folders.Add(normalized);
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        ClipRootsV1.Detach(_source);
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        ClipRootsV1.Attach(_source);
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            ClipRootsV1.Detach(_source);
        }
    }
}
