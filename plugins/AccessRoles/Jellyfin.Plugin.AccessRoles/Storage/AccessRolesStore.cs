using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Enforcement;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Storage;

/// <summary>
/// The roles and assignments store, <c>data/accessroles/accessroles.json</c>: the kit's
/// <see cref="JsonDocumentStore{TDoc}"/> over <see cref="AccessRolesDocument"/>, schema 1, with the kit's rules unchanged:
/// <c>.bak</c>, <c>.v&lt;N&gt;</c> pre-migration copies, read-only on a newer version, a failed migration
/// that never empties data, and daily snapshots (the maintenance task's <c>Snapshots</c> step).
/// </summary>
/// <remarks>
/// The data's security rule (enforcement keeps the last good snapshot in memory; Degraded mode when the store starts
/// read-only or recovered empty) is built on this store's <see cref="Status"/>: <see cref="Degraded"/> since 0.9.13.
/// </remarks>
internal sealed class AccessRolesStore : IDisposable
{
    private readonly JsonDocumentStore<AccessRolesDocument> _store;
    private readonly ILogger _logger;
    private readonly Lock _holdLock = new();
    private bool _restoreConfirmed;
    private bool _restoreDryRunSeen;
    private int _restoreInProgress;

    private AccessRolesStore(JsonDocumentStore<AccessRolesDocument> store, ILogger logger, TimeProvider? timeProvider)
    {
        _store = store;
        _logger = logger;
        Degraded = new DegradedMode(store, logger, timeProvider);
    }

    /// <summary>
    /// Raised after a change through <see cref="UpdateAsync"/> that changed the document and asked to tell (the roles API,
    /// the user-data owner): the library sync's first trigger. The sync's own bookkeeping writes don't
    /// raise it, so a sync never triggers itself. Handlers must be quick and never throw; one that does is logged, never
    /// passed on: the change is saved by then, and its caller must not be told it failed (since 0.9.16).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Gets the migrations from older schema versions: none, since schema 1 is the first.</summary>
    public static IReadOnlyList<IJsonMigration> Migrations => [];

    /// <summary>Gets the kit store, for Diagnostics, snapshots and the conformance tests.</summary>
    public JsonDocumentStore<AccessRolesDocument> Documents => _store;

    /// <summary>
    /// Gets Degraded mode: the users a store that started recovered empty or read-only without a usable
    /// document locks, durable across restarts until an admin confirms.
    /// </summary>
    public DegradedMode Degraded { get; }

    /// <summary>Gets the current document (lock-free; the store loads on first use).</summary>
    public AccessRolesDocument Current => _store.Current;

    /// <summary>Gets the store's status.</summary>
    public StoreStatus Status => _store.Status;

    /// <summary>
    /// Gets a value indicating whether the store refuses writes (a newer version's file, a failed upgrade, a missing file
    /// none of whose snapshots could be restored, <see cref="StoreStatus.ReadOnlyDataMissing"/>, or since the kit's JSON
    /// store hardening a file that can't be read with nothing to take its place, <see cref="StoreStatus.ReadOnlyDataUnreadable"/>).
    /// </summary>
    public bool IsReadOnly => _store.IsReadOnly;

