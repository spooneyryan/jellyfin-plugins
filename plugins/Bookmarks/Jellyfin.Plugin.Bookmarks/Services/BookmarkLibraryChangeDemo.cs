using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The developer demo of the kit's library-change monitor: a consumer that records the batches it receives, so
/// a sandbox can check the monitor against a real library and real scans (<c>scripts/library-changes-probe.mjs</c>). It
/// writes nothing. While the developer setting <c>EnableLibraryChangeDemo</c> is off (the default) its filter takes no
/// change, the full passes it still receives are dropped, and its endpoints answer 403 "turned off". Bookmarks' own
/// consumer, the identity service, uses the same monitor.
/// </summary>
/// <remarks>
/// <para>Its filter takes every kind, user data, the removed item's identity and the top parent, with a short quiet period
/// (<see cref="QuietPeriod"/>, <see cref="MaxWait"/>) so a timer batch shows within seconds and one that falls due during a
/// short sandbox scan has to wait for it.</para>
/// <para>What it records is ids, kinds, flags and counts, and whether <c>IsScanRunning</c> was true when the batch arrived:
/// no names and no paths (a removed item's identity only says whether it had them), so the admin endpoint shows nothing
/// of an item an admin whose libraries are restricted couldn't see otherwise.</para>
/// </remarks>
internal sealed class BookmarkLibraryChangeDemo : ILibraryChangeConsumer, IBookmarkLibraryChangeDemo
{
    /// <summary>The consumer's name in logs and Diagnostics.</summary>
    public const string ConsumerName = "LibraryChangeDemo";

    /// <summary>How many batches are kept.</summary>
    public const int MaxBatches = 50;

    /// <summary>How many changes of each list a recorded batch lists (the counts are complete).</summary>
    public const int MaxChangesPerList = 100;

    /// <summary>The demo's quiet period.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(500);

    /// <summary>The demo's maximum wait.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    private readonly IPluginConfigurationProvider _configuration;
    private readonly ILibraryManager _library;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private readonly Queue<IDictionary<string, object?>> _batches = new();
    private long _received;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkLibraryChangeDemo"/> class.
    /// </summary>
    /// <param name="configuration">The plugin settings.</param>
    /// <param name="library">The library manager, for <c>IsScanRunning</c>.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public BookmarkLibraryChangeDemo(IPluginConfigurationProvider configuration, ILibraryManager library, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(library);
        _configuration = configuration;
        _library = library;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Filter = new LibraryChangeFilter
        {
            IncludeUserData = true,
            CaptureIdentity = true,
            CaptureTopParent = true,
            QuietPeriod = QuietPeriod,
            MaxWait = MaxWait,

            // Read in the handler: nothing is collected while the demo is off.
            Ignore = (_, _) => !IsEnabled,
        };
    }

    /// <inheritdoc />
    public string Name => ConsumerName;

    /// <inheritdoc />
    public LibraryChangeFilter Filter { get; }

    private bool IsEnabled => _configuration.GetConfiguration().EnableLibraryChangeDemo;

    /// <inheritdoc />
    public Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!IsEnabled)
        {
            return Task.CompletedTask;
        }

        var scanRunning = _library.IsScanRunning;
        lock (_lock)
        {
            _received++;
            _batches.Enqueue(new Dictionary<string, object?>
            {
                ["Seq"] = _received,
                ["ReceivedUtc"] = _timeProvider.GetUtcNow().UtcDateTime,
                ["FullPass"] = batch.FullPass,
                ["DuringPostScan"] = batch.DuringPostScan,
                ["AfterFullPass"] = batch.AfterFullPass,
                ["ScanRunning"] = scanRunning,
                ["FirstUtc"] = batch.FirstUtc?.UtcDateTime,
                ["LastUtc"] = batch.LastUtc?.UtcDateTime,
                ["Kinds"] = batch.Kinds.Select(k => k.ToString()).Order(StringComparer.Ordinal).ToList(),
                ["Counts"] = new Dictionary<string, object?>
                {
                    ["Added"] = batch.Added.Count,
                    ["Updated"] = batch.Updated.Count,
                    ["Removed"] = batch.Removed.Count,
                    ["RemovedFolders"] = batch.RemovedFolders.Count,
                    ["UserData"] = batch.UserData.Count,
                },
                ["Added"] = List(batch.Added),
                ["Updated"] = List(batch.Updated),
                ["Removed"] = List(batch.Removed),
                ["RemovedFolders"] = List(batch.RemovedFolders),
                ["UserData"] = List(batch.UserData),
            });
            while (_batches.Count > MaxBatches)
            {
                _batches.Dequeue();
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IDictionary<string, object?> Describe()
    {
        EnsureEnabled();
        lock (_lock)
        {
            return new Dictionary<string, object?>
            {
                ["Received"] = _received,
                ["Batches"] = _batches.ToList(),
            };
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        EnsureEnabled();
        lock (_lock)
        {
            _batches.Clear();
        }
    }

    private static List<Dictionary<string, object?>> List(IReadOnlyList<ItemChange> changes)
        => changes.Take(MaxChangesPerList).Select(Describe).ToList();

    private static Dictionary<string, object?> Describe(ItemChange change)
    {
        var entry = new Dictionary<string, object?>
        {
            ["ItemId"] = change.ItemId,
            ["Kind"] = change.ItemKind?.ToString(),
            ["IsFolder"] = change.IsFolder,
            ["ParentId"] = change.ParentId,
            ["TopParentId"] = change.TopParentId,
        };
        switch (change.Kind)
        {
            case LibraryChangeKind.Updated or LibraryChangeKind.Added when change.UpdateReason != default:
                entry["UpdateReason"] = change.UpdateReason.ToString();
                break;
            case LibraryChangeKind.Removed when change.Identity is { } identity:
                // Captured in the handler, when the item was already gone from the library: no name or path here.
                entry["Identity"] = new Dictionary<string, object?>
                {
                    ["ItemId"] = identity.ItemId,
                    ["Kind"] = identity.Kind,
                    ["HasName"] = !string.IsNullOrEmpty(identity.Name),
                    ["HasPath"] = !string.IsNullOrEmpty(identity.Path),
                    ["Year"] = identity.Year,
                    ["ProviderIds"] = identity.ProviderIds.Count,
                    ["SeriesId"] = identity.SeriesId,
                    ["SeasonId"] = identity.SeasonId,
                    ["Season"] = identity.Season,
                    ["Episode"] = identity.Episode,
                    ["RunTimeTicks"] = identity.RunTimeTicks,
                };
                break;
            case LibraryChangeKind.UserData:
                entry["UserId"] = change.UserId;
                entry["Reason"] = change.SaveReason?.ToString();
                break;
        }

        return entry;
    }

    private void EnsureEnabled()
    {
        if (!IsEnabled)
        {
            throw PluginException.FeatureDisabled("The library change demo is turned off (developer setting EnableLibraryChangeDemo).");
        }
    }
}
