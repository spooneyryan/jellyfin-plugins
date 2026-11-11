using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using JellyfinChannel = MediaBrowser.Controller.Channels.Channel;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// Debounced library-change handling: Jellyfin's item events, filtered per consumer in the
/// handlers, batched with a trailing debounce, never dispatched by the timer while a library scan runs, flushed as a full
/// pass by the plugin's post-scan task inside every scan, and what Jellyfin's own post-scan tasks change after that sent as
/// soon as the scan has ended.
/// </summary>
/// <remarks>
/// <para><b>Subscriptions</b>, made once the startup gate is open (a <see cref="KitBackgroundService"/>) and only when the
/// plugin has consumers: <c>ILibraryManager.ItemAdded</c>, <c>ItemUpdated</c> and <c>ItemRemoved</c>
/// (<c>v10.11.5:MediaBrowser.Controller/Library/ILibraryManager.cs:35-45</c>), and <c>IUserDataManager.UserDataSaved</c>
/// when a consumer opts in. The handlers apply each consumer's <see cref="LibraryChangeFilter"/>, capture what is needed
/// while the item still exists (the removed item's <see cref="IdentitySnapshot"/>, the top parent), add one small
/// <see cref="ItemChange"/> to each accepting consumer's <see cref="LibraryChangeQueue"/> and wake the timer loop. They
/// never throw: <c>UserDataSaved</c> is raised without a try/catch inside the request that saves the data.</para>
/// <para><b>The timer path</b> dispatches a consumer's batch when its quiet period or maximum wait is over, but not while
/// <c>ILibraryManager.IsScanRunning</c> is true: it keeps collecting and looks again every
/// <see cref="ScanRecheckInterval"/>, counting one deferral per batch.</para>
/// <para><b>The post-scan flush bypasses the scan guard.</b> Jellyfin runs the post-scan tasks inside the scan
/// (<c>IsScanRunning</c> is set at <c>LibraryManager.cs:1133</c> and cleared in a <c>finally</c> after them), so the
/// flush can't go through the guarded timer path, or the full pass would only be collected and later go out as an
/// ordinary batch. <see cref="FlushAsync"/> takes every consumer's waiting changes at once and dispatches them
/// synchronously, on the post-scan thread, with <see cref="LibraryChangeBatch.FullPass"/> and
/// <see cref="LibraryChangeBatch.DuringPostScan"/> true, and returns when every consumer is done (the scan's progress bar
/// covers it). An empty full pass is dispatched too: the end of a scan's validation is news in itself.</para>
/// <para><b>After the full pass.</b> Jellyfin 10.11.5 runs the plugins' post-scan tasks first
/// (<c>ApplicationHost.cs:822-826</c>, <c>LibraryManager.cs:263, 1220-1265</c>), so its own (artists, genres, music genres,
/// studios, collections, the clean-up of items whose parent is gone, the splash screen) and <c>UpdateInheritedValues()</c>
/// run after the full pass, still inside the scan. A change raised from a full pass inside a scan until that scan ends is
/// marked (<see cref="LibraryChangeQueue.HasAfterFullPass"/>); the timer path sends such changes as soon as it sees the scan
/// ended, looking every <see cref="ScanEndRecheckInterval"/>, instead of waiting for the quiet period, as a batch with
/// <see cref="LibraryChangeBatch.AfterFullPass"/>. So every change of a scan reaches a consumer in its full pass or in the
/// batch right after the scan. If another scan starts first, its full pass takes them.</para>
/// <para><b>Dispatch</b> is one batch at a time for the whole plugin (the timer path and the flush share a lock), to the
/// consumers one after another, each in a try/catch. Events that arrive during a dispatch go into the next batch.</para>
/// <para><b>Lost events</b>: the queues live in memory, so changes waiting at shutdown, events raised before the gate
/// opened and a batch a consumer failed are lost; the startup verify pass and the daily sweep close the gap. Real-time
/// monitor changes raise the same events without a post-scan run; the timer path covers them.</para>
/// </remarks>
internal sealed class LibraryChangeMonitor : KitBackgroundService
{
    /// <summary>The logger component (<c>Jellyfin.Plugin.&lt;X&gt;.Kit.LibraryChanges</c>).</summary>
    public const string LoggerComponent = "LibraryChanges";

