using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The verify sweep of attached records: for each distinct attached <c>ItemId</c> of every
/// <see cref="IAttachedRecordOwner"/> it asks the cache-backed <c>GetItemById(Guid)</c>, in batches of
/// <see cref="BatchSize"/> with a yield between them, and detaches the records of the ids the library no longer has, keeping
/// their snapshots for the reattach. An item whose parent chain (up to the library's folders) has an item the library no
/// longer has counts as gone too: 10.11.5 keeps a deleted music artist folder's albums and tracks in its item cache. It skips ids that are a pending relink: the <c>NewId</c> of a Committed move-journal
/// line that no scan has picked up yet, for at most 7 days.
/// </summary>
/// <remarks>
/// <para><b>When it runs.</b> Once at startup after the startup gate (a full pass; events raised while the plugin
/// wasn't listening are lost), after any library-change batch with removed folders (whose descendants were deleted
/// without events, and a library folder whose directory is gone raises no event at all, <c>LibraryManager.cs:409-466,
/// 1187-1189</c>), and in the daily maintenance task. It is the plugin's hosted service for the startup pass.</para>
/// <para><b>Queued.</b> Sweeps never run side by side, and requests join: one made while no sweep waits starts the next
/// sweep, which runs as soon as the current one (if any) has finished; requests made before that sweep has started join
/// it, and each caller gets its result. So a folder removal costs at most one sweep per batch window, and a
/// sweep that started before a removal is never taken for one that ran after it. <see cref="Queue"/> asks without
/// waiting.</para>
/// <para>A read-only owner is left alone, and so is a lookup that throws: nothing is detached on an error.</para>
/// </remarks>
internal sealed class AttachedRecordVerifier : KitBackgroundService
{
    /// <summary>The logger component, <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Verifier</c>.</summary>
    public const string LoggerComponent = "Verifier";

    /// <summary>How many ids are looked up between two yields.</summary>
    public const int BatchSize = 500;

    /// <summary>How many sweeps Diagnostics lists.</summary>
    public const int MaxRecent = 10;

    /// <summary>How far up an item's parents the sweep looks before it takes the item as there (<see cref="LibraryPresence"/>).</summary>
    public const int MaxParentDepth = LibraryPresence.MaxParentDepth;

    private readonly ILibraryManager _library;
    private readonly MoveJournalReader _moves;
    private readonly Func<IEnumerable<IAttachedRecordOwner>> _owners;
    private readonly Func<CancellationToken, Task>? _beforeStartupSweep;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Queue<VerifySweepResult> _recent = new();
    private readonly List<VerifySweepReason> _queuedBeforeStart = [];
    private PendingSweep? _next;
    private bool _started;
    private bool _sweeping;
    private bool _startupSweepDone;
    private long _sweeps;
    private long _failures;

    /// <summary>
    /// Initializes a new instance of the <see cref="AttachedRecordVerifier"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="library">The library manager.</param>
    /// <param name="moves">The move-journal reader, for pending relinks.</param>
    /// <param name="owners">The plugin's owners, asked at every use: <c>AddKitVerifier</c> passes the owners of
    /// <c>AddKitRecordOwner</c>, each created on its own, one that can't be created left out and tried again next time
    /// (<see cref="KitComponent{T}"/>).</param>
    /// <param name="logger">The logger (<c>Jellyfin.Plugin.&lt;X&gt;.Kit.Verifier</c>).</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    /// <param name="beforeStartupSweep">Work to do once after the gate, before the startup sweep (the archives' reconcile,
    /// <see cref="DetachedRecords.ReconcileAsync"/>).</param>
    public AttachedRecordVerifier(
        IServerApplicationHost applicationHost,
        ILibraryManager library,
        MoveJournalReader moves,
        Func<IEnumerable<IAttachedRecordOwner>> owners,
        ILogger logger,
        TimeProvider? timeProvider = null,
        Func<CancellationToken, Task>? beforeStartupSweep = null)
        : base(applicationHost, logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(moves);
        ArgumentNullException.ThrowIfNull(owners);
        _library = library;
        _moves = moves;
        _owners = owners;
        _beforeStartupSweep = beforeStartupSweep;
    }

