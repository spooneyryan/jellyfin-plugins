using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Runs the auto-collections whose query reads watch state (they have an evaluation user) when that user's watch state
/// changes: played, unplayed, favourite, rating or position saved at the end of playback, not the progress saved while
/// playing. Another user's changes start nothing.
/// </summary>
/// <remarks>
/// <para>The handler is registered only while some enabled auto-collection with a collection has an evaluation user and
/// the setting is on (checked when the definitions change, and every <see cref="RecheckInterval"/>). A change waits for a
/// quiet period of the library-change debounce (60 s by default), so a series marked played becomes one run; it is
/// asked for at the latest after the longest wait. While a library scan runs nothing is asked for: the pass after the
/// scan covers the change.</para>
/// </remarks>
internal sealed class UserDataTrigger : KitBackgroundService
{
    /// <summary>How often the trigger checks whether it is still needed when nothing else told it.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    private readonly IUserDataManager _userData;
    private readonly AutoCollectionStore _store;
    private readonly IAutoCollectionQueue _queue;
    private readonly Func<AutoCollectionSettings> _settings;
    private readonly Func<bool> _scanRunning;
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (DateTimeOffset Due, DateTimeOffset Latest)> _due = [];
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Dictionary<Guid, Guid[]> _byUser = [];
    private AutoCollectionsDocument? _seen;
    private bool _subscribed;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDataTrigger"/> class.
    /// </summary>
    /// <param name="applicationHost">The server host (the trigger starts after the server's startup).</param>
    /// <param name="userData">The user data manager (its saved event).</param>
    /// <param name="store">The definitions.</param>
    /// <param name="queue">The run queue.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="scanRunning">Whether a library scan runs.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public UserDataTrigger(
        IServerApplicationHost applicationHost,
        IUserDataManager userData,
        AutoCollectionStore store,
        IAutoCollectionQueue queue,
        Func<AutoCollectionSettings> settings,
        Func<bool> scanRunning,
        TimeProvider timeProvider,
        ILogger logger)
        : base(applicationHost, logger, timeProvider)
    {
        _userData = userData;
        _store = store;
        _queue = queue;
        _settings = settings;
        _scanRunning = scanRunning;
    }

    /// <summary>Gets a value indicating whether the handler is registered.</summary>
    public bool IsListening
    {
        get
        {
            lock (_lock)
            {
                return _subscribed;
            }
        }
    }

    /// <summary>Gets how many runs the trigger asked for (tests).</summary>
    public int Asked { get; private set; }

    /// <summary>Checks at once whether the handler is needed (after the definitions changed).</summary>
    public void Recheck()
    {
        Update();
        _wake.Writer.TryWrite(true);
    }

    /// <summary>Takes a saved change of a user's watch state (also called by tests).</summary>
    /// <param name="userId">The user.</param>
    /// <param name="reason">Why it was saved.</param>
    public void OnSaved(Guid userId, UserDataSaveReason reason)
    {
        if (reason is UserDataSaveReason.PlaybackStart or UserDataSaveReason.PlaybackProgress)
        {
            return;
        }

        lock (_lock)
        {
            if (!_byUser.TryGetValue(userId, out var definitions))
            {
                return;
            }

            var now = TimeProvider.GetUtcNow();
            var settings = _settings();
            foreach (var id in definitions)
            {
                var latest = _due.TryGetValue(id, out var waiting) ? waiting.Latest : now + settings.EventMaxWait;
                var due = now + settings.EventDebounce;
                _due[id] = (due < latest ? due : latest, latest);
            }
        }

        _wake.Writer.TryWrite(true);
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                stoppingToken.ThrowIfCancellationRequested();
                Update();
                var wait = AskDue();
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var woken = _wake.Reader.WaitToReadAsync(stop.Token).AsTask();
                await Task.WhenAny(woken, Task.Delay(wait, TimeProvider, stop.Token)).ConfigureAwait(false);
                await stop.CancelAsync().ConfigureAwait(false);
                while (_wake.Reader.TryRead(out _))
                {
                    // Looked at on the next turn.
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_subscribed)
                {
                    _userData.UserDataSaved -= OnUserDataSaved;
                    _subscribed = false;
                }
            }
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e is not null)
        {
            OnSaved(e.UserId, e.SaveReason);
        }
    }

    // The definitions to follow, by evaluation user; the handler registered while there are some.
    private void Update()
    {
        var settings = _settings();
        var document = _store.Current;
        lock (_lock)
        {
            var want = settings.Enabled && settings.UseUserDataTriggers;
            if (!ReferenceEquals(document, _seen) || !want)
            {
                _seen = document;
                _byUser = want
                    ? document.Definitions
                        .Where(d => d is not null && d.Enabled && d.CollectionId is not null && d.EvaluationUserId is not null)
                        .GroupBy(d => d.EvaluationUserId!.Value)
                        .ToDictionary(g => g.Key, g => g.Select(d => d.Id).ToArray())
                    : [];
                foreach (var gone in _due.Keys.Where(id => !_byUser.Values.Any(ids => ids.Contains(id))).ToList())
                {
                    _due.Remove(gone);
                }
            }

            var needed = _byUser.Count > 0;
            if (needed && !_subscribed)
            {
                _userData.UserDataSaved += OnUserDataSaved;
                _subscribed = true;
                Logger.LogDebug("Auto-collections: following the watch state of {Count} evaluation users.", _byUser.Count);
            }
            else if (!needed && _subscribed)
            {
                _userData.UserDataSaved -= OnUserDataSaved;
                _subscribed = false;
                Logger.LogDebug("Auto-collections: no auto-collection reads watch state; its changes are no longer followed.");
            }
        }
    }

    // Asks for the runs that are due (none while a scan runs: the pass after it covers them); returns how long to wait.
    private TimeSpan AskDue()
    {
        var now = TimeProvider.GetUtcNow();
        var ask = new List<Guid>();
        var wait = RecheckInterval;
        lock (_lock)
        {
            foreach (var (id, (due, _)) in _due.ToList())
            {
                if (due <= now)
                {
                    _due.Remove(id);
                    ask.Add(id);
                }
                else if (due - now < wait)
                {
                    wait = due - now;
                }
            }
        }

        if (ask.Count > 0 && !_scanRunning())
        {
            foreach (var id in ask)
            {
                Asked++;
                _queue.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.WatchState)).ContinueWith(
                    t => Logger.LogDebug(t.Exception, "An auto-collection run was refused."),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }
}
