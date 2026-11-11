using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The one place auto-collections are run from: requests and passes queue here and run one at a time.
/// </summary>
/// <remarks>
/// <para><b>Coalescing</b>: a definition has at most one waiting run; a second request joins it (the stronger kind and
/// the more urgent reason win), and a waiting pass takes the definition's waiting run with it when it reaches the
/// definition. A second pass request joins the waiting pass.</para>
/// <para><b>Order</b>: creates first (someone waits for them), then a pass that may start, then the other runs in the
/// order they came.</para>
/// <para><b>Cooldown</b>: a run asked for by library changes starts at least <c>DefinitionCooldown</c> after the
/// definition's previous run; every other reason starts at once.</para>
/// <para><b>Scans</b>: a pass after a scan or at startup waits while a library scan runs (looked at every
/// <see cref="ScanPollInterval"/>); the daily pass waits up to <c>DailyPassScanWait</c>, then runs anyway. Single runs
/// and a pass asked for by hand don't wait. Each run knows whether a scan was running when it (or its pass) started.</para>
/// <para><b>Pass</b>: the definitions share one <see cref="PassItemCache"/> and one time, which relative dates are
/// measured from. Each definition runs in its own try/catch: a failure sets its status to <c>Error</c> and the pass goes
/// on.</para>
/// <para><b>Library changes</b>: the runs a batch of changes asks for share one <see cref="PassItemCache"/> while they run
/// one after another, as a pass's definitions do; it is dropped when the worker has nothing to do or a newer batch asks
/// for runs (its items may have changed).</para>
/// <para><b>State</b>: <c>Queued</c> and <c>Running</c> are kept in memory (<see cref="ActivityOf"/>), never stored; a run
/// stores its final state once, when it ends.</para>
/// </remarks>
internal sealed class AutoCollectionWorker : KitBackgroundService, IAutoCollectionQueue
{
    /// <summary>How often a waiting pass looks whether the library scan has ended.</summary>
    public static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(2);