    /// <summary>How often a due batch looks again whether the library scan it waits for has ended.</summary>
    public static readonly TimeSpan ScanRecheckInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How often the monitor looks whether a scan whose full pass went out has ended, to send the changes raised after the
    /// full pass (Jellyfin's own post-scan tasks) at once.
    /// </summary>
    public static readonly TimeSpan ScanEndRecheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often a consumer that couldn't be created is tried again, while one is missing.
    /// </summary>
    public static readonly TimeSpan ConsumerRetryInterval = TimeSpan.FromMinutes(1);

    private const int MaxStackConsumers = 16;
    private const int MaxParentDepth = 16;

    private readonly ILibraryManager _library;
    private readonly IUserDataManager? _userData;
    private readonly Func<IEnumerable<ILibraryChangeConsumer>> _consumers;
    private readonly Func<bool> _someConsumersUnavailable;
    private readonly object _queuesLock = new();
    private readonly SemaphoreSlim _dispatch = new(1, 1);
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly object _subscriptionLock = new();
    private volatile IReadOnlyList<LibraryChangeQueue>? _queues;
    private DateTimeOffset _nextConsumerRetry;
    private volatile bool _running;
    private bool _subscribed;
    private bool _userDataSubscribed;
    private DateTimeOffset? _subscribedUtc;
    private int _handlerErrors;
    private long _postScanFlushes;
    private long _scanEndSeenAfter;
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryChangeMonitor"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="library">The library manager whose events are watched.</param>
    /// <param name="userData">The user data manager, for consumers that opt in to user data.</param>
    /// <param name="consumers">Resolves the plugin's consumers when they are first needed (and again while
    /// <paramref name="someConsumersUnavailable"/> says one is missing); an exception leaves the monitor without consumers
    /// for that attempt. <c>AddKitLibraryChanges</c> passes the consumers of <c>AddKitLibraryConsumer</c>, each created on its
    /// own (<see cref="KitComponent{T}"/>), so one that can't be created costs only itself.</param>
    /// <param name="options">The plugin's kit options, for the logger.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="timeProvider">The clock of the debounce and the gate; the system clock by default.</param>
    /// <param name="someConsumersUnavailable">Whether a registered consumer couldn't be created: it is tried again every
    /// <see cref="ConsumerRetryInterval"/> and gets the changes that come after; none by default.</param>
    public LibraryChangeMonitor(
        IServerApplicationHost applicationHost,
        ILibraryManager library,
        IUserDataManager? userData,
        Func<IEnumerable<ILibraryChangeConsumer>> consumers,
        KitOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null,
        Func<bool>? someConsumersUnavailable = null)
        : base(applicationHost, CreateLogger(options, loggerFactory), timeProvider)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(consumers);
        _library = library;
        _userData = userData;
        _consumers = consumers;
        _someConsumersUnavailable = someConsumersUnavailable ?? (static () => false);
    }

    /// <summary>
    /// Gets the consumers' queues, created on first use; a consumer created later, after it couldn't be at first, is
    /// added then (<see cref="TryAddUnavailableConsumers"/>).
    /// </summary>
    public IReadOnlyList<LibraryChangeQueue> Queues => _queues ?? LoadQueues();

    /// <summary>
    /// Gets a value indicating whether the library events are subscribed.
    /// </summary>
    public bool IsSubscribed
    {
        get
        {
            lock (_subscriptionLock)
            {
                return _subscribed;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a full pass went out inside a scan that the monitor hasn't yet seen end: a change
    /// raised meanwhile while <c>IsScanRunning</c> is true comes after the full pass.
    /// </summary>
    public bool IsAfterFullPass => Interlocked.Read(ref _postScanFlushes) != Interlocked.Read(ref _scanEndSeenAfter);

    /// <inheritdoc />
    protected override string ServiceName => "The library change monitor";

    /// <summary>
    /// Takes every consumer's waiting changes and dispatches them now, one consumer after another, without the scan guard.
    /// The plugin's post-scan task calls it with <paramref name="fullPass"/> and
    /// <paramref name="duringPostScan"/> true; an empty full pass is dispatched too. It waits for a dispatch in progress.
    /// With <paramref name="duringPostScan"/>, the changes raised from then until the scan ends are marked as after the full
    /// pass, and the timer path sends them as soon as the scan has ended.
    /// </summary>
    /// <param name="fullPass">Whether the batches are the post-scan task's full pass; without it, consumers with nothing waiting are skipped.</param>
    /// <param name="duringPostScan">Whether it runs inside the scan (the post-scan task), while <c>IsScanRunning</c> is true.</param>
    /// <param name="progress">Where progress goes (0 to 100), for the scan's progress bar.</param>
    /// <param name="cancellationToken">The scan's token; a cancelled scan cancels the flush.</param>
    /// <returns>A task that completes when every consumer has handled its batch.</returns>
    public async Task FlushAsync(bool fullPass, bool duringPostScan, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        TryAddUnavailableConsumers();
        var queues = Queues;
        if (_disposed || queues.Count == 0)
        {
            progress?.Report(100);
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Stopping);
        await _dispatch.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (duringPostScan)
            {
                // Jellyfin's own post-scan tasks run after this one (ApplicationHost.cs:822-826): what they change comes
                // after the full pass. A change taken into a consumer's full pass below loses the mark.
                Interlocked.Increment(ref _postScanFlushes);
            }

            for (var i = 0; i < queues.Count; i++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var queue = queues[i];
                var batch = queue.Take(fullPass, duringPostScan);
                if (fullPass || !batch.IsEmpty)
                {
                    await DispatchAsync(queue, batch, linked.Token).ConfigureAwait(false);
                }

                progress?.Report(100.0 * (i + 1) / queues.Count);
            }
        }
        finally
        {
            _dispatch.Release();
            if (duringPostScan)
            {
                // The timer loop watches for the end of the scan.
                _wake.Writer.TryWrite(true);
            }
        }
    }

    /// <summary>
    /// One round of the timer path: dispatches every consumer whose batch is due, unless a library scan runs. A batch with
    /// changes raised after a scan's full pass is due as soon as that scan has ended.
    /// </summary>
    /// <param name="cancellationToken">Cancelled when the server stops.</param>
    /// <returns>How long until the next batch is due or the scan is looked at again; null when nothing waits.</returns>
    public async Task<TimeSpan?> DispatchDueAsync(CancellationToken cancellationToken)
    {
        while (_wake.Reader.TryRead(out _))
        {
            // A change arriving after this point wakes the next wait.
        }

        TimeSpan? wait = null;
        TryAddUnavailableConsumers();
        if (SomeConsumersUnavailable())
        {
            // Look again for the consumer that couldn't be created when its retry is due.
            lock (_queuesLock)
            {
                wait = Earliest(wait, _nextConsumerRetry - TimeProvider.GetUtcNow());
            }
        }

        var flushes = Interlocked.Read(ref _postScanFlushes);
        if (flushes != Interlocked.Read(ref _scanEndSeenAfter))
        {
            if (IsScanRunning())
            {
                wait = ScanEndRecheckInterval;
            }
            else
            {
                // The scan of that full pass has ended; a full pass since the read above keeps its own mark.
                Interlocked.Exchange(ref _scanEndSeenAfter, flushes);
            }
        }

        foreach (var queue in Queues)
        {
            if (!IsDue(queue, out var retry))
            {
                wait = retry is { } later ? Earliest(wait, later) : wait;
                continue;
            }

            await _dispatch.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // The post-scan flush may have taken the changes while this waited, or a scan may have started.
                if (!IsDue(queue, out retry))
                {
                    wait = retry is { } later ? Earliest(wait, later) : wait;
                    continue;
                }

                await DispatchAsync(queue, queue.Take(fullPass: false, duringPostScan: false), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _dispatch.Release();
            }
        }

        return wait;
    }

    /// <summary>
    /// Subscribes to the library events (and to user data when a consumer opts in). Only once, and only with consumers.
    /// </summary>
    public void Subscribe()
    {
        var queues = Queues;
        lock (_subscriptionLock)
        {
            if (_subscribed && !_disposed && !_userDataSubscribed && _userData is not null && queues.Any(q => q.Filter.IncludeUserData))
            {
                // A consumer added later (TryAddUnavailableConsumers) that opts in to user data.
                _userData.UserDataSaved += OnUserDataSaved;
                _userDataSubscribed = true;
                return;
            }

            if (_subscribed || _disposed || queues.Count == 0)
            {
                return;
            }

            _library.ItemAdded += OnItemAdded;
            _library.ItemUpdated += OnItemUpdated;
            _library.ItemRemoved += OnItemRemoved;
            if (_userData is not null && queues.Any(q => q.Filter.IncludeUserData))
            {
                _userData.UserDataSaved += OnUserDataSaved;
                _userDataSubscribed = true;
            }

            _subscribed = true;
            _subscribedUtc = TimeProvider.GetUtcNow();
        }

        Logger.LogInformation("Watching library changes for {Consumers}", string.Join(", ", queues.Select(q => q.Name)));
    }

    /// <summary>
    /// Removes the subscriptions.
    /// </summary>
    public void Unsubscribe()
    {
        lock (_subscriptionLock)
        {
            if (!_subscribed)
            {
                return;
            }

            _library.ItemAdded -= OnItemAdded;
            _library.ItemUpdated -= OnItemUpdated;
            _library.ItemRemoved -= OnItemRemoved;
            if (_userDataSubscribed && _userData is not null)
            {
                _userData.UserDataSaved -= OnUserDataSaved;
            }

            _subscribed = false;
            _userDataSubscribed = false;
        }
    }

    /// <summary>
    /// Describes the monitor for Diagnostics (<c>LibraryChanges</c>): whether it watches, and each consumer's filter, waiting
    /// changes, counters and latest batches.
    /// </summary>
    /// <returns>A plain dictionary.</returns>
    public IDictionary<string, object?> Describe()
    {
        bool subscribed;
        bool userData;
        DateTimeOffset? since;
        lock (_subscriptionLock)
        {
            subscribed = _subscribed;
            userData = _userDataSubscribed;
            since = _subscribedUtc;
        }

        return new Dictionary<string, object?>
        {
            ["Watching"] = subscribed,
            ["UserData"] = userData,
            ["SinceUtc"] = since?.UtcDateTime,
            ["ScanRunning"] = IsScanRunning(),
            ["AfterFullPass"] = IsAfterFullPass,
            ["HandlerErrors"] = Volatile.Read(ref _handlerErrors),
            ["Failures"] = Failures,
            ["Consumers"] = Queues.Select(q => q.Describe()).ToList(),
        };
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            Unsubscribe();
            _dispatch.Dispose();
        }

        base.Dispose();
    }

    /// <summary>
    /// Handles one library event (the <c>ItemAdded</c>, <c>ItemUpdated</c> and <c>ItemRemoved</c> handlers). Never throws.
    /// </summary>
    /// <param name="kind">Which event.</param>
    /// <param name="e">Its arguments.</param>
    internal void OnItemChanged(LibraryChangeKind kind, ItemChangeEventArgs? e)
    {
        try
        {
            if (e?.Item is not { } item)
            {
                return;
            }

            var queues = Queues;
            var count = queues.Count;
            if (count == 0)
            {
                return;
            }

            var itemKind = KindOf(item);
            var isFolder = kind == LibraryChangeKind.Removed ? LibraryChangeFilter.WasFolder(item, e.Parent) : item.IsFolder;
            Span<bool> toList = count <= MaxStackConsumers ? stackalloc bool[count] : new bool[count];
            Span<bool> toFolders = count <= MaxStackConsumers ? stackalloc bool[count] : new bool[count];
            var any = false;
            var identity = false;
            var topParent = false;
            for (var i = 0; i < count; i++)
            {
                var filter = queues[i].Filter;
                try
                {
                    toList[i] = filter.Accepts(kind, item, itemKind);
                    toFolders[i] = kind == LibraryChangeKind.Removed && filter.AcceptsRemovedFolder(item, isFolder);
                }
                catch (Exception ex)
                {
                    // One consumer's broken filter costs only that consumer the event.
                    toList[i] = false;
                    toFolders[i] = false;
                    NoteHandlerError(ex, kind);
                }

                if (toList[i] || toFolders[i])
                {
                    any = true;
                    identity |= kind == LibraryChangeKind.Removed && filter.CaptureIdentity;
                    topParent |= filter.CaptureTopParent;
                }
            }

            if (!any)
            {
                return;
            }

            var afterFullPass = IsRaisedAfterFullPass();
            var change = new ItemChange
            {
                Kind = kind,
                ItemId = item.Id,
                ItemKind = itemKind,
                IsFolder = isFolder,
                ParentId = e.Parent is { } parent && !parent.Id.Equals(Guid.Empty) ? parent.Id : null,
                TopParentId = topParent ? TryFindTopParent(item, e.Parent, kind) : null,

                // Inside the handler: the item and its descendants are gone afterwards. A container that went with
                // it leaves its fields empty.
                Identity = identity ? TryCaptureIdentity(item, kind) : null,
                UpdateReason = kind == LibraryChangeKind.Updated ? e.UpdateReason : default,
                Utc = TimeProvider.GetUtcNow(),
            };
            for (var i = 0; i < count; i++)
            {
                queues[i].Offer(change, toList[i], toFolders[i], afterFullPass);
            }

            _wake.Writer.TryWrite(true);
        }
        catch (Exception ex)
        {
            NoteHandlerError(ex, kind);
        }
    }

    /// <summary>
    /// Handles <c>IUserDataManager.UserDataSaved</c>. Never throws: Jellyfin raises it inside the request that saves.
    /// </summary>
    /// <param name="e">The event's arguments.</param>
    internal void OnUserDataChanged(UserDataSaveEventArgs? e)
    {
        try
        {
            if (e?.Item is not { } item || !LibraryChangeFilter.CountsUserData(e.SaveReason))
            {
                return;
            }

            var queues = Queues;
            var itemKind = KindOf(item);
            ItemChange? change = null;
            var afterFullPass = false;
            foreach (var queue in queues)
            {
                if (!queue.Filter.IncludeUserData || !AcceptsUserData(queue.Filter, item, itemKind))
                {
                    continue;
                }

                if (change is null)
                {
                    afterFullPass = IsRaisedAfterFullPass();
                    change = new ItemChange
                    {
                        Kind = LibraryChangeKind.UserData,
                        ItemId = item.Id,
                        ItemKind = itemKind,
                        IsFolder = item.IsFolder,
                        TopParentId = queues.Any(q => q.Filter.CaptureTopParent) ? TryFindTopParent(item, null, LibraryChangeKind.UserData) : null,
                        UserId = e.UserId,
                        SaveReason = e.SaveReason,
                        Utc = TimeProvider.GetUtcNow(),
                    };
                }

                queue.Offer(change, toList: true, toRemovedFolders: false, afterFullPass);
            }

            if (change is not null)
            {
                _wake.Writer.TryWrite(true);
            }
        }
        catch (Exception ex)
        {
            NoteHandlerError(ex, LibraryChangeKind.UserData);
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        if (Queues.Count == 0 && !SomeConsumersUnavailable())
        {
            return;
        }

        _running = true;
        Subscribe();
        try
        {
            while (true)
            {
                var wait = await DispatchDueAsync(stoppingToken).ConfigureAwait(false);
                await WaitAsync(wait, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _running = false;
            Unsubscribe();
        }
    }

    /// <summary>
    /// Tries again to create the consumers that couldn't be created, at most every <see cref="ConsumerRetryInterval"/>
    /// unless forced: each one created now gets a queue, and the changes from then on (never the ones it missed; the
    /// startup verify pass and the daily sweep close that gap). Called by the timer path and the post-scan flush; it
    /// subscribes the events when the monitor runs.
    /// </summary>
    /// <param name="force">Whether to try now, whenever the last attempt was.</param>
    /// <returns>Whether a consumer was added.</returns>
    internal bool TryAddUnavailableConsumers(bool force = false)
    {
        if (_disposed || !SomeConsumersUnavailable())
        {
            return false;
        }

        var existing = Queues;
        List<string> added;
        lock (_queuesLock)
        {
            var now = TimeProvider.GetUtcNow();
            if (!force && now < _nextConsumerRetry)
            {
                return false;
            }

            _nextConsumerRetry = now + ConsumerRetryInterval;
            existing = _queues ?? existing;
            var all = CreateQueues(existing);
            if (all.Count == existing.Count)
            {
                return false;
            }

            added = all.Skip(existing.Count).Select(q => q.Name).ToList();
            _queues = all;
        }

        Logger.LogInformation("Library changes now go to {Consumers} too, which couldn't be created before", string.Join(", ", added));
        if (_running || IsSubscribed)
        {
            Subscribe();
        }

        _wake.Writer.TryWrite(true);
        return true;
    }

    private static ILogger CreateLogger(KitOptions options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return options.CreateLogger(loggerFactory, LoggerComponent);
    }

    private static TimeSpan Earliest(TimeSpan? current, TimeSpan candidate)
    {
        var positive = candidate < TimeSpan.Zero ? TimeSpan.Zero : candidate;
        return current is { } known && known < positive ? known : positive;
    }

    private static BaseItemKind? KindOf(BaseItem item)
    {
        try
        {
            return item.GetBaseItemKind();
        }
        catch (ArgumentException)
        {
            // A type BaseItemKind doesn't name (a plugin's own item type).
            return null;
        }
    }

    private IReadOnlyList<LibraryChangeQueue> LoadQueues()
    {
        lock (_queuesLock)
        {
            if (_queues is { } loaded)
            {
                return loaded;
            }

            var queues = CreateQueues([]);
            _nextConsumerRetry = TimeProvider.GetUtcNow() + ConsumerRetryInterval;
            _queues = queues;
            return queues;
        }
    }

    // The queues of the consumers the factory gives now, after the existing ones (a consumer already queued keeps its queue).
    private IReadOnlyList<LibraryChangeQueue> CreateQueues(IReadOnlyList<LibraryChangeQueue> existing)
    {
        List<ILibraryChangeConsumer> found;
        try
        {
            found = _consumers().ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "The library change consumers couldn't be created; library changes are not handled");
            return existing;
        }

        var queues = new List<LibraryChangeQueue>(existing);
        foreach (var consumer in found)
        {
            if (consumer is null || existing.Any(q => ReferenceEquals(q.Consumer, consumer)))
            {
                continue;
            }

            try
            {
                queues.Add(new LibraryChangeQueue(consumer));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "The library change consumer {Consumer} has no usable filter and gets no changes", consumer.GetType().Name);
            }
        }

        return queues;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => OnItemChanged(LibraryChangeKind.Added, e);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => OnItemChanged(LibraryChangeKind.Updated, e);

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e) => OnItemChanged(LibraryChangeKind.Removed, e);

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e) => OnUserDataChanged(e);

    private async Task DispatchAsync(LibraryChangeQueue queue, LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        var utc = TimeProvider.GetUtcNow();
        var started = TimeProvider.GetTimestamp();
        var scanRunning = IsScanRunning();
        Exception? error = null;
        try
        {
            await queue.Consumer.HandleAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, or the scan whose full pass this is was cancelled: the batch is lost like any change waiting at shutdown.
            queue.Record(batch, utc, TimeProvider.GetElapsedTime(started), scanRunning, null);
            throw;
        }
        catch (Exception ex)
        {
            error = ex;
            Logger.LogError(
                ex,
                "The library change consumer {Consumer} failed on a batch of {Count} changes (full pass: {FullPass}); the batch is dropped",
                queue.Name,
                batch.Count,
                batch.FullPass);
        }

        var elapsed = TimeProvider.GetElapsedTime(started);
        queue.Record(batch, utc, elapsed, scanRunning, error);
        if (batch.FullPass)
        {
            Logger.LogInformation(
                "Library scan finished: full pass to {Consumer} ({Added} added, {Updated} updated, {Removed} removed, {RemovedFolders} removed folders, {UserData} user data) in {Milliseconds} ms",
                queue.Name,
                batch.Added.Count,
                batch.Updated.Count,
                batch.Removed.Count,
                batch.RemovedFolders.Count,
                batch.UserData.Count,
                Math.Round(elapsed.TotalMilliseconds));
        }
        else if (batch.AfterFullPass)
        {
            Logger.LogDebug(
                "Library scan ended: changes after the full pass to {Consumer} ({Added} added, {Updated} updated, {Removed} removed, {RemovedFolders} removed folders, {UserData} user data) in {Milliseconds} ms",
                queue.Name,
                batch.Added.Count,
                batch.Updated.Count,
                batch.Removed.Count,
                batch.RemovedFolders.Count,
                batch.UserData.Count,
                Math.Round(elapsed.TotalMilliseconds));
        }
        else
        {
            Logger.LogDebug(
                "Library changes to {Consumer}: {Added} added, {Updated} updated, {Removed} removed, {RemovedFolders} removed folders, {UserData} user data in {Milliseconds} ms",
                queue.Name,
                batch.Added.Count,
                batch.Updated.Count,
                batch.Removed.Count,
                batch.RemovedFolders.Count,
                batch.UserData.Count,
                Math.Round(elapsed.TotalMilliseconds));
        }
    }

    private async Task WaitAsync(TimeSpan? wait, CancellationToken cancellationToken)
    {
        if (wait is { } due && due <= TimeSpan.Zero)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = _wake.Reader.WaitToReadAsync(cts.Token).AsTask();
        var timer = wait is { } delay ? Task.Delay(delay, TimeProvider, cts.Token) : Task.Delay(Timeout.Infinite, cts.Token);
        await Task.WhenAny(signal, timer).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private bool SomeConsumersUnavailable()
    {
        try
        {
            return _someConsumersUnavailable();
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Could not tell whether a library change consumer is missing");
            return false;
        }
    }

    private bool AcceptsUserData(LibraryChangeFilter filter, BaseItem item, BaseItemKind? itemKind)
    {
        try
        {
            return filter.Accepts(LibraryChangeKind.UserData, item, itemKind);
        }
        catch (Exception ex)
        {
            NoteHandlerError(ex, LibraryChangeKind.UserData);
            return false;
        }
    }

    // Whether the queue's batch goes out now; otherwise when to look again (null: nothing waits). A batch is due after its
    // quiet period or maximum wait, and one with changes raised after a scan's full pass as soon as that scan has ended;
    // none while a scan runs (a due batch waiting for it counts one deferral).
    private bool IsDue(LibraryChangeQueue queue, out TimeSpan? retry)
    {
        retry = null;
        if (queue.DueAt() is not { } due)
        {
            return false;
        }

        var afterFullPass = queue.HasAfterFullPass;
        var now = TimeProvider.GetUtcNow();
        if (!afterFullPass && due > now)
        {
            retry = due - now;
            return false;
        }

        if (IsScanRunning())
        {
            if (due <= now)
            {
                NoteDeferral(queue);
            }

            retry = afterFullPass ? ScanEndRecheckInterval : ScanRecheckInterval;
            return false;
        }

        return true;
    }

    // In a handler: whether the event comes after a full pass of the scan that is still running (Jellyfin's own post-scan
    // tasks). Two reads, and IsScanRunning only between a full pass and the monitor seeing its scan end.
    private bool IsRaisedAfterFullPass() => IsAfterFullPass && IsScanRunning();

    private bool IsScanRunning()
    {
        try
        {
            return _library.IsScanRunning;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void NoteDeferral(LibraryChangeQueue queue)
    {
        if (queue.NoteDeferral())
        {
            Logger.LogDebug("Library changes for {Consumer} wait for the library scan to end", queue.Name);
        }
    }

    // Captures what it can: a failure costs the change its identity, never the change itself.
    private IdentitySnapshot? TryCaptureIdentity(BaseItem item, LibraryChangeKind kind)
    {
        try
        {
            return ItemIdentity.Capture(item, _library);
        }
        catch (Exception ex)
        {
            NoteHandlerError(ex, kind);
            return null;
        }
    }

    private Guid? TryFindTopParent(BaseItem item, BaseItem? eventParent, LibraryChangeKind kind)
    {
        try
        {
            return FindTopParent(item, eventParent);
        }
        catch (Exception ex)
        {
            NoteHandlerError(ex, kind);
            return null;
        }
    }

    // The item's top parent, as Jellyfin stores it in TopParentId (BaseItem.GetTopParent): the ancestor whose parent is the
    // root folder, which is the folder of a library path (a library's PhysicalFolderIds, not its own id), or a plugin
    // folder such as the collections folder, walked up through the library manager's cache. A removed item's own parent
    // link is cut (LibraryManager.DeleteItem calls SetParent(null)), so the event's parent is where the walk continues.
    private Guid? FindTopParent(BaseItem item, BaseItem? eventParent)
    {
        var current = item;
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            if (current is BasePluginFolder or JellyfinChannel)
            {
                return current.Id;
            }

            BaseItem? parent;
            if (!current.ParentId.Equals(Guid.Empty))
            {
                parent = _library.GetItemById(current.ParentId);
            }
            else
            {
                parent = ReferenceEquals(current, item) ? eventParent : null;
            }

            if (parent is null)
            {
                return null;
            }

            if (parent is AggregateFolder)
            {
                return current.Id;
            }

            current = parent;
        }

        return null;
    }

    private void NoteHandlerError(Exception ex, LibraryChangeKind kind)
    {
        if (Interlocked.Increment(ref _handlerErrors) == 1)
        {
            Logger.LogWarning(ex, "A library event ({Kind}) couldn't be recorded; later ones that fail are logged at Debug", kind);
        }
        else
        {
            Logger.LogDebug(ex, "A library event ({Kind}) couldn't be recorded", kind);
        }
    }
}