    /// <summary>
    /// Gets the last sweep's result, or null.
    /// </summary>
    public VerifySweepResult? LastSweep
    {
        get
        {
            lock (_lock)
            {
                return _recent.LastOrDefault();
            }
        }
    }

    /// <inheritdoc />
    protected override string ServiceName => "The verify sweep";

    /// <summary>
    /// Sweeps now, or joins the next sweep if it hasn't started yet (see the remarks), and waits for it.
    /// </summary>
    /// <param name="reason">Why.</param>
    /// <param name="cancellationToken">Stops the waiting, not the sweep, which other callers may be waiting for.</param>
    /// <returns>The sweep's result.</returns>
    public Task<VerifySweepResult> SweepAsync(VerifySweepReason reason, CancellationToken cancellationToken)
    {
        PendingSweep pending;
        bool start;
        lock (_lock)
        {
            start = _next is null;
            _next ??= new PendingSweep();
            if (!_next.Reasons.Contains(reason))
            {
                _next.Reasons.Add(reason);
            }

            pending = _next;
        }

        if (start)
        {
            _ = RunPendingAsync(pending);
        }

        return pending.Completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Asks for a sweep without waiting: joined as <see cref="SweepAsync"/> is; before the startup sweep has begun, the
    /// startup sweep takes the reason. Never throws.
    /// </summary>
    /// <param name="reason">Why.</param>
    public void Queue(VerifySweepReason reason)
    {
        lock (_lock)
        {
            if (!_started)
            {
                if (!_queuedBeforeStart.Contains(reason))
                {
                    _queuedBeforeStart.Add(reason);
                }

                return;
            }
        }

        _ = SweepAsync(reason, CancellationToken.None).ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Describes the sweeps for Diagnostics (<c>Verifier</c>): the owners, whether a sweep runs or waits, the counts and
    /// the last <see cref="MaxRecent"/> results, newest first.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        var owners = Owners().Select(NameOf).ToList();
        lock (_lock)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Owners"] = owners,
                ["StartupSweepDone"] = _startupSweepDone,
                ["Sweeping"] = _sweeping,
                ["Queued"] = _next is not null,
                ["Sweeps"] = _sweeps,
                ["Failures"] = _failures,
                ["BatchSize"] = BatchSize,
                ["Recent"] = _recent.Reverse().Select(r => r.ToDictionary()).ToList()
            };
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _running.Dispose();
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        List<VerifySweepReason> queued;
        lock (_lock)
        {
            _started = true;
            queued = [.. _queuedBeforeStart];
            _queuedBeforeStart.Clear();
        }

        if (_beforeStartupSweep is not null)
        {
            try
            {
                await _beforeStartupSweep(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                Logger.LogError(ex, "The work before the startup verify sweep failed; the sweep runs anyway");
            }
        }

        if (Owners().Count == 0)
        {
            return;
        }

        foreach (var reason in queued)
        {
            _ = SweepAsync(reason, stoppingToken);
        }

        await SweepAsync(VerifySweepReason.Startup, stoppingToken).ConfigureAwait(false);
    }

    private static string NameOf(IAttachedRecordOwner owner)
    {
        try
        {
            return owner.Name;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "?";
        }
    }

    // The owners that can be created now (the kit's registration creates each one on its own and leaves out one that can't
    // be created, KitComponent<T>): asked at every use, so a failed one is tried again.
    private List<IAttachedRecordOwner> Owners() => _owners().Where(o => o is not null).ToList();

    private async Task RunPendingAsync(PendingSweep pending)
    {
        try
        {
            await _running.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            pending.Completion.TrySetException(ex);
            return;
        }

        try
        {
            List<VerifySweepReason> reasons;
            lock (_lock)
            {
                if (ReferenceEquals(_next, pending))
                {
                    _next = null;
                }

                reasons = [.. pending.Reasons];
                _sweeping = true;
            }

            var result = await SweepOnceAsync(reasons).ConfigureAwait(false);
            lock (_lock)
            {
                _sweeps++;
                _startupSweepDone |= reasons.Contains(VerifySweepReason.Startup);
                _recent.Enqueue(result);
                while (_recent.Count > MaxRecent)
                {
                    _recent.Dequeue();
                }
            }

            pending.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            pending.Completion.TrySetCanceled();
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            Logger.LogError(ex, "The verify sweep failed");
            pending.Completion.TrySetException(ex);
        }
        finally
        {
            lock (_lock)
            {
                _sweeping = false;
            }

            _running.Release();
        }
    }

    private async Task<VerifySweepResult> SweepOnceAsync(IReadOnlyList<VerifySweepReason> reasons)
    {
        var cancellationToken = Stopping;
        var clock = Stopwatch.StartNew();
        var started = TimeProvider.GetUtcNow().UtcDateTime;
        var journal = _moves.Read();
        var results = new List<VerifySweepOwnerResult>();
        foreach (var owner in Owners())
        {
            results.Add(await SweepOwnerAsync(owner, journal, started, cancellationToken).ConfigureAwait(false));
        }

        var result = new VerifySweepResult
        {
            Reasons = reasons,
            StartedUtc = started,
            Duration = clock.Elapsed,
            Owners = results,
            JournalStatus = journal.Status
        };

        if (result.Detached > 0)
        {
            Logger.LogInformation(
                "Verify sweep ({Reasons}): {Detached} records detached whose items are gone, {Checked} items checked in {Milliseconds} ms",
                string.Join(", ", reasons),
                result.Detached,
                result.Checked,
                (long)result.Duration.TotalMilliseconds);
        }
        else
        {
            Logger.LogDebug(
                "Verify sweep ({Reasons}): nothing to detach, {Checked} items checked in {Milliseconds} ms",
                string.Join(", ", reasons),
                result.Checked,
                (long)result.Duration.TotalMilliseconds);
        }

        return result;
    }

    private async Task<VerifySweepOwnerResult> SweepOwnerAsync(IAttachedRecordOwner owner, MoveJournal journal, DateTime nowUtc, CancellationToken cancellationToken)
    {
        string name;
        try
        {
            name = owner.Name;
            if (owner.IsReadOnly)
            {
                return new VerifySweepOwnerResult { Owner = name, Skipped = "ReadOnly" };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "The verify sweep left an owner out: it couldn't be read");
            return new VerifySweepOwnerResult { Owner = "?", Skipped = "Failed", Error = ex.Message };
        }

        try
        {
            var ids = owner.GetAttachedItemIds().Where(id => id != Guid.Empty).Distinct().ToList();
            var missing = new List<Guid>();
            var known = new Dictionary<Guid, bool>();
            var pending = 0;
            var errors = 0;
            foreach (var chunk in ids.Chunk(BatchSize))
            {
                foreach (var id in chunk)
                {
                    bool found;
                    try
                    {
                        found = LibraryPresence.IsInLibrary(_library, id, known);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Never detach on an error: the item may well be there.
                        if (errors++ == 0)
                        {
                            Logger.LogWarning(ex, "The verify sweep couldn't look up an item of {Owner}; its records stay attached", name);
                        }

                        continue;
                    }

                    if (found)
                    {
                        continue;
                    }

                    if (journal.IsPendingRelink(id, nowUtc))
                    {
                        pending++;
                    }
                    else
                    {
                        missing.Add(id);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            var detached = missing.Count == 0 ? 0 : await owner.DetachAsync(missing, nowUtc, cancellationToken).ConfigureAwait(false);
            return new VerifySweepOwnerResult
            {
                Owner = name,
                Checked = ids.Count,
                Missing = missing.Count,
                Pending = pending,
                LookupErrors = errors,
                Detached = detached
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "The verify sweep of {Owner} failed", name);
            return new VerifySweepOwnerResult { Owner = name, Skipped = "Failed", Error = ex.Message };
        }
    }

    private sealed class PendingSweep
    {
        public List<VerifySweepReason> Reasons { get; } = [];

        public TaskCompletionSource<VerifySweepResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