    private readonly DefinitionRunner _runner;
    private readonly AutoCollectionStore _store;
    private readonly AutoCollectionStateStore _state;
    private readonly Func<AutoCollectionSettings> _settings;
    private readonly Func<bool> _scanRunning;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, PendingRun> _runs = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastRun = [];
    private readonly HashSet<Guid> _passQueued = [];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private PendingPass? _pass;
    private AutoCollectionRunConditions? _changeRuns;
    private Guid? _running;
    private long _sequence;
    private int _waits;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionWorker"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="runner">Runs one definition.</param>
    /// <param name="store">The definitions.</param>
    /// <param name="state">The run state.</param>
    /// <param name="settings">The settings, read on each use.</param>
    /// <param name="scanRunning">Whether a library scan is running.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public AutoCollectionWorker(
        IServerApplicationHost applicationHost,
        DefinitionRunner runner,
        AutoCollectionStore store,
        AutoCollectionStateStore state,
        Func<AutoCollectionSettings> settings,
        Func<bool> scanRunning,
        TimeProvider timeProvider,
        ILogger logger)
        : base(applicationHost, logger, timeProvider)
    {
        _runner = runner;
        _store = store;
        _state = state;
        _settings = settings;
        _scanRunning = scanRunning;
    }

    /// <summary>Gets the number of passes that ran.</summary>
    public int PassesRun { get; private set; }

    /// <summary>Gets the number of times the worker went to sleep for want of work.</summary>
    public int Waits => Volatile.Read(ref _waits);

    /// <summary>Gets the cache of the last pass, or batch of runs for library changes, that ran; or <c>null</c>.</summary>
    public PassItemCache? LastPassCache { get; private set; }

    /// <inheritdoc />
    public Task<AutoCollectionState> RequestAsync(AutoCollectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Task<AutoCollectionState> task;
        lock (_lock)
        {
            var now = TimeProvider.GetUtcNow();
            var notBefore = now;
            if (request.Reason == SmartSearchAutoCollectionRunReason.Events && _lastRun.TryGetValue(request.DefinitionId, out var last))
            {
                var due = last + _settings().DefinitionCooldown;
                notBefore = due > now ? due : now;
            }

            if (_runs.TryGetValue(request.DefinitionId, out var waiting))
            {
                waiting.Kind = (AutoCollectionRequestKind)Math.Max((int)waiting.Kind, (int)request.Kind);
                if (request.Reason == SmartSearchAutoCollectionRunReason.Events)
                {
                    _changeRuns = null;
                }

                if (waiting.Reason == SmartSearchAutoCollectionRunReason.Events && request.Reason != SmartSearchAutoCollectionRunReason.Events)
                {
                    waiting.Reason = request.Reason;
                }

                waiting.NotBefore = notBefore < waiting.NotBefore ? notBefore : waiting.NotBefore;
                task = waiting.Done.Task;
            }
            else
            {
                if (request.Reason == SmartSearchAutoCollectionRunReason.Events)
                {
                    _changeRuns = null;
                }

                var run = new PendingRun(request.DefinitionId, request.Kind, request.Reason, notBefore, _sequence++);
                _runs[request.DefinitionId] = run;
                task = run.Done.Task;
            }
        }

        Wake();
        return task;
    }

    /// <inheritdoc />
    public Task RequestPassAsync(SmartSearchAutoCollectionRunReason reason, IProgress<double>? progress = null)
    {
        Task task;
        lock (_lock)
        {
            var now = TimeProvider.GetUtcNow();
            DateTimeOffset? waitUntil = reason switch
            {
                SmartSearchAutoCollectionRunReason.PostScan or SmartSearchAutoCollectionRunReason.Startup => DateTimeOffset.MaxValue,
                SmartSearchAutoCollectionRunReason.Daily => now + _settings().DailyPassScanWait,
                _ => null
            };

            var pass = _pass;
            if (pass is { } waiting)
            {
                // The waiting pass serves this request too; one that may start now makes it start now. The startup pass's
                // own signal to the guard stays whatever the pass is then called.
                if (waitUntil is null || (waiting.WaitUntil is { } until && waitUntil < until))
                {
                    waiting.WaitUntil = waitUntil;
                    waiting.Reason = reason;
                }

                waiting.Startup |= reason == SmartSearchAutoCollectionRunReason.Startup;
            }
            else
            {
                pass = _pass = new PendingPass(reason, waitUntil);
                if (_settings().Enabled)
                {
                    foreach (var definition in _store.Current.Definitions.Where(d => d is not null && d.Enabled))
                    {
                        pass.Queued.Add(definition.Id);
                        _passQueued.Add(definition.Id);
                    }
                }
            }

            if (progress is not null)
            {
                pass.Progress.Add(progress);
            }

            task = pass.Done.Task;
        }

        Wake();
        return task;
    }

    /// <inheritdoc />
    public AutoCollectionActivity? ActivityOf(Guid definitionId)
    {
        lock (_lock)
        {
            if (_running is { } running && running.Equals(definitionId))
            {
                return AutoCollectionActivity.Running;
            }

            if (_runs.ContainsKey(definitionId))
            {
                return AutoCollectionActivity.Queued;
            }

            if (!_passQueued.Contains(definitionId))
            {
                return null;
            }

            // Only a pass has it: one that waits while a scan runs says so.
            return _pass is { WaitUntil: not null } && _scanRunning() ? AutoCollectionActivity.WaitingForScan : AutoCollectionActivity.Queued;
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                while (_wake.Reader.TryRead(out _))
                {
                    // Every request is looked at below.
                }

                var (run, pass, wait) = Next();
                if (run is not null)
                {
                    await RunOneAsync(run, stoppingToken).ConfigureAwait(false);
                }
                else if (pass is not null)
                {
                    await RunPassAsync(pass, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    lock (_lock)
                    {
                        _changeRuns = null;
                    }

                    await WaitAsync(wait, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            CancelWaiting();
            throw;
        }
    }

    // The next work: a create, else a pass that may start, else the oldest run that may start; else how long to wait.
    private (PendingRun? Run, PendingPass? Pass, TimeSpan Wait) Next()
    {
        lock (_lock)
        {
            var now = TimeProvider.GetUtcNow();
            var ready = _runs.Values.Where(r => r.NotBefore <= now).OrderBy(r => r.Kind == AutoCollectionRequestKind.Run ? 1 : 0).ThenBy(r => r.Sequence).FirstOrDefault();
            if (ready is not null && ready.Kind != AutoCollectionRequestKind.Run)
            {
                return (Take(ready, now), null, default);
            }

            var scan = _scanRunning();
            if (_pass is { } pass && !(scan && pass.WaitUntil is { } until && now < until))
            {
                _pass = null;
                pass.ScanRunning = scan;
                return (null, pass, default);
            }

            if (ready is not null)
            {
                return (Take(ready, now), null, default);
            }

            var wait = Timeout.InfiniteTimeSpan;
            if (_runs.Count > 0)
            {
                wait = _runs.Values.Min(r => r.NotBefore) - now;
            }

            if (_pass is { } held)
            {
                var poll = held.WaitUntil is { } heldUntil && heldUntil - now < ScanPollInterval ? heldUntil - now : ScanPollInterval;
                wait = wait == Timeout.InfiniteTimeSpan || poll < wait ? poll : wait;
            }

            // Nothing to do: wait until woken (an infinite wait is -1 ms, so it is never clamped to zero).
            return (null, null, wait != Timeout.InfiniteTimeSpan && wait < TimeSpan.Zero ? TimeSpan.Zero : wait);
        }
    }

    private PendingRun Take(PendingRun run, DateTimeOffset now)
    {
        _runs.Remove(run.DefinitionId);
        run.ScanRunning = _scanRunning();
        _running = run.DefinitionId;
        _lastRun[run.DefinitionId] = now;
        return run;
    }

    private Task RunOneAsync(PendingRun run, CancellationToken stoppingToken)
    {
        var conditions = new AutoCollectionRunConditions(ScanRunning: run.ScanRunning);
        if (run.Reason == SmartSearchAutoCollectionRunReason.Events && run.Kind == AutoCollectionRequestKind.Run)
        {
            lock (_lock)
            {
                _changeRuns ??= new AutoCollectionRunConditions(new PassItemCache(_settings().PassCacheMaxItems), Audience: new AudienceMemo());
                LastPassCache = _changeRuns.ItemCache;
                conditions = _changeRuns with { ScanRunning = run.ScanRunning };
            }
        }

        return SettleAsync(
            run.DefinitionId,
            run.Reason,
            () => run.Kind switch
            {
                AutoCollectionRequestKind.CreateNew => _runner.CreateAsync(run.DefinitionId, run.Reason, stoppingToken, removeIfRefused: true),
                AutoCollectionRequestKind.Create => _runner.CreateAsync(run.DefinitionId, run.Reason, stoppingToken),
                _ => _runner.RunAsync(run.DefinitionId, run.Reason, stoppingToken, conditions)
            },
            run,
            stoppingToken);
    }

    // Runs one definition, then answers the request waiting for it, once the definition no longer shows Running. A refusal
    // (no definition any more, a name taken meanwhile) is only the request's answer; any other failure is also the
    // definition's state.
    private async Task SettleAsync(Guid definitionId, SmartSearchAutoCollectionRunReason reason, Func<Task<AutoCollectionState>> run, PendingRun? request, CancellationToken stoppingToken)
    {
        AutoCollectionState? state = null;
        Exception? failure = null;
        try
        {
            state = await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            Finished(definitionId);
            request?.Done.TrySetCanceled(stoppingToken);
            throw;
        }
        catch (PluginException ex)
        {
            failure = ex;
        }
        catch (Exception ex)
        {
            await FailedAsync(definitionId, reason, ex).ConfigureAwait(false);
            failure = ex;
        }

        Finished(definitionId);
        if (failure is null)
        {
            request?.Done.TrySetResult(state!);
        }
        else
        {
            request?.Done.TrySetException(failure);
        }
    }

    private async Task RunPassAsync(PendingPass pass, CancellationToken stoppingToken)
    {
        var settings = _settings();
        var ids = settings.Enabled ? _store.Current.Definitions.Where(d => d is not null && d.Enabled).Select(d => d.Id).ToList() : [];
        var cache = new PassItemCache(settings.PassCacheMaxItems);
        var conditions = new AutoCollectionRunConditions(cache, TimeProvider.GetUtcNow().UtcDateTime, pass.ScanRunning, new AudienceMemo(), pass.Startup);
        Logger.LogInformation("Auto-collections: a {Reason} pass over {Count} definitions starts.", pass.Reason, ids.Count);
        try
        {
            for (var i = 0; i < ids.Count; i++)
            {
                stoppingToken.ThrowIfCancellationRequested();
                var id = ids[i];
                PendingRun? joined;
                lock (_lock)
                {
                    if (_pass?.Queued.Contains(id) != true)
                    {
                        _passQueued.Remove(id);
                    }

                    joined = _runs.TryGetValue(id, out var waiting) && waiting.Kind == AutoCollectionRequestKind.Run ? waiting : null;
                    if (joined is not null)
                    {
                        _runs.Remove(id);
                    }

                    _running = id;
                    _lastRun[id] = TimeProvider.GetUtcNow();
                }

                await SettleAsync(id, pass.Reason, () => _runner.RunAsync(id, pass.Reason, stoppingToken, conditions), joined, stoppingToken).ConfigureAwait(false);
                foreach (var progress in pass.Progress)
                {
                    progress.Report(100.0 * (i + 1) / ids.Count);
                }
            }

            PassesRun++;
            LastPassCache = cache;
            Logger.LogInformation("Auto-collections: the {Reason} pass ended ({Hits} items shared, {Loads} loaded).", pass.Reason, cache.Hits, cache.Loads);
            pass.Done.TrySetResult();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            pass.Done.TrySetCanceled(stoppingToken);
            throw;
        }
        finally
        {
            lock (_lock)
            {
                foreach (var id in pass.Queued.Where(id => _pass?.Queued.Contains(id) != true))
                {
                    _passQueued.Remove(id);
                }
            }
        }
    }

    private void Finished(Guid definitionId)
    {
        lock (_lock)
        {
            if (_running is { } running && running.Equals(definitionId))
            {
                _running = null;
            }

            _lastRun[definitionId] = TimeProvider.GetUtcNow();
        }
    }

    // An unexpected failure: the definition shows Error (the message for administrators), and the approval and the accepted
    // preview it read are used up as by any run's end; the next run tries again.
    private async Task FailedAsync(Guid definitionId, SmartSearchAutoCollectionRunReason reason, Exception ex)
    {
        Logger.LogError(ex, "Auto-collection {Id} failed to run ({Reason}); the next run tries again.", definitionId, reason);
        if (_store.Find(definitionId) is null)
        {
            return;
        }

        try
        {
            var now = TimeProvider.GetUtcNow().UtcDateTime;
            await _state.UpdateAsync(
                definitionId,
                s => (s ?? new AutoCollectionState()) with
                {
                    Status = SmartSearchAutoCollectionStatus.Error,
                    Reason = AutoCollectionReasons.RunFailed,
                    LastError = AutoCollectionReasons.SafeText(AutoCollectionReasons.RunFailed),
                    LastErrorDetail = ex.Message,
                    LastRunUtc = now,
                    LastReason = reason,
                    PendingGuard = s?.PendingGuard is { } held ? held with { ApprovedUtc = null } : null,
                    AcceptedRemovals = null,
                    AcceptedResultHash = null
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception stateFailure)
        {
            Logger.LogError(stateFailure, "Auto-collection {Id}: its failure couldn't be stored.", definitionId);
        }
    }

    private async Task WaitAsync(TimeSpan wait, CancellationToken stoppingToken)
    {
        Interlocked.Increment(ref _waits);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var woken = _wake.Reader.WaitToReadAsync(stop.Token).AsTask();
        var delay = wait == Timeout.InfiniteTimeSpan ? Task.Delay(Timeout.Infinite, stop.Token) : Task.Delay(wait, TimeProvider, stop.Token);
        await Task.WhenAny(woken, delay).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        stoppingToken.ThrowIfCancellationRequested();
    }

    private void Wake() => _wake.Writer.TryWrite(true);

    private void CancelWaiting()
    {
        lock (_lock)
        {
            foreach (var run in _runs.Values)
            {
                run.Done.TrySetCanceled();
            }

            _runs.Clear();
            _pass?.Done.TrySetCanceled();
            _pass = null;
            _passQueued.Clear();
        }
    }

    private sealed class PendingRun(Guid definitionId, AutoCollectionRequestKind kind, SmartSearchAutoCollectionRunReason reason, DateTimeOffset notBefore, long sequence)
    {
        public Guid DefinitionId { get; } = definitionId;

        public AutoCollectionRequestKind Kind { get; set; } = kind;

        public SmartSearchAutoCollectionRunReason Reason { get; set; } = reason;

        public DateTimeOffset NotBefore { get; set; } = notBefore;

        public long Sequence { get; } = sequence;

        public bool ScanRunning { get; set; }

        public TaskCompletionSource<AutoCollectionState> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class PendingPass(SmartSearchAutoCollectionRunReason reason, DateTimeOffset? waitUntil)
    {
        public SmartSearchAutoCollectionRunReason Reason { get; set; } = reason;

        public bool Startup { get; set; } = reason == SmartSearchAutoCollectionRunReason.Startup;

        public DateTimeOffset? WaitUntil { get; set; } = waitUntil;

        public bool ScanRunning { get; set; }

        public List<IProgress<double>> Progress { get; } = [];

        public HashSet<Guid> Queued { get; } = [];

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
