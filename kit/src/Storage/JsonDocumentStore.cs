using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// A plugin's data in one JSON file, the kit's default storage engine (generalised from Bookmarks 1.0's
/// <c>JsonBookmarkStore</c>). The whole document lives in memory as an immutable snapshot, so reads never block.
/// </summary>
/// <remarks>
/// <para><b>Load</b> (lazily, on first use). A file that reads is the data, even with no records. A missing file falls
/// back to <c>.bak</c>. A file with no bytes or white space only holds no data: it is moved aside as
/// <c>.corrupt-&lt;ts&gt;</c> and counts as missing, and so does an empty <c>.bak</c>. A file that isn't one valid JSON
/// object, or doesn't deserialize at the current schema version, is unreadable: a readable <c>.bak</c> takes its place,
/// the file moved aside as <c>.corrupt-&lt;ts&gt;</c> first; the <c>.bak</c> goes through the same version handling.
/// With no usable <c>.bak</c> (none, an empty one, or an unreadable one, which is moved aside the same way when
/// something takes its place), the store looks in <see cref="SnapshotDirectory"/>: the newest snapshot that reads and
/// holds data is restored (<see cref="StoreStatus.RestoredFromSnapshot"/>, as the SQLite database does). When none can
/// be, the store never starts empty on its own where data may be lost (the SQLite database's rule): an unreadable file
/// or <c>.bak</c> is left in place and the store is read-only (<see cref="StoreStatus.ReadOnlyDataUnreadable"/>), and
/// so it is when snapshots that may hold data exist but none can be used
/// (<see cref="StoreStatus.ReadOnlyDataMissing"/>), until an admin restores a snapshot or starts empty
/// (<see cref="RecoverAsync"/>). It starts empty only when nothing held data: no file (a fresh install,
/// <see cref="StoreStatus.Ok"/>) or empty ones moved aside (<see cref="StoreStatus.RecoveredEmpty"/>), and no snapshot
/// that holds data. A load that moved an empty file aside and then failed (a 503, below) remembers it for the next
/// attempt; one that moved an unreadable file aside and then failed to put the data in its place moves it back. A file
/// of a newer schema version makes the store read-only (<see cref="StoreStatus.ReadOnlyNewerVersion"/>). A file of an
/// older version is copied to <c>.v&lt;N&gt;-&lt;ts&gt;</c> (or an identical earlier copy is reused), upgraded by the
/// <see cref="IJsonMigration"/> steps and saved at once. A failed upgrade never counts as corruption: the file stays
/// byte-identical, nothing is quarantined, <c>.bak</c> isn't tried, and the store serves an empty document read-only
/// (<see cref="StoreStatus.ReadOnlyMigrationFailed"/>). A file that can't be read at all (locked, no access) makes
/// every use throw a 503 <see cref="PluginException"/> until it can be; nothing is moved or replaced then, and only the
/// first failure is logged as an Error.</para>
/// <para><b>Write</b> (<see cref="UpdateAsync"/>). One writer at a time: the new document is serialized to <c>.tmp</c>,
/// flushed to disk, and swapped in with <see cref="File.Replace(string, string, string?)"/>, which keeps the previous file
/// as <c>.bak</c>; where that fails (a read-only or open <c>.bak</c>), a plain overwrite replaces the file and a Warning
/// says that <c>.bak</c> is no longer refreshed. Only then is the new snapshot published. A failed write removes
/// <c>.tmp</c>, leaves memory and file unchanged, and throws a 503 <see cref="PluginException"/>.</para>
/// <para>Time stamps in file names are UTC. Plugins create their store with a logger from
/// <c>KitOptions.CreateLogger(factory, "Storage")</c>, and every plugin's tests run the kit's
/// <c>StoreConformanceTests</c> against it.</para>
/// </remarks>
/// <typeparam name="TDoc">The document type.</typeparam>
internal sealed class JsonDocumentStore<TDoc> : IDisposable
    where TDoc : class, IVersionedDocument
{
    /// <summary>The suffix of <see cref="KeepSnapshotsMarkerPath"/>.</summary>
    public const string KeepSnapshotsMarkerSuffix = KeepSnapshotsMarker.Suffix;

    /// <summary>The suffix of <see cref="LastRestoreMarkerPath"/>.</summary>
    public const string LastRestoreMarkerSuffix = ".last-restore";

    private const string FileTimestampFormat = "yyyyMMddHHmmss";
    private const int WriteBufferSize = 64 * 1024;

    // A pre-upgrade copy is written as <copy>.tmp and renamed once on disk (KeepPreUpgradeCopy); never listed as kept.
    private const string CopyTempSuffix = ".tmp";

    // How much of a snapshot is read to find its Version when listing (a store writes it first).
    private const int VersionPeekBytes = 64 * 1024;

    private readonly string _backupPath;
    private readonly string _tempPath;
    private readonly Func<TDoc> _createEmpty;
    private readonly Func<TDoc, TDoc>? _afterRestoreAtLoad;
    private readonly bool _recoveryPage;
    private readonly Dictionary<int, IJsonMigration> _migrations;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly Lock _loadLock = new();

    // Completed when an admin's recovery makes a read-only store writable (WaitUntilWritableAsync).
    private readonly TaskCompletionSource _writable = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile LoadedState? _state;
    private long _lastSnapshotTicks;

    // The .keep-snapshots marker: the snapshots there were when the data was last found missing or unreadable (the store
    // started empty, or was restored from a snapshot or its backup), never rotated out, across restarts, until one of them
    // is restored. Null when there is none.
    private volatile KeptSnapshots<StoreStatus>? _kept;

    // 1 once the kept snapshots have been mentioned in a Warning in this process.
    private int _keptSnapshotsLogged;

    // 1 once a snapshot outside SnapshotDirectory has been mentioned in a Warning.
    private int _otherSnapshotDirectoryLogged;

    // What an attempt to load moved aside, the file or its .bak, when it then failed (a 503: the .bak or a snapshot
    // couldn't be read just then): the next attempt finds the file missing, and still says what it was. Cleared once a
    // load succeeds; guarded by _loadLock.
    private Slot? _movedFile;
    private Slot? _movedBackup;

    // Whether a failure to read the file has been logged as an Error since the last successful load; guarded by _loadLock.
    private bool _readFailureLogged;

    // 1 once the fallback from File.Replace to a plain overwrite has been logged as a Warning.
    private int _replaceFallbackLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonDocumentStore{TDoc}"/> class. Nothing is read until the store is
    /// first used.
    /// </summary>
    /// <param name="file">The store file, for example <c>&lt;data&gt;/bookmarks/bookmarks.json</c>. Created on first write.</param>
    /// <param name="currentVersion">The schema version this build writes; 1 or more.</param>
    /// <param name="createEmpty">Creates the empty document, at <paramref name="currentVersion"/>.</param>
    /// <param name="migrations">One step from each version below <paramref name="currentVersion"/>.</param>
    /// <param name="logger">The logger (category <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Storage</c>).</param>
    /// <param name="serializerOptions">The serializer options; <see cref="JsonStoreFormat.Options"/> by default.</param>
    /// <param name="timeProvider">The clock for file time stamps; the system clock by default.</param>
    /// <param name="snapshotDirectory">The snapshot folder the store restores from when its file is missing, empty or
    /// unreadable (<see cref="SnapshotDirectory"/>); <c>snapshots</c> next to the file by default.</param>
    /// <param name="afterRestoreAtLoad">The plugin's step for data restored from a snapshot at load
    /// (<see cref="StoreStatus.RestoredFromSnapshot"/>), the one its admin <i>Restore snapshot</i> runs after
    /// <see cref="RestoreSnapshotAsync"/>, such as Bookmarks ending the clip jobs the snapshot held queued or being made. It
    /// gets the restored document at <paramref name="currentVersion"/>, runs under the load lock before the document is
    /// saved, and returns the document to save (the same instance for no change); it must not use the store. If it throws,
    /// or returns no document or one at another version, an Error says so and the restored document is saved as it is.
    /// None by default.</param>
    /// <param name="recoveryPage">Whether the plugin's settings page offers an admin the recovery
    /// (<see cref="RecoverAsync"/>) while the data is missing or unreadable: the banner and the Error say how to go on
    /// accordingly (false for <c>kit-users.json</c> of a plugin without such a page). True by default.</param>
    public JsonDocumentStore(
        string file,
        int currentVersion,
        Func<TDoc> createEmpty,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        JsonSerializerOptions? serializerOptions = null,
        TimeProvider? timeProvider = null,
        string? snapshotDirectory = null,
        Func<TDoc, TDoc>? afterRestoreAtLoad = null,
        bool recoveryPage = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentOutOfRangeException.ThrowIfLessThan(currentVersion, 1);
        ArgumentNullException.ThrowIfNull(createEmpty);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(logger);

        _migrations = new Dictionary<int, IJsonMigration>();
        foreach (var migration in migrations)
        {
            ArgumentNullException.ThrowIfNull(migration, nameof(migrations));
            if (migration.FromVersion < 1 || migration.FromVersion >= currentVersion)
            {
                throw new ArgumentException(
                    $"A migration from version {migration.FromVersion} doesn't fit a store at version {currentVersion}.",
                    nameof(migrations));
            }

            if (!_migrations.TryAdd(migration.FromVersion, migration))
            {
                throw new ArgumentException($"Two migrations start at version {migration.FromVersion}.", nameof(migrations));
            }
        }

        var missing = Enumerable.Range(1, currentVersion - 1).Where(v => !_migrations.ContainsKey(v)).ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException(
                "No migration starts at version " + string.Join(", ", missing) + ".",
                nameof(migrations));
        }

        FilePath = Path.GetFullPath(file);
        StoreName = Path.GetFileNameWithoutExtension(FilePath);
        SnapshotDirectory = Path.GetFullPath(snapshotDirectory ?? Path.Combine(Path.GetDirectoryName(FilePath)!, KitPaths.SnapshotsFolder));
        CurrentVersion = currentVersion;
        SerializerOptions = serializerOptions ?? JsonStoreFormat.Options;
        _backupPath = FilePath + ".bak";
        _tempPath = FilePath + ".tmp";
        _createEmpty = createEmpty;
        _afterRestoreAtLoad = afterRestoreAtLoad;
        _recoveryPage = recoveryPage;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // What was found where the file or its .bak belongs.
    private enum Found
    {
        Missing,

        // No bytes, or white space only: no data.
        Blank,

        // Something this version can't read as the data: maybe the only copy of it.
        Unreadable
    }

    /// <summary>
    /// Gets the full path of the store file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the store's name, the file name without extension (<c>bookmarks</c>); snapshots are named after it.
    /// </summary>
    public string StoreName { get; }

    /// <summary>
    /// Gets the snapshot folder the store looks in when it loads and its file is missing, empty or unreadable with no usable
    /// <c>.bak</c> (<see cref="StoreStatus.RestoredFromSnapshot"/>): <c>&lt;folder of the file&gt;/snapshots</c> unless the constructor
    /// was given another. The snapshot methods take their folder as an argument; pass this one (a Warning says so once
    /// otherwise), or the restore at load can't see the snapshots.
    /// </summary>
    public string SnapshotDirectory { get; }

    /// <summary>
    /// Gets the schema version this build reads and writes.
    /// </summary>
    public int CurrentVersion { get; }

    /// <summary>
    /// Gets the serializer options of the file.
    /// </summary>
    public JsonSerializerOptions SerializerOptions { get; }

    /// <summary>
    /// Gets the current document, an immutable snapshot. Loads the file on first use.
    /// </summary>
    /// <exception cref="PluginException">The file exists but can't be read (503); the next use tries again.</exception>
    public TDoc Current => EnsureLoaded().Document;

    /// <summary>
    /// Gets how the store came up. It doesn't change until the plugin is restarted.
    /// </summary>
    public StoreStatus Status => EnsureLoaded().Status;

    /// <summary>
    /// Gets a value indicating whether the store refuses every write and snapshot: the file comes from a newer plugin
    /// version, upgrading it failed, or it is missing or unreadable with no snapshot to restore (until an admin recovers it,
    /// <see cref="RecoverAsync"/>). Background writers (maintenance, reconciler, sweeps, workers) check it and stay idle.
    /// </summary>
    public bool IsReadOnly => EnsureLoaded().IsReadOnly;

    /// <summary>
    /// Gets the schema version of the file that was loaded, or <c>null</c> when there was none.
    /// </summary>
    public int? LoadedVersion => EnsureLoaded().LoadedVersion;

    /// <summary>
    /// Gets the schema version the data was upgraded from at load, or <c>null</c> when no upgrade ran.
    /// </summary>
    public int? MigratedFrom => EnsureLoaded().MigratedFrom;

    /// <summary>
    /// Gets one sentence about the status for the settings page's banner and Diagnostics, or <c>null</c> when it is
    /// <see cref="StoreStatus.Ok"/>.
    /// </summary>
    public string? StatusMessage => EnsureLoaded().Message;

    /// <summary>
    /// Gets the marker file that keeps the snapshots, daily and pre-operation, taken before the store last started empty or
    /// was restored from a snapshot at load: <c>&lt;file&gt;.keep-snapshots</c>, next to the store file. Deleting it (or
    /// restoring one of those snapshots) lets them rotate normally again.
    /// </summary>
    public string KeepSnapshotsMarkerPath => FilePath + KeepSnapshotsMarkerSuffix;

    /// <summary>
    /// Gets the marker file naming the snapshot restored last (by <see cref="RestoreSnapshotAsync"/> or at load) and when:
    /// <c>&lt;file&gt;.last-restore</c>. A restore at load tries the snapshots taken since then first, then that snapshot,
    /// so the <c>restore</c> pre-operation snapshot, the state an admin replaced, isn't put back ahead of them.
    /// </summary>
    public string LastRestoreMarkerPath => FilePath + LastRestoreMarkerSuffix;

    /// <summary>
    /// Gets the time recorded with the kept snapshots, while there are any: the store last started empty
    /// (<see cref="StoreStatus.RecoveredEmpty"/>), was restored from a snapshot at load or by an admin's recovery
    /// (<see cref="StoreStatus.RestoredFromSnapshot"/>), or from a <c>.bak</c> older than a snapshot that holds other
    /// data (<see cref="StoreStatus.RecoveredFromBackup"/>), and no snapshot of those kept has been restored since. The
    /// snapshots there were then, daily and pre-operation, are the last good copies of the data: their names are
    /// recorded in <see cref="KeepSnapshotsMarkerPath"/>, and rotation keeps exactly those, across restarts, until
    /// <see cref="RestoreSnapshotAsync"/> succeeds with one of them or an admin deletes the marker. The snapshots taken
    /// since rotate as usual, so the kept ones never grow in number until the next incident records the snapshots there
    /// are then. The time is the later of the incident and the newest kept snapshot's time plus a second, so it is
    /// later than every kept snapshot even after the clock moved back (it is what Bookmarks 1.1 reads from the marker).
    /// <c>null</c> when nothing is kept.
    /// </summary>
    public DateTime? KeepSnapshotsBeforeUtc
    {
        get
        {
            EnsureLoaded();
            return _kept?.BeforeUtc;
        }
    }

    /// <summary>
    /// Gets how the data was found when the kept snapshots were recorded (<see cref="KeepSnapshotsBeforeUtc"/>):
    /// <see cref="StoreStatus.RecoveredEmpty"/> (it started empty), <see cref="StoreStatus.RestoredFromSnapshot"/> or
    /// <see cref="StoreStatus.RecoveredFromBackup"/>, so a page can say why they are kept; <c>null</c> when nothing is
    /// kept, or the marker was written by an earlier version, which didn't say.
    /// </summary>
    public StoreStatus? KeptSnapshotsIncident
    {
        get
        {
            EnsureLoaded();
            return _kept?.Incident;
        }
    }

    /// <summary>
    /// Gets when <see cref="SnapshotAsync"/> last wrote a snapshot in this process (UTC), or <c>null</c>.
    /// </summary>
    public DateTime? LastSnapshotUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSnapshotTicks);
            return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// Gets a value indicating whether an admin can recover the data with <see cref="RecoverAsync"/>: it is missing or can't
    /// be read, and no snapshot could take its place (<see cref="StoreStatus.ReadOnlyDataMissing"/>,
    /// <see cref="StoreStatus.ReadOnlyDataUnreadable"/>).
    /// </summary>
    /// <exception cref="PluginException">503: the file can't be read at all (locked, no access).</exception>
    public bool CanRecover => EnsureLoaded().IsRecoverable;

    /// <summary>
    /// Throws the 503 every <see cref="UpdateAsync"/> throws while the store is read-only (<see cref="IsReadOnly"/>), and
    /// does nothing while it is writable. For a request's write that looks something up before it changes the document:
    /// after a failed upgrade (<see cref="StoreStatus.ReadOnlyMigrationFailed"/>) the store serves an empty stand-in, so
    /// the lookup would answer "not found" for data the untouched file still holds.
    /// </summary>
    /// <exception cref="PluginException">503: the store is read-only, or the file can't be read.</exception>
    public void ThrowIfReadOnly() => ThrowIfReadOnly(EnsureLoaded());

    /// <summary>
    /// Waits until the store can be written: at once while it can, else until an admin recovers the data
    /// (<see cref="RecoverAsync"/>). A store that is read-only for another reason (a newer version's file, a failed upgrade)
    /// stays so until the plugin restarts, so the wait ends only with <paramref name="cancellationToken"/>. For a background
    /// writer that found the store read-only when it started, such as Bookmarks' clip worker.
    /// </summary>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>A task that completes once the store can be written.</returns>
    /// <exception cref="PluginException">503: the file can't be read at all (locked, no access).</exception>
    public Task WaitUntilWritableAsync(CancellationToken cancellationToken)
        => EnsureLoaded().IsReadOnly ? _writable.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    /// <summary>
    /// Changes the document. Changes run one at a time, each on the latest document.
    /// </summary>
    /// <param name="change">
    /// Returns the new document, a new instance at <see cref="CurrentVersion"/>; returning the same instance writes nothing.
    /// Runs once, under the writer lock, so it may capture results in local variables.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the writer lock; a started write always finishes.</param>
    /// <returns>The document after the change.</returns>
    /// <exception cref="PluginException">503: the store is read-only, the file can't be read, or the write failed.</exception>
    public async Task<TDoc> UpdateAsync(Func<TDoc, TDoc> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        ThrowIfReadOnly(EnsureLoaded());
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = EnsureLoaded();
            var next = change(state.Document);
            if (ReferenceEquals(next, state.Document))
            {
                return next;
            }

            if (next is null)
            {
                throw new InvalidOperationException("A store change returned no document.");
            }

            if (next.Version != CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"A store change returned a document at version {next.Version}; this store writes version {CurrentVersion}.");
            }

            await CommitAsync(next).ConfigureAwait(false);
            _state = state.WithDocument(next);
            return next;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes the current document once more, so that its <c>.bak</c> holds it too and no earlier version of the data is
    /// left beside the file (since Bookmarks 1.2.1: after a deleted user's records go, the <c>.bak</c> the deletion's write
    /// left would still hold them until the next write). Where the save can't keep a <c>.bak</c> (the plain overwrite's
    /// fallback), a <c>.bak</c> left from before is overwritten with the file's bytes, or deleted, so it holds no earlier
    /// version either; a Warning says so when neither can be done. Nothing is written while the store is read-only or before
    /// its file exists.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the writer lock; a started write always finishes.</param>
    /// <returns>Whether no earlier version is left beside the file: <c>false</c> while the store is read-only, or when a stale
    /// <c>.bak</c> couldn't be replaced or deleted.</returns>
    /// <exception cref="PluginException">503: the write failed.</exception>
    public async Task<bool> RefreshBackupAsync(CancellationToken cancellationToken)
    {
        if (EnsureLoaded().IsReadOnly)
        {
            return false;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = EnsureLoaded();
            if (state.IsReadOnly)
            {
                return false;
            }

            if (!File.Exists(FilePath))
            {
                return !File.Exists(_backupPath);
            }

            await CommitAsync(state.Document).ConfigureAwait(false);
            return ClearStaleBackup();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Writes the current document to <c>&lt;directory&gt;/&lt;store&gt;-&lt;yyyyMMdd-HHmmss&gt;.json</c> and deletes the
    /// store's oldest snapshots there, keeping <paramref name="keep"/>. Does nothing while the store is
    /// read-only, so empty snapshots never rotate the good ones out, and nothing when <paramref name="keep"/> is 0.
    /// </summary>
    /// <remarks>
    /// When the store started empty (<see cref="StoreStatus.RecoveredEmpty"/>: empty files moved aside, or an admin's
    /// choice) or was restored from a snapshot at load or by an admin (<see cref="StoreStatus.RestoredFromSnapshot"/>),
    /// or from a <c>.bak</c> older than a snapshot holding other data, the snapshots there were then are the last good
    /// copies of the data. Snapshots are still written, but only the ones taken since then are rotated; every one there
    /// was then is kept, pre-operation ones too (<see cref="PreOpSnapshotAsync(string, string, CancellationToken)"/>).
    /// Their names are recorded in the <see cref="KeepSnapshotsMarkerPath"/> marker, so the protection survives
    /// restarts (also once the store has been written again and loads as <see cref="StoreStatus.Ok"/>), until
    /// <see cref="RestoreSnapshotAsync"/> succeeds with one of them or an admin deletes the marker (by name); one
    /// Warning per process says so. A snapshot dated more than a day after now is never rotated out
    /// (<see cref="SnapshotRotation.FutureTolerance"/>).
    /// </remarks>
    /// <param name="directory">The snapshot folder, normally <c>&lt;data folder&gt;/snapshots</c>.</param>
    /// <param name="keep">How many snapshots to keep (<c>SnapshotsToKeep</c>); 0 disables snapshots.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot to finish.</param>
    /// <returns>The new snapshot's path, or <c>null</c> when none was written.</returns>
    public async Task<string?> SnapshotAsync(string directory, int keep, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        var state = EnsureLoaded();
        if (state.IsReadOnly)
        {
            _logger.LogDebug("Skipped the snapshot of {File}: the store is read-only ({Status})", FilePath, state.Status);
            return null;
        }

        if (keep == 0)
        {
            return null;
        }

        WarnUnlessSnapshotDirectory(directory);
        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var utc = _timeProvider.GetUtcNow().UtcDateTime;
            var path = await WriteSnapshotFileAsync(directory, SnapshotRotation.NewPath(directory, StoreName, ".json", utc)).ConfigureAwait(false);

            ReleaseIfNoneIsLeft(directory);
            var kept = WarnOfKeptSnapshots(directory);
            SnapshotRotation.Rotate(directory, StoreName, ".json", keep, _logger, nowUtc: utc, keepNames: kept?.Names);
            Interlocked.Exchange(ref _lastSnapshotTicks, utc.Ticks);
            _logger.LogDebug("Wrote the snapshot {Snapshot} of {File}", path, FilePath);
            return path;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    /// <summary>
    /// Takes a pre-operation snapshot before a risky operation:
    /// <c>&lt;directory&gt;/pre-op/&lt;store&gt;-&lt;operation&gt;-&lt;yyyyMMdd-HHmmss&gt;.json</c>, rotated separately
    /// from the daily snapshots (the newest <see cref="SnapshotOperations.ToKeep"/> are kept), so retries never push
    /// the daily ones out. Like the daily ones, the pre-operation snapshots kept since the data was last missing or
    /// unreadable (<see cref="KeepSnapshotsBeforeUtc"/>) are all kept and only the later ones rotate. Does nothing
    /// while the store is read-only.
    /// </summary>
    /// <param name="directory">The snapshot folder (not its <c>pre-op</c> sub-folder).</param>
    /// <param name="operation">One of <see cref="SnapshotOperations.All"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot to finish.</param>
    /// <returns>The new snapshot's path, or <c>null</c> when none was written.</returns>
    public Task<string?> PreOpSnapshotAsync(string directory, string operation, CancellationToken cancellationToken)
        => PreOpSnapshotAsync(directory, operation, spare: null, cancellationToken);

    /// <summary>
    /// Lists the store's snapshots in a folder, daily and pre-operation ones, newest first, for <c>GET
    /// /&lt;Route&gt;/Admin/Snapshots</c>. Each one's schema version is read from the start of the file.
    /// </summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <returns>The snapshots.</returns>
    public IReadOnlyList<StoreSnapshotInfo> ListSnapshots(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        EnsureLoaded();
        var kept = _kept;
        return SnapshotRotation.List(directory, StoreName, ".json")
            .Select(s => new StoreSnapshotInfo(s.Name, SnapshotKind.Daily, null, s.Utc, s.Size, PeekVersion(s.Path), kept?.Keeps(s) == true))
            .Concat(SnapshotRotation.ListPreOp(directory, StoreName, ".json")
                .Select(s => new StoreSnapshotInfo(s.Name, SnapshotKind.PreOp, s.Operation, s.Utc, s.Size, PeekVersion(s.Path), kept?.Keeps(s) == true)))
            .OrderByDescending(s => s.Utc)
            .ThenBy(s => s.Kind)
            .ToList();
    }

    /// <summary>
    /// Lists the copies of the store file kept next to it until an admin deletes them (listed on the Diagnostics tab),
    /// newest first: each <c>&lt;file&gt;.v&lt;N&gt;-&lt;yyyyMMddHHmmss&gt;</c> made before an upgrade, the file to put
    /// back to use the previous plugin version again, and each <c>&lt;file&gt;.corrupt-&lt;yyyyMMddHHmmss&gt;</c> moved
    /// aside because it couldn't be read. A name made unique with <c>-1</c>, <c>-2</c>, … counts too; any other file
    /// doesn't, such as the <c>.tmp</c> a copy is written to before it gets its name (a kill can leave one; the next
    /// upgrade deletes it). Only the folder is read, never the files, and the store isn't loaded: a first load that
    /// upgrades adds a copy.
    /// </summary>
    /// <returns>The kept files; none when the folder doesn't exist or can't be listed.</returns>
    public IReadOnlyList<StoreKeptFile> ListKeptFiles()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        var prefix = Path.GetFileName(FilePath) + ".";
        var kept = new List<StoreKeptFile>();
        try
        {
            if (!Directory.Exists(directory))
            {
                return kept;
            }

            foreach (var path in Directory.EnumerateFiles(directory, prefix + "*"))
            {
                var name = Path.GetFileName(path);
                if (name.Length > prefix.Length
                    && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && TryParseKeptName(name.AsSpan(prefix.Length), out var kind, out var version, out var utc))
                {
                    kept.Add(new StoreKeptFile(name, kind, version, utc, new FileInfo(path).Length));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not list the kept copies of {File}", FilePath);
        }

        return kept.OrderByDescending(f => f.Utc).ThenByDescending(f => f.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// <i>Restore snapshot</i> in the running server: replaces the document with a snapshot's,
    /// after a pre-operation snapshot of the current state, whose rotation never deletes the snapshot being restored. An
    /// older snapshot goes through the store's migrations first. When the snapshot is one of those kept since the data was
    /// last missing or unreadable (<see cref="KeepSnapshotsBeforeUtc"/>; <see cref="StoreSnapshotInfo.Kept"/>), the data is
    /// back and the kept snapshots rotate normally again; restoring a snapshot taken since leaves them protected, whatever
    /// its name's date, and the result says so.
    /// The restore is recorded in <see cref="LastRestoreMarkerPath"/>. The plugin then runs its verify sweep, if it has one.
    /// </summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <param name="name">A daily or pre-operation snapshot's file name, as <see cref="ListSnapshots"/> gives it.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was restored, the safety snapshot, and whether earlier snapshots are still kept.</returns>
    /// <exception cref="PluginException">404 no snapshot of this store has that name; 409 the snapshot is from a newer
    /// plugin version or can't be read, or the store is read-only; 503 the write failed.</exception>
    public async Task<SnapshotRestoreResult> RestoreSnapshotAsync(string directory, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ThrowIfReadOnlyForAdmin();

        var (snapshot, version, document) = await ReadSnapshotFileAsync(directory, name, cancellationToken).ConfigureAwait(false);

        // The safety snapshot's rotation spares the snapshot being restored (the oldest pre-operation one, say), so it is
        // still there if the write below fails, and still listed after it succeeds.
        var safety = await PreOpSnapshotAsync(directory, SnapshotOperations.Restore, snapshot.Operation is null ? null : snapshot.Name, cancellationToken).ConfigureAwait(false);
        await UpdateAsync(_ => document, cancellationToken).ConfigureAwait(false);
        RememberRestore(snapshot);

        // Only one of the snapshots kept since the data was missing or unreadable brings the data back; after restoring
        // one taken since (empty, or nearly: "Start empty, then import" undone with its import snapshot), the kept ones
        // stay protected, whatever its name's date says (the clock may have been behind).
        var kept = _kept;
        if (kept is not null && kept.ReleasedBy(snapshot))
        {
            ReleaseKeptSnapshots();
        }
        else if (kept is not null)
        {
            _logger.LogWarning(
                "{Snapshot} isn't one of the {Count} snapshots kept since {File} was found missing or unreadable on {SinceUtc} UTC (it was taken since, or its date can't tell); those are still kept until one of them is restored",
                snapshot.Name,
                kept.Names.Count,
                FilePath,
                kept.SinceUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }

        var keepBefore = KeepSnapshotsBeforeUtc;

        _logger.LogWarning(
            "Restored {File} from the snapshot {Snapshot} (schema {Version}); the state before was saved as {Safety}",
            FilePath,
            snapshot.Name,
            version,
            safety is null ? "(none)" : Path.GetFileName(safety));
        return new SnapshotRestoreResult(snapshot.Name, safety is null ? null : Path.GetFileName(safety), version, version < CurrentVersion ? version : null, keepBefore);
    }

    /// <summary>
    /// Reads a snapshot's document as <see cref="RestoreSnapshotAsync"/> would restore it (an older one upgraded), without
    /// restoring it: for a dry run, or for a plugin's check of what a restore would change.
    /// </summary>
    /// <param name="directory">The snapshot folder.</param>
    /// <param name="name">A daily or pre-operation snapshot's file name, as <see cref="ListSnapshots"/> gives it.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <returns>The document, at the current version.</returns>
    /// <exception cref="PluginException">404 no snapshot of this store has that name; 409 the snapshot is from a newer
    /// plugin version or can't be read; 503 the file couldn't be read.</exception>
    public async Task<TDoc> ReadSnapshotAsync(string directory, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        return (await ReadSnapshotFileAsync(directory, name, cancellationToken).ConfigureAwait(false)).Document;
    }

    /// <summary>
    /// An admin's way out of <see cref="StoreStatus.ReadOnlyDataMissing"/> and
    /// <see cref="StoreStatus.ReadOnlyDataUnreadable"/> (as the SQLite database's <c>RecoverAsync(snapshot | null,
    /// startEmpty)</c>): the snapshot named is restored, or with <paramref name="startEmpty"/> the data starts empty,
    /// the only way it does where data may be lost. Whatever is found where the file and its <c>.bak</c> belong (what
    /// couldn't be read) is moved aside first as <c>&lt;file&gt;.corrupt-&lt;ts&gt;</c>, never overwritten or deleted,
    /// and every snapshot there is, daily and pre-operation, is kept by <see cref="KeepSnapshotsMarkerPath"/> until one
    /// of them is restored (the snapshots that couldn't be used may be the only copies of some data). A restored
    /// snapshot is read and checked first (an older one upgraded), goes through the plugin's <c>afterRestoreAtLoad</c>
    /// step as at load, and is recorded in <see cref="LastRestoreMarkerPath"/>. The store is writable at once
    /// (<see cref="StoreStatus.RestoredFromSnapshot"/> or <see cref="StoreStatus.RecoveredEmpty"/>, with a banner
    /// saying an admin chose it, until the plugin restarts), and <see cref="WaitUntilWritableAsync"/> ends. One Warning
    /// says what was done. The plugin then runs its verify sweep, if it has one.
    /// </summary>
    /// <param name="directory">The snapshot folder (<see cref="SnapshotDirectory"/>).</param>
    /// <param name="snapshotName">A daily or pre-operation snapshot's file name, as <see cref="ListSnapshots"/> gives it; <c>null</c> to start empty.</param>
    /// <param name="startEmpty">Start empty instead of restoring a snapshot.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>What was restored, or that the data started empty, and what was moved aside.</returns>
    /// <exception cref="PluginException">400 neither or both of a snapshot and <paramref name="startEmpty"/>; 404 no snapshot
    /// of this store has that name; 409 the data isn't missing or unreadable (<see cref="StoreMessages.NothingToRecover"/>),
    /// a readable data file or <c>.bak</c> is in place now (put back while the server runs:
    /// <see cref="StoreMessages.ReadableFileInPlace"/>; a restart uses it), or the snapshot is from a newer plugin version,
    /// can't be read, holds no data, or the server may not read it; 503 the snapshot couldn't be read just now, or the write
    /// failed, and nothing was changed.</exception>
    public async Task<StoreRecoveryResult> RecoverAsync(string directory, string? snapshotName, bool startEmpty, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        var name = string.IsNullOrWhiteSpace(snapshotName) ? null : snapshotName.Trim();
        if (startEmpty == (name is not null))
        {
            throw PluginException.Invalid(startEmpty ? "Choose a snapshot to restore or start empty, not both." : "Choose a snapshot to restore, or start empty.");
        }

        ThrowUnlessRecoverable(EnsureLoaded());
        WarnUnlessSnapshotDirectory(directory);
        StoreSnapshot? snapshot = null;
        var version = 0;
        var document = startEmpty ? CreateEmpty() : null;
        if (name is not null)
        {
            snapshot = FindSnapshot(directory, name);
            (version, document) = ReadForRecovery(snapshot);
        }

        StoreRecoveryResult result;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_loadLock)
            {
                // The state can't change while the writer lock is held, but it is only ever read under the load lock here.
                var state = _state ?? throw new InvalidOperationException("The store isn't loaded.");
                ThrowUnlessRecoverable(state);

                // The state says what was found at load; an admin may have put a readable file back since, which this
                // would move aside as unreadable.
                ThrowIfReadableFileInPlace();
                if (snapshot is not null)
                {
                    document = AfterRestoreAtLoad(document!, snapshot);
                }

                var moved = CommitRecovery(document!).Select(p => Path.GetFileName(p)).ToList();
                if (snapshot is not null)
                {
                    RememberRestore(snapshot);
                }

                var reason = state.Reason ?? "The data was missing";
                var keepBefore = KeepEverySnapshot(_timeProvider.GetUtcNow().UtcDateTime, reason, snapshot is null ? StoreStatus.RecoveredEmpty : StoreStatus.RestoredFromSnapshot, _kept);
                var kept = moved.Count == 0 ? string.Empty : " What was found in its place is kept as " + string.Join(" and ", moved) + ".";
                var marker = Path.GetFileName(KeepSnapshotsMarkerPath);
                var keptNote = keepBefore is null ? string.Empty : " Every snapshot is kept until you restore one of them (or delete " + marker + ").";
                if (snapshot is null)
                {
                    _logger.LogWarning(
                        "An admin started {File} empty: {Reason}. Moved aside: {Moved}. {Kept}",
                        FilePath,
                        reason,
                        moved.Count == 0 ? "nothing" : string.Join(", ", moved),
                        keepBefore is null ? "There is no snapshot to keep." : "Every snapshot in " + SnapshotDirectory + " is kept until one of them is restored or " + marker + " is deleted.");
                    _state = new LoadedState(
                        document!,
                        StoreStatus.RecoveredEmpty,
                        null,
                        null,
                        reason + "; an admin chose to start empty." + kept + keptNote);
                }
                else
                {
                    _logger.LogWarning(
                        "An admin restored {File} from the snapshot {Snapshot} taken {SnapshotUtc} UTC (schema {Version}): {Reason}. Moved aside: {Moved}. Every snapshot in {Directory} is kept until one of them is restored or {Marker} is deleted",
                        FilePath,
                        snapshot.Name,
                        snapshot.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        version,
                        reason,
                        moved.Count == 0 ? "nothing" : string.Join(", ", moved),
                        SnapshotDirectory,
                        marker);
                    _state = new LoadedState(
                        document!,
                        StoreStatus.RestoredFromSnapshot,
                        version,
                        version < CurrentVersion ? version : null,
                        string.Create(CultureInfo.InvariantCulture, $"{reason}; an admin restored the data from the snapshot {snapshot.Name} taken {snapshot.Utc:yyyy-MM-dd HH:mm} UTC, so changes made after it are missing.")
                            + kept + keptNote);
                }

                result = new StoreRecoveryResult(snapshot?.Name, snapshot?.Utc, snapshot is null ? null : version, snapshot is not null && version < CurrentVersion ? version : null, moved, keepBefore);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        _writable.TrySetResult();
        return result;
    }

    /// <summary>
    /// Gets the data for an export's <c>Data</c> with the schema version it is at, for <c>ExportEnvelope.Create</c>.
    /// Normally the current document at <see cref="CurrentVersion"/>. While the file comes from a newer plugin version
    /// (<see cref="StoreStatus.ReadOnlyNewerVersion"/>), the file's own JSON as it was loaded, at its version, so
    /// nothing this version can't read is lost and that version can import it (export still works).
    /// </summary>
    /// <returns>The data and its schema version.</returns>
    /// <exception cref="PluginException">503: the file can't be read, or its upgrade failed
    /// (<see cref="StoreStatus.ReadOnlyMigrationFailed"/>: the store holds nothing faithful to export; the unchanged file
    /// and its <c>.v&lt;N&gt;</c> copy are the backup), or it is missing or unreadable and no snapshot could be restored
    /// (<see cref="StoreStatus.ReadOnlyDataMissing"/>, <see cref="StoreStatus.ReadOnlyDataUnreadable"/>: the snapshots, and
    /// the unreadable file, are the backup).</exception>
    public StoreExportData ExportData()
    {
        var state = EnsureLoaded();
        switch (state.Status)
        {
            case StoreStatus.ReadOnlyMigrationFailed:
                throw new PluginException(PluginErrorKind.Unavailable, StoreMessages.ExportMigrationFailed);
            case StoreStatus.ReadOnlyDataMissing:
                throw new PluginException(PluginErrorKind.Unavailable, StoreMessages.ExportDataMissing);
            case StoreStatus.ReadOnlyDataUnreadable:
                throw new PluginException(PluginErrorKind.Unavailable, StoreMessages.ExportDataUnreadable);
            case StoreStatus.ReadOnlyNewerVersion when state.NewerFile is { } newer && state.LoadedVersion is { } version:
                try
                {
                    return new StoreExportData(version, ParseObject(JsonStoreFormat.SkipByteOrderMark(newer)));
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Could not export {File}: the file of a newer plugin version isn't a JSON object", FilePath);
                    throw new PluginException(PluginErrorKind.Unavailable, StoreMessages.ExportNewerUnreadable, ex);
                }

            default:
                return new StoreExportData(CurrentVersion, (JsonObject)JsonSerializer.SerializeToNode(state.Document, SerializerOptions)!);
        }
    }

    /// <summary>
    /// Reads data of this store's shape written at a schema version, such as an export envelope's <c>Data</c>: an older
    /// version goes through the store's migrations, a newer one is refused. The node isn't changed.
    /// </summary>
    /// <param name="data">The document as JSON.</param>
    /// <param name="schemaVersion">The version it was written at (the envelope's <c>SchemaVersion</c>).</param>
    /// <param name="source">What the data is, for messages ("The export").</param>
    /// <returns>The document at the current version.</returns>
    /// <exception cref="PluginException">409 a newer schema version; 400 the data can't be read at its version.</exception>
    public TDoc ReadData(JsonObject data, int schemaVersion, string source = "The export")
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);

        if (data[JsonStoreFormat.VersionProperty] is JsonValue declared
            && (!declared.TryGetValue<int>(out var dataVersion) || dataVersion != schemaVersion))
        {
            throw PluginException.Invalid(string.Create(CultureInfo.InvariantCulture, $"{source}'s data says it is at another schema version than {schemaVersion}."));
        }

        return Upgrade((JsonObject)data.DeepClone(), schemaVersion, PluginErrorKind.Invalid, source);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _writeLock.Dispose();
        _snapshotLock.Dispose();
    }

    // The kit's admin data operations (restore, import, remap, apply policy) answer 409 while the store is read-only,
    // as the kit's Remap does; ordinary writes keep the 503 of ThrowIfReadOnly.
    internal void ThrowIfReadOnlyForAdmin()
    {
        var state = EnsureLoaded();
        if (state.IsReadOnly)
        {
            throw PluginException.Conflict(state.Message ?? ReadOnlyMessage(state.Status) ?? StoreMessages.MigrationFailed);
        }
    }

    private static string? ReadOnlyMessage(StoreStatus status) => status switch
    {
        StoreStatus.ReadOnlyNewerVersion => StoreMessages.NewerVersion,
        StoreStatus.ReadOnlyMigrationFailed => StoreMessages.MigrationFailed,
        StoreStatus.ReadOnlyDataMissing => StoreMessages.DataMissing,
        StoreStatus.ReadOnlyDataUnreadable => StoreMessages.DataUnreadable,
        _ => null
    };

    private static void ThrowUnlessRecoverable(LoadedState state)
    {
        if (!state.IsRecoverable)
        {
            throw PluginException.Conflict(StoreMessages.NothingToRecover);
        }
    }

    // A user who isn't an admin can't open the plugin's settings page or install a version: the 503 tells them to ask
    // the admin instead (PluginExceptionFilterAttribute picks the sentence).
    private static void ThrowIfReadOnly(LoadedState state)
    {
        if (state.IsReadOnly)
        {
            throw new PluginException(PluginErrorKind.Unavailable, ReadOnlyMessage(state.Status) ?? StoreMessages.MigrationFailed)
            {
                NonAdminMessage = StoreMessages.ReadOnlyForUsers(state.Status)
            };
        }
    }

    private static string UniquePath(string path)
    {
        var candidate = path;
        for (var n = 1; File.Exists(candidate); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{path}-{n}");
        }

        return candidate;
    }

    // What follows "<file>." in a kept copy's name: "v<N>-<yyyyMMddHHmmss>" or "corrupt-<yyyyMMddHHmmss>", each maybe with
    // UniquePath's "-<n>".
    private static bool TryParseKeptName(ReadOnlySpan<char> rest, out StoreKeptFileKind kind, out int? version, out DateTime utc)
    {
        const string Corrupt = "corrupt-";
        kind = StoreKeptFileKind.PreUpgradeCopy;
        version = null;
        utc = default;
        if (rest.StartsWith(Corrupt, StringComparison.Ordinal))
        {
            kind = StoreKeptFileKind.Corrupt;
            rest = rest[Corrupt.Length..];
        }
        else if (rest.Length > 1 && rest[0] == 'v')
        {
            var dash = rest.IndexOf('-');
            if (dash < 2 || !int.TryParse(rest[1..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                return false;
            }

            version = parsed;
            rest = rest[(dash + 1)..];
        }
        else
        {
            return false;
        }

        if (rest.Length < FileTimestampFormat.Length
            || !DateTime.TryParseExact(rest[..FileTimestampFormat.Length], FileTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc))
        {
            return false;
        }

        var suffix = rest[FileTimestampFormat.Length..];
        return suffix.IsEmpty
            || (suffix.Length > 1 && suffix[0] == '-' && int.TryParse(suffix[1..], NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static JsonObject ParseObject(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var root = JsonNode.Parse(utf8) as JsonObject ?? throw new JsonException("The file is not a JSON object.");

            // JsonObject builds its property table lazily and refuses a name that appears twice; do it here.
            _ = root.Count;
            return root;
        }
        catch (ArgumentException ex)
        {
            throw new JsonException(ex.Message, ex);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the temporary file {Path}", path);
        }
    }

    // Reads the Version at the start of a snapshot, or null when it isn't there (a store writes it first) or can't be read.
    private static int? PeekVersion(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, VersionPeekBytes)];
            stream.ReadExactly(buffer);
            var reader = new Utf8JsonReader(JsonStoreFormat.SkipByteOrderMark(buffer), isFinalBlock: buffer.Length == stream.Length, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isVersion = reader.ValueTextEquals(JsonStoreFormat.VersionProperty);
                if (!reader.Read())
                {
                    return null;
                }

                if (isVersion)
                {
                    return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var version) && version >= 1 ? version : null;
                }

                if (!reader.TrySkip())
                {
                    return null;
                }
            }

            // No Version property in a complete file: version 1.
            return reader.TokenType == JsonTokenType.EndObject ? 1 : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or EndOfStreamException)
        {
            return null;
        }
    }

    // A document written at an older version goes through the migrations; a newer one is refused. Errors are PluginExceptions.
    private TDoc Upgrade(JsonObject root, int version, PluginErrorKind unreadable, string source)
    {
        if (version > CurrentVersion)
        {
            throw PluginException.Conflict(string.Create(
                CultureInfo.InvariantCulture,
                $"{source} was written by a newer plugin version (schema {version}; this version reads up to {CurrentVersion}). Install that version to use it."));
        }

        var step = version;
        try
        {
            root[JsonStoreFormat.VersionProperty] = version;
            for (; step < CurrentVersion; step++)
            {
                _migrations[step].Apply(root);
                root[JsonStoreFormat.VersionProperty] = step + 1;
            }

            var document = root.Deserialize<TDoc>(SerializerOptions) ?? throw new JsonException("It holds no document.");
            if (document.Version != CurrentVersion)
            {
                throw new JsonException(string.Create(CultureInfo.InvariantCulture, $"It reads as version {document.Version}."));
            }

            return document;
        }
        catch (Exception ex) when (ex is not PluginException and not OutOfMemoryException)
        {
            var what = step < CurrentVersion
                ? string.Create(CultureInfo.InvariantCulture, $"the upgrade from schema {step} to {step + 1} failed")
                : string.Create(CultureInfo.InvariantCulture, $"it can't be read at schema {CurrentVersion}");
            _logger.LogWarning(ex, "{Source} for {File} couldn't be read: {What}", source, FilePath, what);
            throw new PluginException(unreadable, source + " couldn't be read: " + what + " (" + ex.Message + ")", ex);
        }
    }

    // A pre-operation snapshot whose rotation never deletes the snapshot named spare (the one being restored).
    private async Task<string?> PreOpSnapshotAsync(string directory, string operation, string? spare, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        SnapshotOperations.Require(operation);

        var state = EnsureLoaded();
        if (state.IsReadOnly)
        {
            _logger.LogDebug("Skipped the {Operation} snapshot of {File}: the store is read-only ({Status})", operation, FilePath, state.Status);
            return null;
        }

        WarnUnlessSnapshotDirectory(directory);
        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var utc = _timeProvider.GetUtcNow().UtcDateTime;
            var path = await WriteSnapshotFileAsync(
                SnapshotRotation.PreOpDirectory(directory),
                SnapshotRotation.NewPreOpPath(directory, StoreName, operation, ".json", utc)).ConfigureAwait(false);
            SnapshotRotation.RotatePreOp(directory, StoreName, ".json", SnapshotOperations.ToKeep, _logger, spare, nowUtc: utc, keepNames: WarnOfKeptSnapshots(directory)?.Names);
            _logger.LogInformation("Wrote the snapshot {Snapshot} of {File} before {Operation}", Path.GetFileName(path), FilePath, operation);
            return path;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    // The kept snapshots for a rotation, daily or pre-operation, with one Warning per process (until a restore releases
    // them) while there are any, so kept snapshots never pile up unmentioned.
    private KeptSnapshots<StoreStatus>? WarnOfKeptSnapshots(string directory)
    {
        EnsureLoaded();
        var kept = _kept;
        if (kept is not null && Interlocked.Exchange(ref _keptSnapshotsLogged, 1) == 0)
        {
            _logger.LogWarning(
                "{File} started empty, or was restored from a snapshot or its backup, after its data was missing or unreadable; the earlier snapshots in {Directory}, daily and pre-operation (the {Count} there were on {SinceUtc} UTC), are kept until one of them is restored or {Marker} is deleted",
                FilePath,
                directory,
                kept.Names.Count,
                kept.SinceUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Path.GetFileName(KeepSnapshotsMarkerPath));
        }

        return kept;
    }

    // At the daily snapshot, just after it was written (so the folder is there and lists): a marker none of whose
    // snapshots is in the folder any more (deleted by hand, or rotated out by an earlier version after a downgrade)
    // keeps nothing, so it is released, with an Information, rather than kept for good with its Warning and its
    // Diagnostics line (since Bookmarks 1.2.1; until then it stayed until a restore of one of them or an admin deleted
    // it, which the SQLite database still follows). A kept snapshot put back after that rotates as any.
    private void ReleaseIfNoneIsLeft(string directory)
    {
        EnsureLoaded();
        var kept = _kept;
        if (kept is null || !IsSnapshotDirectory(directory))
        {
            return;
        }

        var there = SnapshotRotation.List(directory, StoreName, ".json").Concat(SnapshotRotation.ListPreOp(directory, StoreName, ".json"));
        if (there.Any(kept.Keeps))
        {
            return;
        }

        lock (_loadLock)
        {
            if (!ReferenceEquals(_kept, kept))
            {
                return;
            }

            _logger.LogInformation(
                "None of the {Count} snapshots of {File} kept since {SinceUtc} UTC is in {Directory} any more; {Marker} is deleted and the snapshots rotate as usual",
                kept.Names.Count,
                FilePath,
                kept.SinceUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                directory,
                Path.GetFileName(KeepSnapshotsMarkerPath));
            ReleaseKeptSnapshots(restored: false);
        }
    }

    // Whether a folder is SnapshotDirectory, the one the kept snapshots' names are of.
    private bool IsSnapshotDirectory(string directory)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)),
            Path.TrimEndingDirectorySeparator(SnapshotDirectory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The restore at load looks only in SnapshotDirectory: say once if a plugin writes its snapshots somewhere else.
    private void WarnUnlessSnapshotDirectory(string directory)
    {
        if (!IsSnapshotDirectory(directory)
            && Interlocked.Exchange(ref _otherSnapshotDirectoryLogged, 1) == 0)
        {
            _logger.LogWarning(
                "Snapshots of {File} are written to {Directory}, but the store restores from {SnapshotDirectory} when its file is missing; give the store the folder its snapshots go to",
                FilePath,
                directory,
                SnapshotDirectory);
        }
    }

    // Writes the current document to path through a .tmp file; directory is created first.
    private async Task<string> WriteSnapshotFileAsync(string directory, string path)
    {
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try
        {
            var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferSize, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, EnsureLoaded().Document, SerializerOptions, CancellationToken.None).ConfigureAwait(false);
#pragma warning disable CA1849 // FlushAsync can't force the write through to disk.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }

            File.Move(temp, path);
            return path;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    // Runs under _loadLock, once a load succeeded: reads the .keep-snapshots marker, and writes it when the store started
    // empty now, was restored from a snapshot, or from a .bak older than a snapshot holding other data (KeepEverySnapshot).
    // moved: this load (or an attempt of it that failed) moved a file aside. A new incident records the snapshots there are
    // then, the earlier kept ones among them; a store still empty after a restart with nothing moved keeps the first record.
    private void UpdateKeepSnapshotsMarker(LoadedState state, bool moved)
    {
        var existing = ReadKeepSnapshotsMarker();
        var incident = state.Status == StoreStatus.RestoredFromSnapshot
            || (state.Status == StoreStatus.RecoveredEmpty && (existing is null || moved))
            || state.KeepsSnapshots;
        if (!incident)
        {
            _kept = existing;
            return;
        }

        KeepEverySnapshot(_timeProvider.GetUtcNow().UtcDateTime, state.Message, state.Status, existing);
    }

    // Runs under _loadLock: every snapshot there is now, daily and pre-operation, is kept from now on, by name (a
    // snapshot taken after this, while the clock is behind, is dated before any time that covers the ones there are, so
    // a time can't tell them apart). The marker also holds the later of now and the newest snapshot's time plus a
    // second, never moved back, which Bookmarks 1.1 reads as "every snapshot dated before it". With no snapshot at all
    // there is nothing to keep, and no marker. Returns that time, or null.
    private DateTime? KeepEverySnapshot(DateTime now, string? reason, StoreStatus incident, KeptSnapshots<StoreStatus>? existing)
    {
        var snapshots = SnapshotRotation.List(SnapshotDirectory, StoreName, ".json").Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, ".json")).ToList();
        var kept = KeepSnapshotsMarker.Record(snapshots, now, incident, existing);
        if (kept is null)
        {
            ReleaseKeptSnapshots(restored: false);
            return null;
        }

        _kept = kept;
        Interlocked.Exchange(ref _keptSnapshotsLogged, 0);
        if (KeepSnapshotsMarker.Write(KeepSnapshotsMarkerPath, kept, reason, SerializerOptions, _logger, FilePath))
        {
            _logger.LogInformation("Recorded in {Marker} that the {Count} snapshots of {File} there are now are to be kept", KeepSnapshotsMarkerPath, kept.Names.Count, FilePath);
        }

        return kept.BeforeUtc;
    }

    // The marker's kept snapshots, or null when there is none (KeepSnapshotsMarker.Read, shared with the SQLite
    // database): the names; Bookmarks 1.1's {KeepSnapshotsBeforeUtc, Reason} and 1.2's first {KeepSnapshotsBeforeUtc,
    // SinceUtc, Reason} recorded by name once, only one of them dated before the incident releasing them; a marker that
    // can't be read left as it is and still protecting, by its own time, in this process.
    private KeptSnapshots<StoreStatus>? ReadKeepSnapshotsMarker()
        => KeepSnapshotsMarker.Read<StoreStatus>(KeepSnapshotsMarkerPath, FilePath, NamesDatedBefore, SerializerOptions, _logger, _timeProvider);

    // The names of the snapshots there are, daily and pre-operation, dated before a time.
    private HashSet<string> NamesDatedBefore(DateTime utc)
        => SnapshotRotation.List(SnapshotDirectory, StoreName, ".json")
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, ".json"))
            .Where(s => SnapshotRotation.IsBefore(s, utc))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Records the snapshot just restored (by an admin, or at load), so a later restore at load tries the snapshots taken
    // since then first, then this one, before the rest (RecoveryCandidates).
    private void RememberRestore(StoreSnapshot snapshot)
    {
        try
        {
            var marker = new JsonObject
            {
                ["Snapshot"] = snapshot.Name,
                ["RestoredUtc"] = _timeProvider.GetUtcNow().UtcDateTime
            };
            File.WriteAllText(LastRestoreMarkerPath, marker.ToJsonString(SerializerOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write {Marker}", LastRestoreMarkerPath);
        }
    }

    private (string Name, DateTime Utc)? ReadLastRestore()
    {
        try
        {
            if (File.Exists(LastRestoreMarkerPath)
                && JsonNode.Parse(File.ReadAllBytes(LastRestoreMarkerPath)) is JsonObject marker
                && marker["Snapshot"] is JsonValue name
                && name.TryGetValue<string>(out var snapshot)
                && !string.IsNullOrWhiteSpace(snapshot)
                && marker["RestoredUtc"] is JsonValue value
                && value.TryGetValue<DateTime>(out var utc))
            {
                return (snapshot, utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not read {Marker}; the snapshots are tried newest first", LastRestoreMarkerPath);
        }

        return null;
    }

    // The snapshots a restore at load tries, daily and pre-operation ones, newest first; but after a restore, the ones
    // taken since come first, then the restored one, then the rest: the "restore" pre-operation snapshot taken just
    // before a restore holds the state that was replaced (the SQLite database's order). Snapshots whose names are dated
    // more than SnapshotRotation.FutureTolerance after now come last, in the same order (future): the clock was wrong
    // when they were taken, or is wrong now, so their names say nothing about how new their data is.
    private List<StoreSnapshot> RecoveryCandidates(DateTime nowUtc, out List<StoreSnapshot> future)
    {
        var all = SnapshotRotation.List(SnapshotDirectory, StoreName, ".json")
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, ".json"))
            .OrderByDescending(s => s.Utc)
            .ThenByDescending(s => s.Sequence)
            .ToList();
        if (all.Count > 0 && ReadLastRestore() is { } last)
        {
            var second = new DateTime(last.Utc.Ticks - (last.Utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            all = all.Where(s => s.Utc > second)
                .Concat(all.Where(s => string.Equals(s.Name, last.Name, StringComparison.OrdinalIgnoreCase)))
                .Concat(all)
                .Distinct()
                .ToList();
        }

        future = all.Where(s => SnapshotRotation.IsFutureDated(s, nowUtc)).ToList();
        return future.Count == 0 ? all : all.Where(s => !SnapshotRotation.IsFutureDated(s, nowUtc)).Concat(future).ToList();
    }

    // Runs under _loadLock, when neither the file nor its .bak gave the data (as for the SQLite database): deleted by
    // hand, a manual restore half done, a backup of snapshots/ alone put back, or a file that can't be read. Reads
    // only. reason says what happened. The first snapshot that reads and holds data (RecoveryCandidates' order) is
    // chosen, its document upgraded to the current version. Snapshots of the empty document, or with no bytes or white
    // space only, hold no data, so a fresh install that took snapshots before its first write still starts empty. Those
    // that may hold data but can't be used (a newer schema, unreadable, no permission to read them) are Unusable. A
    // snapshot that can't be read just now (in use) throws SnapshotUnavailableException: nothing is decided, Load
    // answers 503, and the next use tries again.
    private SnapshotChoice ChooseSnapshot(string reason)
    {
        var candidates = RecoveryCandidates(_timeProvider.GetUtcNow().UtcDateTime, out var future);
        var passed = new List<string>();
        var unusable = new List<string>();
        var noData = new List<string>();
        if (candidates.Count == 0)
        {
            return new SnapshotChoice(null, null, 0, passed, unusable, noData, future, 0);
        }

        var empty = JsonSerializer.SerializeToNode(CreateEmpty(), SerializerOptions);
        foreach (var candidate in candidates)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(candidate.Path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted since it was listed.
                continue;
            }
            catch (UnauthorizedAccessException ex)
            {
                // No permission to read it: unlike a lock, trying again at every use won't change that, so it is passed
                // over like a snapshot that can't be read, and named in the banner.
                _logger.LogWarning(ex, "Could not read the snapshot {Snapshot} of {File}: no permission to read it, so it is passed over", candidate.Path, FilePath);
                unusable.Add(candidate.Name + " (no permission to read it)");
                passed.Add(unusable[^1]);
                continue;
            }
            catch (IOException ex)
            {
                // In use just now (a backup or another program has it open): never a reason to fall back to an older
                // snapshot, or to nothing.
                throw new SnapshotUnavailableException(candidate, reason, ex);
            }

            // No bytes, or white space only: as good as no snapshot.
            if (IsBlank(bytes))
            {
                noData.Add(candidate.Name);
                passed.Add(candidate.Name + " (holds no data)");
                continue;
            }

            var utf8 = JsonStoreFormat.SkipByteOrderMark(bytes);
            int version;
            TDoc document;
            try
            {
                version = JsonStoreFormat.ReadVersion(utf8) ?? 1;
                if (version > CurrentVersion)
                {
                    unusable.Add(string.Create(CultureInfo.InvariantCulture, $"{candidate.Name} (schema {version}, from a newer plugin version)"));
                    passed.Add(unusable[^1]);
                    continue;
                }

                document = Upgrade(ParseObject(utf8), version, PluginErrorKind.Conflict, "The snapshot " + candidate.Name);
            }
            catch (Exception ex) when (ex is JsonException or PluginException)
            {
                _logger.LogDebug(ex, "The snapshot {Snapshot} of {File} can't be restored", candidate.Name, FilePath);
                unusable.Add(candidate.Name + " (can't be read)");
                passed.Add(unusable[^1]);
                continue;
            }

            if (JsonNode.DeepEquals(JsonSerializer.SerializeToNode(document, SerializerOptions), empty))
            {
                noData.Add(candidate.Name);
                passed.Add(candidate.Name + " (holds no data)");
                continue;
            }

            return new SnapshotChoice(candidate, document, version, passed, unusable, noData, future, candidates.Count);
        }

        return new SnapshotChoice(null, null, 0, passed, unusable, noData, future, candidates.Count);
    }

    // ReadOnlyDataMissing: the file was missing or empty with no usable .bak, and snapshots that may hold the data exist but
    // none can be used. Nothing is written, and no snapshot is touched.
    private LoadedState DataMissing(string reason, SnapshotChoice choice)
    {
        var names = string.Join(", ", choice.Unusable);
        _logger.LogError(
            "{File}: {Reason}. Its snapshots may hold the data, but none of them could be restored ({Snapshots}); nothing was written and the data is read-only until {WayOut}",
            FilePath,
            reason,
            names,
            _recoveryPage ? "an admin restores a snapshot or starts empty, or a readable copy is put back and the server restarted" : "a readable copy is put back with the server stopped");
        return new LoadedState(
            CreateEmpty(),
            StoreStatus.ReadOnlyDataMissing,
            null,
            null,
            reason + ". Its snapshots may hold the data, but none of them could be restored: " + names + ". Nothing can be changed, and the snapshots are left as they are." + FutureNote(choice.Future)
                + (_recoveryPage ? " To go on, restore a snapshot that can be read (put one back in " + SnapshotDirectory + " first if you have one), or start empty, on the plugin's settings page; or stop" : " To go on, stop")
                + " the server, copy the snapshot you want over " + Path.GetFileName(FilePath)
                + " (or put back another readable copy of it), or install the plugin version that wrote them, and start the server again.",
            reason: reason);
    }

    // ReadOnlyDataUnreadable: the file, or the .bak beside a missing or empty file, holds something unreadable, and no
    // readable .bak or snapshot that holds data could take its place. The data may be in what couldn't be read, so the store
    // never starts empty on its own (the SQLite database's rule): what couldn't be read is left in place, nothing is
    // written, and no snapshot is touched, so the next start finds the same (it would find the file missing, and start
    // empty, had it been moved aside).
    private LoadedState DataUnreadable(string reason, Slot file, Slot backup, SnapshotChoice choice)
    {
        var snapshots = choice.Count == 0 ? "There is no snapshot of it."
            : choice.Unusable.Count > 0 ? "Its snapshots may hold the data, but none of them could be restored: " + string.Join(", ", choice.Unusable) + "."
            : "None of its snapshots holds any data: " + string.Join(", ", choice.NoData) + ".";
        var left = file.InPlace && backup.InPlace ? " the data file and its backup are left as they are"
            : file.InPlace ? " the data file is left as it is"
            : " its backup is left as it is";
        var name = Path.GetFileName(FilePath);
        var wayOut = !_recoveryPage
            ? " To go on, stop the server, put a readable copy of " + name + " back (or move what couldn't be read aside, to start empty), and start the server again."
            : (choice.Count == 0 ? " To go on, start empty on the plugin's settings page" : " To go on, restore one of the snapshots, or start empty, on the plugin's settings page")
                + " (what couldn't be read is then kept as " + name + ".corrupt-<time>); or stop the server, put a readable copy of " + name + " back, and start the server again.";
        _logger.LogError(
            file.Error ?? backup.Error,
            "{File}: {Reason}. {Snapshots} Nothing was changed, and the data is read-only until {WayOut}",
            FilePath,
            reason,
            snapshots,
            !_recoveryPage ? "a readable copy is put back, or what couldn't be read is moved aside, with the server stopped"
                : choice.Count == 0 ? "an admin starts empty, or a readable copy is put back and the server restarted"
                : "an admin restores a snapshot or starts empty, or a readable copy is put back and the server restarted");
        return new LoadedState(
            CreateEmpty(),
            StoreStatus.ReadOnlyDataUnreadable,
            null,
            null,
            reason + ". " + snapshots + " Nothing was changed:" + left + ", and the data is read-only." + FutureNote(choice.Future) + wayOut,
            reason: reason);
    }

    // " Dated later than now (...), so tried last: a, b." for a read-only banner, or nothing.
    private static string FutureNote(List<StoreSnapshot> future)
        => future.Count == 0 ? string.Empty : " Dated later than now (the clock is wrong now, or was when they were taken), so tried last: " + string.Join(", ", future.Select(s => s.Name)) + ".";

    // The same for a restore's banner: if the clock is wrong now (it moved back), they may be newer than the one restored.
    private static string RestoredFutureNote(List<StoreSnapshot> future)
        => future.Count == 0 ? string.Empty
            : " Dated later than now, so tried last: " + string.Join(", ", future.Select(s => s.Name)) + ". If the clock is wrong now rather than when they were taken, they may hold newer data than the snapshot restored: they are kept too, so restore the one you want on the plugin's settings page.";

    private LoadedState RestoredAtLoad(SnapshotChoice choice, string reason)
    {
        var snapshot = choice.Snapshot!;
        var version = choice.Version;
        var document = AfterRestoreAtLoad(choice.Document!, snapshot);
        try
        {
            // Saved at once, as an upgrade is. If that fails, the restored data is still served, the next write saves it,
            // and a restart before then restores it again.
            Commit(document);
        }
        catch (PluginException)
        {
            // Commit logged the failure.
        }

        RememberRestore(snapshot);
        _logger.LogWarning(
            "Restored {File} from the snapshot {Snapshot} taken {SnapshotUtc} UTC (schema {Version}): {Reason}. Changes made after the snapshot are missing; the earlier snapshots, daily and pre-operation, are kept until one of them is restored or {Marker} is deleted",
            FilePath,
            snapshot.Name,
            snapshot.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            version,
            reason,
            Path.GetFileName(KeepSnapshotsMarkerPath));
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"{reason}, so the data was restored from the snapshot {snapshot.Name} taken {snapshot.Utc:yyyy-MM-dd HH:mm} UTC; changes made after it are missing. The earlier snapshots, daily and pre-operation, are kept until you restore one of them (or delete {Path.GetFileName(KeepSnapshotsMarkerPath)}).");
        if (choice.Passed.Count > 0)
        {
            message += " Passed over first: " + string.Join(", ", choice.Passed) + ".";
        }

        return new LoadedState(document, StoreStatus.RestoredFromSnapshot, version, version < CurrentVersion ? version : null, message + RestoredFutureNote(choice.Future));
    }

    // An admin's chosen snapshot (RecoverAsync), read and checked: 404 gone, 409 unusable, 503 in use just now.
    private (int Version, TDoc Document) ReadForRecovery(StoreSnapshot snapshot)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(snapshot.Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw PluginException.NotFound("No snapshot of this data has that name.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not read the snapshot {Snapshot} to restore {File}: no permission to read it", snapshot.Path, FilePath);
            throw PluginException.Conflict("The snapshot " + snapshot.Name + " can't be restored: the server has no permission to read it.");
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read the snapshot {Snapshot} to restore {File} just now", snapshot.Path, FilePath);
            throw new PluginException(PluginErrorKind.Unavailable, "The snapshot " + snapshot.Name + " couldn't be read just now; try again in a moment. The server log has the details.", ex);
        }

        if (IsBlank(bytes))
        {
            throw PluginException.Conflict("The snapshot " + snapshot.Name + " holds no data. To start over, start empty instead.");
        }

        try
        {
            var utf8 = JsonStoreFormat.SkipByteOrderMark(bytes);
            var version = JsonStoreFormat.ReadVersion(utf8) ?? 1;
            return (version, Upgrade(ParseObject(utf8), version, PluginErrorKind.Conflict, "The snapshot " + snapshot.Name));
        }
        catch (JsonException ex)
        {
            throw PluginException.Conflict("The snapshot " + snapshot.Name + " couldn't be read: " + ex.Message);
        }
    }

    // A snapshot read and upgraded to the current version, as a restore takes it.
    private async Task<(StoreSnapshot Snapshot, int Version, TDoc Document)> ReadSnapshotFileAsync(string directory, string name, CancellationToken cancellationToken)
    {
        var snapshot = FindSnapshot(directory, name);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(snapshot.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read the snapshot {Snapshot}", snapshot.Path);
            throw new PluginException(PluginErrorKind.Unavailable, "The snapshot couldn't be read. The server log has the details.", ex);
        }

        try
        {
            var utf8 = JsonStoreFormat.SkipByteOrderMark(bytes);
            var version = JsonStoreFormat.ReadVersion(utf8) ?? 1;
            return (snapshot, version, Upgrade(ParseObject(utf8), version, PluginErrorKind.Conflict, "The snapshot " + snapshot.Name));
        }
        catch (JsonException ex)
        {
            throw PluginException.Conflict("The snapshot " + snapshot.Name + " couldn't be read: " + ex.Message);
        }
    }

    // A daily or pre-operation snapshot of this store by its file name (any case), or 404; anything else, a path included,
    // isn't one.
    private StoreSnapshot FindSnapshot(string directory, string? name)
        => SnapshotRotation.List(directory, StoreName, ".json")
            .Concat(SnapshotRotation.ListPreOp(directory, StoreName, ".json"))
            .FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw PluginException.NotFound("No snapshot of this data has that name.");

    // Runs under _loadLock, for an admin's recovery, before anything is moved: the status says what was found at load,
    // so the file and its .bak are read again. One that reads now (a readable copy put back while the server runs, at
    // any schema version) is refused with 409: the recovery would move it aside as unreadable and start empty or
    // restore an older snapshot over it. A restart loads it the usual way, the .bak and version rules included, which
    // loading it here would have to repeat. One that can't be opened just now isn't refused: moving it fails the same
    // way, and the recovery answers 503 with nothing changed.
    private void ThrowIfReadableFileInPlace()
    {
        foreach (var path in new[] { FilePath, _backupPath })
        {
            byte[] bytes;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not read {Path} again before an admin's recovery of {File}", path, FilePath);
                continue;
            }

            if (IsBlank(bytes) || Parse(bytes).Error is not null)
            {
                continue;
            }

            _logger.LogWarning(
                "An admin's recovery of {File} was refused: {Path} can be read now (put back while the server runs?), so nothing was moved; restart the server to use it",
                FilePath,
                path);
            throw PluginException.Conflict(StoreMessages.ReadableFileInPlace(Path.GetFileName(path)));
        }
    }

    // Runs under _loadLock, for an admin's recovery: the document is written to .tmp and flushed, whatever is where the file
    // and its .bak belong is moved aside as <file>.corrupt-<ts> (never overwritten), and only then is the .tmp put in place.
    // A failure puts back what was moved and throws the 503 of a failed write: nothing changed. Returns the new names.
    private List<string> CommitRecovery(TDoc document)
    {
        var moved = new List<(string From, string To)>();
        try
        {
            CreateDirectory();
            using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferSize))
            {
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            foreach (var path in new[] { FilePath, _backupPath })
            {
                if (File.Exists(path))
                {
                    moved.Add((path, MoveAside(path)));
                }
            }

            File.Move(_tempPath, FilePath);
            return moved.Select(m => m.To).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Move(moved[i].To, moved[i].From);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(back, "Could not move {Moved} back to {Path} after the recovery failed", moved[i].To, moved[i].From);
                }
            }

            throw WriteFailed(ex);
        }
        catch
        {
            TryDelete(_tempPath);
            throw;
        }
    }

    // The plugin's afterRestoreAtLoad step on the restored document; a step that fails leaves it as it was, with an Error.
    private TDoc AfterRestoreAtLoad(TDoc document, StoreSnapshot snapshot)
    {
        if (_afterRestoreAtLoad is null)
        {
            return document;
        }

        try
        {
            var after = _afterRestoreAtLoad(document);
            if (after is not null && after.Version == CurrentVersion)
            {
                return after;
            }

            _logger.LogError(
                "The plugin's step after restoring {File} from {Snapshot} at load returned {What}; the restored data is saved as it is",
                FilePath,
                snapshot.Name,
                after is null ? "no document" : string.Create(CultureInfo.InvariantCulture, $"a document at version {after.Version}"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "The plugin's step after restoring {File} from {Snapshot} at load failed; the restored data is saved as it is", FilePath, snapshot.Name);
        }

        return document;
    }

    // After a restore of a kept snapshot (restored): the data is back, the kept snapshots rotate normally again. Else an
    // incident with no snapshot at all: there is nothing to keep.
    private void ReleaseKeptSnapshots(bool restored = true)
    {
        if (Interlocked.Exchange(ref _kept, null) is null && !File.Exists(KeepSnapshotsMarkerPath))
        {
            return;
        }

        try
        {
            File.Delete(KeepSnapshotsMarkerPath);
            Interlocked.Exchange(ref _keptSnapshotsLogged, 0);
            if (restored)
            {
                _logger.LogInformation("A snapshot of {File} was restored; its earlier snapshots rotate normally again", FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Marker}; the earlier snapshots stay protected after a restart", KeepSnapshotsMarkerPath);
        }
    }

    private LoadedState EnsureLoaded()
    {
        var state = _state;
        if (state is not null)
        {
            return state;
        }

        lock (_loadLock)
        {
            return _state ??= Load();
        }
    }

    private TDoc CreateEmpty()
    {
        var empty = _createEmpty() ?? throw new InvalidOperationException("The store's empty-document factory returned null.");
        if (empty.Version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The store's empty document is at version {empty.Version}; this store writes version {CurrentVersion}.");
        }

        return empty;
    }

    private string Timestamp() => _timeProvider.GetUtcNow().UtcDateTime.ToString(FileTimestampFormat, CultureInfo.InvariantCulture);

    // Runs under _loadLock.
    private LoadedState Load()
    {
        LoadedState state;
        try
        {
            state = LoadFromDisk();
            UpdateKeepSnapshotsMarker(state, moved: _movedFile is not null || _movedBackup is not null);
        }
        catch (SnapshotUnavailableException ex)
        {
            // The file is missing (or was empty) and the snapshot to restore is in use just now: nothing is restored,
            // and the next use tries again. As below, only the first failure is an Error, naming the snapshot, not the
            // missing file; the 503 names the snapshot's file only.
            if (_readFailureLogged)
            {
                _logger.LogDebug(ex.Inner, "Still could not read the snapshot {Snapshot} of {File}", ex.Snapshot.Path, FilePath);
            }
            else
            {
                _readFailureLogged = true;
                _logger.LogError(
                    ex.Inner,
                    "{File}: {Reason}, and its snapshot {Snapshot} couldn't be read just now; nothing was restored, and the data is unavailable until it can be read (the next use tries again)",
                    FilePath,
                    ex.Reason,
                    ex.Snapshot.Path);
            }

            throw new PluginException(PluginErrorKind.Unavailable, StoreMessages.SnapshotReadFailed(ex.Snapshot.Name), ex.Inner);
        }
        catch (LoadStepFailedException ex)
        {
            // A step of putting the data back failed: moving what couldn't be read aside, or writing the copy kept
            // before an upgrade. What this attempt had moved is back in place (LoadFromDisk), and the next use tries
            // again; logged as above, worded for what failed.
            if (_readFailureLogged)
            {
                _logger.LogDebug(ex.InnerException, "Still could not load {File}: {What}", FilePath, ex.What);
            }
            else
            {
                _readFailureLogged = true;
                _logger.LogError(ex.InnerException, "{File}: {What}; the data is unavailable until that works (the next use tries again)", FilePath, ex.What);
            }

            throw new PluginException(PluginErrorKind.Unavailable, ex.Message, ex.InnerException);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not corruption: nothing is moved or replaced, and the next use tries again. Any signed-in user's request
            // can cause that retry, so only the first failure is an Error; the rest are Debug until a load succeeds.
            if (_readFailureLogged)
            {
                _logger.LogDebug(ex, "Still could not read {File}", FilePath);
            }
            else
            {
                _readFailureLogged = true;
                _logger.LogError(ex, "Could not read {File}; the data is unavailable until it can be read", FilePath);
            }

            throw new PluginException(PluginErrorKind.Unavailable, StoreMessages.ReadFailed, ex);
        }

        _movedFile = null;
        _movedBackup = null;
        if (_readFailureLogged)
        {
            _readFailureLogged = false;
            _logger.LogInformation("{File} could be read again", FilePath);
        }

        return state;
    }

    // Runs under _loadLock: the file, else its .bak, else the snapshots (the class's remarks; the kit's README has the table of
    // every case).
    private LoadedState LoadFromDisk()
    {
        var file = Slot.Missing;
        if (File.Exists(FilePath))
        {
            var bytes = File.ReadAllBytes(FilePath);
            if (IsBlank(bytes))
            {
                // No data: moved aside all the same (so a restore never turns it into the .bak), then as good as missing.
                file = _movedFile = new Slot(Found.Blank, MoveAsideOrFail(isBackup: false));
                _logger.LogWarning("{File} was empty; moved it to {CorruptFile}", FilePath, file.MovedTo);
            }
            else
            {
                var parsed = Parse(bytes);
                if (parsed.Error is null)
                {
                    return Use(FilePath, bytes, parsed, recovery: null);
                }

                // Left in place until something takes its place (below).
                file = new Slot(Found.Unreadable, Error: parsed.Error);
            }
        }
        else if (_movedFile is { } earlier)
        {
            // An earlier attempt of this load moved it aside and then failed: say what it was.
            file = earlier;
        }

        var backup = Slot.Missing;
        if (File.Exists(_backupPath))
        {
            var bytes = File.ReadAllBytes(_backupPath);
            if (IsBlank(bytes))
            {
                backup = _movedBackup = new Slot(Found.Blank, MoveAsideOrFail(isBackup: true));
                _logger.LogWarning("The backup {Backup} of {File} was empty; moved it to {CorruptFile}", _backupPath, FilePath, backup.MovedTo);
            }
            else
            {
                var parsed = Parse(bytes);
                if (parsed.Error is null)
                {
                    // The .bak takes the file's place: an unreadable file goes aside first, so a save never makes it the .bak.
                    var fileInPlace = file.InPlace;
                    var backupUtc = LastWriteUtc(_backupPath);
                    file = MoveAsideIfUnreadable(file, isBackup: false);
                    if (file.Kind == Found.Missing)
                    {
                        _logger.LogWarning("{File} is missing; using its backup {Backup}", FilePath, _backupPath);
                    }

                    LoadedState used;
                    try
                    {
                        used = Use(_backupPath, bytes, parsed, FileReason(file));
                    }
                    catch (LoadStepFailedException) when (fileInPlace)
                    {
                        // The .bak's copy from before its upgrade couldn't be written: the file goes back, so that the
                        // next attempt, or a restart, finds what this one found.
                        PutBack(file, isBackup: false);
                        throw;
                    }

                    LogMovedAside(file, fileInPlace, isBackup: false);
                    return used.Status == StoreStatus.RecoveredFromBackup ? NoteNewerSnapshot(used, backupUtc) : used;
                }

                backup = new Slot(Found.Unreadable, Error: parsed.Error);
            }
        }
        else if (_movedBackup is { } earlier)
        {
            backup = earlier;
        }

        // A fresh install, or the data is gone or unreadable: with snapshots of it there, never start empty beside them, or
        // the daily rotation would delete the last copies of the data within SnapshotsToKeep days.
        var reason = FileReason(file) + BackupReason(backup, file);
        var choice = ChooseSnapshot(reason);
        if (choice.Snapshot is not null)
        {
            // What couldn't be read goes aside first (never overwritten, never deleted): the restored data is saved as
            // the file, and a later save would otherwise make the file the .bak, or overwrite the .bak. When the .bak
            // can't be moved, the file goes back, as an admin's recovery does: otherwise the next start would find only
            // the .bak, and never name the file moved aside.
            var fileInPlace = file.InPlace;
            var backupInPlace = backup.InPlace;
            file = MoveAsideIfUnreadable(file, isBackup: false);
            try
            {
                backup = MoveAsideIfUnreadable(backup, isBackup: true);
            }
            catch (LoadStepFailedException) when (fileInPlace)
            {
                PutBack(file, isBackup: false);
                throw;
            }

            LogMovedAside(file, fileInPlace, isBackup: false);
            LogMovedAside(backup, backupInPlace, isBackup: true);
            return RestoredAtLoad(choice, FileReason(file) + BackupReason(backup, file));
        }

        if (file.Kind == Found.Unreadable || backup.Kind == Found.Unreadable)
        {
            return DataUnreadable(reason, file, backup, choice);
        }

        if (choice.Unusable.Count > 0)
        {
            return DataMissing(reason, choice);
        }

        if (file.Kind == Found.Missing && backup.Kind == Found.Missing)
        {
            _logger.LogDebug("{File} doesn't exist yet, and none of its {Count} snapshots holds data; starting empty", FilePath, choice.Count);
            return new LoadedState(CreateEmpty(), StoreStatus.Ok, null, null, null);
        }

        // Only files that held no data, moved aside: nothing is lost, so the data starts empty, and the banner says so.
        _logger.LogError("{File}: {Reason}; no snapshot holds data, so it starts with an empty document", FilePath, reason);
        return new LoadedState(CreateEmpty(), StoreStatus.RecoveredEmpty, null, null, reason + ", so the data starts empty.");
    }

    // The banner's first words on the file: "The data file was missing" or what was found and where it went.
    private static string FileReason(Slot file) => file.Kind switch
    {
        Found.Blank => "The data file was empty and was moved to " + Path.GetFileName(file.MovedTo),
        Found.Unreadable when file.MovedTo is not null => "The data file couldn't be read and was moved to " + Path.GetFileName(file.MovedTo),
        Found.Unreadable => "The data file couldn't be read",
        _ => "The data file was missing"
    };

    // What follows on the .bak, when it gave nothing: none, or what was found and where it went.
    private static string BackupReason(Slot backup, Slot file) => backup.Kind switch
    {
        Found.Blank => ", and its backup was empty and was moved to " + Path.GetFileName(backup.MovedTo),
        Found.Unreadable when backup.MovedTo is not null => ", and its backup couldn't be read and was moved to " + Path.GetFileName(backup.MovedTo),
        Found.Unreadable => file.Kind == Found.Unreadable ? ", and its backup couldn't be read either" : ", and its backup couldn't be read",
        _ => " and there was no backup"
    };

    // No bytes, or only white space (after a byte order mark): no data at all, as good as a missing file.
    private static bool IsBlank(byte[] bytes)
    {
        foreach (var b in JsonStoreFormat.SkipByteOrderMark(bytes))
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    // Moves the file or its .bak aside as <file>.corrupt-<ts>, a name no file has (UniquePath): never overwritten, never
    // deleted, and listed with the kept files.
    private string MoveAside(string path)
    {
        var target = UniquePath(FilePath + ".corrupt-" + Timestamp());
        File.Move(path, target);
        return target;
    }

    // MoveAside for a load: the file or its .bak, a failure as the 503 of a step that failed (LoadStepFailedException),
    // so that it isn't taken for a file that can't be read.
    private string MoveAsideOrFail(bool isBackup)
    {
        try
        {
            return MoveAside(isBackup ? _backupPath : FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LoadStepFailedException(
                isBackup ? "its backup " + _backupPath + " couldn't be moved aside to put the data back in its place" : "it couldn't be moved aside to put its backup or a snapshot in its place",
                StoreMessages.MoveAsideFailed,
                ex);
        }
    }

    // An unreadable file or .bak still in place goes aside, remembered in case this attempt fails; anything else stays.
    // Logged by LogMovedAside once the step it is part of has worked: one that fails puts it back (PutBack).
    private Slot MoveAsideIfUnreadable(Slot slot, bool isBackup)
    {
        if (!slot.InPlace)
        {
            return slot;
        }

        var moved = slot with { MovedTo = MoveAsideOrFail(isBackup) };
        if (isBackup)
        {
            _movedBackup = moved;
        }
        else
        {
            _movedFile = moved;
        }

        return moved;
    }

    // The Error of an unreadable file or .bak this attempt moved aside (MoveAsideIfUnreadable), now that it stays there.
    private void LogMovedAside(Slot slot, bool movedNow, bool isBackup)
    {
        if (!movedNow || slot.MovedTo is null)
        {
            return;
        }

        if (isBackup)
        {
            _logger.LogError(slot.Error, "Could not read the backup {Backup} of {File}; moved it to {CorruptFile}", _backupPath, FilePath, slot.MovedTo);
        }
        else
        {
            _logger.LogError(slot.Error, "Could not read {File}; moved it to {CorruptFile}", FilePath, slot.MovedTo);
        }
    }

    // Puts back what this attempt of a load moved aside when a later step of it failed, so the next attempt, or a restart,
    // finds what this one found (the way an admin's recovery puts back what it moved). If that fails too, the slot stays
    // remembered (_movedFile, _movedBackup), so the next attempt still names it, and an Error says where it is.
    private void PutBack(Slot slot, bool isBackup)
    {
        var path = isBackup ? _backupPath : FilePath;
        if (slot.MovedTo is not { } movedTo)
        {
            return;
        }

        try
        {
            File.Move(movedTo, path);
            if (isBackup)
            {
                _movedBackup = null;
            }
            else
            {
                _movedFile = null;
            }

            _logger.LogDebug("Moved {Moved} back to {Path}: the rest of the load failed", movedTo, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not move {Moved} back to {Path} after the load failed; it stays where it is, and the banner names it", movedTo, path);
        }
    }

    private DateTime? LastWriteUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the time of {Path}", path);
            return null;
        }
    }

    // RecoveredFromBackup: the .bak is the data before the last write, or older (a write that couldn't refresh it, as
    // ReplaceFile's fallback says). When the newest snapshot that holds data (RecoveryCandidates' order) was taken
    // after the .bak was written and holds other data, it may hold changes the .bak lacks: the banner names it, and the
    // snapshots there are now are kept (the marker), or they would rotate out within SnapshotsToKeep days. Reads only;
    // a snapshot that can't be read is passed over.
    private LoadedState NoteNewerSnapshot(LoadedState state, DateTime? backupUtc)
    {
        if (backupUtc is null)
        {
            return state;
        }

        var backupSecond = new DateTime(backupUtc.Value.Ticks - (backupUtc.Value.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        JsonNode? empty = null;
        foreach (var candidate in RecoveryCandidates(_timeProvider.GetUtcNow().UtcDateTime, out _))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(candidate.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not read the snapshot {Snapshot} to compare it with the backup of {File}", candidate.Path, FilePath);
                continue;
            }

            if (IsBlank(bytes))
            {
                continue;
            }

            JsonNode? node;
            try
            {
                var utf8 = JsonStoreFormat.SkipByteOrderMark(bytes);
                var version = JsonStoreFormat.ReadVersion(utf8) ?? 1;
                if (version > CurrentVersion)
                {
                    continue;
                }

                node = JsonSerializer.SerializeToNode(Upgrade(ParseObject(utf8), version, PluginErrorKind.Conflict, "The snapshot " + candidate.Name), SerializerOptions);
            }
            catch (Exception ex) when (ex is JsonException or PluginException)
            {
                continue;
            }

            empty ??= JsonSerializer.SerializeToNode(CreateEmpty(), SerializerOptions);
            if (JsonNode.DeepEquals(node, empty))
            {
                continue;
            }

            // The newest snapshot that holds data.
            if (candidate.Utc < backupSecond || JsonNode.DeepEquals(node, JsonSerializer.SerializeToNode(state.Document, SerializerOptions)))
            {
                return state;
            }

            _logger.LogWarning(
                "{File} was restored from its backup, written {BackupUtc} UTC, but its snapshot {Snapshot} taken {SnapshotUtc} UTC holds other data, maybe changes the backup lacks; every snapshot is kept until one of them is restored or {Marker} is deleted",
                FilePath,
                backupUtc.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                candidate.Name,
                candidate.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Path.GetFileName(KeepSnapshotsMarkerPath));
            return state.WithNote(
                string.Create(CultureInfo.InvariantCulture, $" A newer snapshot from {candidate.Utc:yyyy-MM-dd HH:mm} UTC exists ({candidate.Name}); restore it on the plugin's settings page if it holds changes the backup lacks. The snapshots, daily and pre-operation, are kept until you restore one of them (or delete {Path.GetFileName(KeepSnapshotsMarkerPath)})."),
                keepsSnapshots: true);
        }

        return state;
    }

    // Reads the version and, at the current version, the document: Error is set when the file isn't one JSON object with a
    // valid Version, or doesn't deserialize at the current version. An older or newer file is only versioned here.
    private Parsed Parse(byte[] bytes)
    {
        try
        {
            var declared = JsonStoreFormat.ReadVersion(JsonStoreFormat.SkipByteOrderMark(bytes));
            var version = declared ?? 1;
            return version == CurrentVersion ? new Parsed(version, DeserializeCurrent(bytes, versionMissing: declared is null), null) : new Parsed(version, null, null);
        }
        catch (JsonException ex)
        {
            return new Parsed(0, null, ex);
        }
    }

    // The data at its version: read as it is, upgraded and saved, or a newer version's served read-only. recovery is null for
    // the file itself, else why its .bak is used instead.
    private LoadedState Use(string path, byte[] bytes, Parsed parsed, string? recovery)
    {
        if (parsed.Version > CurrentVersion)
        {
            return LoadNewer(path, bytes, parsed.Version);
        }

        if (parsed.Version < CurrentVersion)
        {
            return Migrate(path, bytes, parsed.Version, recovery);
        }

        if (recovery is null)
        {
            _logger.LogInformation("Loaded {File} (schema version {Version})", path, parsed.Version);
            return new LoadedState(parsed.Document!, StoreStatus.Ok, parsed.Version, null, null);
        }

        _logger.LogWarning("Restored {File} from its backup {Backup}; the most recent change may be missing", FilePath, path);
        return new LoadedState(
            parsed.Document!,
            StoreStatus.RecoveredFromBackup,
            parsed.Version,
            null,
            recovery + "; the backup copy was used, so the most recent change may be missing.");
    }

    private TDoc DeserializeCurrent(byte[] bytes, bool versionMissing)
    {
        var utf8 = JsonStoreFormat.SkipByteOrderMark(bytes);
        if (!versionMissing)
        {
            return JsonSerializer.Deserialize<TDoc>(utf8, SerializerOptions)
                ?? throw new JsonException("The file holds no document.");
        }

        // A file without a Version is version 1, which is current here: give the document its version explicitly.
        var root = ParseObject(utf8);
        root[JsonStoreFormat.VersionProperty] = CurrentVersion;
        return root.Deserialize<TDoc>(SerializerOptions) ?? throw new JsonException("The file holds no document.");
    }

    private LoadedState LoadNewer(string path, byte[] bytes, int version)
    {
        // Show what this version can read, but never write: the file stays exactly as the newer version left it.
        TDoc document;
        try
        {
            document = JsonSerializer.Deserialize<TDoc>(JsonStoreFormat.SkipByteOrderMark(bytes), SerializerOptions) ?? CreateEmpty();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "{File} can't be shown by this version either; serving no data", path);
            document = CreateEmpty();
        }

        _logger.LogError(
            "{File} was written by a newer plugin version (schema {Version}; this version reads up to {CurrentVersion}). The data is read-only until that version is installed again or an export is restored",
            path,
            version,
            CurrentVersion);
        return new LoadedState(
            document,
            StoreStatus.ReadOnlyNewerVersion,
            version,
            null,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The data was written by a newer plugin version (schema {version}; this version reads up to {CurrentVersion}), so it is read-only. Install that version or restore an export."),
            newerFile: bytes);
    }

    private LoadedState Migrate(string path, byte[] bytes, int fromVersion, string? recovery)
    {
        string copyPath;
        try
        {
            copyPath = KeepPreUpgradeCopy(path, bytes, fromVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing is upgraded without its copy; not a file that can't be read.
            throw new LoadStepFailedException(
                string.Create(CultureInfo.InvariantCulture, $"the copy of {path} kept before its upgrade from schema {fromVersion} couldn't be written"),
                StoreMessages.UpgradeCopyFailed,
                ex);
        }

        var step = fromVersion;
        TDoc document;
        try
        {
            var root = ParseObject(JsonStoreFormat.SkipByteOrderMark(bytes));
            for (; step < CurrentVersion; step++)
            {
                _migrations[step].Apply(root);
                root[JsonStoreFormat.VersionProperty] = step + 1;
            }

            document = root.Deserialize<TDoc>(SerializerOptions) ?? throw new JsonException("The upgraded file holds no document.");
            if (document.Version != CurrentVersion)
            {
                throw new JsonException(
                    string.Create(CultureInfo.InvariantCulture, $"The upgraded document reads as version {document.Version}."));
            }
        }
        catch (Exception ex)
        {
            // Never the corrupt-file path: the file stays as it is, and so does .bak.
            var failedStep = step < CurrentVersion
                ? string.Create(CultureInfo.InvariantCulture, $"the migration from version {step} to {step + 1}")
                : "reading the upgraded data";
            _logger.LogError(
                ex,
                "Could not upgrade {File} from schema {FromVersion} to {ToVersion}: {FailedStep} failed. Nothing was changed; the data is read-only until the previous plugin version is installed or an export is restored",
                path,
                fromVersion,
                CurrentVersion,
                failedStep);
            return new LoadedState(
                CreateEmpty(),
                StoreStatus.ReadOnlyMigrationFailed,
                fromVersion,
                null,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The data couldn't be upgraded from schema {fromVersion} to {CurrentVersion} ({failedStep} failed); nothing was changed. Install the previous plugin version or restore an export."));
        }

        try
        {
            // Persist at once. If that fails, the upgraded data is still served and the next write saves it.
            Commit(document);
            _logger.LogInformation(
                "Upgraded {File} from schema {FromVersion} to {ToVersion}; the previous file was copied to {Copy}",
                FilePath,
                fromVersion,
                CurrentVersion,
                copyPath);
        }
        catch (PluginException)
        {
            // Commit logged the failure.
        }

        var upgraded = string.Create(
            CultureInfo.InvariantCulture,
            $"The data was upgraded from schema {fromVersion} to {CurrentVersion}; the previous file was kept as {Path.GetFileName(copyPath)}.");
        return recovery is null
            ? new LoadedState(document, StoreStatus.Migrated, fromVersion, fromVersion, upgraded)
            : new LoadedState(
                document,
                StoreStatus.RecoveredFromBackup,
                fromVersion,
                fromVersion,
                recovery + "; the backup copy was used, so the most recent change may be missing. " + upgraded);
    }

    // Keeps the file as it was before touching anything, as .v<N>-<ts>: the bytes the upgrade read, the original's
    // exactly. They are written to <copy>.tmp and flushed to disk before the copy gets its name, so a kill or a power
    // cut never leaves a partial file under a name ListKeptFiles lists and the downgrade procedure restores; a .tmp a
    // killed copy left is never reused and is deleted here. While an upgrade keeps failing, every start loads the same
    // file again: an identical earlier copy is reused rather than adding one more full copy per restart (the copies
    // stay until an admin deletes them).
    private string KeepPreUpgradeCopy(string path, byte[] bytes, int fromVersion)
    {
        var filePrefix = Path.GetFileName(FilePath) + ".";
        var prefix = string.Create(CultureInfo.InvariantCulture, $"{filePrefix}v{fromVersion}-");
        var directory = Path.GetDirectoryName(FilePath)!;
        string? reusable = null;
        var leftovers = new List<string>();
        foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            var name = Path.GetFileName(candidate);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = name.AsSpan(filePrefix.Length);
            if (rest.EndsWith(CopyTempSuffix, StringComparison.OrdinalIgnoreCase)
                && TryParseKeptName(rest[..^CopyTempSuffix.Length], out _, out _, out _))
            {
                leftovers.Add(candidate);
            }
            else if (reusable is null && TryParseKeptName(rest, out _, out _, out _) && HasContent(candidate, bytes))
            {
                reusable = candidate;
            }
        }

        foreach (var leftover in leftovers)
        {
            _logger.LogInformation("Deleting {Copy}, a copy of {File} that an earlier upgrade attempt didn't finish", leftover, path);
            TryDelete(leftover);
        }

        if (reusable is not null)
        {
            _logger.LogInformation("Reusing {Copy}, an identical copy of {File} from an earlier upgrade attempt", reusable, path);
            return reusable;
        }

        var copyPath = UniquePath(string.Create(CultureInfo.InvariantCulture, $"{FilePath}.v{fromVersion}-{Timestamp()}"));
        var copyTempPath = copyPath + CopyTempSuffix;
        try
        {
            using (var stream = new FileStream(copyTempPath, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferSize))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(copyTempPath, copyPath);
        }
        catch
        {
            TryDelete(copyTempPath);
            throw;
        }

        return copyPath;
    }

    private bool HasContent(string file, byte[] bytes)
    {
        try
        {
            return new FileInfo(file).Length == bytes.LongLength && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable old copy doesn't stop the upgrade; a new copy is made instead.
            _logger.LogDebug(ex, "Could not compare {Copy}", file);
            return false;
        }
    }

    // Synchronous commit, for the save right after an upgrade, which runs inside the load lock.
    private void Commit(TDoc document)
    {
        try
        {
            CreateDirectory();
            using (var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferSize))
            {
                JsonSerializer.Serialize(stream, document, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            ReplaceFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw WriteFailed(ex);
        }
        catch
        {
            TryDelete(_tempPath);
            throw;
        }
    }

    // Writes the document to disk and only then lets the caller publish it, so memory never gets ahead of the file.
    // Deliberately not cancellable: once a write has started it should finish.
    private async Task CommitAsync(TDoc document)
    {
        try
        {
            CreateDirectory();
            var stream = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None, WriteBufferSize, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, CancellationToken.None).ConfigureAwait(false);

                // Make sure the data is on disk before the swap, so a power cut can't leave an empty file.
#pragma warning disable CA1849 // FlushAsync can't force the write through to disk.
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }

            ReplaceFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw WriteFailed(ex);
        }
        catch
        {
            TryDelete(_tempPath);
            throw;
        }
    }

    // RefreshBackupAsync, after its write (under the writer lock): a .bak whose bytes aren't the file's (the save fell back to
    // a plain overwrite, so .bak is from before) is overwritten with the file's bytes, else deleted; false, with a Warning,
    // when neither works.
    private bool ClearStaleBackup()
    {
        try
        {
            if (!File.Exists(_backupPath) || File.ReadAllBytes(_backupPath).AsSpan().SequenceEqual(File.ReadAllBytes(FilePath)))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not compare {Backup} with {File}", _backupPath, FilePath);
        }

        try
        {
            File.Copy(FilePath, _backupPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not overwrite {Backup} with {File}", _backupPath, FilePath);
        }

        try
        {
            File.Delete(_backupPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{Backup} holds an earlier version of {File} and couldn't be overwritten or deleted; delete it by hand", _backupPath, FilePath);
            return false;
        }
    }

    private void CreateDirectory()
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void ReplaceFile()
    {
        if (!File.Exists(FilePath))
        {
            File.Move(_tempPath, FilePath);
            return;
        }

        try
        {
            File.Replace(_tempPath, FilePath, _backupPath, ignoreMetadataErrors: true);
        }
        catch (IOException ex)
        {
            // Some file systems don't support replacing with a backup, and a read-only or open .bak can't be replaced;
            // a plain overwrite is still atomic. .bak then goes stale, which weakens recovery: say so once, as a Warning.
            File.Move(_tempPath, FilePath, overwrite: true);
            if (Interlocked.Exchange(ref _replaceFallbackLogged, 1) == 0)
            {
                _logger.LogWarning(
                    ex,
                    "Could not save {File} with a backup; overwrote it instead, so {Backup} is no longer kept up to date. Check that the backup isn't read-only or open in another program",
                    FilePath,
                    _backupPath);
            }
            else
            {
                _logger.LogDebug(ex, "Could not save {File} with a backup; overwrote it instead", FilePath);
            }
        }
    }

    private PluginException WriteFailed(Exception ex)
    {
        TryDelete(_tempPath);
        _logger.LogError(ex, "Could not save {File}; nothing was changed", FilePath);
        return new PluginException(PluginErrorKind.Unavailable, StoreMessages.WriteFailed, ex);
    }

    // Parse's outcome: Error set, or the version, with the document at the current version.
    private readonly record struct Parsed(int Version, TDoc? Document, JsonException? Error);

    // MovedTo: where it was moved (<file>.corrupt-<ts>), or null while it is in place (or missing); Error: why an unreadable
    // one couldn't be read.
    private sealed record Slot(Found Kind, string? MovedTo = null, JsonException? Error = null)
    {
        public static Slot Missing { get; } = new(Found.Missing);

        public bool InPlace => Kind == Found.Unreadable && MovedTo is null;
    }

    // What ChooseSnapshot found (RecoveryCandidates' order): the snapshot that reads and holds data, with its document at the
    // current version and its own version; those passed over before it, and why; those that may hold data but can't be used;
    // those that hold none; those dated in the future; how many there were.
    private sealed record SnapshotChoice(
        StoreSnapshot? Snapshot,
        TDoc? Document,
        int Version,
        List<string> Passed,
        List<string> Unusable,
        List<string> NoData,
        List<StoreSnapshot> Future,
        int Count);

    private sealed class LoadedState
    {
        public LoadedState(TDoc document, StoreStatus status, int? loadedVersion, int? migratedFrom, string? message, byte[]? newerFile = null, string? reason = null, bool keepsSnapshots = false)
        {
            Document = document;
            Status = status;
            LoadedVersion = loadedVersion;
            MigratedFrom = migratedFrom;
            Message = message;
            NewerFile = newerFile;
            Reason = reason;
            KeepsSnapshots = keepsSnapshots;
        }

        public TDoc Document { get; }

        public StoreStatus Status { get; }

        public int? LoadedVersion { get; }

        public int? MigratedFrom { get; }

        public string? Message { get; }

        // ReadOnlyNewerVersion: the file as it was read, which an export passes on unchanged.
        public byte[]? NewerFile { get; }

        // ReadOnlyDataMissing and ReadOnlyDataUnreadable: what happened to the file and its .bak, the banner's first sentence,
        // which an admin's recovery repeats.
        public string? Reason { get; }

        // RecoveredFromBackup with a newer snapshot holding other data (NoteNewerSnapshot): the load records the kept snapshots,
        // as RecoveredEmpty and RestoredFromSnapshot do.
        public bool KeepsSnapshots { get; }

        public bool IsReadOnly => Status is StoreStatus.ReadOnlyNewerVersion or StoreStatus.ReadOnlyMigrationFailed or StoreStatus.ReadOnlyDataMissing or StoreStatus.ReadOnlyDataUnreadable;

        public bool IsRecoverable => Status is StoreStatus.ReadOnlyDataMissing or StoreStatus.ReadOnlyDataUnreadable;

        public LoadedState WithDocument(TDoc document) => new(document, Status, LoadedVersion, MigratedFrom, Message, NewerFile, Reason, KeepsSnapshots);

        public LoadedState WithNote(string note, bool keepsSnapshots) => new(Document, Status, LoadedVersion, MigratedFrom, Message + note, NewerFile, Reason, keepsSnapshots);
    }

    // The restore at load needs a snapshot that is in use just now (RestoreAtLoad): Load answers 503 naming it, and the next
    // use tries again.
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Never leaves JsonDocumentStore: Load turns it into the 503 of a store that can't be read yet.")]
    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private; always made with its snapshot.")]
    private sealed class SnapshotUnavailableException(StoreSnapshot snapshot, string reason, IOException inner)
        : Exception("The snapshot " + snapshot.Name + " couldn't be read just now.", inner)
    {
        public StoreSnapshot Snapshot { get; } = snapshot;

        public IOException Inner { get; } = inner;

        // What happened to the file ("The data file was missing and there was no backup").
        public string Reason { get; } = reason;
    }

    // A step of a load that puts the data back failed on disk (moving what couldn't be read aside, writing the copy kept
    // before an upgrade): Load answers the 503 of Message, its user-facing sentence, and logs What; the next use tries again.
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Never leaves JsonDocumentStore: Load turns it into a 503.")]
    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private; always made with what failed.")]
    private sealed class LoadStepFailedException(string what, string message, Exception inner) : Exception(message, inner)
    {
        // What failed, for the log ("it couldn't be moved aside to put its backup or a snapshot in its place").
        public string What { get; } = what;
    }
}
