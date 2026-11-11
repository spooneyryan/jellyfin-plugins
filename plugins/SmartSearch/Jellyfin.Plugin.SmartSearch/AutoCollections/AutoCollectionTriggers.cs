using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.SmartSearch.Storage;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The triggers that aren't library changes or endpoints: the startup pass, <c>StartupPassDelay</c> after the server's
/// startup (it heals links the server's startup cleanup removed, and waits for a running scan like any pass after a
/// scan), and a run of every definition that follows a saved query when that query is replaced, removed or added back
/// (an edit, a forced or bulk change, its owner's queries moved or removed, an import or restore).
/// </summary>
/// <remarks>Both only ask the worker; nothing runs here. A pass also re-reads every followed query, so a change that
/// didn't reach this hook (a restore from a snapshot) is caught by the next pass.</remarks>
internal sealed class AutoCollectionTriggers : KitBackgroundService
{
    private readonly IAutoCollectionQueue _queue;
    private readonly AutoCollectionStore _store;
    private readonly SavedQueryStore _queries;
    private readonly Func<AutoCollectionSettings> _settings;
    private readonly Func<Guid, bool> _createPending;
    private int _startupAsked;
    private volatile bool _listening;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionTriggers"/> class.
    /// </summary>
    /// <param name="applicationHost">The server, for the startup gate.</param>
    /// <param name="queue">Where runs are asked for.</param>
    /// <param name="store">The definitions.</param>
    /// <param name="queries">The saved queries.</param>
    /// <param name="settings">The settings, read on each use.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="createPending">Whether a definition without a collection still has a create to finish (its state's
    /// <see cref="AutoCollectionState.CreatePending"/>); none by default.</param>
    public AutoCollectionTriggers(
        IServerApplicationHost applicationHost,
        IAutoCollectionQueue queue,
        AutoCollectionStore store,
        SavedQueryStore queries,
        Func<AutoCollectionSettings> settings,
        TimeProvider timeProvider,
        ILogger logger,
        Func<Guid, bool>? createPending = null)
        : base(applicationHost, logger, timeProvider)
    {
        _queue = queue;
        _store = store;
        _queries = queries;
        _settings = settings;
        _createPending = createPending ?? (_ => false);
    }

    /// <summary>Gets a value indicating whether changed saved queries are listened to (after the server's startup).</summary>
    public bool IsListening => _listening;

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _queries.QueriesChanged += OnQueriesChanged;
        _listening = true;
        try
        {
            var delay = _settings().StartupPassDelay;
            if (delay > TimeSpan.Zero && Volatile.Read(ref _startupAsked) == 0)
            {
                await Task.Delay(delay, TimeProvider, stoppingToken).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _startupAsked, 1) == 0 && _settings().Enabled)
                {
                    Observe(_queue.RequestPassAsync(SmartSearchAutoCollectionRunReason.Startup));
                }
            }

            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _listening = false;
            _queries.QueriesChanged -= OnQueriesChanged;
        }
    }

    private void OnQueriesChanged(object? sender, SavedQueriesChangedEventArgs e)
    {
        try
        {
            if (!_settings().Enabled)
            {
                return;
            }

            var changed = e.QueryIds.ToHashSet();
            foreach (var definition in _store.Current.Definitions.Where(d => d is not null
                && d.Enabled
                && (d.CollectionId is not null || _createPending(d.Id))
                && d.Source.Mode.Value == SmartSearchAutoCollectionSourceMode.Reference
                && d.Source.SavedQueryId is { } id
                && changed.Contains(id)))
            {
                Observe(_queue.RequestAsync(new AutoCollectionRequest(definition.Id, SmartSearchAutoCollectionRunReason.Saved)));
            }
        }
        catch (Exception ex)
        {
            // A saved query's write never fails because of us; the next pass catches up.
            Logger.LogError(ex, "Auto-collections: the runs for changed saved queries couldn't be asked for.");
        }
    }

    private void Observe(Task task)
        => task.ContinueWith(
            t => Logger.LogDebug(t.Exception, "An auto-collection run was refused."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
