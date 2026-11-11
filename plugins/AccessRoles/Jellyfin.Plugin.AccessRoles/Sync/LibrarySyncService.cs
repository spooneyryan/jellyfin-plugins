using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Configuration;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// Schedules the library sync (its triggers below): a kit background service,
/// so nothing runs before Jellyfin's startup gate and no exception stops the server.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>a run at startup, after the gate (trigger 8);</item>
/// <item>a store change (a role or assignment saved, trigger 1), debounced <see cref="Debounce"/>;</item>
/// <item>a library structure call the filter saw (trigger 5) at once, and again <see cref="FollowUps"/> later (a rename
/// completes about 140 ms after the dashboard's request, as measured on 10.11.5); a new library's <c>ItemAdded</c> (trigger 6),
/// debounced;</item>
/// <item>the backstop every <c>ReconcileIntervalMinutes</c> (trigger 8), from the last run, read again whenever the settings
/// are saved;</item>
/// <item>a settings save that turns on <c>DriftPolicy=Reapply</c>, <c>LibrarySyncEnabled</c> or <c>EnforcementEnabled</c>,
/// debounced <see cref="Debounce"/>;</item>
/// <item>the post-write checks when they are due, and a run <see cref="RetryDelay"/> after a write that didn't happen or a
/// check that found other access.</item>
/// </list>
/// <para>Runs never overlap (<see cref="LibrarySync"/>), and an admin's <c>POST /AccessRoles/Admin/Sync</c> waits its turn.</para>
/// </remarks>
internal sealed class LibrarySyncService : KitBackgroundService
{
    /// <summary>How long a store change or a new library waits for more of the same before the run (2 s).</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>How long after a write that didn't happen the sync tries again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>The runs again after a library structure call, for a rename or an add that completes after its response.</summary>
    public static readonly IReadOnlyList<TimeSpan> FollowUps = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)];

    private readonly LibrarySync _sync;
    private readonly AccessRolesStore _store;
    private readonly PolicyWriter _writer;
    private readonly ILibraryManager? _libraryManager;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly LibrarySyncStatus _status;
    private readonly object _lock = new();
    private readonly List<(DateTime Due, SyncTrigger Trigger)> _requests = [];
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (bool Reapply, bool Sync, bool Enforcement, bool Mirror) _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibrarySyncService"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="sync">The sync runs.</param>
    /// <param name="store">The store, whose changes trigger a run.</param>
    /// <param name="writer">The policy writer, whose post-write checks are scheduled here.</param>
    /// <param name="libraryManager">The library manager, whose <c>ItemAdded</c> of a library triggers a run; <c>null</c> in tests.</param>
    /// <param name="configuration">The settings (the backstop interval).</param>
    /// <param name="status">What Diagnostics shows.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock of the waits; the system clock by default.</param>
    public LibrarySyncService(
        IServerApplicationHost applicationHost,
        LibrarySync sync,
        AccessRolesStore store,
        PolicyWriter writer,
        ILibraryManager? libraryManager,
        IPluginConfigurationProvider configuration,
        LibrarySyncStatus status,
        ILogger logger,
        TimeProvider? timeProvider = null)
        : base(applicationHost, logger, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(status);
        _sync = sync;
        _store = store;
        _writer = writer;
        _libraryManager = libraryManager;
        _configuration = configuration;
        _status = status;
    }

    /// <summary>Gets the runs asked for and not yet started, soonest first (Diagnostics and the tests).</summary>
    internal IReadOnlyList<(DateTime Due, SyncTrigger Trigger)> PendingRequests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests.OrderBy(r => r.Due)];
            }
        }
    }

    /// <summary>
    /// Asks for a run.
    /// </summary>
    /// <param name="trigger">Why.</param>
    /// <param name="delay">How long from now; a store change replaces an earlier store change's run (debounce), as do a retry,
    /// a user update event and a settings save; the sign-in and new-user follow-ups due within a second of one already asked for are one.</param>
    public void Request(SyncTrigger trigger, TimeSpan delay)
    {
        var due = TimeProvider.GetUtcNow().UtcDateTime + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
        lock (_lock)
        {
            if (trigger is SyncTrigger.StoreChanged or SyncTrigger.Retry or SyncTrigger.UserUpdated or SyncTrigger.SettingsChanged)
            {
                _requests.RemoveAll(r => r.Trigger == trigger);
            }
            else if (trigger is SyncTrigger.SignIn or SyncTrigger.UserCreated && _requests.Exists(r => r.Trigger == trigger && (r.Due - due).Duration() < TimeSpan.FromSeconds(1)))
            {
                return;
            }

            _requests.Add((due, trigger));
            _signal.TrySetResult();
        }
    }

    /// <summary>
    /// Tells the sync about a library structure call the filter saw succeed: a removal is recorded as
    /// a deletion, a rename as a pending rename, and a run follows at once and again shortly after.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task that completes when the call is recorded.</returns>
    public async Task LibraryStructureChangedAsync(LibraryStructureCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        switch (call.Kind)
        {
            case LibraryStructureCallKind.Remove when call.LibraryId is { } removed:
                _sync.RecordRemoval(removed);
                break;
            case LibraryStructureCallKind.Rename when call.LibraryId is { } renamed && call.Name is { } oldName && call.NewName is { } newName:
                await _sync.RecordRenameAsync(renamed, oldName, newName, cancellationToken).ConfigureAwait(false);
                break;
        }

        Request(SyncTrigger.LibraryChanged, TimeSpan.Zero);
        foreach (var later in FollowUps)
        {
            Request(SyncTrigger.LibraryChanged, later);
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _store.Changed += OnStoreChanged;
        _store.Degraded.Resolved += OnStoreChanged;
        _writer.VerificationScheduled += OnVerificationScheduled;
        _sync.RetryWanted += OnRetryWanted;
        _sync.RanForEveryone += OnRanForEveryone;
        lock (_lock)
        {
            _settings = Settings(_configuration.GetConfiguration());
        }

        _configuration.Changed += OnConfigurationChanged;
        if (_libraryManager is not null)
        {
            _libraryManager.ItemAdded += OnItemAdded;
        }

        try
        {
            await RunOnceAsync(SyncTrigger.Startup, stoppingToken).ConfigureAwait(false);
            var lastRun = Now();
            while (!stoppingToken.IsCancellationRequested)
            {
                // The backstop from the last run, with the interval as the settings say now: a save wakes the loop
                // (OnConfigurationChanged), so a changed interval applies at once.
                var nextInterval = lastRun + LibrarySync.Interval(_configuration.GetConfiguration());
                Task signal;
                lock (_lock)
                {
                    if (_signal.Task.IsCompleted)
                    {
                        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }

                    signal = _signal.Task;
                }

                var now = Now();
                if (_writer.NextVerificationDue is { } verifyDue && verifyDue <= now)
                {
                    if (await _sync.VerifyDueAsync(stoppingToken).ConfigureAwait(false))
                    {
                        Request(SyncTrigger.Retry, RetryDelay);
                    }

                    continue;
                }

                var trigger = TakeDue(now) ?? (now >= nextInterval ? SyncTrigger.Interval : null);
                if (trigger is { } due)
                {
                    await RunOnceAsync(due, stoppingToken).ConfigureAwait(false);
                    lastRun = Now();
                    continue;
                }

                var wakeAt = new[] { nextInterval, NextRequest() ?? DateTime.MaxValue, _writer.NextVerificationDue ?? DateTime.MaxValue }.Min();
                _status.NextRun(new[] { nextInterval, NextRequest() ?? DateTime.MaxValue }.Min());
                var wait = wakeAt - now;
                using var cancelWait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                await Task.WhenAny(signal, Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, TimeProvider, cancelWait.Token)).ConfigureAwait(false);
                await cancelWait.CancelAsync().ConfigureAwait(false);
                stoppingToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _store.Changed -= OnStoreChanged;
            _store.Degraded.Resolved -= OnStoreChanged;
            _writer.VerificationScheduled -= OnVerificationScheduled;
            _sync.RetryWanted -= OnRetryWanted;
            _sync.RanForEveryone -= OnRanForEveryone;
            _configuration.Changed -= OnConfigurationChanged;
            if (_libraryManager is not null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
            }
        }
    }

    private static (bool Reapply, bool Sync, bool Enforcement, bool Mirror) Settings(PluginConfiguration configuration)
        => (!string.Equals(LibrarySync.DriftPolicy(configuration), LibrarySync.ReportOnlyPolicy, StringComparison.Ordinal), configuration.LibrarySyncEnabled, configuration.EnforcementEnabled, configuration.MirrorPreferenceLockToPolicy);

    private async Task RunOnceAsync(SyncTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            // A write left undone asks for a run again soon through LibrarySync.RetryWanted, also after an admin's sync.
            await _sync.RunAsync(new SyncRequest(trigger, AuditActor.System), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // One failed run never stops the sync: it is logged, shown in Diagnostics, and tried again.
            Logger.LogError(ex, "Access Roles' library sync run ({Trigger}) failed; it runs again in {Seconds} s", trigger, RetryDelay.TotalSeconds);
            _status.Failed(ex.Message, Now());
            Request(SyncTrigger.Retry, RetryDelay);
        }
    }

    private SyncTrigger? TakeDue(DateTime now)
    {
        lock (_lock)
        {
            var due = _requests.Where(r => r.Due <= now).OrderBy(r => r.Due).ToList();
            if (due.Count == 0)
            {
                return null;
            }

            _requests.RemoveAll(r => r.Due <= now);
            return due[0].Trigger;
        }
    }

    private DateTime? NextRequest()
    {
        lock (_lock)
        {
            return _requests.Count == 0 ? null : _requests.Min(r => r.Due);
        }
    }

    // A change of the store, or an admin's end of Degraded mode (the mirror puts back the flags of
    // the users it no longer locks, and the sync paused for it resumes).
    private void OnStoreChanged(object? sender, EventArgs e) => Request(SyncTrigger.StoreChanged, Debounce);

    private void OnRetryWanted(object? sender, EventArgs e) => Request(SyncTrigger.Retry, RetryDelay);

    // A run for everyone that started after a settings save read the saved settings, so the save's own run is done (an
    // admin's Sync right after turning the sync back on runs once, not twice).
    private void OnRanForEveryone(object? sender, DateTime started)
    {
        lock (_lock)
        {
            _requests.RemoveAll(r => r.Trigger == SyncTrigger.SettingsChanged && r.Due - Debounce <= started);
        }
    }

    private void OnVerificationScheduled(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _signal.TrySetResult();
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        // Every item of a scan passes here: only a library's folder asks for a run.
        if (e?.Item is CollectionFolder)
        {
            Request(SyncTrigger.LibraryChanged, Debounce);
        }
    }

    // A settings save: the loop wakes and reads the interval again; a setting that lets the sync
    // write (more) once turned on gets a run soon, as the Settings tab says it applies at once. The optional mirror
    // (MirrorPreferenceLockToPolicy) gets one either way: on, it turns the flags off; off, it puts
    // them back.
    private void OnConfigurationChanged(object? sender, EventArgs e)
    {
        var now = Settings(_configuration.GetConfiguration());
        bool turnedOn;
        lock (_lock)
        {
            var before = _settings;
            _settings = now;
            turnedOn = (now.Reapply && !before.Reapply) || (now.Sync && !before.Sync) || (now.Enforcement && !before.Enforcement) || now.Mirror != before.Mirror;
            _signal.TrySetResult();
        }

        if (turnedOn)
        {
            Request(SyncTrigger.SettingsChanged, Debounce);
        }
    }

    private DateTime Now() => TimeProvider.GetUtcNow().UtcDateTime;
}
