using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The auto-collections' run state, <c>data/smartsearch/auto-collections-state.json</c>, through the kit's JSON store.
/// </summary>
/// <remarks>
/// <para><b>Rebuildable.</b> No run reads its decisions from here (every run diffs against the live collection), so where
/// the kit would keep an unreadable file read-only until an administrator decides (no <c>.bak</c>, no snapshot), this
/// store starts empty at once, the unreadable file kept aside by the kit; the next run of each definition fills it again.
/// The state has no snapshots of its own.</para>
/// <para>A file of a newer schema version stays read-only, as the kit leaves it: the state is then kept in memory only,
/// so the tab still shows each run, and the newer file is left for the newer version.</para>
/// </remarks>
internal sealed class AutoCollectionStateStore : IDisposable
{
    /// <summary>The store's file name.</summary>
    public const string FileName = "auto-collections-state.json";

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _recovery = new(1, 1);
    private readonly Lock _memoryLock = new();
    private AutoCollectionsStateDocument? _memory;
    private int _readOnlyLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionStateStore"/> class.
    /// </summary>
    /// <param name="file">The store file.</param>
    /// <param name="snapshotDirectory">Where the kit would look for snapshots of the file (none are taken).</param>
    /// <param name="logger">The store's logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public AutoCollectionStateStore(string file, string snapshotDirectory, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        SnapshotDirectory = snapshotDirectory;
        Documents = CreateStore(file, logger, timeProvider, snapshotDirectory);
    }

    /// <summary>Gets the kit store.</summary>
    public JsonDocumentStore<AutoCollectionsStateDocument> Documents { get; }

    /// <summary>Gets the folder the kit looks in for snapshots.</summary>
    public string SnapshotDirectory { get; }

    /// <summary>Gets the current state document: the file's, or the one kept in memory while the file is read-only.</summary>
    public AutoCollectionsStateDocument Current
    {
        get
        {
            lock (_memoryLock)
            {
                return _memory ?? Documents.Current;
            }
        }
    }

    /// <summary>Creates the kit store: schema version 1, no migrations, the kit's format.</summary>
    /// <param name="file">The file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="snapshotDirectory">The snapshot folder.</param>
    /// <returns>The store.</returns>
    public static JsonDocumentStore<AutoCollectionsStateDocument> CreateStore(string file, ILogger logger, TimeProvider? timeProvider = null, string? snapshotDirectory = null)
        => new(file, AutoCollectionsStateDocument.CurrentVersion, AutoCollectionsStateDocument.CreateEmpty, [], logger, JsonStoreFormat.Options, timeProvider, snapshotDirectory);

    /// <summary>Gets a definition's state.</summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <returns>The state, or <c>null</c> when it never ran.</returns>
    public AutoCollectionState? Find(Guid definitionId) => Current.States.TryGetValue(definitionId, out var state) ? state : null;

    /// <summary>
    /// Loads the file, starting empty when the kit found it unreadable (see the class). Called before every change; cheap
    /// once loaded.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting.</param>
    /// <returns>A task.</returns>
    public async Task EnsureUsableAsync(CancellationToken cancellationToken)
    {
        if (!Documents.CanRecover)
        {
            return;
        }

        await _recovery.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Documents.CanRecover)
            {
                _logger.LogWarning("The auto-collections' run state ({Status}) can't be read; it starts empty and the next runs fill it again.", Documents.Status);
                await Documents.RecoverAsync(SnapshotDirectory, null, startEmpty: true, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _recovery.Release();
        }
    }

    /// <summary>Changes one definition's state (the file's, or the memory's while the file is read-only).</summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <param name="change">The change, given the current state or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The state after the change.</returns>
    public async Task<AutoCollectionState> UpdateAsync(Guid definitionId, Func<AutoCollectionState?, AutoCollectionState> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        AutoCollectionState? result = null;
        AutoCollectionsStateDocument Apply(AutoCollectionsStateDocument document)
        {
            var states = new Dictionary<Guid, AutoCollectionState>(document.States ?? new Dictionary<Guid, AutoCollectionState>());
            states.TryGetValue(definitionId, out var current);
            result = change(current);
            if (ReferenceEquals(result, current))
            {
                return document;
            }

            states[definitionId] = result;
            return document with { States = states };
        }

        await EnsureUsableAsync(cancellationToken).ConfigureAwait(false);
        if (!Documents.IsReadOnly)
        {
            await Documents.UpdateAsync(Apply, cancellationToken).ConfigureAwait(false);
            return result!;
        }

        if (Interlocked.Exchange(ref _readOnlyLogged, 1) == 0)
        {
            _logger.LogWarning("The auto-collections' run state file is read-only ({Status}); the state is kept in memory until a restart.", Documents.Status);
        }

        lock (_memoryLock)
        {
            _memory = Apply(_memory ?? Documents.Current);
        }

        return result!;
    }

    /// <summary>Drops the states of definitions that no longer exist.</summary>
    /// <param name="definitionIds">The ids of the definitions that exist.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>The number of states dropped.</returns>
    public async Task<int> PruneAsync(IReadOnlyCollection<Guid> definitionIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitionIds);
        var keep = definitionIds.ToHashSet();
        var dropped = 0;
        AutoCollectionsStateDocument Apply(AutoCollectionsStateDocument document)
        {
            var states = (document.States ?? new Dictionary<Guid, AutoCollectionState>()).Where(p => keep.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
            dropped = (document.States?.Count ?? 0) - states.Count;
            return dropped == 0 ? document : document with { States = states };
        }

        await EnsureUsableAsync(cancellationToken).ConfigureAwait(false);
        if (!Documents.IsReadOnly)
        {
            await Documents.UpdateAsync(Apply, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            lock (_memoryLock)
            {
                _memory = Apply(_memory ?? Documents.Current);
            }
        }

        return dropped;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Documents.Dispose();
        _recovery.Dispose();
    }
}
