using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The auto-collections' share of the kit's library-change monitor: the full pass inside a scan only asks for a pass
/// (which waits for the scan to end) and returns at once; a batch of changes outside scans asks for a run of every
/// definition whose item kinds it touches.
/// </summary>
/// <remarks>
/// <para>Only library items count; collections, folders, libraries and views are ignored (a collection's own changes,
/// ours included, never start a run). The quiet period and the longest wait are read once, when the monitor creates the
/// consumer (a restart applies new ones). A change to a series or a season counts for episodes too, and a removed folder
/// for every definition. Library-level narrowing (the changed item's top folder) isn't applied: every definition over
/// the changed kinds runs, and one with nothing to change writes nothing.</para>
/// </remarks>
internal sealed class AutoCollectionsChangeConsumer : ILibraryChangeConsumer
{
    private static readonly BaseItemKind[] SeriesFamily = [BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode];

    private readonly AutoCollectionStore _store;
    private readonly AutoCollectionChecks _checks;
    private readonly AutoCollectionCheckContextSource _context;
    private readonly IAutoCollectionQueue _queue;
    private readonly ILogger _logger;
    private readonly Func<Guid, bool> _createPending;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionsChangeConsumer"/> class.
    /// </summary>
    /// <param name="store">The definitions.</param>
    /// <param name="checks">The definition checks (a definition's query and its kinds).</param>
    /// <param name="context">The check context and the settings.</param>
    /// <param name="queue">Where runs are asked for.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="createPending">Whether a definition without a collection still has a create to finish (its state's
    /// <see cref="AutoCollectionState.CreatePending"/>); none by default.</param>
    public AutoCollectionsChangeConsumer(AutoCollectionStore store, AutoCollectionChecks checks, AutoCollectionCheckContextSource context, IAutoCollectionQueue queue, ILogger logger, Func<Guid, bool>? createPending = null)
    {
        _createPending = createPending ?? (_ => false);
        _store = store;
        _checks = checks;
        _context = context;
        _queue = queue;
        _logger = logger;
        var settings = context.Settings;
        Filter = new LibraryChangeFilter
        {
            Ignore = static (_, item) => item is BoxSet or CollectionFolder or UserView || item.GetType() == typeof(Folder) || item is AggregateFolder,
            CaptureIdentity = false,
            QuietPeriod = settings.EventDebounce,
            MaxWait = settings.EventMaxWait
        };
    }

    /// <inheritdoc />
    public string Name => "AutoCollections";

    /// <inheritdoc />
    public LibraryChangeFilter Filter { get; }

    /// <inheritdoc />
    public Task HandleAsync(LibraryChangeBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_context.Settings.Enabled)
        {
            return Task.CompletedTask;
        }

        if (batch.FullPass)
        {
            // Never wait here: the server holds its file watcher and inherited values back until every post-scan task returns.
            Observe(_queue.RequestPassAsync(SmartSearchAutoCollectionRunReason.PostScan));
            return Task.CompletedTask;
        }

        if (batch.IsEmpty)
        {
            return Task.CompletedTask;
        }

        foreach (var id in Affected(batch))
        {
            Observe(_queue.RequestAsync(new AutoCollectionRequest(id, SmartSearchAutoCollectionRunReason.Events)));
        }

        return Task.CompletedTask;
    }

    // The enabled definitions with a collection (or a create to finish) whose item kinds the batch touches; every one for a
    // removed folder.
    private List<Guid> Affected(LibraryChangeBatch batch)
    {
        var kinds = batch.Kinds.ToHashSet();
        if (kinds.Overlaps(SeriesFamily))
        {
            kinds.UnionWith(SeriesFamily);
        }

        var all = batch.RemovedFolders.Count > 0;
        var context = _context.Create();
        var affected = new List<Guid>();
        foreach (var definition in _store.Current.Definitions.Where(d => d is not null && d.Enabled && (d.CollectionId is not null || _createPending(d.Id))))
        {
            if (all)
            {
                affected.Add(definition.Id);
                continue;
            }

            // A definition whose check fails runs anyway: its run records why.
            var check = _checks.CheckForRun(definition, context);
            var definitionKinds = check.Query?.Scope.ItemKinds;
            if (!check.Ok || definitionKinds is null || definitionKinds.Count == 0 || definitionKinds.Any(kinds.Contains))
            {
                affected.Add(definition.Id);
            }
        }

        return affected;
    }

    private void Observe(Task task)
        => task.ContinueWith(
            t => _logger.LogDebug(t.Exception, "An auto-collection run asked for by library changes was refused."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