    /// <summary>
    /// Gets or sets a value indicating whether an admin confirmed the library sync, in this process, after the store was
    /// restored from a snapshot at load (<see cref="StoreStatus.RestoredFromSnapshot"/>; <c>Sync/RestoreHold</c>).
    /// </summary>
    public bool RestoreConfirmed
    {
        get => Volatile.Read(ref _restoreConfirmed);
        set => Volatile.Write(ref _restoreConfirmed, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether an admin ran the library sync's dry run, in this process, while it was held
    /// after a restore (<c>Sync/RestoreHold</c>: the confirmation needs it).
    /// </summary>
    public bool RestoreDryRunSeen
    {
        get => Volatile.Read(ref _restoreDryRunSeen);
        set => Volatile.Write(ref _restoreDryRunSeen, value);
    }

    /// <summary>
    /// Gets a value indicating whether an admin's <i>Restore snapshot</i> is between its start and the end of its save
    /// (since 1.0.1): restores run one at a time, and no confirmation lifts a hold meanwhile.
    /// </summary>
    public bool RestoreInProgress => Volatile.Read(ref _restoreInProgress) != 0;

    /// <summary>
    /// Starts a restore: <c>false</c> when another one is in progress (it then answers 409 <c>RestoreInProgress</c>). Never
    /// waits, so it takes part in no lock order; <see cref="EndRestore"/> ends it.
    /// </summary>
    /// <returns><c>true</c> when this call started it.</returns>
    public bool TryBeginRestore() => Interlocked.CompareExchange(ref _restoreInProgress, 1, 0) == 0;

    /// <summary>Ends the restore <see cref="TryBeginRestore"/> started, whatever its outcome.</summary>
    public void EndRestore() => Volatile.Write(ref _restoreInProgress, 0);

    /// <summary>
    /// Creates the store over a file.
    /// </summary>
    /// <param name="file">The store file, normally <see cref="AccessRolesPaths.StoreFile"/>.</param>
    /// <param name="logger">The logger (category <c>Jellyfin.Plugin.AccessRoles.Kit.Storage</c>).</param>
    /// <param name="timeProvider">The clock of file time stamps; the system clock by default.</param>
    /// <returns>The store; nothing is read until it is first used.</returns>
    public static AccessRolesStore Create(string file, ILogger logger, TimeProvider? timeProvider = null)
        => new(CreateDocumentStore(file, logger, timeProvider), logger, timeProvider);

    /// <summary>
    /// Creates the store over a file with another schema version and migrations (tests: a migration that fails).
    /// </summary>
    /// <param name="file">The store file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="currentVersion">The schema version the store reads and writes.</param>
    /// <param name="migrations">The migrations to it.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <returns>The store.</returns>
    internal static AccessRolesStore CreateWithMigrations(string file, ILogger logger, int currentVersion, IReadOnlyList<IJsonMigration> migrations, TimeProvider? timeProvider = null)
        => new(new JsonDocumentStore<AccessRolesDocument>(file, currentVersion, () => new AccessRolesDocument { Version = currentVersion }, migrations, logger, JsonStoreFormat.Options, timeProvider), logger, timeProvider);

    /// <summary>
    /// Creates the kit store exactly as the plugin does (the conformance tests open it this way). The status page offers no
    /// recovery (the kit's <c>RecoverAsync</c>: restore a snapshot or start empty while the data is missing or unreadable;
    /// Access Roles' snapshot endpoints are the Data tab's), so the store's banner and Error name the way out without one:
    /// a readable copy put back with the server stopped.
    /// </summary>
    /// <param name="file">The store file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock of file time stamps.</param>
    /// <param name="recoveryPage">Whether the messages name a recovery on the status page: never for the plugin; the kit's
    /// conformance suite, whose expected messages assume one, passes <c>true</c> (only the wording differs).</param>
    /// <returns>The kit store.</returns>
    public static JsonDocumentStore<AccessRolesDocument> CreateDocumentStore(string file, ILogger logger, TimeProvider? timeProvider = null, bool recoveryPage = false)
        => new(file, AccessRolesDocument.CurrentVersion, AccessRolesDocument.CreateEmpty, Migrations, logger, JsonStoreFormat.Options, timeProvider, recoveryPage: recoveryPage);

    /// <summary>
    /// Changes the document through the kit store's one writer; returning the same instance writes nothing.
    /// </summary>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <param name="notify">Whether a change raises <see cref="Changed"/>; the library sync's own writes pass <c>false</c>.</param>
    /// <returns>The document stored afterwards.</returns>
    public async Task<AccessRolesDocument> UpdateAsync(Func<AccessRolesDocument, AccessRolesDocument> change, CancellationToken cancellationToken, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(change);
        var changed = false;
        var saved = await _store.UpdateAsync(
            document =>
            {
                var next = change(document);
                changed = !ReferenceEquals(next, document);
                return next;
            },
            cancellationToken).ConfigureAwait(false);
        if (changed && notify)
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogError(ex, "Access Roles saved a change, but a handler of its Changed event failed; the library sync's next run picks the change up");
            }
        }

        return saved;
    }

    /// <summary>
    /// Writes the empty schema 1 document when there is no store file yet, so a fresh install has its data folder and file
    /// from the first start. Only a store that loaded <c>Ok</c> with no file is written: a store
    /// that recovered from <c>.bak</c>, started empty after a corrupt file, or is read-only is left as it is, for the admin
    /// and for Degraded mode to see.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns><c>true</c> when the file was written.</returns>
    public async Task<bool> CreateFileIfMissingAsync(CancellationToken cancellationToken)
    {
        if (_store.Status != StoreStatus.Ok || File.Exists(_store.FilePath))
        {
            return false;
        }

        // A new instance, so the store writes it (the same instance skips the write).
        await _store.UpdateAsync(document => document with { }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _store.Dispose();

    /// <summary>
    /// Runs an action under the lock of the library sync's hold file (<c>Sync/RestoreHold</c>, 1.0.1): whoever writes,
    /// replaces or deletes it compares its owner under this lock. It is held only around the file's own reads and writes,
    /// never while waiting for the store's writer or anything else, so it takes part in no lock order.
    /// </summary>
    /// <typeparam name="T">The action's result.</typeparam>
    /// <param name="action">The action: the hold file's reads and writes only.</param>
    /// <returns>What the action answered.</returns>
    internal T WithHoldLock<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_holdLock)
        {
            return action();
        }
    }
}
