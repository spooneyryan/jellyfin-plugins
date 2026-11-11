using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.SmartSearch.Integration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Listens for edits made to auto-collections outside the plugin (the server's collection editor, the server's own adds
/// after a scan, other plugins), for removed items that are pinned or excluded, for a removed auto-collection, and for
/// Library Maintenance's move hook.
/// </summary>
/// <remarks>
/// <para>The handlers only queue (they run on the caller's thread); one loop does the work. An edit is kept in the
/// definition's state until its next run sorts it into a pin, an exclude or a revert (<see cref="ManualEditKeeper"/>), and
/// that run is asked for at once, except while a library scan runs: the pass after the scan sorts the edits made during
/// it, so none is dropped. Events of the items the plugin itself is writing through the server's collection calls are
/// ignored (<see cref="CollectionWriteSuppression"/>). More edits than a state keeps stop the definition's runs (Guarded,
/// <see cref="AutoCollectionReasons.TooManyManualEdits"/>) rather than dropping any.</para>
/// <para>A removed item detaches the pins and excludes on it (a moved item's come back through the journal); a removed
/// auto-collection gets a run, which shows it deleted. At start, and whenever Library Maintenance calls
/// <see cref="ItemMovedHookV1"/>, the pins and excludes catch up with the move journal and the definitions they moved
/// in run again.</para>
/// </remarks>
internal sealed class CollectionEditListener : KitBackgroundService
{
    private readonly ICollectionManager _collections;
    private readonly ILibraryManager _library;
    private readonly AutoCollectionStore _store;
    private readonly AutoCollectionStateStore _state;
    private readonly IAutoCollectionQueue _queue;
    private readonly ManualEditKeeper _keeper;
    private readonly CollectionWriteSuppression _suppression;
    private readonly Func<AutoCollectionSettings> _settings;
    private readonly Func<bool> _scanRunning;
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action _hook;
    private (AutoCollectionsDocument? Document, HashSet<Guid> Ids) _entryIds = (null, []);

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionEditListener"/> class.
    /// </summary>
    /// <param name="applicationHost">The server host (the loop starts after the server's startup).</param>
    /// <param name="collections">The collection manager (its add and remove events).</param>
    /// <param name="library">The library manager (its removed items).</param>
    /// <param name="store">The definitions.</param>
    /// <param name="state">The run state (the edits waiting for a run).</param>
    /// <param name="queue">The run queue.</param>
    /// <param name="keeper">The pins and excludes keeper (the journal catch-up).</param>
    /// <param name="suppression">The collections the plugin is writing.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="scanRunning">Whether a library scan runs.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public CollectionEditListener(
        IServerApplicationHost applicationHost,
        ICollectionManager collections,
        ILibraryManager library,
        AutoCollectionStore store,
        AutoCollectionStateStore state,
        IAutoCollectionQueue queue,
        ManualEditKeeper keeper,
        CollectionWriteSuppression suppression,
        Func<AutoCollectionSettings> settings,
        Func<bool> scanRunning,
        TimeProvider timeProvider,
        ILogger logger)
        : base(applicationHost, logger, timeProvider)
    {
        _collections = collections;
        _library = library;
        _store = store;
        _state = state;
        _queue = queue;
        _keeper = keeper;
        _suppression = suppression;
        _settings = settings;
        _scanRunning = scanRunning;
        _hook = () => _work.Writer.TryWrite(Work.CatchUp);
    }

    private enum WorkKind
    {
        Edit,
        ItemRemoved,
        CollectionRemoved,
        CatchUpJournal,
        Mark
    }

    /// <summary>Gets how many edits were recorded (tests).</summary>
    public int Recorded { get; private set; }

    /// <summary>Waits until the work queued so far is done (tests).</summary>
    /// <returns>A task.</returns>
    public Task WhenIdleAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Writer.TryWrite(new Work(WorkKind.Mark, Guid.Empty, [], false, default, done));
        return done.Task;
    }

    /// <summary>Records an add or remove event (also called by tests).</summary>
    /// <param name="box">The collection.</param>
    /// <param name="itemIds">The items.</param>
    /// <param name="added">Whether they were added.</param>
    public void OnCollectionEdited(BoxSet box, IReadOnlyCollection<Guid> itemIds, bool added)
    {
        if (box is null || itemIds is null || itemIds.Count == 0 || TetherOf(box) is not { } definitionId)
        {
            return;
        }

        var edited = _suppression.NotWriting(box.Id, itemIds);
        if (edited.Count > 0)
        {
            _work.Writer.TryWrite(new Work(WorkKind.Edit, definitionId, [.. edited], added, TimeProvider.GetUtcNow().UtcDateTime, null));
        }
    }

    /// <inheritdoc />
    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        _collections.ItemsAddedToCollection += OnAdded;
        _collections.ItemsRemovedFromCollection += OnRemoved;
        _library.ItemRemoved += OnItemRemoved;
        ItemMovedHookV1.Attach(_hook);
        _work.Writer.TryWrite(Work.CatchUp);
        try
        {
            while (await _work.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                var batch = new List<Work>();
                while (batch.Count < 10_000 && _work.Reader.TryRead(out var work))
                {
                    batch.Add(work);
                }

                try
                {
                    await HandleAsync(batch, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    Logger.LogError(ex, "Auto-collections: edits made outside the plugin couldn't be recorded; the next pass compares the collections again.");
                }
                finally
                {
                    foreach (var mark in batch.Where(w => w.Done is not null))
                    {
                        mark.Done!.TrySetResult();
                    }
                }
            }
        }
        finally
        {
            ItemMovedHookV1.Detach(_hook);
            _library.ItemRemoved -= OnItemRemoved;
            _collections.ItemsRemovedFromCollection -= OnRemoved;
            _collections.ItemsAddedToCollection -= OnAdded;
        }
    }

    private static Guid? TetherOf(BoxSet box)
        => box.ProviderIds is { } ids
            && ids.TryGetValue(AutoCollectionDefinition.TetherKey, out var value)
            && Guid.TryParseExact(value, "N", out var id)
                ? id
                : null;

    private void OnAdded(object? sender, CollectionModifiedEventArgs e) => OnEvent(e, added: true);

    private void OnRemoved(object? sender, CollectionModifiedEventArgs e) => OnEvent(e, added: false);

    private void OnEvent(CollectionModifiedEventArgs e, bool added)
    {
        try
        {
            OnCollectionEdited(e.Collection, e.ItemsChanged.Where(i => i is not null).Select(i => i.Id).ToList(), added);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Auto-collections: a collection edit couldn't be queued.");
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is { } item)
        {
            _work.Writer.TryWrite(new Work(item is BoxSet ? WorkKind.CollectionRemoved : WorkKind.ItemRemoved, item.Id, [], false, TimeProvider.GetUtcNow().UtcDateTime, null));
        }
    }

    private async Task HandleAsync(List<Work> batch, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var runs = new Dictionary<Guid, SmartSearchAutoCollectionRunReason>();

        // Edits, per definition, kept in its state in the order they came.
        foreach (var group in batch.Where(w => w.Kind == WorkKind.Edit).GroupBy(w => w.Id))
        {
            if (_store.Find(group.Key) is not { } definition)
            {
                continue;
            }

            var edits = group.SelectMany(w => w.ItemIds.Select(id => new AutoCollectionManualEdit { ItemId = id, Added = w.Added, Utc = w.Utc })).ToList();
            await _state.UpdateAsync(
                group.Key,
                s =>
                {
                    var current = s ?? new AutoCollectionState();
                    // Over the limit, the edits kept are the oldest, and the runs stop until a manager decides: dropping
                    // any would have the next run revert them.
                    var pending = (current.PendingEdits ?? []).Concat(edits).ToList();
                    return pending.Count > AutoCollectionState.MaxPendingEdits
                        ? current with { PendingEdits = pending.Take(AutoCollectionState.MaxPendingEdits).ToList(), PendingEditsOverflow = true }
                        : current with { PendingEdits = pending };
                },
                cancellationToken).ConfigureAwait(false);
            Recorded += edits.Count;
            Logger.LogDebug("Auto-collection {Id}: {Count} items were added or removed outside the plugin.", group.Key, edits.Count);
            if (definition.Enabled)
            {
                runs[group.Key] = SmartSearchAutoCollectionRunReason.ManualEdit;
            }
        }

        // A removed auto-collection: its run shows it deleted.
        foreach (var removed in batch.Where(w => w.Kind == WorkKind.CollectionRemoved))
        {
            if (_store.FindByCollection(removed.Id) is { Enabled: true } definition)
            {
                runs[definition.Id] = SmartSearchAutoCollectionRunReason.Events;
            }
        }

        // Removed items detach the pins and excludes on them.
        var gone = batch.Where(w => w.Kind == WorkKind.ItemRemoved).Select(w => w.Id).Where(EntryIds().Contains).ToHashSet();
        if (gone.Count > 0 && !_store.IsReadOnly)
        {
            var now = TimeProvider.GetUtcNow().UtcDateTime;
            await _store.UpdateAsync(d => AutoCollectionStore.WithDefinitions(d, d.Definitions.Select(x => Detach(x, gone, now))), cancellationToken).ConfigureAwait(false);
            Logger.LogInformation("Auto-collections: {Count} pinned or excluded items were removed from the library; their entries are detached.", gone.Count);
        }

        // The move journal: entries follow their moved items, and their definitions run again.
        if (batch.Any(w => w.Kind == WorkKind.CatchUpJournal))
        {
            foreach (var id in await _keeper.CatchUpAsync(_store, cancellationToken).ConfigureAwait(false))
            {
                if (_store.Find(id) is { Enabled: true })
                {
                    runs.TryAdd(id, SmartSearchAutoCollectionRunReason.Moved);
                }
            }
        }

        // The runs, except while a scan runs: the pass after it sorts those edits.
        if (runs.Count == 0 || !settings.Enabled || _scanRunning())
        {
            return;
        }

        foreach (var (id, reason) in runs)
        {
            if (_store.Find(id) is { } definition && (definition.CollectionId is not null || reason == SmartSearchAutoCollectionRunReason.Events))
            {
                Observe(_queue.RequestAsync(new AutoCollectionRequest(id, reason)));
            }
        }
    }

    private HashSet<Guid> EntryIds()
    {
        var document = _store.Current;
        if (!ReferenceEquals(document, _entryIds.Document))
        {
            var ids = document.Definitions
                .Where(d => d?.ManualEdits is not null)
                .SelectMany(d => (d.ManualEdits.Pinned ?? []).Concat(d.ManualEdits.Excluded ?? []))
                .Where(e => e is not null && e.DetachedUtc is null)
                .Select(e => e.ItemId)
                .ToHashSet();
            _entryIds = (document, ids);
        }

        return _entryIds.Ids;
    }

    private static AutoCollectionDefinition Detach(AutoCollectionDefinition definition, HashSet<Guid> gone, DateTime now)
    {
        if (definition?.ManualEdits is not { } edits)
        {
            return definition!;
        }

        static List<AutoCollectionItemEntry> Mark(IReadOnlyList<AutoCollectionItemEntry>? entries, HashSet<Guid> gone, DateTime now)
            => (entries ?? []).Where(e => e is not null).Select(e => e.DetachedUtc is null && gone.Contains(e.ItemId) ? e with { DetachedUtc = now } : e).ToList();

        var touched = (edits.Pinned ?? []).Concat(edits.Excluded ?? []).Any(e => e is not null && e.DetachedUtc is null && gone.Contains(e.ItemId));
        return touched ? definition with { ManualEdits = edits with { Pinned = Mark(edits.Pinned, gone, now), Excluded = Mark(edits.Excluded, gone, now) } } : definition;
    }

    private void Observe(Task task)
        => task.ContinueWith(
            t => Logger.LogDebug(t.Exception, "An auto-collection run was refused."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private sealed record Work(WorkKind Kind, Guid Id, IReadOnlyList<Guid> ItemIds, bool Added, DateTime Utc, TaskCompletionSource? Done)
    {
        public static Work CatchUp { get; } = new(WorkKind.CatchUpJournal, Guid.Empty, [], false, default, null);
    }
}
