using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// A plugin's data in one SQLite file through the server's own Microsoft.Data.Sqlite: the
/// kit's opt-in engine, compiled only with <c>KitUseSqlite=true</c>, for data that needs server-side queries, many small
/// writes or unbounded tables (User Ratings' <c>userratings.db</c>).
/// </summary>
/// <remarks>
/// <para><b>Open</b> (lazily on first use, or eagerly through <see cref="Open"/> from the kit's hosted service). Every
/// connection runs <see cref="OpenPragmas"/>: <c>journal_mode=WAL</c>, <c>synchronous=NORMAL</c>,
/// <c>foreign_keys=ON</c>, <c>busy_timeout=5000</c> (verified in a sandbox on 10.11.5), <c>journal_size_limit</c>
/// (<see cref="JournalSizeLimitBytes"/>) and <c>secure_delete=ON</c> (<see cref="SecureDelete"/>: what a write deletes
/// is overwritten with zeros, so it isn't left in the file's free space for a snapshot's copy to carry), because a
/// pooled connection keeps the state its last user left. A database written by a newer plugin version
/// (<c>user_version</c> above <see cref="CurrentVersion"/>, in the header or, after a crash, still only in the
/// <c>-wal</c>, read through a read-only connection) is opened read-only and never written
/// (<see cref="SqliteDatabaseStatus.ReadOnlyNewerVersion"/>). Otherwise <c>PRAGMA quick_check</c> runs. A line that
/// reports a failure to read (<see cref="SqliteCheckResult.IsInconclusive"/>) makes it
/// <see cref="SqliteDatabaseStatus.Unavailable"/>, tried again at the next use. Damage (a failed check, or
/// <c>SQLITE_CORRUPT</c> (11) or <c>SQLITE_NOTADB</c> (26)) is confirmed after a pause by <c>PRAGMA integrity_check</c>
/// on a fresh read-only connection; only then is the database moved together with its <c>-wal</c> and <c>-shm</c> into
/// <c>quarantine-&lt;yyyyMMdd-HHmmss&gt;/</c> and the newest snapshot that passes <c>PRAGMA integrity_check</c> put in
/// its place, with no sidecars (<see cref="SqliteDatabaseStatus.RestoredFromSnapshot"/>). The same restore runs when
/// the file is missing, or empty, while snapshots of its data exist. When no snapshot passes, nothing is moved and the
/// database stays <see cref="SqliteDatabaseStatus.ReadOnlyCorrupt"/>; starting empty is an admin's choice
/// (<see cref="RecoverAsync(string, bool, CancellationToken)"/>, <see cref="SqliteDatabaseStatus.RecoveredEmpty"/>),
/// and an admin's recovery checks the database in place again first, refusing one that passes (a restart opens it). A
/// step that fails after the move aside puts everything back as it was. Every recovery writes
/// <see cref="RecoveryJournalPath"/> before it moves anything and deletes it once it is done or put back, so a server
/// stopped during one, or a put back that couldn't finish, is put back by the next open before anything else, never a
/// new empty database or a snapshot restored beside the data in its folder. Every incident records the snapshots there
/// are by name in <see cref="KeepSnapshotsMarkerPath"/>, before the copy takes the database's place, kept until one of
/// them is restored (or all of them are gone). A snapshot that can't be read just then (in use, a read error) is "try
/// again later", never a reason to skip it. Then the pending <see cref="SqliteMigration"/> steps run in one transaction
/// after a pre-operation snapshot (<see cref="SnapshotOperations.Migrate"/>); a failure rolls everything back
/// (<see cref="SqliteDatabaseStatus.ReadOnlyMigrationFailed"/>).</para>
/// <para><b>Reads and writes.</b> <see cref="Read{T}"/> uses pooled connections, which stay on as for Jellyfin's own
/// <c>jellyfin.db</c>; <see cref="WriteAsync{T}"/> runs one writer at a time (one <see cref="SemaphoreSlim"/>) in a
/// <c>BEGIN IMMEDIATE</c> transaction. <c>SQLITE_CORRUPT</c> or <c>SQLITE_NOTADB</c> on either makes the database
/// read-only with a banner (<see cref="SqliteDatabaseStatus.ReadOnlyCorrupt"/>) until it is restored; reads go on through
/// read-only connections, and one read-only connection stays open, so no connection of ours that closes later (the pool
/// closes idle ones after a few minutes) checkpoints the WAL into the damaged file.</para>
/// <para><b>Snapshots</b> (<see cref="SnapshotAsync"/>, <see cref="PreOpSnapshotAsync(string, CancellationToken)"/>):
/// <c>BackupDatabase</c> into a temporary file through an unpooled connection, then <c>PRAGMA journal_mode=DELETE</c>
/// on it (a backup of a WAL database is a WAL file, which a read-only check leaves <c>-wal</c> and <c>-shm</c> beside),
/// then <c>PRAGMA integrity_check</c> read-only and unpooled; only a snapshot that passes gets its name, so a damaged
/// live database never rotates the good snapshots out. The daily class keeps <c>SnapshotsToKeep</c>, the pre-operation
/// class the newest five, each rotated on its own, a snapshot's sidecars with it.</para>
/// <para><b>Pools</b>. Before anything moves, copies or replaces the database file, this database's pools are
/// cleared with <c>SqliteConnection.ClearPool</c>, never <c>ClearAllPools</c>, which would also close Jellyfin's pooled
/// <c>jellyfin.db</c> connections in the same process; <see cref="Release"/> and <see cref="Dispose"/> do the same at
/// shutdown, which checkpoints the WAL and frees the file. Snapshot, verify, migration and restore connections are
/// unpooled.</para>
/// <para><b>Isolation</b>. The constructor and every member without a SQLite type in its signature touch no
/// Microsoft.Data.Sqlite type: SQLite work sits in <see cref="MethodImplOptions.NoInlining"/> methods called inside a
/// catch, so a build that can't bind the server's Microsoft.Data.Sqlite makes the database
/// <see cref="SqliteDatabaseStatus.Unavailable"/> with one Error instead of stopping the server.</para>
/// <para>Time stamps in file names are UTC. Plugins create the database with a logger from
/// <c>KitOptions.CreateLogger(factory, "Storage")</c>, normally through <c>AddKitSqliteDatabase</c>.</para>
/// </remarks>
internal sealed class SqliteDatabase : IDisposable
{
    /// <summary>The extension of the database and its snapshots.</summary>
    public const string Extension = ".db";

    /// <summary>The name prefix of the quarantine folders next to the database.</summary>
    public const string QuarantinePrefix = "quarantine-";

    /// <summary>The suffix of <see cref="KeepSnapshotsMarkerPath"/>, as for the JSON store.</summary>
    public const string KeepSnapshotsMarkerSuffix = KeepSnapshotsMarker.Suffix;

    /// <summary>The suffix of <see cref="LastRestoreMarkerPath"/>.</summary>
    public const string LastRestoreMarkerSuffix = ".last-restore";

    /// <summary>The suffix of <see cref="DamageMarkerPath"/>.</summary>
    public const string DamageMarkerSuffix = ".damaged";

    /// <summary>The name of the note a quarantine folder holds about why the database was moved there.</summary>
    public const string QuarantineNoteName = "reason.txt";

    /// <summary>The suffix of <see cref="RecoveryJournalPath"/>.</summary>
    public const string RecoveryJournalSuffix = SqliteRecoveryJournal.Suffix;

    /// <summary>
    /// How large, in bytes, the <c>-wal</c> file may stay after a checkpoint has reset it (<c>PRAGMA
    /// journal_size_limit</c>, 4 MiB). Without a limit SQLite reuses the file at the size of the largest batch written
    /// between two checkpoints and doesn't shrink it while a connection is open, so after one large import the WAL
    /// could stay many times the size of the database beside it; with it the file is cut to at most this once a
    /// checkpoint resets it, while the everyday writes of a few rows stay far below it.
    /// </summary>
    public const long JournalSizeLimitBytes = 4L * 1024 * 1024;

    /// <summary>The pragmas run on every open of a writable connection (read back in a sandbox).</summary>
    public const string OpenPragmas = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_size_limit=4194304; PRAGMA secure_delete=ON;";

    /// <summary>The pragmas of a writable connection of a database that opted out of <see cref="SecureDelete"/>.</summary>
    public const string OpenPragmasWithoutSecureDelete = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA journal_size_limit=4194304; PRAGMA secure_delete=OFF;";

    /// <summary>
    /// The problem code of the 409 an admin's data operation answers while the database is damaged
    /// (<see cref="SqliteDatabaseStatus.ReadOnlyCorrupt"/>), also when the operation's own check is what found it: a
    /// page then offers the damaged database's restore or Start empty.
    /// </summary>
    public const string DamagedCode = "Damaged";

    /// <summary>The pragmas of a read-only connection, which can't change the journal mode.</summary>
    public const string ReadOnlyPragmas = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";

    /// <summary>
    /// How long, in seconds, a command waits for a lock another connection holds before the database answers 503 with
    /// <see cref="SqliteMessages.Busy"/> (a database locked or busy longer than 5 s answers 503). Every connection
    /// string sets it as Microsoft.Data.Sqlite's <c>DefaultTimeout</c>: SQLite's own busy handler waits
    /// <c>busy_timeout</c> (5000 ms) and Microsoft.Data.Sqlite then retries until the command timeout, whose default of
    /// 30 s made a locked database a raw "database is locked" 500 after about 34 s, with the writer lock held all
    /// along.
    /// </summary>
    public const int CommandTimeoutSeconds = 5;

    // SQLite's primary result codes the database reacts to.
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteIoErr = 10;
    private const int SqliteCorrupt = 11;
    private const int SqliteFull = 13;
    private const int SqliteNotADatabase = 26;

    // Why a snapshot was passed over without reading it (the JSON store's wording).
    private const string NoPermission = "no permission to read it";

    private const int CheckLines = 20;
    private const string TimestampFormat = "yyyyMMdd-HHmmss";
    private const string TempSuffix = ".tmp";
    private const string RestoreTempSuffix = ".restore.tmp";

    // The copy an admin's recovery checks the database in place on first (ThrowIfReadableDatabaseInPlace).
    private const string RecheckSuffix = ".recheck.tmp";

    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long after a failed open the next use tries again; uses in between get the 503 at once.</summary>
    public static readonly TimeSpan OpenRetryInterval = TimeSpan.FromSeconds(5);

    private readonly IReadOnlyList<SqliteMigration> _migrations;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);

    // Readers and writers hold it shared while they use a connection; a restore holds it alone while the file is moved or
    // replaced, so no connection of ours keeps the file open then. Taken and released synchronously, on one thread.
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.SupportsRecursion);
    private readonly Lock _openLock = new();
    private readonly Lock _guardLock = new();

    // The requested checkpoints' lock, and the stop of a round running when the database is disposed.
    private readonly Lock _checkpointLock = new();
    private readonly CancellationTokenSource _checkpointStop = new();
    private readonly CancellationToken _checkpointStopToken;

    private volatile OpenState? _state;

    // While the database is ReadOnlyCorrupt: one read-only, unpooled connection held open, so that no connection of
    // ours that closes later is the last one, which would checkpoint the WAL into the damaged file (a SqliteConnection,
    // kept as IDisposable: no field names a SQLite type, see Isolation above).
    private IDisposable? _corruptGuard;
    private volatile SqliteCheckResult? _lastQuickCheck;
    private volatile SqliteRecoveryResult? _lastRecovery;
    private volatile SqliteReindexResult? _lastReindex;
    private volatile string? _lastOpenError;

    // The 503's sentence while the open is tried again later: a snapshot's name when one couldn't be read just then.
    private volatile string? _unavailableMessage;

    // The quarantine folder of a recovery that didn't finish, whose files wait there while a readable database put back
    // by hand is used as it is (its journal kept), as the last open found it; Notice names it while the folder is
    // there. LogFilesOnly: the folder holds the damaged database's -wal or -shm without the database, which are never
    // moved back beside the one in place (SQLite would replay them into it).
    private volatile KeptAsideFolder? _keptAside;

    // The .keep-snapshots marker: the snapshots there were at the last incident (a restore at open, an admin's
    // recovery, a start empty), by name, never rotated out, across restarts, until one of them is restored (as the JSON
    // store keeps them). Null when there is none.
    private volatile KeptSnapshots<SqliteDatabaseStatus>? _kept;

    // The snapshot whose "couldn't be read just now" was last logged at Warning: the next attempts on it log at Debug.
    private string? _snapshotInUseLogged;
    private string? _pooledConnectionString;
    private string? _readOnlyConnectionString;
    private string? _unpooledConnectionString;
    private long _generation;
    private long _restores;
    private long _lastSnapshotTicks;
    private bool _openFailureLogged;
    private long _nextOpenAttemptTicks;
    private int _journalModeWarned;

    // 1 once the kept snapshots were named in a Warning in this process (the JSON store's WarnOfKeptSnapshots), 0 again
    // when an incident records new ones or they are released.
    private int _keptSnapshotsLogged;
    private volatile bool _disposed;

    // The checkpoints (RequestCheckpoint), under _checkpointLock (above): the requests counted, the latest request a
    // complete checkpoint covered, the round running now (null when none runs) and what Diagnostics shows.
    private long _checkpointRequested;
    private long _checkpointCompleted;
    private TaskCompletionSource<bool>? _checkpointRound;
    private DateTime? _lastCheckpointUtc;
    private DateTime? _lastCheckpointTryUtc;
    private string? _lastCheckpointOutcome;
    private int _checkpointRoundsFailed;

    // Whether the last open found frames in the -wal before its first connection (OpenCore; EnsureOpen asks for a
    // checkpoint then).
    private bool _walFramesAtOpen;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDatabase"/> class. Nothing is opened, and no SQLite type is
    /// touched, until the database is first used.
    /// </summary>
    /// <param name="file">The database file, for example <c>&lt;data&gt;/userratings/userratings.db</c>. Created on first use.</param>
    /// <param name="migrations">The schema steps, one for each version from 1 to the current one.</param>
    /// <param name="logger">The logger (category <c>Jellyfin.Plugin.&lt;X&gt;.Kit.Storage</c>).</param>
    /// <param name="snapshotDirectory">The snapshot folder; <c>snapshots</c> next to the file by default.</param>
    /// <param name="timeProvider">The clock for file and folder time stamps; the system clock by default.</param>
    public SqliteDatabase(
        string file,
        IReadOnlyList<SqliteMigration> migrations,
        ILogger logger,
        string? snapshotDirectory = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentNullException.ThrowIfNull(logger);

        var ordered = migrations.OrderBy(m => m?.Version ?? 0).ToList();
        if (ordered.Count == 0)
        {
            throw new ArgumentException("A SQLite database needs at least the migration that creates its schema (version 1).", nameof(migrations));
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(ordered[i], nameof(migrations));
            ordered[i].Validate();
            if (ordered[i].Version != i + 1)
            {
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"The SQLite migrations must be versions 1 to {ordered.Count}, each once; version {i + 1} is missing or repeated."),
                    nameof(migrations));
            }
        }

        _migrations = ordered;
        FilePath = Path.GetFullPath(file);
        Folder = Path.GetDirectoryName(FilePath)!;
        StoreName = Path.GetFileNameWithoutExtension(FilePath);
        SnapshotDirectory = Path.GetFullPath(snapshotDirectory ?? Path.Combine(Folder, KitPaths.SnapshotsFolder));
        CurrentVersion = ordered[^1].Version;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _checkpointStopToken = _checkpointStop.Token;
    }

    // What one try of a requested checkpoint found (TryCheckpoint).
    private enum CheckpointOutcome
    {
        Complete,
        Readers,
        Writer,
        ReadOnly,
        Stopped,
        Failed
    }

    // What a snapshot's copy for a restore turned out to be (PrepareRestoreCopy).
    private enum RestoreCopy
    {
        Ready,
        Unusable,
        Unreadable
    }

    /// <summary>
    /// Gets how long the open waits before it checks a database that failed its <c>quick_check</c> a second time
    /// (with <c>PRAGMA integrity_check</c>, on a fresh read-only connection), before anything is moved. Tests shorten it.
    /// </summary>
    internal TimeSpan DamageRecheckDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets how long pre-operation snapshots are kept, by age instead of the newest
    /// <see cref="SnapshotOperations.ToKeep"/> (opt-in, the <c>preOpKeepWithin</c> of <c>AddKitSqliteDatabase</c>):
    /// every one taken within this time, up to <see cref="SnapshotOperations.MaxKeptByAge"/>, and none older (no newest
    /// five kept besides), rotated when one is taken and at every daily snapshot (<see cref="SnapshotAsync"/>), with
    /// the copies of <see cref="PreOpCopies"/>. Null keeps the newest five, as before.
    /// </summary>
    public TimeSpan? PreOpKeepWithin { get; init; }

    /// <summary>
    /// Gets the files whose pre-operation copies, in the same <c>pre-op</c> folder, the daily snapshot rotates by
    /// <see cref="PreOpKeepWithin"/> too: file names such as <c>userratings-archived.jsonl</c>, whose copies are
    /// <c>userratings-archived-&lt;operation&gt;-&lt;yyyyMMdd-HHmmss&gt;.jsonl</c> (the kit's archive's). None by
    /// default.
    /// </summary>
    public IReadOnlyList<string> PreOpCopies { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether deleted content is overwritten with zeros (<c>PRAGMA secure_delete=ON</c> on
    /// every writable connection; on by default). Without it SQLite leaves a deleted row's bytes in the page's free
    /// space and in free pages, where they stay in the file, and in every snapshot's copy of it (<c>BackupDatabase</c>
    /// copies pages as they are), until the space is reused. A database whose deletes carry nothing private, and that
    /// writes a lot, may turn it off (the <c>secureDelete</c> of <c>AddKitSqliteDatabase</c>).
    /// </summary>
    public bool SecureDelete { get; init; } = true;

    /// <summary>
    /// Gets how long a requested checkpoint waits before its first try (<see cref="RequestCheckpoint"/>), so the requests of a
    /// burst of writes become one checkpoint. Tests shorten it.
    /// </summary>
    internal TimeSpan CheckpointDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets the waits between the tries of a requested checkpoint that another connection's read (or a write) kept from
    /// finishing; after the last one the checkpoint stays pending, logged at Warning, until a later write or the daily
    /// maintenance tries again. About two minutes in all by default. Tests shorten them.
    /// </summary>
    internal IReadOnlyList<TimeSpan> CheckpointRetryDelays { get; init; } =
        [.. new[] { 0.5, 1, 2, 4, 8, 15, 30, 30, 30 }.Select(TimeSpan.FromSeconds)];

    /// <summary>
    /// Gets a value indicating whether a requested checkpoint hasn't completed yet (<see cref="RequestCheckpoint"/>): one is
    /// waiting or trying now, or its tries ran out and it waits for a later write or the daily maintenance.
    /// </summary>
    public bool CheckpointPending
    {
        get
        {
            lock (_checkpointLock)
            {
                return _checkpointCompleted < _checkpointRequested;
            }
        }
    }

    /// <summary>
    /// Gets what tests make a recovery's later step fail with: called with <c>install</c> before the checked snapshot's
    /// copy is renamed into place of the database moved aside (at each of the three tries), and with <c>open</c> before what
    /// took its place is opened again; whatever it throws is that step's failure. <c>null</c> outside tests.
    /// </summary>
    internal Action<string>? RecoveryStepFault { get; init; }

    /// <summary>
    /// Gets what tests make a <c>VACUUM</c> fail with (<see cref="SqliteMigration.VacuumAfter"/>, and its retry at a later
    /// open): called before each <c>VACUUM</c>; whatever it throws is that <c>VACUUM</c>'s failure. <c>null</c> outside tests.
    /// </summary>
    internal Action? VacuumFault { get; init; }

    /// <summary>
    /// Gets what tests run inside a requested checkpoint's try, to interleave with it: called with <c>locked</c> while the
    /// try holds the writer lock, before it checkpoints, and with <c>tried</c> after the try let the lock go, before its
    /// outcome is counted. <c>null</c> outside tests.
    /// </summary>
    internal Action<string>? CheckpointTryHook { get; init; }

    /// <summary>
    /// Gets the marker of a <see cref="SqliteMigration.VacuumAfter"/> step whose <c>VACUUM</c> hasn't succeeded yet:
    /// <c>&lt;file&gt;.vacuum-pending</c> beside the database, written before the <c>VACUUM</c> and deleted once one
    /// succeeds, so a failed one (a full disk: it needs room for a copy) is tried again at each later open.
    /// </summary>
    public string VacuumPendingPath => FilePath + ".vacuum-pending";

    /// <summary>
    /// Gets the full path of the database file.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Gets the folder that holds the database, its sidecars and its quarantine folders.
    /// </summary>
    public string Folder { get; }

    /// <summary>
    /// Gets the database's name, the file name without extension (<c>userratings</c>); snapshots are named after it.
    /// </summary>
    public string StoreName { get; }

    /// <summary>
    /// Gets the snapshot folder; the pre-operation snapshots are in its <c>pre-op</c> sub-folder.
    /// </summary>
    public string SnapshotDirectory { get; }

    /// <summary>
    /// Gets the schema version (<c>PRAGMA user_version</c>) this build reads and writes: the last migration's.
    /// </summary>
    public int CurrentVersion { get; }

    /// <summary>
    /// Gets the marker file that keeps the snapshots, daily and pre-operation, there were at the last incident:
    /// <c>&lt;file&gt;.keep-snapshots</c>, the JSON store's format and code (<see cref="KeepSnapshotsMarker"/>). Every
    /// incident (a restore at open, an admin's recovery, a start empty) records the snapshots there are then by name,
    /// so rotation keeps exactly those, whatever the clock or their names' dates; an admin's
    /// <see cref="RestoreSnapshotAsync"/> of one of them releases them, and one taken since never does (as for the JSON
    /// store: with the clock behind the newest snapshots at the incident, every snapshot taken afterwards was dated
    /// before the time the marker held, counted as kept and, restored, released the kept ones). A development build's
    /// marker of the time alone is read as it meant and recorded by name once.
    /// </summary>
    public string KeepSnapshotsMarkerPath => FilePath + KeepSnapshotsMarkerSuffix;

    /// <summary>
    /// Gets the marker of the last restore, <c>&lt;file&gt;.last-restore</c>: the snapshot that became the data and when.
    /// A corruption restore prefers the snapshots taken after it, then that snapshot, before any older one: the
    /// pre-operation snapshot <c>restore</c> taken just before holds the state an admin chose to replace, so it is newer
    /// than the restored data without being its history.
    /// </summary>
    public string LastRestoreMarkerPath => FilePath + LastRestoreMarkerSuffix;

    /// <summary>
    /// Gets the marker a damage found at run time leaves (<c>&lt;file&gt;.damaged</c>): damage <c>quick_check</c>
    /// doesn't see (an index whose entries don't match its table, found by the <c>integrity_check</c> before a
    /// pre-operation snapshot) is found again after a restart. The open then runs <c>integrity_check</c>: a database
    /// that passes is opened, one whose indexes only are damaged has them rebuilt (<see cref="LastReindex"/>), any
    /// other is restored from the newest good snapshot. The marker goes once the database passes, or a snapshot took
    /// its place.
    /// </summary>
    public string DamageMarkerPath => FilePath + DamageMarkerSuffix;

    /// <summary>
    /// Gets the journal of a recovery under way, <c>&lt;file&gt;.recovery</c> (<see cref="SqliteRecoveryJournal"/>):
    /// written and flushed before a recovery moves anything, deleted once it is done or what it moved is back. The open
    /// finds it after the server stopped or was killed during a recovery, or after a put back that couldn't finish, and
    /// puts back what it names first.
    /// </summary>
    public string RecoveryJournalPath => FilePath + RecoveryJournalSuffix;

    /// <summary>
    /// Gets how the database came up, or what was found since (<see cref="SqliteDatabaseStatus.ReadOnlyCorrupt"/>). Opens
    /// it on first use; <see cref="SqliteDatabaseStatus.Unavailable"/> when that fails.
    /// </summary>
    public SqliteDatabaseStatus Status => TryEnsureOpen()?.Status ?? SqliteDatabaseStatus.Unavailable;

    /// <summary>
    /// Gets a value indicating whether the database refuses every write and snapshot: it comes from a newer plugin
    /// version, its upgrade failed, it is damaged, or it can't be opened. Background writers check it and stay idle.
    /// </summary>
    public bool IsReadOnly => TryEnsureOpen() is not { } state || IsReadOnlyStatus(state.Status);

    /// <summary>
    /// Gets one sentence about the status for the settings page's banner and Diagnostics, or <c>null</c> when it is
    /// <see cref="SqliteDatabaseStatus.Ok"/>.
    /// </summary>
    public string? StatusMessage => TryEnsureOpen() is { } state ? state.Message : UnavailableMessage;

    /// <summary>
    /// Gets one sentence for an admin about something to deal with while the database works, for the settings page's
    /// banner and Diagnostics, or <c>null</c>: a recovery that didn't finish has its files in a quarantine folder,
    /// named here, while a readable database put back by hand is used as it is
    /// (<see cref="SqliteMessages.KeptAside"/>). It stays, at every open, until that folder is deleted or moved.
    /// </summary>
    public string? Notice
    {
        get
        {
            TryEnsureOpen();
            var kept = _keptAside;
            if (kept is null)
            {
                return null;
            }

            var message = kept.LogFilesOnly ? SqliteMessages.KeptAsideLogFiles(kept.Folder) : SqliteMessages.KeptAside(kept.Folder);
            try
            {
                return Directory.Exists(Path.Combine(Folder, kept.Folder)) ? message : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return message;
            }
        }
    }

    /// <summary>
    /// Gets the schema version the database had when it was opened (or restored), or <c>null</c> when it was created.
    /// </summary>
    public int? LoadedVersion => TryEnsureOpen()?.LoadedVersion;

    /// <summary>
    /// Gets the schema version the database is at now: <see cref="CurrentVersion"/>, unless it is read-only.
    /// </summary>
    public int? SchemaVersion => TryEnsureOpen()?.SchemaVersion;

    /// <summary>
    /// Gets the schema version the database was upgraded from when it was opened, or <c>null</c> when no upgrade ran.
    /// </summary>
    public int? MigratedFrom => TryEnsureOpen()?.MigratedFrom;

    /// <summary>
    /// Gets a counter that every committed write, restore and recovery raises (<c>Generation</c>), so a cache can
    /// tell whether the data changed since it filled.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Gets a counter that only a restore raises: an admin's snapshot restore, a corruption restore (or a start empty)
    /// while the server runs, and the restore at open. While the server runs it moves once the
    /// file is replaced, just before <see cref="Generation"/> moves for the same restore. A cache that drops its entries one
    /// write at a time compares it on every read and starts over when it moved (its entries hold the state from before the
    /// data was replaced, and no write named them), and discards a fill whose query began before it moved.
    /// </summary>
    public long RestoreCount => Interlocked.Read(ref _restores);

    /// <summary>
    /// Gets the last <c>PRAGMA quick_check</c> in this process, or <c>null</c>.
    /// </summary>
    public SqliteCheckResult? LastQuickCheck => _lastQuickCheck;

    /// <summary>
    /// Gets the last corruption restore in this process, or <c>null</c>.
    /// </summary>
    public SqliteRecoveryResult? LastRecovery => _lastRecovery;

    /// <summary>
    /// Gets the last rebuild of the indexes at open in this process (<see cref="DamageMarkerPath"/>), or <c>null</c>.
    /// </summary>
    public SqliteReindexResult? LastReindex => _lastReindex;

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
    /// Gets the time recorded with the kept snapshots (<see cref="KeepSnapshotsMarkerPath"/>), while there are any: the later
    /// of the last incident (a restore at open, an admin's recovery, a start empty) and the newest kept snapshot's time plus
    /// a second, never moved back, so it is later than every kept snapshot; <c>null</c> when nothing is kept (no incident, no
    /// snapshot then, or an admin has restored one of them since). Which snapshots are kept goes by their names, not by this
    /// time: one taken since, while the clock was behind, is dated before it and still rotates.
    /// </summary>
    public DateTime? KeepSnapshotsBeforeUtc
    {
        get
        {
            TryEnsureOpen();
            return _kept?.BeforeUtc;
        }
    }

    /// <summary>
    /// Gets how the data was found when the kept snapshots were recorded: <see cref="SqliteDatabaseStatus.RestoredFromSnapshot"/>
    /// or <see cref="SqliteDatabaseStatus.RecoveredEmpty"/>; <c>null</c> when nothing is kept, or the marker was written
    /// before it said.
    /// </summary>
    public SqliteDatabaseStatus? KeptSnapshotsIncident
    {
        get
        {
            TryEnsureOpen();
            return _kept?.Incident;
        }
    }

    // The 503's sentence while the database can't be opened: the snapshot's name when a restore waits for one that
    // couldn't be read just then, else the general one. Path-free, as every message a client sees.
    private string UnavailableMessage => _unavailableMessage ?? SqliteMessages.Unavailable;

    // The pragmas of this database's writable connections (SecureDelete).
    private string WritablePragmas => SecureDelete ? OpenPragmas : OpenPragmasWithoutSecureDelete;

    /// <summary>
    /// Opens the database now, if it isn't open yet: the integrity check, a corruption restore, the upgrade. Never
    /// throws; the kit's hosted service calls it once the server has started.
    /// </summary>
    /// <returns>The status.</returns>
    public SqliteDatabaseStatus Open() => Status;

    /// <summary>
    /// Throws the 503 every <see cref="WriteAsync{T}"/> throws while the database is read-only, and does nothing while it
    /// is writable. For a request's write that looks something up before it changes anything.
    /// </summary>
    /// <exception cref="PluginException">503: the database is read-only or can't be opened.</exception>
    public void ThrowIfReadOnly() => ThrowIfReadOnly(EnsureOpen());

    /// <summary>
    /// Runs a read on a pooled connection. Several statements see one state of the data only inside a transaction the
    /// read begins itself (<c>connection.BeginTransaction(deferred: true)</c>, each command given it).
    /// </summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="read">The read; it must not keep the connection.</param>
    /// <returns>Its result.</returns>
    /// <exception cref="PluginException">503: the database can't be opened, its upgrade failed, its file is missing, the
    /// read found it damaged (it is read-only from then on), a read failed on disk, or another connection kept the
    /// database locked for more than <see cref="CommandTimeoutSeconds"/> (<see cref="SqliteMessages.Busy"/>).</exception>
    public T Read<T>(Func<SqliteConnection, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var state = EnsureOpen();
        if (state.Status == SqliteDatabaseStatus.ReadOnlyMigrationFailed)
        {
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.MigrationFailed);
        }

        if (state.Missing)
        {
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Missing);
        }

        EnterShared();
        try
        {
            HoldCorruptGuardIfDamaged();
            using var connection = OpenForRead(state);
            return read(connection);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            MarkCorrupt(DescribeSqliteError(ex), ex);
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Corrupt, ex);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            throw Busy(ex, "read");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteIoErr)
        {
            // A read error says nothing about the data (a lock another program holds, a network drive): nothing is marked.
            _logger.LogWarning(ex, "A read of {File} failed on disk; nothing was changed", FilePath);
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.ReadFailed, ex);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    /// <summary>
    /// Runs a write: one at a time, in a <c>BEGIN IMMEDIATE</c> transaction that commits when the write returns and
    /// rolls back when it throws. Every command must be given the transaction. The wait for this database's writer lock
    /// (the writes before it in this process) has the same budget as a lock another connection holds: what is left of
    /// <see cref="CommandTimeoutSeconds"/> since the call began, then 503 <see cref="SqliteMessages.Busy"/>. So while
    /// the database is busy, writes queued behind one another answer within about 5 s each, not 5, 11, 16 … s one after
    /// another, and one member's queue never holds up everyone else's answer.
    /// </summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="write">The write; synchronous, it must not keep the connection or the transaction.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer lock; a started write always finishes.</param>
    /// <returns>Its result.</returns>
    /// <exception cref="PluginException">503: the database is read-only, can't be opened, is damaged (found now), the
    /// disk is full, another connection kept it locked for more than <see cref="CommandTimeoutSeconds"/>, or the writes
    /// before this one kept the writer lock that long (<see cref="SqliteMessages.Busy"/>; the writer lock is released
    /// then, the write rolled back or never begun).</exception>
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken)
        => RunWriteAsync(write, commit: true, cancellationToken);

    /// <summary>
    /// Runs a write as <see cref="WriteAsync{T}"/> does,
    /// or, with <paramref name="commit"/> false, runs it and rolls it back: an import's dry run is the real import undone
    /// (<see cref="SqliteStoreImport"/>).
    /// </summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="write">The write.</param>
    /// <param name="commit">Whether the transaction commits.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    /// <returns>Its result.</returns>
    internal async Task<T> RunWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, bool commit, CancellationToken cancellationToken)
        => (await RunWriteAsync(write, commit, preOpOperation: null, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// Runs a write as
    /// <see cref="RunWriteAsync{T}(Func{SqliteConnection, SqliteTransaction, T}, bool, CancellationToken)"/> does,
    /// after a pre-operation snapshot taken in the same hold of the writer lock: the snapshot then holds exactly the
    /// state the write replaces, and no other write can commit in between and be in neither. Lock order: the writer
    /// lock, then the snapshot lock.
    /// </summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="write">The write.</param>
    /// <param name="commit">Whether the transaction commits.</param>
    /// <param name="preOpOperation">One of <see cref="SnapshotOperations.All"/>, or <c>null</c> for no snapshot.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer and snapshot locks.</param>
    /// <returns>Its result and the snapshot's path (<c>null</c> when none was asked for or it couldn't be written).</returns>
    internal Task<(T Result, string? Safety)> RunWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, bool commit, string? preOpOperation, CancellationToken cancellationToken)
        => RunWriteAsync(write, commit, preOpOperation, check: null, cancellationToken);

    /// <summary>
    /// Runs a write as
    /// <see cref="RunWriteAsync{T}(Func{SqliteConnection, SqliteTransaction, T}, bool, string, CancellationToken)"/>
    /// does, with <paramref name="check"/> run first, before the pre-operation snapshot, in a transaction of its own
    /// that is always rolled back, in the same hold of the writer lock: a check that throws (an import whose plan
    /// changed since its dry run) writes nothing and leaves no snapshot (a refused import used to take an <c>import</c>
    /// snapshot, one more in the rotation for a write that never happened). The check sees exactly the state the write
    /// will, since no other write can commit in between. It runs only when a snapshot is asked for (without one, the
    /// write's own check refuses it, rolled back), and a check that does the write's work holds the writer lock for it
    /// twice.
    /// </summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="write">The write.</param>
    /// <param name="commit">Whether the transaction commits.</param>
    /// <param name="preOpOperation">One of <see cref="SnapshotOperations.All"/>, or <c>null</c> for no snapshot.</param>
    /// <param name="check">Runs before the snapshot and throws to refuse the write; <c>null</c> for none.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer and snapshot locks.</param>
    /// <returns>Its result and the snapshot's path (<c>null</c> when none was asked for or it couldn't be written).</returns>
    internal async Task<(T Result, string? Safety)> RunWriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, bool commit, string? preOpOperation, Action<SqliteConnection, SqliteTransaction>? check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        var begun = Stopwatch.GetTimestamp();
        ThrowIfReadOnly(EnsureOpen());
        await WaitForWriterAsync(begun, cancellationToken).ConfigureAwait(false);
        (T Result, string? Safety) outcome;
        try
        {
            ThrowIfReadOnly(EnsureOpen());
            string? safety = null;
            if (preOpOperation is not null)
            {
                if (check is not null)
                {
                    WriteCore(
                        (connection, transaction) =>
                        {
                            check(connection, transaction);
                            return 0;
                        },
                        commit: false);
                }

                safety = await PreOpSnapshotAsync(preOpOperation, spare: null, cancellationToken).ConfigureAwait(false);

                // The snapshot's check may have found the live database damaged.
                ThrowIfReadOnly(EnsureOpen());
            }

            var result = WriteCore(write, commit);
            if (commit)
            {
                Interlocked.Increment(ref _generation);
            }

            outcome = (result, safety);
        }
        finally
        {
            _writeLock.Release();
        }

        // A checkpoint whose tries ran out is tried again after a later write.
        if (commit)
        {
            RetryPendingCheckpoint();
        }

        return outcome;
    }

    /// <summary>
    /// Runs <c>PRAGMA quick_check</c> (at the start of the daily maintenance). A failure makes the database read-only
    /// with the integrity banner until it is restored.
    /// </summary>
    /// <returns>The result; <c>null</c> when the database can't be opened.</returns>
    public SqliteCheckResult? QuickCheck()
    {
        if (TryEnsureOpen() is not { } state)
        {
            return null;
        }

        try
        {
            return QuickCheckCore(state);
        }
        catch (Exception ex) when (IsBindingFailure(ex))
        {
            NoteBindingFailure(ex);
            return null;
        }
    }

    /// <summary>
    /// Asks for a <c>PRAGMA wal_checkpoint(TRUNCATE)</c> and returns at once (it replaces an earlier checkpoint that
    /// ran on the caller's thread): every frame of the <c>-wal</c> goes into the database file and the <c>-wal</c> is
    /// cut to zero bytes, so the earlier versions of the pages a write changed (a deleted row's, which the <c>-wal</c>
    /// keeps until it is reset) leave it soon rather than at the next automatic checkpoint. A plugin asks for it after
    /// the commit of a deletion that must not linger.
    /// </summary>
    /// <remarks>
    /// <para>The checkpoint runs in the background: after <see cref="CheckpointDelay"/> (the requests of a burst become one),
    /// on a connection with no busy wait, holding this database's writer lock only while it runs and never while it waits:
    /// another connection reading an older snapshot makes it stop at once, incomplete, and it is tried again after each of
    /// <see cref="CheckpointRetryDelays"/> (a write that wants the writer lock meanwhile gets it at once; before, the
    /// checkpoint held it through its 5 s busy wait, and a write queued behind it was a 503). When the tries run out it is
    /// logged at Warning and stays pending (<see cref="CheckpointPending"/>, Diagnostics' <c>Checkpoint</c>): the next write
    /// to commit and the daily maintenance's <c>Checkpoint</c> step try again. Nothing is done while the database is
    /// read-only, so a damaged file is never written; the request stays pending.</para>
    /// </remarks>
    /// <returns>A task that completes when a checkpoint covering this request completed (<c>true</c>), or when its tries
    /// ran out or the database is read-only or stopping (<c>false</c>, still pending). It never fails.</returns>
    public Task<bool> RequestCheckpoint()
    {
        lock (_checkpointLock)
        {
            if (_disposed)
            {
                return Task.FromResult(false);
            }

            _checkpointRequested++;
            return StartCheckpointRoundLocked();
        }
    }

    /// <summary>
    /// Waits for the checkpoints asked for so far, without asking for one: the round running now, if any.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><c>true</c> when none is pending (at once when none was asked for), <c>false</c> when the round running now
    /// ended with one pending, or one is pending and no round runs.</returns>
    public Task<bool> WaitForCheckpointAsync(CancellationToken cancellationToken)
    {
        lock (_checkpointLock)
        {
            return _checkpointRound is { } round
                ? round.Task.WaitAsync(cancellationToken)
                : Task.FromResult(_checkpointCompleted >= _checkpointRequested);
        }
    }

    /// <summary>
    /// Asks for a checkpoint (<see cref="RequestCheckpoint"/>) and waits for its outcome: the daily maintenance's
    /// <c>Checkpoint</c> step, the backstop of every checkpoint asked for since. Never for a request's thread, which asks
    /// with <see cref="RequestCheckpoint"/> and goes on.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait (the checkpoint itself goes on).</param>
    /// <returns>Whether the <c>-wal</c> was checkpointed in full and cut.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task<bool> CheckpointAsync(CancellationToken cancellationToken) => RequestCheckpoint().WaitAsync(cancellationToken);

    /// <summary>
    /// Writes a daily snapshot, <c>&lt;snapshots&gt;/&lt;store&gt;-&lt;yyyyMMdd-HHmmss&gt;.db</c>, checked with
    /// <c>PRAGMA integrity_check</c> before it gets its name, and deletes the oldest daily snapshots, keeping
    /// <paramref name="keep"/>. With <see cref="PreOpKeepWithin"/> set it also deletes the pre-operation snapshots, and
    /// the copies of <see cref="PreOpCopies"/>, older than that (they used to wait for the next pre-operation snapshot,
    /// which may never come). Does nothing while the database is read-only (or damaged); writes no snapshot when
    /// <paramref name="keep"/> is 0. A snapshot that fails its check is deleted and logged as an Error, the earlier
    /// ones are kept, and the live database is checked next.
    /// </summary>
    /// <param name="keep">How many daily snapshots to keep (<c>SnapshotsToKeep</c>); 0 disables them.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot to finish.</param>
    /// <returns>The new snapshot's path, or <c>null</c> when none was written.</returns>
    public async Task<string?> SnapshotAsync(int keep, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        if (!CanSnapshot("daily"))
        {
            return null;
        }

        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var utc = _timeProvider.GetUtcNow().UtcDateTime;
            ExpirePreOp(utc);
            if (keep == 0)
            {
                return null;
            }

            var path = SnapshotRotation.NewPath(SnapshotDirectory, StoreName, Extension, utc);
            if (!WriteSnapshot(path))
            {
                return null;
            }

            SnapshotRotation.Rotate(SnapshotDirectory, StoreName, Extension, keep, _logger, nowUtc: utc, companionSuffixes: SqliteFiles.SidecarSuffixes, keepNames: KeptForRotation()?.Names);
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
    /// Takes a pre-operation snapshot before a risky operation (migrations, an import, a restore, a purge, a plugin's
    /// own): <c>&lt;snapshots&gt;/pre-op/&lt;store&gt;-&lt;operation&gt;-&lt;yyyyMMdd-HHmmss&gt;.db</c>, checked as a
    /// daily one, rotated on its own (the newest <see cref="SnapshotOperations.ToKeep"/>), so retries never push the
    /// daily snapshots out. Does nothing while the database is read-only.
    /// </summary>
    /// <param name="operation">One of <see cref="SnapshotOperations.All"/>.</param>
    /// <param name="cancellationToken">Cancels waiting for another snapshot to finish.</param>
    /// <returns>The new snapshot's path, or <c>null</c> when none was written.</returns>
    public Task<string?> PreOpSnapshotAsync(string operation, CancellationToken cancellationToken)
        => PreOpSnapshotAsync(operation, spare: null, cancellationToken);

    /// <summary>
    /// Lists the database's snapshots, daily and pre-operation ones, newest first, for <c>GET
    /// /&lt;Route&gt;/Admin/Snapshots</c>, each one's schema version read from its header.
    /// </summary>
    /// <returns>The snapshots.</returns>
    public IReadOnlyList<StoreSnapshotInfo> ListSnapshots()
    {
        TryEnsureOpen();
        var kept = _kept;
        return SnapshotRotation.List(SnapshotDirectory, StoreName, Extension)
            .Select(s => new StoreSnapshotInfo(s.Name, SnapshotKind.Daily, null, s.Utc, s.Size, SqliteFiles.ReadHeader(s.Path)?.UserVersion, kept?.Keeps(s) == true))
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension)
                .Select(s => new StoreSnapshotInfo(s.Name, SnapshotKind.PreOp, s.Operation, s.Utc, s.Size, SqliteFiles.ReadHeader(s.Path)?.UserVersion, kept?.Keeps(s) == true)))
            .OrderByDescending(s => s.Utc)
            .ThenBy(s => s.Kind)
            .ToList();
    }

    /// <summary>
    /// Lists the quarantine folders next to the database, newest first (kept until an admin deletes them).
    /// </summary>
    /// <returns>The folders; none when there are none or the folder can't be listed.</returns>
    public IReadOnlyList<SqliteQuarantineInfo> ListQuarantines()
    {
        var found = new List<SqliteQuarantineInfo>();
        try
        {
            if (!Directory.Exists(Folder))
            {
                return found;
            }

            foreach (var directory in new DirectoryInfo(Folder).EnumerateDirectories(QuarantinePrefix + "*"))
            {
                var stamp = directory.Name.AsSpan(QuarantinePrefix.Length);
                if (stamp.Length < TimestampFormat.Length
                    || !DateTime.TryParseExact(stamp[..TimestampFormat.Length], TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
                {
                    continue;
                }

                var files = directory.GetFiles();
                found.Add(new SqliteQuarantineInfo(directory.Name, utc, files.Select(f => f.Name).Order(StringComparer.Ordinal).ToList(), files.Sum(f => f.Length)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not list the quarantine folders of {File}", FilePath);
        }

        return found.OrderByDescending(q => q.Utc).ThenByDescending(q => q.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// <i>Restore snapshot</i> in the running server. On a healthy database, under the writer lock: a pre-operation
    /// snapshot of the current state (whose rotation never deletes the snapshot being restored), then, in the same
    /// hold, the snapshot is copied into the live database with <c>BackupDatabase</c>, this database's pools cleared
    /// first and no connection of ours open, so the live database stays in WAL mode and nothing beside it changes; an
    /// older snapshot is then upgraded. No write can commit between the safety snapshot and the restore. On a damaged
    /// database (<see cref="SqliteDatabaseStatus.ReadOnlyCorrupt"/>) it is the corruption restore with this snapshot
    /// (<see cref="RecoverAsync(string, CancellationToken)"/>). The plugin then runs its verify sweep and clears its
    /// caches.
    /// </summary>
    /// <param name="name">A daily or pre-operation snapshot's file name, as <see cref="ListSnapshots"/> gives it.</param>
    /// <param name="cancellationToken">Cancels waiting for the database.</param>
    /// <returns>What was restored and the safety snapshot.</returns>
    /// <exception cref="PluginException">404 no snapshot of this database has that name; 409 the snapshot is from a newer
    /// plugin version or fails its integrity check, or the database is read-only for another reason; 503 the database
    /// can't be opened or stayed in use, or the snapshot couldn't be read just then.</exception>
    public async Task<SnapshotRestoreResult> RestoreSnapshotAsync(string name, CancellationToken cancellationToken)
    {
        var state = EnsureOpen();
        var snapshot = FindSnapshot(name);
        if (state.Status == SqliteDatabaseStatus.ReadOnlyCorrupt)
        {
            // The damaged database is kept in its quarantine folder (LastRecovery), not in a safety snapshot.
            await RecoverAsync(snapshot.Name, cancellationToken).ConfigureAwait(false);
            return new SnapshotRestoreResult(snapshot.Name, null, LoadedVersion ?? CurrentVersion, MigratedFrom, KeepSnapshotsBeforeUtc);
        }

        ThrowIfReadOnlyForAdmin(state);
        var version = VerifySnapshotForRestore(snapshot);

        // The writer lock first, then the safety snapshot (the writer → snapshot order of every path that takes both).
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? safety;
        int? migratedFrom;
        try
        {
            ThrowIfReadOnlyForAdmin(EnsureOpen());

            // The safety snapshot's rotation spares the snapshot being restored (the oldest pre-operation one, say).
            safety = await PreOpSnapshotAsync(SnapshotOperations.Restore, snapshot.Operation is null ? null : snapshot.Name, cancellationToken).ConfigureAwait(false);

            // Its check may have found the live database damaged: then the corruption restore is the way, not this one.
            ThrowIfReadOnlyForAdmin(EnsureOpen());
            string? damage;
            Exception? damageError;
            EnterExclusive();
            try
            {
                migratedFrom = RestoreCoreNotingDamage(snapshot, version, out damage, out damageError);
            }
            finally
            {
                _gate.ExitWriteLock();

                // Also when the restore failed part way: a cache that refills for nothing costs less than a stale one.
                Interlocked.Increment(ref _restores);
                Interlocked.Increment(ref _generation);
            }

            // The live database was damaged after all (a page no check before could read): marked outside the gate,
            // then the 409 of a damaged database, never a raw SqliteException.
            if (damage is not null)
            {
                MarkCorrupt("the restore: " + damage, damageError);
                ThrowIfReadOnlyForAdmin(EnsureOpen());
                throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Corrupt, damageError);
            }
        }
        finally
        {
            _writeLock.Release();
        }

        AfterSnapshotRestored(snapshot);
        RememberRestore(snapshot);
        _logger.LogWarning(
            "Restored {File} from the snapshot {Snapshot} (schema {Version}); the state before was saved as {Safety}",
            FilePath,
            snapshot.Name,
            version,
            safety is null ? "(none)" : Path.GetFileName(safety));
        return new SnapshotRestoreResult(snapshot.Name, safety is null ? null : Path.GetFileName(safety), version, migratedFrom, KeepSnapshotsBeforeUtc);
    }

    /// <summary>
    /// The corruption restore for a database found damaged while the server runs
    /// (<see cref="SqliteDatabaseStatus.ReadOnlyCorrupt"/>); the same happens on its own when the database fails its
    /// check at open. The status says what was found then, so under the writer lock the database in place is checked
    /// again first, on a copy of it with its <c>-wal</c> and <c>-journal</c> as found (the <c>-shm</c> left out), as a
    /// restart would open it: one that passes <c>PRAGMA integrity_check</c> and holds a schema (an admin put a good
    /// copy back while the server runs, or the damage found was a read that failed) is refused with 409
    /// <see cref="StoreMessages.ReadableFileInPlace"/> and nothing is moved; a restart opens it (as for the JSON
    /// store). Then, with no connection of ours open and this database's pools cleared: the chosen snapshot (or the
    /// newest one that passes) is copied to a temporary file and checked with <c>PRAGMA integrity_check</c>; the
    /// database, its <c>-wal</c> and <c>-shm</c> are moved together into <c>quarantine-&lt;yyyyMMdd-HHmmss&gt;/</c>;
    /// the checked copy is renamed into place with no sidecars; the database is opened again (checked, upgraded). When
    /// no snapshot passes, nothing is moved (409); starting empty is
    /// <see cref="RecoverAsync(string, bool, CancellationToken)"/> with <c>startEmpty</c>. Changes made after the
    /// snapshot are lost; <see cref="SqliteRecoveryResult.LostHours"/> says how many hours that covers. Should a step
    /// after the move aside fail (the rename, the open, the check in place, the upgrade, a new empty database's
    /// schema), the copy or the new file goes, what was moved goes back where it was, the quarantine folder and the
    /// markers are as they were, and the answer says so (<see cref="SqliteMessages.RecoveryPutBack"/>: 503, or 409 for
    /// a snapshot that can't be upgraded); if a file can't go back, the answer and the banner name the folder it is in
    /// (<see cref="SqliteMessages.RecoveryNotPutBack"/>, as for the JSON store), and <see cref="RecoveryJournalPath"/>
    /// stays, so the next start puts it back first. Each step is in that journal before it is taken: a server stopped
    /// during the recovery leaves what the next open needs to put it all back. The check in place and its copy run in
    /// this request under the writer lock: seconds for User Ratings' sizes, and writes are refused anyway while the
    /// database is damaged.
    /// </summary>
    /// <param name="snapshotName">A snapshot's file name, or <c>null</c> for the newest one that passes its check.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    /// <returns>Where the damaged files went and what took their place.</returns>
    /// <exception cref="PluginException">404 no snapshot has that name; 409 the database isn't damaged (use
    /// <see cref="RestoreSnapshotAsync"/>), a database in place passes its check now (put back while the server runs:
    /// <see cref="StoreMessages.ReadableFileInPlace"/>; a restart opens it), the named snapshot fails its check, or none
    /// passes; 503 it can't be opened, stayed in use, couldn't be checked again or moved aside, or a snapshot couldn't be
    /// read just then.</exception>
    public Task<SqliteRecoveryResult> RecoverAsync(string? snapshotName, CancellationToken cancellationToken)
        => RecoverAsync(snapshotName, startEmpty: false, cancellationToken);

    /// <summary>
    /// The corruption restore as <see cref="RecoverAsync(string, CancellationToken)"/>, or, with
    /// <paramref name="startEmpty"/>, the admin's explicit choice to start empty: no snapshot is restored, the damaged
    /// database (or what was found in place of a missing one) is moved into a quarantine folder, a new empty database is
    /// created (<see cref="SqliteDatabaseStatus.RecoveredEmpty"/>), and the daily snapshots taken before it are kept by
    /// <see cref="KeepSnapshotsMarkerPath"/> until one of them is restored. The kit never starts empty on its own.
    /// </summary>
    /// <param name="snapshotName">A snapshot's file name, or <c>null</c>.</param>
    /// <param name="startEmpty">Start empty instead of restoring a snapshot; <paramref name="snapshotName"/> must be <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer lock.</param>
    /// <returns>Where the damaged files went and what took their place.</returns>
    /// <exception cref="PluginException">400 a snapshot and <paramref name="startEmpty"/> both given; else as
    /// <see cref="RecoverAsync(string, CancellationToken)"/>.</exception>
    public async Task<SqliteRecoveryResult> RecoverAsync(string? snapshotName, bool startEmpty, CancellationToken cancellationToken)
    {
        if (startEmpty && snapshotName is not null)
        {
            throw PluginException.Invalid("Choose a snapshot to restore or start empty, not both.");
        }

        var state = EnsureOpen();
        if (state.Status != SqliteDatabaseStatus.ReadOnlyCorrupt)
        {
            throw PluginException.Conflict("The database passed its checks, so there is nothing to recover; restore a snapshot instead.");
        }

        var snapshot = snapshotName is null ? null : FindSnapshot(snapshotName);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (EnsureOpen().Status != SqliteDatabaseStatus.ReadOnlyCorrupt)
            {
                throw PluginException.Conflict("The database was restored in the meantime.");
            }

            // The status says what was found at open or since; an admin may have put a good database back in place
            // since, which this would quarantine as damaged (as for the JSON store).
            ThrowIfReadableDatabaseInPlace();
            EnterExclusive();
            try
            {
                var current = EnsureOpen();
                return RecoverAtRuntime(current.Problem ?? "found damaged", snapshot, startEmpty, damaged: !current.Missing);
            }
            finally
            {
                _gate.ExitWriteLock();

                // Also when it failed after the files changed (the next use opens them): caches refill rather than go stale.
                Interlocked.Increment(ref _restores);
                Interlocked.Increment(ref _generation);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Gets the data for an export's <c>Data</c> with the schema version it is at, read in one transaction so
    /// it is one state of the data. Read-only databases export faithfully or not at all, as the JSON store does: after a
    /// failed upgrade, for a newer version's database and for a damaged one it is 503.
    /// </summary>
    /// <param name="read">Builds the plugin's <c>Data</c> object; every command must be given the transaction.</param>
    /// <returns>The data at <see cref="CurrentVersion"/>.</returns>
    /// <exception cref="PluginException">503 as above, when the database can't be opened, or when another connection kept it
    /// locked for more than <see cref="CommandTimeoutSeconds"/> (<see cref="SqliteMessages.Busy"/>).</exception>
    public StoreExportData ExportData(Func<SqliteConnection, SqliteTransaction, JsonObject> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var state = EnsureOpen();
        switch (state.Status)
        {
            case SqliteDatabaseStatus.ReadOnlyMigrationFailed:
                throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.ExportMigrationFailed);
            case SqliteDatabaseStatus.ReadOnlyNewerVersion:
                throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.ExportNewerVersion);
            case SqliteDatabaseStatus.ReadOnlyCorrupt:
                throw new PluginException(PluginErrorKind.Unavailable, state.Missing ? SqliteMessages.Missing : SqliteMessages.ExportCorrupt);
        }

        var data = Read(connection =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = read(connection, transaction);
            transaction.Commit();
            return result;
        });
        return new StoreExportData(CurrentVersion, data);
    }

    /// <summary>
    /// Clears this database's connection pools (never every pool in the process: Jellyfin's <c>jellyfin.db</c> is
    /// pooled in it too), which checkpoints the WAL and lets go of the file. The kit's hosted service calls it when the
    /// server stops; later uses open new connections.
    /// </summary>
    /// <param name="reason">Why, for the log.</param>
    /// <returns><c>free</c> when no handle to the file is left, <c>missing</c>, <c>locked</c>, or <c>error</c>.</returns>
    public string Release(string reason)
    {
        try
        {
            ReleaseConnections();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not release the connections to {File} ({Reason})", FilePath, reason);
            return "error";
        }

        var outcome = SqliteFiles.TryOpenExclusive(FilePath);
        _logger.LogInformation(
            "Released the connections to {File} ({Reason}): the file is {Outcome}, sidecars left: {Sidecars}",
            FilePath,
            reason,
            outcome,
            SqliteFiles.ListSidecars(FilePath).Count);
        return outcome;
    }

    /// <summary>
    /// Describes the database for Diagnostics: its status, versions, last checks, snapshots, quarantine folders
    /// and the last recovery. Opens it on first use and never throws.
    /// </summary>
    /// <returns>Plain values.</returns>
    public IDictionary<string, object?> Describe()
    {
        var state = TryEnsureOpen();
        var quarantines = ListQuarantines();
        IReadOnlyList<StoreSnapshotInfo> snapshots;
        try
        {
            snapshots = ListSnapshots();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            snapshots = [];
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Engine"] = "SQLite",
            ["Name"] = StoreName,
            ["File"] = FilePath,
            ["Version"] = state?.SchemaVersion,
            ["CurrentVersion"] = CurrentVersion,
            ["Status"] = (state?.Status ?? SqliteDatabaseStatus.Unavailable).ToString(),
            ["ReadOnly"] = state is null || IsReadOnlyStatus(state.Status),
            ["Message"] = state is null ? UnavailableMessage : state.Message,
            ["Notice"] = Notice,
            ["Error"] = state is null ? _lastOpenError : null,
            ["MigratedFrom"] = state?.MigratedFrom,
            ["Generation"] = Generation,
            ["LastQuickCheck"] = _lastQuickCheck?.ToDictionary(),
            ["LastSnapshotUtc"] = LastSnapshotUtc,
            ["KeepSnapshotsBeforeUtc"] = KeepSnapshotsBeforeUtc,
            ["DailySnapshots"] = snapshots.Count(s => s.Kind == SnapshotKind.Daily),
            ["PreOpSnapshots"] = snapshots.Count(s => s.Kind == SnapshotKind.PreOp),
            ["NewestSnapshot"] = snapshots.Count == 0 ? null : snapshots[0].ToDictionary(),
            ["Recovery"] = _lastRecovery?.ToDictionary(),
            ["Reindexed"] = _lastReindex?.ToDictionary(),
            ["DamageMarker"] = File.Exists(DamageMarkerPath),
            ["LastRestore"] = ReadLastRestore() is { } last ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["Snapshot"] = last.Name, ["Utc"] = last.Utc } : null,
            ["Quarantines"] = quarantines.Take(20).Select(q => q.ToDictionary()).ToList(),
            ["QuarantineCount"] = quarantines.Count,
            ["Checkpoint"] = DescribeCheckpoint()
        };
    }

    /// <summary>
    /// Describes the requested checkpoints (<see cref="RequestCheckpoint"/>) for Diagnostics' <c>Checkpoint</c>: whether one
    /// is pending and whether a round runs now, the requests counted and the latest one a complete checkpoint covered, when
    /// the last one completed and the last try ran, its outcome (<c>Complete</c>, <c>Readers</c>: another connection read an
    /// older snapshot, <c>Writer</c>: a write held the writer lock, <c>ReadOnly</c>, <c>Failed</c>), and the rounds whose
    /// tries ran out. A probe waits for <c>Pending</c> false to know a deletion's checkpoint is done.
    /// </summary>
    /// <returns>Plain values.</returns>
    public IDictionary<string, object?> DescribeCheckpoint()
    {
        lock (_checkpointLock)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Pending"] = _checkpointCompleted < _checkpointRequested,
                ["Running"] = _checkpointRound is not null,
                ["Requested"] = _checkpointRequested,
                ["Completed"] = _checkpointCompleted,
                ["LastCompleteUtc"] = _lastCheckpointUtc,
                ["LastTryUtc"] = _lastCheckpointTryUtc,
                ["LastOutcome"] = _lastCheckpointOutcome,
                ["FailedRounds"] = _checkpointRoundsFailed
            };
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A requested checkpoint stops: no new try, and a try running now (which holds the writer lock) is waited for.
        _checkpointStop.Cancel();
        try
        {
            _writeLock.Wait(TimeSpan.FromSeconds(CommandTimeoutSeconds));
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            ReleaseConnections();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not release the connections to {File} on dispose", FilePath);
        }
        finally
        {
            ReleaseCorruptGuard();
        }

        _writeLock.Dispose();
        _snapshotLock.Dispose();
        _gate.Dispose();
        _checkpointStop.Dispose();
    }

    /// <summary>
    /// Tells whether an exception is a failure to bind or load the server's SQLite assemblies, as opposed to an error
    /// of an operation.
    /// </summary>
    /// <param name="ex">The exception.</param>
    /// <returns><c>true</c> for a binding failure.</returns>
    internal static bool IsBindingFailure(Exception ex)
        => ex switch
        {
            FileNotFoundException f => f.FileName is { } name && (name.StartsWith("Microsoft.Data.Sqlite", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase)),
            FileLoadException or TypeLoadException or MissingMemberException or BadImageFormatException or TypeInitializationException or DllNotFoundException or EntryPointNotFoundException => true,
            _ => false
        };

    // The kit's admin data operations answer 409 while the database is read-only (the JSON store's rule).
    internal void ThrowIfReadOnlyForAdmin() => ThrowIfReadOnlyForAdmin(EnsureOpen());

    private static void ThrowIfReadOnlyForAdmin(OpenState state)
    {
        if (IsReadOnlyStatus(state.Status))
        {
            throw new PluginException(PluginErrorKind.Conflict, state.Message ?? ReadOnlyMessage(state.Status))
            {
                Code = state.Status == SqliteDatabaseStatus.ReadOnlyCorrupt ? DamagedCode : null
            };
        }
    }

    private static bool IsReadOnlyStatus(SqliteDatabaseStatus status)
        => status is SqliteDatabaseStatus.ReadOnlyNewerVersion or SqliteDatabaseStatus.ReadOnlyMigrationFailed or SqliteDatabaseStatus.ReadOnlyCorrupt or SqliteDatabaseStatus.Unavailable;

    private static string ReadOnlyMessage(SqliteDatabaseStatus status) => status switch
    {
        SqliteDatabaseStatus.ReadOnlyNewerVersion => SqliteMessages.NewerVersion,
        SqliteDatabaseStatus.ReadOnlyMigrationFailed => SqliteMessages.MigrationFailed,
        SqliteDatabaseStatus.ReadOnlyCorrupt => SqliteMessages.Corrupt,
        _ => SqliteMessages.Unavailable
    };

    private static void ThrowIfReadOnly(OpenState state)
    {
        if (IsReadOnlyStatus(state.Status))
        {
            throw new PluginException(
                PluginErrorKind.Unavailable,
                state.Missing ? SqliteMessages.Missing : state is { Status: SqliteDatabaseStatus.ReadOnlyCorrupt, IndexesOnly: true } ? SqliteMessages.IndexesDamaged : ReadOnlyMessage(state.Status));
        }
    }

    private static bool IsCorruption(SqliteException ex) => ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase;

    // SQLITE_BUSY (5) and SQLITE_LOCKED (6): another connection (another process, a backup tool, an admin's sqlite3 shell)
    // held a lock for longer than the command timeout. Never damage.
    private static bool IsBusy(SqliteException ex) => ex.SqliteErrorCode is SqliteBusy or SqliteLocked;

    // Whether a file is there but this process may not read it (an ACL), which no retry changes; false when it can be
    // opened, is in use (a sharing or lock violation) or is gone.
    private static bool MayNotRead(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // No SQLite type here: it describes a failure to open, which may be a failure to bind Microsoft.Data.Sqlite itself.
    private static string DescribeError(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static string DescribeSqliteError(SqliteException ex)
        => string.Create(CultureInfo.InvariantCulture, $"SQLite error {ex.SqliteErrorCode} ({ex.SqliteExtendedErrorCode}): {ex.Message}");

    private static SqliteConnection OpenConnection(string connectionString, string? pragmas)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            if (pragmas is not null)
            {
                Execute(connection, null, pragmas);
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Review SQL queries for security vulnerabilities: the kit's own pragmas and the plugin's migration steps, never user input.
        command.CommandText = sql;
#pragma warning restore CA2100
        return command;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = CreateCommand(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = CreateCommand(connection, transaction, sql);
        return command.ExecuteScalar();
    }

    private static int ReadUserVersion(SqliteConnection connection, SqliteTransaction? transaction)
        => Convert.ToInt32(Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string FileConnectionString(string path, bool readOnly)
        => new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = CommandTimeoutSeconds
        }.ToString();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ClearPool(string? connectionString)
    {
        if (connectionString is null)
        {
            return;
        }

        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static List<string> ForeignKeyViolations(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<string>();
        using var command = CreateCommand(connection, transaction, "PRAGMA foreign_key_check;");
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{reader.GetString(0)} row {(reader.IsDBNull(1) ? "?" : reader.GetInt64(1).ToString(CultureInfo.InvariantCulture))} -> {reader.GetString(2)}"));
        }

        return rows;
    }

    private OpenState EnsureOpen()
    {
        var state = _state;
        if (state is not null)
        {
            return state;
        }

        state = EnsureOpenLocked(out var walLeft);

        // A -wal with frames left at a writable open (the process before stopped with a checkpoint still pending, as a
        // kill or a power cut stops it): a checkpoint is asked for, as the write that left them had, so the earlier
        // versions of a deleted row's pages leave the database file and the -wal in the background soon after the
        // start, not at the next deletion or the daily maintenance. A request is in memory only, so each start that
        // finds frames asks again until one completes.
        if (walLeft)
        {
            RequestCheckpoint();
        }

        return state;
    }

    // Whether the -wal holds frames before the first connection of an open (OpenCore): what the process before left.
    private bool HasWalFrames()
    {
        try
        {
            var wal = new FileInfo(FilePath + "-wal");
            return File.Exists(FilePath) && wal.Exists && wal.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not read the size of the -wal of {File}", FilePath);
            return false;
        }
    }

    private OpenState EnsureOpenLocked(out bool walLeft)
    {
        walLeft = false;
        OpenState? state;
        lock (_openLock)
        {
            state = _state;
            if (state is not null)
            {
                return state;
            }

            ObjectDisposedException.ThrowIf(_disposed, this);

            // A locked file costs SQLite's own retries (about 1.5 s on Windows) at every attempt; requests that come in the
            // meantime get the 503 at once.
            var now = UtcNow().Ticks;
            if (now < _nextOpenAttemptTicks)
            {
                throw new PluginException(PluginErrorKind.Unavailable, UnavailableMessage);
            }

            try
            {
                state = OpenCore();
            }
            catch (Exception ex)
            {
                _lastOpenError = DescribeError(ex);
                _unavailableMessage = ex is RetryLaterException { PublicMessage: { } message } ? message : null;
                _nextOpenAttemptTicks = now + OpenRetryInterval.Ticks;
                if (!_openFailureLogged)
                {
                    _openFailureLogged = true;
                    _logger.LogError(ex, "Could not open the database {File}; nothing was changed, and it is tried again at the next use after {Seconds} s", FilePath, OpenRetryInterval.TotalSeconds);
                }
                else
                {
                    _logger.LogDebug(ex, "Could not open the database {File}", FilePath);
                }

                throw new PluginException(PluginErrorKind.Unavailable, UnavailableMessage, ex);
            }

            if (_openFailureLogged)
            {
                _openFailureLogged = false;
                _logger.LogInformation("Opened the database {File} after an earlier failure", FilePath);
            }

            _lastOpenError = null;
            _unavailableMessage = null;
            _snapshotInUseLogged = null;
            walLeft = _walFramesAtOpen && state.Status is (SqliteDatabaseStatus.Ok or SqliteDatabaseStatus.Migrated);
            _state = state;
            return state;
        }
    }

    private OpenState? TryEnsureOpen()
    {
        try
        {
            return EnsureOpen();
        }
        catch (PluginException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return _state;
        }
    }

    private void NoteBindingFailure(Exception ex)
    {
        _lastOpenError = DescribeError(ex);
        _logger.LogError(ex, "The server's Microsoft.Data.Sqlite can't be used by this build; {File} is unavailable", FilePath);
    }

    private void EnterShared()
    {
        if (!_gate.TryEnterReadLock(GateTimeout))
        {
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Busy);
        }
    }

    private void EnterExclusive()
    {
        if (!_gate.TryEnterWriteLock(GateTimeout))
        {
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Busy);
        }
    }

    private bool CanSnapshot(string kind)
    {
        var status = TryEnsureOpen()?.Status ?? SqliteDatabaseStatus.Unavailable;
        if (IsReadOnlyStatus(status))
        {
            _logger.LogDebug("Skipped the {Kind} snapshot of {File}: the database is read-only ({Status})", kind, FilePath, status);
            return false;
        }

        return true;
    }

    private async Task<string?> PreOpSnapshotAsync(string operation, string? spare, CancellationToken cancellationToken)
    {
        SnapshotOperations.Require(operation);
        if (!CanSnapshot(operation))
        {
            return null;
        }

        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The live database is checked first, on a connection of its own (a pooled one's page cache can hide damage
            // written to the file from outside). Damaged: no snapshot, so a damaged copy never pushes a good one out of the
            // rotation, and the database is marked damaged; the caller's next check then answers as for a damaged database.
            return CheckBeforeSnapshot(operation) ? TakePreOpSnapshot(operation, spare) : null;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    // Under _snapshotLock: integrity_check of the live database before a pre-operation snapshot. False when it is
    // damaged (marked so); a check that couldn't read part of the file says nothing about the data and the snapshot
    // goes ahead, its copy checked as always.
    private bool CheckBeforeSnapshot(string operation)
    {
        if (TryEnsureOpen() is not { Missing: false })
        {
            return true;
        }

        SqliteCheckResult check;
        try
        {
            check = CheckLiveFile(SqliteCheckResult.IntegrityCheck);
        }
        catch (Exception ex) when (IsBindingFailure(ex))
        {
            NoteBindingFailure(ex);
            return true;
        }

        if (check.IsInconclusive)
        {
            _logger.LogWarning("The integrity_check of {File} before the {Operation} snapshot couldn't read part of the file ({Error}); the snapshot goes ahead", FilePath, operation, check.IoError);
            return true;
        }

        if (!check.Ok)
        {
            _logger.LogError("The integrity_check of {File} before the {Operation} snapshot failed ({Check}); no snapshot was taken and the earlier ones are kept", FilePath, operation, check.Summary);
            MarkCorrupt("integrity_check before the " + operation + " snapshot: " + check.Summary, null, IsIndexesOnly(check));
            return false;
        }

        return true;
    }

    // Under _snapshotLock: the daily expiry of the pre-operation snapshots and copies kept by age (PreOpKeepWithin).
    private void ExpirePreOp(DateTime utc)
    {
        if (PreOpKeepWithin is not { } within)
        {
            return;
        }

        var deleted = SnapshotRotation.RotatePreOp(SnapshotDirectory, StoreName, Extension, SnapshotOperations.ToKeep, _logger, nowUtc: utc, companionSuffixes: SqliteFiles.SidecarSuffixes, keepNames: KeptForRotation()?.Names, keepWithin: within).Count;
        foreach (var copy in PreOpCopies)
        {
            deleted += SnapshotRotation.RotatePreOp(SnapshotDirectory, Path.GetFileNameWithoutExtension(copy), Path.GetExtension(copy), SnapshotOperations.ToKeep, _logger, nowUtc: utc, keepWithin: within).Count;
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Deleted {Count} pre-operation snapshots and copies of {File} older than {Days} days", deleted, FilePath, within.TotalDays);
        }
    }

    // Under _snapshotLock, or while the database is being opened (nothing else can snapshot then).
    private string? TakePreOpSnapshot(string operation, string? spare)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var path = SnapshotRotation.NewPreOpPath(SnapshotDirectory, StoreName, operation, Extension, now);
        if (!WriteSnapshot(path))
        {
            return null;
        }

        SnapshotRotation.RotatePreOp(SnapshotDirectory, StoreName, Extension, SnapshotOperations.ToKeep, _logger, spare, nowUtc: now, companionSuffixes: SqliteFiles.SidecarSuffixes, keepNames: KeptForRotation()?.Names, keepWithin: PreOpKeepWithin);
        _logger.LogInformation("Took the pre-operation snapshot {Snapshot} of {File} ({Operation})", Path.GetFileName(path), FilePath, operation);
        return path;
    }

    private bool WriteSnapshot(string path)
    {
        try
        {
            return WriteSnapshotCore(path);
        }
        catch (Exception ex) when (IsBindingFailure(ex))
        {
            NoteBindingFailure(ex);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write the snapshot {Snapshot} of {File}", path, FilePath);
            return false;
        }
    }

    private StoreSnapshot FindSnapshot(string? name)
        => SnapshotRotation.List(SnapshotDirectory, StoreName, Extension)
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension))
            .FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw PluginException.NotFound("No snapshot of this data has that name.");

    // Checks a snapshot before a restore: from this or an older version, and passing integrity_check. Returns its version.
    private int VerifySnapshotForRestore(StoreSnapshot snapshot)
    {
        if (!SqliteFiles.TryReadHeader(snapshot.Path, out var read))
        {
            // No permission never passes by trying again; a snapshot in use does (the JSON store's rule).
            if (MayNotRead(snapshot.Path))
            {
                throw PluginException.Conflict("The snapshot " + snapshot.Name + " can't be restored: the server has " + NoPermission + ".");
            }

            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.SnapshotInUse(snapshot.Name));
        }

        var header = read ?? throw PluginException.Conflict("The snapshot " + snapshot.Name + " isn't a SQLite database.");
        if (header.UserVersion > CurrentVersion)
        {
            throw PluginException.Conflict(string.Create(
                CultureInfo.InvariantCulture,
                $"The snapshot {snapshot.Name} was written by a newer plugin version (schema {header.UserVersion}; this version reads up to {CurrentVersion})."));
        }

        SqliteCheckResult check;
        try
        {
            check = CheckFile(snapshot.Path);
        }
        catch (Exception ex) when (IsBindingFailure(ex))
        {
            NoteBindingFailure(ex);
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Unavailable, ex);
        }

        if (check.IsInconclusive)
        {
            _logger.LogWarning("The snapshot {Snapshot} of {File} couldn't be checked just now ({Error}); nothing was restored", snapshot.Name, FilePath, check.IoError);
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.SnapshotUnavailable);
        }

        if (!check.Ok)
        {
            throw PluginException.Conflict("The snapshot " + snapshot.Name + " failed its integrity check: " + check.FirstMessage);
        }

        return header.UserVersion;
    }

    // After an admin's logical restore (RestoreSnapshotAsync): only one of the kept snapshots restored brings the data
    // back and releases them; after one taken since (undoing "start empty, then import" with its import snapshot, say),
    // whatever its name's date, they stay kept (the clock may have been behind, as for the JSON store). An incident (a
    // corruption restore, at open or an admin's, a start empty) never releases the marker; it records the snapshots
    // there are then (KeepSnapshotsAtIncident).
    private void AfterSnapshotRestored(StoreSnapshot snapshot)
    {
        var kept = _kept;
        if (kept is not null && kept.ReleasedBy(snapshot))
        {
            ReleaseKeptSnapshots();
        }
        else if (kept is not null)
        {
            _logger.LogWarning(
                "{Snapshot} isn't one of the {Count} snapshots of {File} kept since its last incident on {SinceUtc} UTC (it was taken since, or its date can't tell); those are still kept until one of them is restored",
                snapshot.Name,
                kept.Names.Count,
                FilePath,
                kept.SinceUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        }
    }

    private void MarkCorrupt(string reason, Exception? ex, bool indexesOnly = false)
    {
        OpenState? before;
        lock (_openLock)
        {
            before = _state;
            if (before is null || before.Status is SqliteDatabaseStatus.ReadOnlyCorrupt or SqliteDatabaseStatus.ReadOnlyNewerVersion)
            {
                before = null;
            }
            else
            {
                // Reads go on through read-only connections, which never checkpoint.
                _state = before with
                {
                    Status = SqliteDatabaseStatus.ReadOnlyCorrupt,
                    Message = indexesOnly
                        ? "The data's indexes failed their integrity check (" + reason + "); the rows themselves read intact, but nothing can be changed. Restart the server to rebuild the indexes (a copy of the data as it is now is kept first), or restore a snapshot."
                        : "The data failed its integrity check (" + reason + "), so nothing can be changed. Restore a snapshot, or restart the server to restore the newest good snapshot automatically.",
                    Problem = reason,
                    ReadOnlyConnections = true,
                    IndexesOnly = indexesOnly
                };
            }
        }

        if (before is not null)
        {
            _logger.LogError(ex, "The database {File} is damaged ({Reason}); it is read-only until a snapshot is restored", FilePath, reason);
            WriteDamageMarker(reason, indexesOnly);

            // The read-write connections in the pool go now, while a read-only one holds the file: none of them closes
            // last later, which would checkpoint the WAL into the damaged file and delete it. Inside the gate (shared, or
            // again by the thread that holds it), so no restore is moving the file meanwhile.
            if (!_gate.TryEnterReadLock(GateTimeout))
            {
                return;
            }

            try
            {
                HoldCorruptGuard();
                ClearPool(_pooledConnectionString);
            }
            catch (Exception clear)
            {
                _logger.LogDebug(clear, "Could not close the read-write connections to the damaged {File}", FilePath);
            }
            finally
            {
                _gate.ExitReadLock();
            }
        }
    }

    // Inside the gate: no restore is under way, so _state is current and no file is being moved.
    private void HoldCorruptGuardIfDamaged()
    {
        if (_state is { Status: SqliteDatabaseStatus.ReadOnlyCorrupt, Missing: false })
        {
            HoldCorruptGuard();
        }
    }

    // Closes this database's connections: while it is damaged, with a read-only connection holding the file meanwhile, so
    // the WAL stays as it was found; the guard held since closes last.
    private void ReleaseConnections()
    {
        if (_state is { Status: SqliteDatabaseStatus.ReadOnlyCorrupt })
        {
            ClearPoolsKeepingFiles();
        }
        else
        {
            ClearPools();
        }

        ReleaseCorruptGuard();
    }

    private void ReleaseCorruptGuard()
    {
        IDisposable? guard;
        lock (_guardLock)
        {
            guard = _corruptGuard;
            _corruptGuard = null;
        }

        guard?.Dispose();
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    private string Timestamp() => UtcNow().ToString(TimestampFormat, CultureInfo.InvariantCulture);

    // At every open attempt, before anything rotates: the marker as the JSON store reads it (KeepSnapshotsMarker.Read),
    // and a development build's marker of the time alone (before the names) read as it meant: the snapshots dated
    // before it recorded by name once, only one of them dated before that time releasing them.
    private void LoadKeepSnapshotsMarker()
        => _kept = KeepSnapshotsMarker.Read<SqliteDatabaseStatus>(KeepSnapshotsMarkerPath, FilePath, NamesDatedBefore, JsonStoreFormat.Options, _logger, _timeProvider, readsPlainTime: true);

    // At every incident (a restore at open, an admin's recovery, a start empty): every snapshot there is now, daily and
    // pre-operation, is kept from now on, by name, the earlier kept ones among them (as for the JSON store: a snapshot
    // taken after this while the clock is behind is dated before any time that covers the ones there are, so a time
    // can't tell them apart). The marker also holds the later of now and the newest snapshot's time plus a second,
    // never moved back (KeepSnapshotsBeforeUtc). With no snapshot at all there is nothing to keep, and no marker.
    private void KeepSnapshotsAtIncident(DateTime nowUtc, SqliteDatabaseStatus incident, string reason)
    {
        var snapshots = SnapshotRotation.List(SnapshotDirectory, StoreName, Extension)
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension))
            .ToList();
        var kept = KeepSnapshotsMarker.Record(snapshots, nowUtc, incident, _kept);
        if (kept is null)
        {
            ReleaseKeptSnapshots(restored: false);
            return;
        }

        _kept = kept;
        Interlocked.Exchange(ref _keptSnapshotsLogged, 0);
        if (KeepSnapshotsMarker.Write(KeepSnapshotsMarkerPath, kept, reason, JsonStoreFormat.Options, _logger, FilePath))
        {
            _logger.LogInformation("Recorded in {Marker} that the {Count} snapshots of {File} there are now are to be kept", KeepSnapshotsMarkerPath, kept.Names.Count, FilePath);
        }
    }

    // The names of the snapshots there are, daily and pre-operation, dated before a time.
    private HashSet<string> NamesDatedBefore(DateTime utc)
        => SnapshotRotation.List(SnapshotDirectory, StoreName, Extension)
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension))
            .Where(s => SnapshotRotation.IsBefore(s, utc))
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Snapshots dated more than a day after now (taken while the clock was wrong, or it is wrong now), apart from one:
    // their names say nothing about how new their data is, so a restore tries them last and the banner names them.
    private List<string> DatedLater(string? except)
    {
        var now = UtcNow();
        return SnapshotRotation.List(SnapshotDirectory, StoreName, Extension)
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension))
            .Where(s => SnapshotRotation.IsFutureDated(s, now) && !string.Equals(s.Name, except, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.Utc)
            .Select(s => s.Name)
            .ToList();
    }

    // The kept snapshots for a rotation, daily or pre-operation, just after a snapshot was written into the folder (so
    // the folder is there and can be listed): one Warning per process while there are any, naming the marker, as the
    // JSON store's WarnOfKeptSnapshots does, so kept snapshots never pile up unmentioned. A marker none of whose
    // snapshots is there any more (deleted by hand, or moved away) stays, as the JSON store's does, until a restore of
    // one of them or an admin deletes it (a deliberate choice): it protects them should they come back (a backup of
    // snapshots/ put back), and it costs nothing while they are gone, since it keeps no other snapshot. (Before, it
    // went with an Information.)
    private KeptSnapshots<SqliteDatabaseStatus>? KeptForRotation()
    {
        var kept = _kept;
        if (kept is null)
        {
            return null;
        }

        var since = kept.SinceUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        if (Interlocked.Exchange(ref _keptSnapshotsLogged, 1) == 0)
        {
            _logger.LogWarning(
                "{File} was restored from a snapshot, or started empty, after its data was found damaged or missing; the earlier snapshots in {Directory}, daily and pre-operation (the {Count} there were on {SinceUtc} UTC), are kept until one of them is restored or {Marker} is deleted",
                FilePath,
                SnapshotDirectory,
                kept.Names.Count,
                since,
                Path.GetFileName(KeepSnapshotsMarkerPath));
        }

        return kept;
    }

    // After a restore of a kept snapshot (restored): the data is back, the kept snapshots rotate normally again. Else an
    // incident with no snapshot at all: there is nothing to keep.
    private void ReleaseKeptSnapshots(bool restored = true)
    {
        Interlocked.Exchange(ref _keptSnapshotsLogged, 0);
        if (Interlocked.Exchange(ref _kept, null) is null && !File.Exists(KeepSnapshotsMarkerPath))
        {
            return;
        }

        try
        {
            File.Delete(KeepSnapshotsMarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Marker}; the earlier snapshots stay protected after a restart", KeepSnapshotsMarkerPath);
        }

        if (restored)
        {
            _logger.LogInformation("A snapshot of {File} kept since its last incident was restored; its earlier snapshots rotate normally again", FilePath);
        }
    }

    // ------------------------------------------------------------------------------------------------ SQLite work The
    // methods below that touch Microsoft.Data.Sqlite types are never inlined and are called only inside a catch, or
    // from members with a SQLite type in their signature, so a binding failure surfaces where it is caught.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string PooledConnectionString()
        => _pooledConnectionString ??= new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = CommandTimeoutSeconds
        }.ToString();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string ReadOnlyConnectionString()
        => _readOnlyConnectionString ??= new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
            DefaultTimeout = CommandTimeoutSeconds
        }.ToString();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private string UnpooledConnectionString()
        => _unpooledConnectionString ??= new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = CommandTimeoutSeconds
        }.ToString();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ClearPools()
    {
        ClearPool(_pooledConnectionString);
        ClearPool(_readOnlyConnectionString);
    }

    // Clears the pools without letting SQLite change the damaged files: the last of our connections to close would
    // checkpoint the WAL into the database and delete it. A read-only connection held open meanwhile keeps its shared
    // lock on the file (in WAL mode a connection keeps it while open), so none of ours is the last; it closes last, and a
    // read-only connection never checkpoints. The database, its -wal and its -shm are then moved exactly as found.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ClearPoolsKeepingFiles()
    {
        SqliteConnection? guard = null;
        if (File.Exists(FilePath))
        {
            guard = new SqliteConnection(FileConnectionString(FilePath, readOnly: true));
            try
            {
                guard.Open();
                Scalar(guard, null, "PRAGMA user_version;");
            }
            catch (SqliteException ex)
            {
                // A file that isn't a database (SQLITE_NOTADB) has no WAL of its own to keep.
                _logger.LogDebug(ex, "Could not hold {File} open while its pools were cleared", FilePath);
            }
        }

        try
        {
            ClearPools();
        }
        finally
        {
            // The guard held while the database is damaged, then this one, last.
            ReleaseCorruptGuard();
            guard?.Dispose();
        }
    }

    // A read-only, unpooled connection to what a recovery put in the database's place, opened right after the rename
    // and held until the recovery's journal is deleted, or until its put back has cleared the pools; null when it can't
    // be opened (the open then finds the file isn't a database anyway).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private IDisposable? HoldPlacedGuard()
    {
        var guard = new SqliteConnection(FileConnectionString(FilePath, readOnly: true));
        try
        {
            // A test's stand-in for a guard that can't be opened (the put back's own ClearPoolsKeepingFiles then
            // carries it alone).
            RecoveryStepFault?.Invoke("guard");
            guard.Open();
            Scalar(guard, null, "PRAGMA user_version;");
            return guard;
        }
        catch (SqliteException ex)
        {
            guard.Dispose();
            _logger.LogDebug(ex, "Could not hold what the recovery put in place of {File} open", FilePath);
            return null;
        }
    }

    // Holds one read-only, unpooled connection to the damaged database open (ReadOnlyCorrupt) until the next restore,
    // Release or Dispose: Microsoft.Data.Sqlite closes idle pooled connections after a few minutes, and the last read-write
    // one to close would otherwise checkpoint the WAL into the damaged file, so the quarantine would no longer hold the
    // files as they were found.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void HoldCorruptGuard()
    {
        lock (_guardLock)
        {
            if (_corruptGuard is not null || _disposed || !File.Exists(FilePath))
            {
                return;
            }

            var guard = new SqliteConnection(FileConnectionString(FilePath, readOnly: true));
            try
            {
                guard.Open();
                Scalar(guard, null, "PRAGMA user_version;");
                _corruptGuard = guard;
            }
            catch (SqliteException ex)
            {
                guard.Dispose();
                _logger.LogDebug(ex, "Could not hold the damaged {File} open", FilePath);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqliteConnection OpenForRead(OpenState state)
        => state.ReadOnlyConnections
            ? OpenConnection(ReadOnlyConnectionString(), ReadOnlyPragmas)
            : OpenConnection(PooledConnectionString(), WritablePragmas);

    // SQLITE_BUSY or SQLITE_LOCKED once the command timeout ran out: 503 with the path-free Busy, one Warning without a
    // stack trace, and nothing marked (a lock says nothing about the data).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private PluginException Busy(SqliteException ex, string what)
    {
        _logger.LogWarning(
            "Another connection kept {File} locked for more than {Seconds} s ({Error}); the {What} was refused with 503 and nothing was changed",
            FilePath,
            CommandTimeoutSeconds,
            DescribeSqliteError(ex),
            what);
        return new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Busy, ex);
    }

    // The writer lock, waited for with what is left of the command timeout since the write began (an open may have used
    // some of it): writes queued behind a busy one answer 503 Busy within about 5 s each.
    private async Task WaitForWriterAsync(long begun, CancellationToken cancellationToken)
    {
        var left = TimeSpan.FromSeconds(CommandTimeoutSeconds) - Stopwatch.GetElapsedTime(begun);
        if (await _writeLock.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogInformation(
            "A write to {File} waited {Seconds} s for the writes before it and was refused with 503; nothing was changed",
            FilePath,
            CommandTimeoutSeconds);
        throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Busy);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T WriteCore<T>(Func<SqliteConnection, SqliteTransaction, T> write, bool commit)
    {
        EnterShared();
        try
        {
            using var connection = OpenConnection(PooledConnectionString(), WritablePragmas);
            using var transaction = connection.BeginTransaction();
            var result = write(connection, transaction);
            if (commit)
            {
                transaction.Commit();
            }
            else
            {
                transaction.Rollback();
            }

            return result;
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            MarkCorrupt(DescribeSqliteError(ex), ex);
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.Corrupt, ex);
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            throw Busy(ex, "write");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteFull or SqliteIoErr)
        {
            _logger.LogError(ex, "A write to {File} failed on disk; it was rolled back", FilePath);
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.WriteFailed, ex);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    // Under _checkpointLock: the round running now, or a new one started on the thread pool.
    private Task<bool> StartCheckpointRoundLocked()
    {
        if (_checkpointRound is { } running)
        {
            return running.Task;
        }

        var round = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _checkpointRound = round;
        _ = Task.Run(() => RunCheckpointRoundAsync(round));
        return round.Task;
    }

    // After a write's commit: a checkpoint still pending, whose round's tries ran out, gets a new round.
    private void RetryPendingCheckpoint()
    {
        lock (_checkpointLock)
        {
            if (!_disposed && _checkpointRound is null && _checkpointCompleted < _checkpointRequested)
            {
                StartCheckpointRoundLocked();
            }
        }
    }

    // One round: the delay, then tries until a complete checkpoint covers every request counted so far, the tries run out
    // (a Warning; still pending), the database is read-only or the database stops. Never throws.
    private async Task RunCheckpointRoundAsync(TaskCompletionSource<bool> round)
    {
        var outcome = false;
        try
        {
            outcome = await CheckpointRoundAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A checkpoint of {File} failed; it stays pending, and the next write or the daily maintenance tries again", FilePath);
            lock (_checkpointLock)
            {
                _checkpointRound = null;
            }
        }
        finally
        {
            round.TrySetResult(outcome);
        }
    }

    private async Task<bool> CheckpointRoundAsync()
    {
        var started = Stopwatch.GetTimestamp();
        var tries = 0;
        var delay = CheckpointDelay;
        while (true)
        {
            if (!await CheckpointDelayAsync(delay).ConfigureAwait(false))
            {
                return EndCheckpointRound();
            }

            long covers;
            lock (_checkpointLock)
            {
                covers = _checkpointRequested;
            }

            var outcome = TryCheckpoint();
            CheckpointTryHook?.Invoke("tried");
            tries++;
            lock (_checkpointLock)
            {
                _lastCheckpointTryUtc = UtcNow();
                _lastCheckpointOutcome = outcome.ToString();
                if (outcome == CheckpointOutcome.Complete)
                {
                    _lastCheckpointUtc = _lastCheckpointTryUtc;
                    _checkpointCompleted = Math.Max(_checkpointCompleted, covers);
                    if (_checkpointCompleted >= _checkpointRequested)
                    {
                        _checkpointRound = null;
                        return true;
                    }
                }
            }

            switch (outcome)
            {
                case CheckpointOutcome.Complete:
                    // Requests counted while it ran: one more, after the delay.
                    tries = 0;
                    delay = CheckpointDelay;
                    continue;
                case CheckpointOutcome.ReadOnly:
                case CheckpointOutcome.Stopped:
                    _logger.LogDebug("Did not checkpoint {File}: {Outcome}; it stays pending", FilePath, outcome);
                    return EndCheckpointRound();
            }

            if (tries > CheckpointRetryDelays.Count)
            {
                lock (_checkpointLock)
                {
                    _checkpointRoundsFailed++;
                }

                _logger.LogWarning(
                    "The checkpoint of {File} couldn't finish in {Tries} tries over {Seconds:0} s ({Why}); the earlier versions of the pages written since the last one stay in its -wal, and the checkpoint stays pending: the next write or the daily maintenance tries again",
                    FilePath,
                    tries,
                    Stopwatch.GetElapsedTime(started).TotalSeconds,
                    outcome switch
                    {
                        CheckpointOutcome.Writer => "writes kept the writer lock",
                        CheckpointOutcome.Readers => "another connection kept reading an older snapshot",
                        _ => "it failed"
                    });
                return EndCheckpointRound();
            }

            delay = CheckpointRetryDelays[tries - 1];
        }
    }

    // The round ends with requests still pending: the next write, the maintenance or another request starts a new one.
    private bool EndCheckpointRound()
    {
        lock (_checkpointLock)
        {
            _checkpointRound = null;
            return _checkpointCompleted >= _checkpointRequested;
        }
    }

    // False when the database stops meanwhile.
    private async Task<bool> CheckpointDelayAsync(TimeSpan delay)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _checkpointStopToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        return !_disposed;
    }

    // One try: the writer lock taken only if it is free (never waited for), then the checkpoint with no busy wait, so the
    // lock is held only while frames are copied, never while another connection's read is waited for.
    private CheckpointOutcome TryCheckpoint()
    {
        if (_disposed)
        {
            return CheckpointOutcome.Stopped;
        }

        if (TryEnsureOpen() is not { } state || IsReadOnlyStatus(state.Status))
        {
            return CheckpointOutcome.ReadOnly;
        }

        bool entered;
        try
        {
            entered = _writeLock.Wait(0);
        }
        catch (ObjectDisposedException)
        {
            return CheckpointOutcome.Stopped;
        }

        if (!entered)
        {
            return CheckpointOutcome.Writer;
        }

        try
        {
            if (_disposed)
            {
                return CheckpointOutcome.Stopped;
            }

            if (TryEnsureOpen() is not { } now || IsReadOnlyStatus(now.Status))
            {
                return CheckpointOutcome.ReadOnly;
            }

            CheckpointTryHook?.Invoke("locked");
            if (_disposed)
            {
                return CheckpointOutcome.Stopped;
            }

            return CheckpointCore();
        }
        catch (Exception ex) when (IsBindingFailure(ex))
        {
            NoteBindingFailure(ex);
            return CheckpointOutcome.Failed;
        }
        catch (PluginException ex)
        {
            _logger.LogDebug(ex, "Did not checkpoint {File} just now", FilePath);
            return CheckpointOutcome.Failed;
        }
        finally
        {
            try
            {
                _writeLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    // Under the writer lock (TryCheckpoint): TRUNCATE with busy_timeout 0, so a reader of an older snapshot makes it stop
    // at once (busy, the frames it could copy copied) instead of being waited for. The next open of a pooled connection
    // sets busy_timeout again (OpenPragmas); this one sets it back itself.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private CheckpointOutcome CheckpointCore()
    {
        EnterShared();
        try
        {
            using var connection = OpenConnection(PooledConnectionString(), WritablePragmas);
            Execute(connection, null, "PRAGMA busy_timeout=0;");
            try
            {
                using var command = CreateCommand(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
                using var reader = command.ExecuteReader();
                var complete = reader.Read() && reader.GetInt64(0) == 0;
                if (!complete)
                {
                    _logger.LogDebug("The checkpoint of {File} didn't finish: another connection was reading an older snapshot", FilePath);
                }

                return complete ? CheckpointOutcome.Complete : CheckpointOutcome.Readers;
            }
            finally
            {
                Execute(connection, null, "PRAGMA busy_timeout=5000;");
            }
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            MarkCorrupt(DescribeSqliteError(ex), ex);
            return CheckpointOutcome.ReadOnly;
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            return CheckpointOutcome.Readers;
        }
        catch (SqliteException ex)
        {
            _logger.LogDebug("Could not checkpoint {File} just now ({Error})", FilePath, DescribeSqliteError(ex));
            return CheckpointOutcome.Failed;
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    // quick_check at run time. Damage makes the database ReadOnlyCorrupt; a check that couldn't read part of the file (a
    // line naming an I/O error, or an error other than SQLITE_CORRUPT/SQLITE_NOTADB) says nothing about the data: it is
    // logged and the next maintenance checks again, with no banner.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqliteCheckResult QuickCheckCore(OpenState state)
    {
        if (state.Missing)
        {
            return _lastQuickCheck = new SqliteCheckResult(SqliteCheckResult.QuickCheck, UtcNow(), false, [SqliteMessages.Missing]);
        }

        var check = CheckLiveFile(SqliteCheckResult.QuickCheck);
        _lastQuickCheck = check;
        if (check.IsInconclusive)
        {
            _logger.LogWarning("The quick_check of {File} couldn't read part of the file ({Error}); nothing was changed, and the next maintenance checks again", FilePath, check.IoError);
        }
        else if (!check.Ok)
        {
            MarkCorrupt("quick_check: " + check.Summary, null);
        }

        return check;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqliteCheckResult RunCheck(SqliteConnection connection, string kind)
    {
        var lines = new List<string>();
        using (var command = CreateCommand(connection, null, string.Create(CultureInfo.InvariantCulture, $"PRAGMA {kind}({CheckLines});")))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                lines.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
            }
        }

        return new SqliteCheckResult(kind, UtcNow(), lines.Count == 1 && string.Equals(lines[0], "ok", StringComparison.Ordinal), lines);
    }

    // A check of the live database inside the gate, on a read-only connection of its own: a pooled connection keeps its
    // page cache while the WAL index is unchanged, so damage written to the file from outside (a bad sector, a stray
    // write) was hidden from quick_check and from the snapshot's BackupDatabase. Read-only, it never checkpoints; an
    // error that stops the check is a failed one (inconclusive when it isn't corruption).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqliteCheckResult CheckLiveFile(string kind)
    {
        EnterShared();
        try
        {
            HoldCorruptGuardIfDamaged();
            using var connection = OpenConnection(FileConnectionString(FilePath, readOnly: true), ReadOnlyPragmas);
            return RunCheck(connection, kind);
        }
        catch (SqliteException ex)
        {
            return new SqliteCheckResult(kind, UtcNow(), false, [DescribeSqliteError(ex)]);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    // integrity_check, read-only and unpooled (a DELETE-mode file then leaves nothing beside it): on a snapshot or its
    // copy, and the second check of the live database at open. An error that stops the check is a failed check; one other
    // than SQLITE_CORRUPT or SQLITE_NOTADB (locked, I/O, full) is inconclusive (SqliteCheckResult.IsInconclusive).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqliteCheckResult CheckFile(string path)
    {
        try
        {
            using var connection = OpenConnection(FileConnectionString(path, readOnly: true), null);
            return RunCheck(connection, SqliteCheckResult.IntegrityCheck);
        }
        catch (SqliteException ex)
        {
            return new SqliteCheckResult(SqliteCheckResult.IntegrityCheck, UtcNow(), false, [DescribeSqliteError(ex)]);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool WriteSnapshotCore(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        DeleteStaleTempFiles(directory);
        var temp = path + TempSuffix;

        EnterShared();
        try
        {
            using var source = OpenConnection(PooledConnectionString(), WritablePragmas);
            using var destination = OpenConnection(FileConnectionString(temp, readOnly: false), null);
            source.BackupDatabase(destination);

            // A backup of a WAL database is a WAL file; one in rollback mode is a single self-contained file.
            Scalar(destination, null, "PRAGMA journal_mode=DELETE;");
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            TryDeleteWithSidecars(temp);
            MarkCorrupt(DescribeSqliteError(ex), ex);
            return false;
        }
        catch (SqliteException ex)
        {
            TryDeleteWithSidecars(temp);
            _logger.LogError(ex, "Could not write the snapshot {Snapshot} of {File}", path, FilePath);
            return false;
        }
        finally
        {
            _gate.ExitReadLock();
        }

        var check = CheckFile(temp);
        if (check.IsInconclusive)
        {
            // The copy couldn't be read back (a virus scanner, a full disk): that says nothing about the live database.
            TryDeleteWithSidecars(temp);
            _logger.LogError(
                "The new snapshot {Snapshot} of {File} couldn't be checked ({Error}); it was deleted and the earlier snapshots are kept",
                Path.GetFileName(path),
                FilePath,
                check.IoError);
            return false;
        }

        if (!check.Ok)
        {
            TryDeleteWithSidecars(temp);
            _logger.LogError(
                "The new snapshot {Snapshot} of {File} failed its integrity check ({Check}); it was deleted and the earlier snapshots are kept. The live database is checked next",
                Path.GetFileName(path),
                FilePath,
                check.Summary);

            // While the database is being opened (a pre-upgrade snapshot) there is no state yet and its check just passed.
            if (_state is { } state)
            {
                QuickCheckCore(state);
            }

            return false;
        }

        File.Move(temp, path);
        return true;
    }

    // Under the writer lock, before an admin's recovery moves anything (as the JSON store's ThrowIfReadableFileInPlace
    // does): the status says what was found, at open or since (damaged, or missing), and the recovery would quarantine
    // whatever is in place as damaged, then start empty or restore an older snapshot over it. So the database in place
    // is checked again as a restart's open would find it: the file with its -wal and -journal as they are (the -shm
    // left out; SQLite rebuilds the index from the -wal), copied beside it and checked there, so that no connection of
    // ours opens the files in place (none checkpoints into them, rolls a journal back or makes a -shm beside them). One
    // that passes integrity_check and holds a schema, at any version (a good copy an admin put back while the server
    // runs, or damage found at run time that was a read that failed), is refused with 409: a restart opens it the usual
    // way. Damaged, empty, or no database at all: the recovery goes on. One that can't be read just now: 503, nothing
    // moved (moving it would quarantine a file nobody checked). The copies are made outside the gate, so reads go on
    // meanwhile: while the database is ReadOnlyCorrupt no connection of ours writes or checkpoints (reads use read-only
    // connections and the guard, writes and snapshots are refused, and this holds the writer lock), so only another
    // program writing to the files could change them between two copies. The -wal is copied before the database for
    // that case: a checkpoint between the two then finds its frames in both, which replay to the same pages, where the
    // database copied first would miss them and the -wal copied after a reset hold frames of a later state, which can
    // make a good file look damaged. The copy and its integrity_check run in the admin's request, under the writer
    // lock: seconds for User Ratings' sizes, and nothing waits for them but another admin's recovery or restore (writes
    // are refused while the database is damaged, reads go on).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowIfReadableDatabaseInPlace()
    {
        bool readable;
        try
        {
            readable = ReadsAsADatabase();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            _logger.LogWarning(ex, "Did not recover {File}: the database in place couldn't be checked again before anything was moved; nothing was changed", FilePath);
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.RecheckFailed, ex);
        }

        if (readable)
        {
            _logger.LogWarning(
                "An admin's recovery of {File} was refused: the database in place passes integrity_check now (put back while the server runs, or the damage found was a read that failed), so nothing was moved; restart the server to use it",
                FilePath);
            throw PluginException.Conflict(StoreMessages.ReadableFileInPlace(Path.GetFileName(FilePath)));
        }
    }

    // Whether the database in place reads as a restart's open would find it (ThrowIfReadableDatabaseInPlace; at open, a file
    // in the place of one a recovery that didn't finish moved aside): the file with its -wal and -journal as they are,
    // copied beside it and checked there, so no connection of ours opens the files in place. True when it passes
    // integrity_check and holds a schema, at any version; false when it is damaged, empty, not a SQLite database, or
    // missing. Throws IOException, UnauthorizedAccessException or SqliteException when it can't be read just now.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ReadsAsADatabase()
    {
        if (!File.Exists(FilePath))
        {
            return false;
        }

        var copy = FilePath + RecheckSuffix;
        try
        {
            SqliteFiles.DeleteWithSidecars(copy);
            if (File.Exists(FilePath + "-wal"))
            {
                CopyAsFound(FilePath + "-wal", copy + "-wal");
            }

            RecoveryStepFault?.Invoke("recheck");
            CopyAsFound(FilePath, copy);
            if (File.Exists(FilePath + "-journal"))
            {
                CopyAsFound(FilePath + "-journal", copy + "-journal");
            }

            if (!SqliteFiles.TryReadHeader(copy, out var header))
            {
                throw new IOException("The copy " + copy + " couldn't be read.");
            }

            if (header is null)
            {
                // Empty, or not a SQLite database: nothing a restart would open as data.
                return false;
            }

            using var connection = OpenConnection(FileConnectionString(copy, readOnly: false), null);
            var check = RunCheck(connection, SqliteCheckResult.IntegrityCheck);
            if (check.IsInconclusive)
            {
                throw new IOException("integrity_check couldn't read part of the copy (" + check.IoError + ")");
            }

            return check.Ok
                && (ReadUserVersion(connection, null) != 0 || Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_master;"), CultureInfo.InvariantCulture) != 0);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            // Damaged as found.
            _logger.LogDebug(ex, "{File} is damaged as found", FilePath);
            return false;
        }
        finally
        {
            TryDeleteWithSidecars(copy);
        }
    }

    // Copies a file as it is, with read, write and delete sharing, so a connection holding it (the damaged database's
    // guard) neither blocks the copy nor sees anything change.
    private static void CopyAsFound(string from, string to)
    {
        using var source = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var target = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
    }

    // A snapshot killed half-way leaves <name>.db.tmp; only snapshots of this database, never anything else.
    private void DeleteStaleTempFiles(string directory)
    {
        foreach (var stale in Directory.EnumerateFiles(directory, StoreName + "-*" + Extension + TempSuffix))
        {
            TryDeleteWithSidecars(stale);
        }
    }

    private void TryDeleteWithSidecars(string path)
    {
        try
        {
            SqliteFiles.DeleteWithSidecars(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete {Path}", path);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private OpenState OpenCore()
    {
        Directory.CreateDirectory(Folder);
        _walFramesAtOpen = HasWalFrames();
        _keptAside = null;
        LoadKeepSnapshotsMarker();
        DeleteLeftoversOfAStop();

        // A recovery the process before didn't finish: what it moved goes back before anything looks at the files.
        if (ResumeInterruptedRecovery() is { } stuck)
        {
            return stuck;
        }

        // A newer version's database is never written, not even its journal mode: its header is read first...
        if (SqliteFiles.ReadHeader(FilePath) is { } header && header.UserVersion > CurrentVersion)
        {
            return OpenNewer(header.UserVersion);
        }

        if (!File.Exists(FilePath))
        {
            // A -wal or -shm without its database goes first, whatever happens next: a good database
            // copied back later, while no snapshot could be restored, would replay another database's log.
            var orphans = QuarantineOrphanSidecars();

            // Deleted by hand, a manual restore half done, or a backup of snapshots/ alone put back: with snapshots of
            // its data there, the newest good one is restored, and a new database never starts empty beside them on its
            // own (the daily rotation would then delete the last copies of the data).
            if (DataSnapshotsExist())
            {
                return RecoverAtOpen("the database file was missing", null, damaged: false, version: null, orphans);
            }

            return OpenChecked(null);
        }

        // The sidecars as found, before the first connection of ours (the probe's included) makes an empty -wal and a -shm.
        var found = SqliteFiles.ListSidecars(FilePath).Select(p => Path.GetFileName(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ...then through the -wal, on a read-only connection, before any read-write one: a newer version's upgrade still
        // only in the -wal (a crash before the checkpoint) is never checkpointed into the file, nor checked read-write.
        var version = ProbeUserVersion();
        if (version > CurrentVersion)
        {
            return OpenNewer(version.Value);
        }

        string? confirmed;
        try
        {
            // Damage found at run time (DamageMarkerPath) is checked with integrity_check, which sees what quick_check
            // doesn't (KIT-1); otherwise quick_check, and integrity_check before anything is moved.
            confirmed = ReadDamageMarker() is { } marked
                ? RecheckMarkedDamage(marked)
                : CheckLive() is { } problem ? ConfirmDamage(problem) : null;
        }
        catch (RetryLaterException)
        {
            // Tried again in a moment: the check's read-write connection mustn't wait in the pool meanwhile, where closing
            // it last would checkpoint the WAL into a file that may be damaged; and the empty -wal and the -shm our own
            // connections made go, so the next attempt finds the files as they were.
            ClearPoolsKeepingFiles();
            DeleteSidecarsOfTheCheck(found);
            throw;
        }

        if (confirmed is not null)
        {
            return RecoverAtOpen(confirmed, found, damaged: true, version);
        }

        if (version == 0 && !HasTables() && DataSnapshotsExist())
        {
            return RecoverAtOpen("the database file was empty: schema 0, no tables", found, damaged: false, version);
        }

        return OpenChecked(null);
    }

    // The restore at open of a damaged database, or of a missing or empty one whose data has snapshots: the newest
    // snapshot that passes takes its place (RestoredFromSnapshot). With none passing, nothing is moved and the database
    // is ReadOnlyCorrupt, its undamaged tables readable; only an admin starts it empty. A snapshot that can't be read
    // just then makes the open fail (Unavailable, RetryLaterException), nothing moved, and the next use tries again. A
    // snapshot that can't be put in place once the database was moved aside (renamed, opened, checked again, upgraded):
    // what was moved is put back as it was found, and the database is ReadOnlyCorrupt with a banner that says so, until
    // an admin restores a snapshot or starts empty, or a restart tries again (as for the JSON store). orphans: the
    // sidecars found without their database, already moved into their own quarantine folder, named by the recovery's
    // banner when it moved nothing else.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private OpenState RecoverAtOpen(string problem, IReadOnlySet<string>? found, bool damaged, int? version, (string Folder, IReadOnlyList<string> Files)? orphans = null)
    {
        (SqliteRecoveryResult Recovery, OpenState State)? done;
        IReadOnlyList<string> tried;
        try
        {
            done = Recover(problem, null, found, startEmpty: false, damaged, out tried, orphans);
        }
        catch (RetryLaterException)
        {
            throw;
        }
        catch (RecoveryFailedException ex)
        {
            var what = (damaged ? "The data failed its integrity check (" : "The data wasn't there (") + problem + "). ";
            if (ex.Stuck.Count > 0)
            {
                // The journal stays (stuck): the next open puts it back first, once nothing is in the way.
                return new OpenState(SqliteDatabaseStatus.ReadOnlyCorrupt, version, version, null, what + SqliteMessages.RecoveryNotPutBack(ex.Failure, ex.Folder!), true, StuckProblem(problem, ex.Folder!), Missing: true);
            }

            if (damaged)
            {
                HoldCorruptGuard();
            }

            return new OpenState(
                SqliteDatabaseStatus.ReadOnlyCorrupt,
                version,
                version,
                null,
                what + ex.Failure + (ex.Moved == 0 ? string.Empty : ", so what had been moved aside was put back as it was found")
                    + ", and nothing can be changed. Restore a snapshot or start empty from the plugin's settings page, or restart the server to try again. The server log has the details.",
                true,
                problem,
                Missing: !damaged);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "{File} is damaged or missing ({Reason}) but what was there couldn't be moved aside; it is read-only, and nothing was changed", FilePath, problem);
            if (damaged)
            {
                HoldCorruptGuard();
            }

            return new OpenState(
                SqliteDatabaseStatus.ReadOnlyCorrupt,
                version,
                version,
                null,
                (damaged ? "The data failed its integrity check (" : "The data wasn't there (") + problem + ") and couldn't be moved aside, so nothing can be changed. Close any program that has the database open, then restart the server.",
                true,
                problem,
                Missing: !damaged);
        }

        if (done is { } restored)
        {
            Interlocked.Increment(ref _restores);
            return restored.State;
        }

        var triedText = tried.Count == 0 ? "there is no snapshot yet" : "tried: " + string.Join(", ", tried);
        var later = DatedLater(null);
        var laterText = later.Count == 0 ? string.Empty : " Dated later than now (the clock is wrong now, or was when they were taken), so tried last: " + string.Join(", ", later) + ".";
        string message;
        if (damaged)
        {
            _logger.LogError(
                "{File} is damaged ({Reason}) and no snapshot passed its integrity check ({Tried}). Nothing was moved: it is read-only, its undamaged tables can still be read, and only an admin can start it empty",
                FilePath,
                problem,
                triedText);
            message = "The data failed its integrity check (" + problem + ") and no snapshot passed its own (" + triedText + "), so nothing was moved and nothing can be changed; undamaged data can still be read. To go on, start empty from the plugin's settings page (the damaged database is moved to a quarantine folder and the snapshots are kept), then import an export." + laterText;
            HoldCorruptGuard();
        }
        else
        {
            _logger.LogError(
                "{File} has no data ({Reason}) while snapshots of it exist, and none of them could be restored ({Tried}). Nothing was created: it is read-only until an admin restores a snapshot or starts it empty",
                FilePath,
                problem,
                triedText);
            message = "The data wasn't there (" + problem + ") and none of its snapshots could be restored (" + triedText + "), so nothing can be changed. Restore a snapshot, or start empty, from the plugin's settings page; the snapshots are kept." + laterText;
        }

        return new OpenState(SqliteDatabaseStatus.ReadOnlyCorrupt, version, version, null, message, true, problem, Missing: !damaged);
    }

    // After a recovery (or with no database yet): open, check again, upgrade.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private OpenState OpenChecked(SqliteRecoveryResult? recovery)
    {
        if (recovery is not null && File.Exists(FilePath) && CheckLive() is { } again)
        {
            // The checked snapshot failed in place: never loop; the database stays read-only.
            HoldCorruptGuard();
            return new OpenState(SqliteDatabaseStatus.ReadOnlyCorrupt, null, null, null, "The data failed its integrity check again after it was restored (" + again + "); nothing can be changed. The server log has the details.", true, again);
        }

        int version;
        using (var connection = OpenConnection(PooledConnectionString(), WritablePragmas))
        {
            version = ReadUserVersion(connection, null);
            WarnUnlessWal(connection);
        }

        if (version > CurrentVersion)
        {
            // Newer frames in a -wal (a newer version's upgrade, then a crash): the read-only probe at open catches that
            // first; this stays for a file that changed in between.
            ClearPools();
            return OpenNewer(version);
        }

        int? migratedFrom = null;
        var created = version == 0 && !HasTables();
        if (version < CurrentVersion)
        {
            if (!created && TakePreOpSnapshot(SnapshotOperations.Migrate, null) is null)
            {
                const string NoSnapshot = "The data couldn't be upgraded because the snapshot before the upgrade couldn't be taken; nothing was changed. The server log has the details.";
                _logger.LogError("Did not upgrade {File} from schema {From} to {To}: the pre-operation snapshot failed", FilePath, version, CurrentVersion);
                return new OpenState(SqliteDatabaseStatus.ReadOnlyMigrationFailed, version, version, null, NoSnapshot, false);
            }

            // In a recovery the upgrade's frames stay in the -wal until the journal is gone, so the file in place is still
            // the one whose identity the journal holds, should the server stop meanwhile.
            var failure = Migrate(version, holdCheckpoints: recovery is not null);
            if (failure is not null)
            {
                return new OpenState(SqliteDatabaseStatus.ReadOnlyMigrationFailed, version, version, null, failure, false);
            }

            if (!created)
            {
                migratedFrom = version;
                _logger.LogInformation("Upgraded {File} from schema {From} to {To}", FilePath, version, CurrentVersion);
            }
        }

        // A VacuumAfter step's VACUUM that failed at an earlier open (its marker is there): tried again now, before any other
        // use, never in a recovery's open (whose frames stay in the -wal) nor right after the upgrade that just tried it.
        if (recovery is null && version == CurrentVersion && !created && File.Exists(VacuumPendingPath))
        {
            RetryPendingVacuum();
        }

        _lastRecovery = recovery ?? _lastRecovery;
        if (recovery is not null)
        {
            return new OpenState(
                recovery.RestoredFrom is null ? SqliteDatabaseStatus.RecoveredEmpty : SqliteDatabaseStatus.RestoredFromSnapshot,
                version,
                CurrentVersion,
                migratedFrom,
                RecoveryMessage(recovery),
                false);
        }

        return new OpenState(
            migratedFrom is null ? SqliteDatabaseStatus.Ok : SqliteDatabaseStatus.Migrated,
            created ? null : version,
            CurrentVersion,
            migratedFrom,
            migratedFrom is null ? null : string.Create(CultureInfo.InvariantCulture, $"The data was upgraded from schema {migratedFrom} to {CurrentVersion}; a snapshot from before the upgrade was kept."),
            false);
    }

    private static string RecoveryMessage(SqliteRecoveryResult recovery)
    {
        if (recovery.RestoredFrom is null)
        {
            return (recovery.QuarantineFolder is null ? "An admin chose to start empty" : "An admin chose to start empty, and the data was moved to " + recovery.QuarantineFolder)
                + "; the earlier snapshots are kept. Import an export, or restore one of them, to bring the data back.";
        }

        var restored = string.Create(
            CultureInfo.InvariantCulture,
            $"it was restored from the snapshot {recovery.RestoredFrom} taken {recovery.SnapshotUtc:yyyy-MM-dd HH:mm} UTC, so changes made in the {recovery.LostSpan} before the restore are missing.");
        if (recovery.PassedOver is { Count: > 0 } passed)
        {
            restored += " Passed over first: " + string.Join(", ", passed) + ".";
        }

        if (recovery.DatedLater is { Count: > 0 } later)
        {
            restored += " Dated later than now, so tried last: " + string.Join(", ", later)
                + ". If the clock is wrong now rather than when they were taken, they may hold newer data than the snapshot restored: they are kept too, so restore the one you want on the plugin's settings page.";
        }

        restored += " Every snapshot is kept until you restore one of them.";

        if (recovery.Damaged)
        {
            return "The data was damaged and was moved to " + recovery.QuarantineFolder + "; " + restored;
        }

        return "The data wasn't there (" + recovery.Reason + ")"
            + (recovery.QuarantineFolder is null ? string.Empty : ", and what was found in its place was moved to " + recovery.QuarantineFolder)
            + "; " + restored;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private OpenState OpenNewer(int version)
    {
        _logger.LogError(
            "{File} was written by a newer plugin version (schema {Version}; this version knows up to {Current}); it is read-only and nothing is changed",
            FilePath,
            version,
            CurrentVersion);
        var state = new OpenState(SqliteDatabaseStatus.ReadOnlyNewerVersion, version, version, null, SqliteMessages.NewerVersion, true);
        try
        {
            using var connection = OpenConnection(ReadOnlyConnectionString(), ReadOnlyPragmas);
            var check = RunCheck(connection, SqliteCheckResult.QuickCheck);
            _lastQuickCheck = check;
            if (!check.Ok)
            {
                _logger.LogError("{File} of a newer plugin version failed its quick_check ({Check}); it is left as it is", FilePath, check.FirstMessage);
            }
        }
        catch (SqliteException ex)
        {
            _lastQuickCheck = new SqliteCheckResult(SqliteCheckResult.QuickCheck, UtcNow(), false, [DescribeSqliteError(ex)]);
            _logger.LogError(ex, "{File} of a newer plugin version couldn't be checked; it is left as it is", FilePath);
        }

        return state;
    }

    // user_version through the -wal on a read-only, unpooled connection, which never checkpoints: null when the file is
    // damaged (SQLITE_CORRUPT, SQLITE_NOTADB; the check finds it next); any other error (locked, no access) propagates and
    // the database is Unavailable, with nothing changed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int? ProbeUserVersion()
    {
        try
        {
            using var connection = OpenConnection(FileConnectionString(FilePath, readOnly: true), ReadOnlyPragmas);
            return ReadUserVersion(connection, null);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            return null;
        }
    }

    // quick_check on the live database: null when it passes, else why. SQLITE_CORRUPT and SQLITE_NOTADB count as a failed
    // check. A line that reports a failure to read (SqliteCheckResult.IsInconclusive), and any other error (locked, no
    // access), make the open fail: the database is Unavailable, nothing is moved, and the next use tries again.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? CheckLive()
    {
        SqliteCheckResult check;
        try
        {
            using var connection = OpenConnection(PooledConnectionString(), WritablePragmas);
            check = RunCheck(connection, SqliteCheckResult.QuickCheck);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            var reason = DescribeSqliteError(ex);
            _lastQuickCheck = new SqliteCheckResult(SqliteCheckResult.QuickCheck, UtcNow(), false, [reason]);
            return reason;
        }

        _lastQuickCheck = check;
        if (check.Ok)
        {
            return null;
        }

        if (check.IsInconclusive)
        {
            throw new RetryLaterException("The quick_check of " + FilePath + " couldn't read part of the file (" + check.IoError + "); nothing was moved, and it is checked again at the next use");
        }

        return "quick_check: " + check.Summary;
    }

    // At open, with a damage marker (KIT-1): integrity_check on a fresh read-only, unpooled connection. It passes: the marker
    // goes and the open goes on (repaired by hand, or the damage was in a page cache only). Only indexes are damaged: they
    // are rebuilt (TryReindex), the marker goes and the open goes on. Otherwise: the reason, for the restore of the newest
    // good snapshot. A check that can't read part of the file: the open fails and is tried again, nothing moved.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? RecheckMarkedDamage(string marked)
    {
        _logger.LogWarning("{File} was found damaged before the restart ({Marked}); it is checked with integrity_check", FilePath, marked);
        var check = CheckFile(FilePath);
        _lastQuickCheck = check;
        if (check.Ok)
        {
            _logger.LogWarning("{File} passes integrity_check now; it is opened as it is", FilePath);
            ClearDamageMarker();
            return null;
        }

        if (check.IsInconclusive)
        {
            throw new RetryLaterException("The integrity_check of " + FilePath + " that a damage marker asked for couldn't read part of the file (" + check.IoError + "); nothing was moved, and it is checked again at the next use");
        }

        var reason = "integrity_check: " + check.Summary;
        if (!IsIndexesOnly(check))
        {
            return reason;
        }

        if (TryReindex(reason + " (found before the restart: " + marked + ")") is not { } failed)
        {
            ClearDamageMarker();
            return null;
        }

        _logger.LogError("{File}: rebuilding its indexes didn't repair it ({Failed}); the newest good snapshot is restored instead", FilePath, failed);
        return reason + "; " + failed;
    }

    // Only indexes damaged (IsIndexesOnly): a copy of the database as found, then REINDEX on a read-write connection of its
    // own (the tables are the source of the indexes), then integrity_check again. Null when it repaired the database; else
    // why not (the copy, REINDEX, or the check after it failed), and the open restores a snapshot.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? TryReindex(string reason)
    {
        var utc = UtcNow();
        string folder;
        var copied = new List<string>();
        try
        {
            var stem = Path.Combine(Folder, QuarantinePrefix + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "-reindex");
            folder = stem;
            for (var n = 1; Directory.Exists(folder) || File.Exists(folder); n++)
            {
                folder = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}");
            }

            Directory.CreateDirectory(folder);
            foreach (var file in SqliteFiles.SidecarSuffixes.Select(s => FilePath + s).Prepend(FilePath).Where(File.Exists))
            {
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
                copied.Add(Path.GetFileName(file));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "the database couldn't be copied aside before REINDEX (" + ex.Message + ")";
        }

        try
        {
            using (var connection = OpenConnection(UnpooledConnectionString(), WritablePragmas))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "REINDEX;";
                command.ExecuteNonQuery();
            }

            var after = CheckFile(FilePath);
            if (!after.Ok)
            {
                return "integrity_check after REINDEX: " + after.Summary;
            }
        }
        catch (SqliteException ex)
        {
            return "REINDEX failed: " + DescribeSqliteError(ex);
        }

        _lastReindex = new SqliteReindexResult(utc, reason, Path.GetFileName(folder), copied);
        _logger.LogWarning("{File}: only its indexes were damaged ({Reason}); they were rebuilt from the tables (REINDEX), and the database as found is kept in {Folder}", FilePath, reason, Path.GetFileName(folder));
        return null;
    }

    // Whether every line of a failed integrity_check names an index whose entries don't match its table: the tables read
    // intact, so REINDEX can rebuild the indexes from them. A check that lists only its first lines is judged on those;
    // the check after REINDEX catches the rest.
    internal static bool IsIndexesOnly(SqliteCheckResult check)
    {
        ArgumentNullException.ThrowIfNull(check);
        return check.IsDamaged && check.Messages.Count > 0 && check.Messages.All(m => IsIndexLine(m.Trim()));
    }

    private static bool IsIndexLine(string line)
        => (line.StartsWith("row ", StringComparison.Ordinal) && line.Contains(" missing from index ", StringComparison.Ordinal))
            || line.StartsWith("wrong # of entries in index ", StringComparison.Ordinal)
            || line.StartsWith("non-unique entry in index ", StringComparison.Ordinal)
            || line.StartsWith("non-unique entry in UNIQUE index ", StringComparison.Ordinal);

    // The damage marker (DamageMarkerPath): what was found and when, written by MarkCorrupt, read at open. Best effort: a
    // marker that can't be written leaves the database as before (found again at open only by a check that sees it).
    private void WriteDamageMarker(string reason, bool indexesOnly)
    {
        try
        {
            var text = new JsonObject { ["Utc"] = UtcNow(), ["Reason"] = reason, ["IndexesOnly"] = indexesOnly }.ToJsonString();
            var temp = DamageMarkerPath + TempSuffix;
            File.WriteAllText(temp, text);
            File.Move(temp, DamageMarkerPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The damage marker of {File} couldn't be written; a restart finds the damage only if its check sees it", FilePath);
        }
    }

    // The marker's reason, or null without one; a marker that can't be read still says the database was found damaged.
    private string? ReadDamageMarker()
    {
        if (!File.Exists(DamageMarkerPath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(DamageMarkerPath))?["Reason"]?.GetValue<string>() ?? "a damage marker without a reason";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return "a damage marker that couldn't be read (" + ex.Message + ")";
        }
    }

    private void ClearDamageMarker()
    {
        try
        {
            File.Delete(DamageMarkerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The damage marker of {File} couldn't be deleted; the next open checks the database again", FilePath);
        }
    }

    // Before anything is moved at open: after a pause, integrity_check on a fresh read-only, unpooled connection (none of
    // the first check's cached pages) must find damage too. It passes: the first failure was transient, and the database
    // is opened as it is. It can't read part of the file: the open fails and is tried again. Returns the reason, or null.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? ConfirmDamage(string problem)
    {
        _logger.LogWarning("{File} failed its quick_check at open ({Problem}); it is checked again with integrity_check before anything is moved", FilePath, problem);
        if (DamageRecheckDelay > TimeSpan.Zero)
        {
            Thread.Sleep(DamageRecheckDelay);
        }

        var check = CheckFile(FilePath);
        _lastQuickCheck = check;
        if (check.Ok)
        {
            _logger.LogWarning("{File} passed integrity_check on a fresh connection after its quick_check failed ({Problem}); it is opened as it is", FilePath, problem);
            return null;
        }

        if (check.IsInconclusive)
        {
            throw new RetryLaterException("The integrity_check of " + FilePath + " that confirms damage couldn't read part of the file (" + check.IoError + "); nothing was moved, and it is checked again at the next use");
        }

        return problem;
    }

    // Whether a snapshot of this database's data exists: a daily or pre-operation one with user_version 1 or more, or one
    // that can't be read just now (it may well be). Then a missing or empty database is restored, never started empty.
    private bool DataSnapshotsExist()
        => SnapshotRotation.List(SnapshotDirectory, StoreName, Extension)
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension))
            .Any(s => !SqliteFiles.TryReadHeader(s.Path, out var header) || header is { UserVersion: >= 1 });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool HasTables()
    {
        using var connection = OpenConnection(PooledConnectionString(), WritablePragmas);
        return Convert.ToInt64(Scalar(connection, null, "SELECT COUNT(*) FROM sqlite_master;"), CultureInfo.InvariantCulture) > 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void WarnUnlessWal(SqliteConnection connection)
    {
        var mode = Convert.ToString(Scalar(connection, null, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture);
        if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _journalModeWarned, 1) == 0)
        {
            _logger.LogWarning("{File} is in journal mode {Mode}, not WAL: readers wait for writers. Is it on a network drive?", FilePath, mode);
        }
    }

    // Runs every step above fromVersion in one transaction. Returns null on success, else the status message;
    // the transaction is rolled back, so the database and its user_version are unchanged. holdCheckpoints: no automatic
    // checkpoint on this connection, so a large upgrade's frames stay in the -wal and the database file isn't written (a
    // recovery's open, while its journal holds the file's identity).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? Migrate(int fromVersion, bool holdCheckpoints = false)
    {
        var pending = _migrations.Where(m => m.Version > fromVersion).ToList();
        var rebuild = pending.Any(m => m.RequiresRebuild);
        var step = pending[0];

        // Unpooled: a rebuild turns foreign keys off, and a pooled connection would hand that to the next user.
        using var connection = OpenConnection(UnpooledConnectionString(), WritablePragmas);
        try
        {
            if (holdCheckpoints)
            {
                Execute(connection, null, "PRAGMA wal_autocheckpoint=0;");
            }

            if (rebuild)
            {
                // Before BEGIN: inside a transaction PRAGMA foreign_keys does nothing (sqlite.org/foreignkeys.html §2).
                Execute(connection, null, "PRAGMA foreign_keys=OFF;");
            }

            using var transaction = connection.BeginTransaction();
            if (rebuild && Convert.ToInt64(Scalar(connection, transaction, "PRAGMA foreign_keys;"), CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException("Foreign keys are still on inside the upgrade's transaction, so a table rebuild would delete child rows.");
            }

            foreach (var migration in pending)
            {
                step = migration;
                Execute(connection, transaction, migration.Sql);
            }

            if (rebuild)
            {
                var violations = ForeignKeyViolations(connection, transaction);
                if (violations.Count > 0)
                {
                    throw new InvalidOperationException(string.Create(
                        CultureInfo.InvariantCulture,
                        $"PRAGMA foreign_key_check found {violations.Count} rows that break a foreign key after the rebuild: {string.Join("; ", violations.Take(5))}."));
                }
            }

            Execute(connection, transaction, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {CurrentVersion};"));
            transaction.Commit();
            if (pending.FirstOrDefault(m => m.VacuumAfter) is { } vacuum)
            {
                Vacuum(connection, vacuum, holdCheckpoints);
            }

            return null;
        }
        catch (SqliteException ex) when (IsBusy(ex))
        {
            // Another connection held the database: nothing failed and nothing changed, so the upgrade is tried again at the
            // next use instead of leaving the data read-only as a failed upgrade until a restart.
            _logger.LogWarning(
                "Did not upgrade {File} from schema {From} to {To} just now: another connection kept it locked ({Error}); nothing was changed, and it is tried again",
                FilePath,
                fromVersion,
                CurrentVersion,
                DescribeSqliteError(ex));
            throw new RetryLaterException("Another connection kept " + FilePath + " locked during its upgrade (" + ex.Message + "); nothing was changed, and it is tried again at the next use")
            {
                PublicMessage = SqliteMessages.Busy
            };
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
            _logger.LogError(
                ex,
                "Could not upgrade {File} from schema {From} to {To}: the step to version {Step} ({Name}) failed; everything was rolled back and the data is read-only",
                FilePath,
                fromVersion,
                CurrentVersion,
                step.Version,
                step.Name);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Stored data couldn't be upgraded from schema {fromVersion} to {CurrentVersion}: the step to version {step.Version} ({step.Name}) failed, so nothing was changed. Install the previous plugin version or restore an export.");
        }
        finally
        {
            if (rebuild)
            {
                try
                {
                    Execute(connection, null, "PRAGMA foreign_keys=ON;");
                }
                catch (SqliteException ex)
                {
                    _logger.LogDebug(ex, "Could not turn foreign keys on again on the upgrade's own connection to {File}", FilePath);
                }
            }
        }
    }

    // A step's VACUUM after the upgrade committed (SqliteMigration.VacuumAfter): the file is rebuilt with only what is
    // live, so what earlier deletes left in free space is gone, then the -wal is checkpointed and cut (not while a
    // recovery holds its frames there). Best effort: the upgrade stands. The marker (VacuumPendingPath) is written
    // first and deleted once the VACUUM succeeds, so one that fails (a full disk: it needs room for a copy) is a
    // Warning and is tried again at the next open (RetryPendingVacuum).
    private void Vacuum(SqliteConnection connection, SqliteMigration step, bool holdCheckpoints)
    {
        var what = string.Create(CultureInfo.InvariantCulture, $"the upgrade's step to version {step.Version} ({step.Name})");
        WriteVacuumPending(what);
        try
        {
            VacuumFault?.Invoke();
            Execute(connection, null, "VACUUM;");
            if (!holdCheckpoints)
            {
                Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            }

            DeleteVacuumPending();
            _logger.LogInformation("Vacuumed {File} after {What}", FilePath, what);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(
                "The upgrade of {File} to version {Step} ({Name}) is done, but its VACUUM failed ({Error}); what earlier deletes left in the file's free space stays until it is reused or the VACUUM succeeds: it is tried again at the next start",
                FilePath,
                step.Version,
                step.Name,
                DescribeSqliteError(ex));
        }
    }

    // A VacuumAfter step's VACUUM that failed at an earlier open, tried again on a connection of its own (at open, in
    // OpenChecked). One that fails again is a Warning and stays pending; the database opens either way.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RetryPendingVacuum()
    {
        string what;
        try
        {
            what = File.ReadAllText(VacuumPendingPath).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            what = "an earlier upgrade";
        }

        try
        {
            using var connection = OpenConnection(UnpooledConnectionString(), WritablePragmas);
            VacuumFault?.Invoke();
            Execute(connection, null, "VACUUM;");
            Execute(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
            DeleteVacuumPending();
            _logger.LogInformation("Vacuumed {File}: the VACUUM after {What}, which failed before, is done now", FilePath, what);
        }
        catch (SqliteException ex) when (!IsCorruption(ex))
        {
            _logger.LogWarning(
                "The VACUUM of {File} after {What} failed again ({Error}); what earlier deletes left in the file's free space stays, and it is tried again at the next start",
                FilePath,
                what,
                DescribeSqliteError(ex));
        }
    }

    private void WriteVacuumPending(string what)
    {
        try
        {
            File.WriteAllText(VacuumPendingPath, what + " (" + UtcNow().ToString("O", CultureInfo.InvariantCulture) + ")");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not write {Marker}; a VACUUM that fails now isn't tried again", VacuumPendingPath);
        }
    }

    private void DeleteVacuumPending()
    {
        try
        {
            File.Delete(VacuumPendingPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Marker}; the next open runs the VACUUM again", VacuumPendingPath);
        }
    }

    // RestoreCore, with a corruption it hits in the live database (damage no check before could read) handed back for
    // MarkCorrupt outside the gate.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int? RestoreCoreNotingDamage(StoreSnapshot snapshot, int version, out string? damage, out Exception? error)
    {
        damage = null;
        error = null;
        try
        {
            return RestoreCore(snapshot, version);
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            damage = DescribeSqliteError(ex);
            error = ex;
            return null;
        }
    }

    // The logical restore (healthy database), under the writer lock and the gate held alone. Returns the version the
    // snapshot was upgraded from, or null.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private int? RestoreCore(StoreSnapshot snapshot, int version)
    {
        ClearPools();
        using (var source = OpenConnection(FileConnectionString(snapshot.Path, readOnly: true), null))
        using (var live = OpenConnection(UnpooledConnectionString(), WritablePragmas))
        {
            source.BackupDatabase(live);
            var mode = Convert.ToString(Scalar(live, null, "PRAGMA journal_mode;"), CultureInfo.InvariantCulture);
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                Scalar(live, null, "PRAGMA journal_mode=WAL;");
            }
        }

        if (version >= CurrentVersion)
        {
            ReplaceState(version, null);
            return null;
        }

        string? failure;
        try
        {
            failure = Migrate(version);
        }
        catch (RetryLaterException ex)
        {
            // The snapshot is in place at its older schema; the next use opens and upgrades it as a start does.
            lock (_openLock)
            {
                _state = null;
            }

            throw new PluginException(PluginErrorKind.Unavailable, ex.PublicMessage ?? SqliteMessages.Busy, ex);
        }

        if (failure is not null)
        {
            lock (_openLock)
            {
                _state = new OpenState(SqliteDatabaseStatus.ReadOnlyMigrationFailed, version, version, null, failure, false);
            }

            return null;
        }

        ReplaceState(version, version);
        return version;
    }

    private void ReplaceState(int loadedVersion, int? migratedFrom)
    {
        lock (_openLock)
        {
            _state = new OpenState(
                migratedFrom is null ? SqliteDatabaseStatus.Ok : SqliteDatabaseStatus.Migrated,
                loadedVersion,
                CurrentVersion,
                migratedFrom,
                migratedFrom is null ? null : string.Create(CultureInfo.InvariantCulture, $"The restored snapshot was upgraded from schema {migratedFrom} to {CurrentVersion}."),
                false);
        }
    }

    // The corruption restore of a damaged (or missing) database found while the server runs, or an admin's start empty;
    // under the writer lock, the gate held alone. Nothing passes and no start empty: nothing moved, 409. A later step that
    // fails once the database was moved aside: everything put back as it was, and the answer says so.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private SqliteRecoveryResult RecoverAtRuntime(string reason, StoreSnapshot? snapshot, bool startEmpty, bool damaged)
    {
        (SqliteRecoveryResult Recovery, OpenState State)? done;
        IReadOnlyList<string> tried;
        try
        {
            done = Recover(reason, snapshot, null, startEmpty, damaged, out tried);
        }
        catch (RetryLaterException ex)
        {
            _logger.LogWarning("Did not restore {File}: {Reason}", FilePath, ex.Message);
            throw new PluginException(PluginErrorKind.Unavailable, ex.PublicMessage ?? SqliteMessages.SnapshotUnavailable, ex);
        }
        catch (RecoveryFailedException ex)
        {
            if (ex.Stuck.Count > 0)
            {
                // What was moved aside is in its quarantine folder: nothing reads or creates the file at that path until an
                // admin's recovery or a restart.
                var message = SqliteMessages.RecoveryNotPutBack(ex.Failure, ex.Folder!);
                lock (_openLock)
                {
                    _state = new OpenState(SqliteDatabaseStatus.ReadOnlyCorrupt, null, null, null, message, true, StuckProblem(reason, ex.Folder!), Missing: true);
                }

                throw new PluginException(PluginErrorKind.Unavailable, message, ex);
            }

            if (damaged)
            {
                HoldCorruptGuard();
            }

            throw new PluginException(ex.Kind, SqliteMessages.RecoveryPutBack(ex.Failure, ex.Moved > 0), ex);
        }
        catch (IOException ex)
        {
            throw new PluginException(PluginErrorKind.Unavailable, SqliteMessages.MoveAsideFailed(damaged), ex);
        }

        if (done is not { } restored)
        {
            _logger.LogError("Did not restore {File}: no snapshot passed its integrity check (tried: {Tried}); nothing was changed", FilePath, tried.Count == 0 ? "none" : string.Join(", ", tried));
            throw PluginException.Conflict(SqliteMessages.NoSnapshotPassed);
        }

        lock (_openLock)
        {
            _state = restored.State;
        }

        return restored.Recovery;
    }

    // Picks and checks the snapshot (none when startEmpty: a new empty database, prepared the same way), moves the
    // database with its sidecars into a quarantine folder, puts the checked copy (or the new database) in its place and
    // opens it again (checked, upgraded, or given its schema). Returns null, with nothing moved, when no snapshot
    // passes; a snapshot that can't be read just then throws RetryLaterException, nothing moved; a move aside that
    // fails puts back what it moved and throws IOException. Once the database was moved aside, a later step that fails
    // (the rename, the open, the check in place, the upgrade, the new database's schema, the journal) puts everything
    // back as it was (PutBack) and throws RecoveryFailedException, which says what failed and, if a file couldn't be
    // moved back, where it is (as for the JSON store). Every step is on the disk before it is taken
    // (RecoveryJournalPath): the journal with the files to move before the first move, the kept snapshots and then the
    // identity of what takes the database's place before the rename, so a server stopped at any point leaves what the
    // next open needs to put everything back. The journal goes once the open succeeded, before the new state is
    // published, and only then is the recovery done. orphans: sidecars without their database that the open moved aside
    // first, named in the result when nothing else was moved.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private (SqliteRecoveryResult Recovery, OpenState State)? Recover(string reason, StoreSnapshot? named, IReadOnlySet<string>? foundBeforeCheck, bool startEmpty, bool damaged, out IReadOnlyList<string> tried, (string Folder, IReadOnlyList<string> Files)? orphans = null)
    {
        var utc = UtcNow();
        ClearPoolsKeepingFiles();
        if (foundBeforeCheck is not null)
        {
            DeleteSidecarsOfTheCheck(foundBeforeCheck);
        }

        var temp = FilePath + RestoreTempSuffix;
        var skipped = new List<string>();

        // For the banners: each name, with why when it was passed over without being read (no permission).
        var passedOver = new List<string>();
        tried = passedOver;
        StoreSnapshot? chosen = null;
        if (!startEmpty)
        {
            var candidates = named is not null ? [named] : RecoveryCandidates();
            string? lastDetail = null;
            foreach (var candidate in candidates)
            {
                var outcome = PrepareRestoreCopy(candidate, temp, out var detail);
                if (outcome == RestoreCopy.Ready)
                {
                    chosen = candidate;
                    break;
                }

                if (outcome == RestoreCopy.Unreadable)
                {
                    // Never a reason to fall back to an older snapshot, or to nothing: try again later.
                    throw new RetryLaterException("The snapshot " + candidate.Name + " couldn't be read just now (" + detail + "), so " + FilePath + " was left as it is; it is tried again")
                    {
                        PublicMessage = SqliteMessages.SnapshotInUse(candidate.Name)
                    };
                }

                skipped.Add(candidate.Name);
                passedOver.Add(detail is null ? candidate.Name : candidate.Name + " (" + detail + ")");
                lastDetail = detail;
            }

            if (chosen is null)
            {
                if (named is not null)
                {
                    throw PluginException.Conflict(lastDetail == NoPermission
                        ? "The snapshot " + named.Name + " can't be restored: the server has " + NoPermission + ", so nothing was changed."
                        : "The snapshot " + named.Name + " failed its integrity check or is from a newer plugin version, so nothing was changed.");
                }

                return null;
            }

            _snapshotInUseLogged = null;
        }
        else
        {
            try
            {
                PrepareEmptyDatabase(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                TryDeleteWithSidecars(temp);
                _logger.LogError(ex, "Did not start {File} empty: the new database couldn't be made beside it; nothing was moved", FilePath);
                throw new RecoveryFailedException("The new empty database couldn't be created", PluginErrorKind.Unavailable, null, 0, [], ex);
            }
        }

        var why = startEmpty ? "an admin chose to start empty" : damaged ? "it failed its integrity check" : "a snapshot of the data was restored in its place";
        var undo = CaptureForPutBack();
        var journal = new SqliteRecoveryJournalEntry(null, [], SqliteRecoveryJournal.Quarantining, utc, damaged, reason, chosen?.Name, null, null, EarlierJournal());
        string? folder;
        IReadOnlyList<string> moved;
        try
        {
            (folder, moved, var written) = Quarantine(reason, why, utc, journal);
            journal = written ?? journal;
        }
        catch
        {
            // What was moved went back (or Quarantine says where it is); the checked copy goes too.
            TryDeleteWithSidecars(temp);
            throw;
        }

        var (shownFolder, shownFiles) = moved.Count == 0 && orphans is { } found ? (found.Folder, found.Files) : (folder, moved);
        var recovery = new SqliteRecoveryResult(utc, reason, shownFolder, shownFiles, chosen?.Name, chosen?.Utc, skipped, damaged, passedOver, chosen is null ? [] : DatedLater(chosen.Name));

        // What failed, for the answer and the banner (path-free).
        var failure = chosen is null ? "The new empty database couldn't be created" : "The snapshot " + chosen.Name + " couldn't be put in place of the database";
        var placed = false;

        // From the rename until the journal is gone: a read-only connection to what is in the database's place, so none
        // of our read-write connections is ever the last to close and checkpoint the -wal into the file (a pool
        // cleared, an idle connection the pool closes), and the file keeps the identity the journal holds, whatever the
        // pooling does. A read-only connection never checkpoints.
        IDisposable? placedGuard = null;
        OpenState state;
        try
        {
            try
            {
                // The kept snapshots before the copy goes in: a copy in place that a restart would open as
                // it is never stands there without them.
                KeepSnapshotsAtIncident(utc, chosen is null ? SqliteDatabaseStatus.RecoveredEmpty : SqliteDatabaseStatus.RestoredFromSnapshot, reason);
                if (chosen is not null)
                {
                    RememberRestore(chosen);
                }

                RecoveryStepFault?.Invoke("marker");

                // What takes the database's place, by size and SHA-256, before it does: the next open deletes that and
                // nothing else.
                journal = journal with { Step = SqliteRecoveryJournal.Installing, Placed = SqliteRecoveryJournal.Identify(temp) };
                WriteJournal(journal);
                InstallRestoreCopy(temp);

                // From here on whatever is at the database's path is ours: the copy, or the new database.
                placed = true;
                placedGuard = HoldPlacedGuard();
                RecoveryStepFault?.Invoke("installed");
                if (chosen is not null)
                {
                    failure = "The snapshot " + chosen.Name + " couldn't be opened in place of the database";
                }

                RecoveryStepFault?.Invoke("open");
                state = OpenChecked(recovery);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException or InvalidOperationException)
            {
                throw Failed(failure, PluginErrorKind.Unavailable, folder, moved, placed, temp, undo, journal, placedGuard, ex);
            }

            if (state.Status is not (SqliteDatabaseStatus.RestoredFromSnapshot or SqliteDatabaseStatus.RecoveredEmpty))
            {
                // The open found the copy damaged in place, or the upgrade (or the new database's schema) failed: nothing of
                // it is kept, and the database goes back.
                var (what, kind) = (chosen, state.Status) switch
                {
                    (null, _) => ("The new empty database couldn't be created", PluginErrorKind.Unavailable),
                    (_, SqliteDatabaseStatus.ReadOnlyMigrationFailed) => (string.Create(CultureInfo.InvariantCulture, $"The snapshot {chosen.Name} couldn't be upgraded to schema {CurrentVersion}"), PluginErrorKind.Conflict),
                    (_, SqliteDatabaseStatus.ReadOnlyCorrupt) => ("The snapshot " + chosen.Name + " failed its integrity check once in place of the database", PluginErrorKind.Unavailable),
                    _ => (failure, PluginErrorKind.Unavailable)
                };
                throw Failed(what + " (" + state.Status + ")", kind, folder, moved, placed, temp, undo, journal, placedGuard, null, what);
            }

            RecoveryStepFault?.Invoke("opened");
            try
            {
                // Done: the journal goes, an earlier recovery's inside it too (what that one moved stays in its folder: an
                // admin chose to go on without it). While it is there a restart takes this recovery for one that didn't
                // finish.
                File.Delete(RecoveryJournalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Failed("The recovery couldn't be recorded as finished (its journal couldn't be deleted)", PluginErrorKind.Unavailable, folder, moved, placed, temp, undo, journal, placedGuard, ex);
            }

            // Another database took the damaged one's place (a restore at open, an admin's, a start empty): the damage
            // marker was the damaged one's (KIT-1).
            ClearDamageMarker();
        }
        finally
        {
            // The journal is gone, or the put back (Failed) has closed it already.
            placedGuard?.Dispose();
        }

        if (chosen is null)
        {
            _logger.LogWarning(
                "{File} starts empty, as an admin chose ({Reason}); moved aside: {Files}. The snapshots taken before now are kept until one of them is restored",
                FilePath,
                reason,
                moved.Count == 0 ? "nothing" : folder + ": " + string.Join(", ", moved));
        }
        else if (damaged)
        {
            _logger.LogError(
                "{File} was damaged ({Reason}). It was moved to {Folder} ({Files}), and the snapshot {Snapshot} taken {SnapshotUtc} was put in its place; changes made after it are missing",
                FilePath,
                reason,
                folder,
                string.Join(", ", moved),
                chosen.Name,
                chosen.Utc);
        }
        else
        {
            _logger.LogError(
                "{File} had no data ({Reason}) while snapshots of it exist. The snapshot {Snapshot} taken {SnapshotUtc} was put in its place (moved aside first: {Files}); changes made after it are missing",
                FilePath,
                reason,
                chosen.Name,
                chosen.Utc,
                shownFiles.Count == 0 ? "nothing" : shownFolder + ": " + string.Join(", ", shownFiles));
        }

        return (recovery, state);
    }

    // A recovery's later step failed once the database was moved aside: everything is put back (PutBack), one Error says
    // what failed and what went back (or where what couldn't go back is), and the exception carries it to the answer.
    private RecoveryFailedException Failed(string logged, PluginErrorKind kind, string? folder, IReadOnlyList<string> moved, bool placed, string temp, RecoveryUndo undo, SqliteRecoveryJournalEntry journal, IDisposable? placedGuard, Exception? ex, string? failure = null)
    {
        var stuck = PutBack(folder, moved, placed, temp, undo, journal, placedGuard, failure ?? logged);
        if (stuck.Count == 0)
        {
            _logger.LogError(
                ex,
                "Did not recover {File}: {Failure}. What had been moved aside ({Moved}) was put back as it was, and nothing was changed",
                FilePath,
                logged,
                moved.Count == 0 ? "nothing" : folder + ": " + string.Join(", ", moved));
        }
        else
        {
            _logger.LogError(
                ex,
                "Did not recover {File}: {Failure}, and {Stuck} couldn't be moved back: they are in {Folder}. Move them back by hand with the server stopped, or restore a snapshot or start empty; the next start tries to put them back first",
                FilePath,
                logged,
                string.Join(", ", stuck),
                Path.Combine(Folder, folder!));
        }

        return new RecoveryFailedException(failure ?? logged, kind, folder, moved.Count, stuck, ex);
    }

    // Before a recovery moves anything: the two markers' bytes (null when there is none) and the pre-operation snapshots,
    // so that a recovery that fails later leaves them as they were (PutBack).
    private RecoveryUndo CaptureForPutBack()
    {
        return new RecoveryUndo(
            ReadMarker(KeepSnapshotsMarkerPath),
            ReadMarker(LastRestoreMarkerPath),
            _kept,
            SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension).Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));

        (bool Known, byte[]? Bytes) ReadMarker(string path)
        {
            try
            {
                return (true, File.Exists(path) ? File.ReadAllBytes(path) : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not read {Marker} before a recovery; a recovery that fails leaves it as that one writes it", path);
                return (false, null);
            }
        }
    }

    // After a recovery moved the database aside and a later step failed (as the JSON store puts back): our connections
    // to what took its place are closed, with a read-only one holding the file meanwhile (ClearPoolsKeepingFiles, then
    // the recovery's own guard), so the last of them to close never checkpoints the -wal into it (ClearPools used to,
    // so the file no longer had the identity in the journal, and when it then couldn't be deleted a restart opened it
    // as an admin's own database, with the data left in the quarantine folder), and it is deleted with its sidecars,
    // the sidecars first (a copy of a snapshot that is still in the snapshots folder, or a new empty database: neither
    // holds anything else); the moved files go back from the quarantine folder, the sidecars first and the database
    // last, three tries each (MoveBack), and the folder (its note) goes; the two markers are as they were, and the
    // pre-operation snapshot the open took before upgrading the copy goes (the snapshot restored holds the same data).
    // The journal then goes (or the earlier recovery's is the journal again); while anything is still in the folder, or
    // what took the database's place couldn't be deleted, it stays as stuck, so the next open puts it back. Returns the
    // names that couldn't be moved back: empty when everything is as it was.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private List<string> PutBack(string? folder, IReadOnlyList<string> moved, bool placed, string temp, RecoveryUndo undo, SqliteRecoveryJournalEntry journal, IDisposable? placedGuard, string failure)
    {
        ClearPoolsKeepingFiles();
        placedGuard?.Dispose();
        TryDeleteWithSidecars(temp);
        var stuck = new List<string>();
        var complete = true;
        if (placed)
        {
            try
            {
                SqliteFiles.DeleteWithSidecars(FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not delete what a failed recovery put in place of {File}, so what it moved aside stays in its quarantine folder", FilePath);
                stuck.AddRange(moved);
                complete = false;
            }
        }

        if (folder is not null && complete)
        {
            var folderPath = Path.Combine(Folder, folder);
            if (MoveBack(folderPath, moved) is { } why)
            {
                _logger.LogError("Could not put back what a failed recovery of {File} moved into {Folder}: {Why}; it stays together there", FilePath, folderPath, why);
                stuck.AddRange(moved);
                complete = false;
            }
            else
            {
                RemoveQuarantineFolder(folderPath);
            }
        }

        RestoreMarker(KeepSnapshotsMarkerPath, undo.KeepMarker);
        RestoreMarker(LastRestoreMarkerPath, undo.LastRestore);
        _kept = undo.Kept;
        foreach (var taken in SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension)
            .Where(s => s.Operation == SnapshotOperations.Migrate && !undo.PreOp.Contains(s.Name)))
        {
            TryDeleteWithSidecars(taken.Path);
        }

        try
        {
            if (complete)
            {
                RestoreEarlierJournal(journal.Earlier);
            }
            else
            {
                WriteJournal(journal with { Step = SqliteRecoveryJournal.Stuck, Failure = failure });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not update {Journal} after a failed recovery; the next start reads it as it is", RecoveryJournalPath);
        }

        return stuck;
    }

    private void RestoreMarker(string path, (bool Known, byte[]? Bytes) before)
    {
        if (!before.Known)
        {
            return;
        }

        try
        {
            if (before.Bytes is null)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllBytes(path, before.Bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not put {Marker} back as it was after a failed recovery", path);
        }
    }

    // A quarantine folder whose files all went back: its note goes, then the folder, if nothing else is in it.
    private void RemoveQuarantineFolder(string folderPath)
    {
        try
        {
            File.Delete(Path.Combine(folderPath, QuarantineNoteName));
            Directory.Delete(folderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the quarantine folder {Folder}, whose files were all moved back", folderPath);
        }
    }

    // Moves what a recovery moved into its quarantine folder back next to the database, the sidecars first and the
    // database last, three tries each: the database is never at its path without the -wal it was found with, whenever
    // the process stops. Nothing moves while something else is in the way (a folder or a file in the place of one of
    // them); should a move fail, the ones already back return to the folder, so everything stays together there.
    // Returns null when all of them are back, else why not.
    private string? MoveBack(string folderPath, IReadOnlyList<string> names)
    {
        var order = names.Where(n => !IsDatabaseName(n)).Concat(names.Where(IsDatabaseName)).ToList();
        foreach (var name in order)
        {
            var target = Path.Combine(Folder, name);
            if (Directory.Exists(target))
            {
                return "a folder is in the place of " + name;
            }

            if (File.Exists(target))
            {
                return "another " + name + " is in its place";
            }
        }

        var back = new List<string>();
        foreach (var name in order)
        {
            try
            {
                MoveWithRetries(Path.Combine(folderPath, name), Path.Combine(Folder, name), "moveback:" + name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                foreach (var returned in back)
                {
                    try
                    {
                        File.Move(Path.Combine(Folder, returned), Path.Combine(folderPath, returned));
                    }
                    catch (Exception again) when (again is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogError(again, "Could not move {Name} into {Folder} again, beside the rest of what a recovery moved there", returned, folderPath);
                    }
                }

                return name + " couldn't be moved back (" + ex.Message + ")";
            }

            back.Add(name);
            RecoveryStepFault?.Invoke("putback:" + name);
        }

        return null;
    }

    // A file moved by a recovery, three tries (InstallRestoreCopy's): a scanner or an indexer holding it a moment is no
    // reason to leave the database's files apart. step is the test hook's name for each try.
    private void MoveWithRetries(string from, string to, string step)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                RecoveryStepFault?.Invoke(step);
                File.Move(from, to);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 3)
            {
                _logger.LogDebug(ex, "Could not move {From} to {To} (try {Attempt}); trying again", from, to, attempt);
                Thread.Sleep(100 * attempt);
            }
        }
    }

    private bool IsDatabaseName(string name) => string.Equals(name, Path.GetFileName(FilePath), StringComparison.OrdinalIgnoreCase);

    private void WriteJournal(SqliteRecoveryJournalEntry entry) => SqliteRecoveryJournal.Write(RecoveryJournalPath, entry);

    // Reads the journal (SqliteRecoveryJournal.Read: only the database's own files and a quarantine folder's name). An
    // earlier recovery's entry it leaves out (malformed, or nested deeper than eight) is said with the folder it names,
    // since what that recovery moved may still be there.
    private SqliteRecoveryJournalEntry? ReadJournal(out string? error)
    {
        var entry = SqliteRecoveryJournal.Read(RecoveryJournalPath, Path.GetFileName(FilePath), out error, out var dropped);
        foreach (var note in dropped)
        {
            _logger.LogWarning(
                "The journal {Journal} holds {Dropped}: it is left out and nothing it names is moved back; what that recovery moved may still be in that folder next to {File}, to be put back by hand with the server stopped",
                RecoveryJournalPath,
                note,
                FilePath);
        }

        return entry;
    }

    // Once a recovery is put back: the earlier recovery's journal is the journal again, or there is none.
    private void RestoreEarlierJournal(SqliteRecoveryJournalEntry? earlier)
    {
        if (earlier is null)
        {
            File.Delete(RecoveryJournalPath);
        }
        else
        {
            WriteJournal(earlier);
        }
    }

    // The journal of an earlier recovery whose put back couldn't finish, to keep inside the new one's (Earlier). One that
    // can't be read is set aside first, so the new journal never takes its place unseen.
    private SqliteRecoveryJournalEntry? EarlierJournal()
    {
        if (!File.Exists(RecoveryJournalPath))
        {
            return null;
        }

        var entry = ReadJournal(out var error);
        if (entry is null && error is not null)
        {
            SetAsideUnreadableJournal(error);
        }

        return entry;
    }

    private void SetAsideUnreadableJournal(string error)
    {
        var aside = RecoveryJournalPath + ".unreadable-" + Timestamp();
        try
        {
            File.Move(RecoveryJournalPath, aside, overwrite: true);
            _logger.LogWarning("The journal {Journal} of a recovery that didn't finish can't be read ({Error}); it was set aside as {Aside}", RecoveryJournalPath, error, Path.GetFileName(aside));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "The journal {Journal} of a recovery that didn't finish can't be read ({Error}), nor set aside", RecoveryJournalPath, error);
        }
    }

    // A stuck state's problem, for the recovery an admin starts from it: why, and where what was moved aside is.
    private static string StuckProblem(string reason, string folder) => reason + "; what was moved aside is in " + folder;

    // At open, before anything else looks at the files: a recovery the process before didn't finish (the server stopped
    // or was killed during it, or its put back couldn't finish) left its journal. What it moved goes back first: at a
    // restart nothing holds the files. What it put in the database's place goes only when it is that file, byte for
    // byte (the size and SHA-256 the journal recorded before the rename): a copy of a snapshot that is still in
    // snapshots/, or a new empty database, which no member wrote to (the journal goes before the recovery's state is
    // published, and the recovery's own upgrade holds its checkpoints). Then the open goes on as usual, so a damaged
    // database is restored again and its banner names the new folder. If something else is in the database's place (a
    // folder, a file that isn't a readable database), or a move fails, the database is ReadOnlyCorrupt and treated as
    // missing, the banner naming the folder: never a new empty database beside the data, never a snapshot restored over
    // it without an admin; the journal stays, and the next open tries again. A readable database that isn't the one the
    // recovery put there (an admin's own copy, put back by hand) is opened as it is, the folder kept: what an admin's
    // recovery refuses with "restart the server to use it", the restart opens; the journal then stays as
    // the lasting record of what is in the folder, and Notice names the folder (Diagnostics, the settings page's
    // banner) at every open until the folder is gone (a Warning alone, once, would not be enough). An earlier
    // recovery's journal kept inside it goes back next. A folder the journal names that can't be read (no permission, a
    // file in its place) is no "nothing to put back": the database stays read-only, and the banner says so. One that
    // can't be read: with no database in place, the same read-only state (where its files went isn't known); with one
    // in place, it is set aside and the database opened as usual. Returns the read-only state, or null when the open
    // goes on.
    private OpenState? ResumeInterruptedRecovery()
    {
        if (!File.Exists(RecoveryJournalPath))
        {
            return null;
        }

        var current = ReadJournal(out var error);
        if (current is null)
        {
            if (error is null)
            {
                return null;
            }

            if (File.Exists(FilePath))
            {
                _logger.LogWarning("{File} is in place, so it is opened as it is; what the recovery that didn't finish moved may be in a quarantine folder next to it", FilePath);
                SetAsideUnreadableJournal(error);
                return null;
            }

            _logger.LogError("A recovery of {File} didn't finish and its journal {Journal} can't be read ({Error}), while the database isn't there: it is read-only and nothing creates it until an admin acts", FilePath, RecoveryJournalPath, error);
            return new OpenState(SqliteDatabaseStatus.ReadOnlyCorrupt, null, null, null, SqliteMessages.UnreadableRecoveryJournal(Path.GetFileName(RecoveryJournalPath)), true, "a recovery didn't finish and its journal can't be read", Missing: true);
        }

        while (true)
        {
            if (PutBackFromJournal(current, out var keptAside) is { } obstacle)
            {
                return StuckAtOpen(current, obstacle);
            }

            if (keptAside is { } kept)
            {
                // Someone else's readable database is in place and the recovery's files wait in their folder: the journal
                // stays (an earlier one inside it too, next once this one's folder is dealt with) and Notice names the folder.
                _keptAside = kept;
                return null;
            }

            var next = current.Earlier;
            try
            {
                RestoreEarlierJournal(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not update {Journal}; the next open finds what it names back in place", RecoveryJournalPath);
            }

            if (next is null)
            {
                return null;
            }

            current = next;
        }
    }

    // One journal's put back at open: what the recovery put in the database's place goes when it is that file, then
    // what is still in the folder comes back (MoveBack) and the folder goes. Someone else's file in the database's
    // place that reads as a database (an admin's own copy, put back by hand) is opened as it is, the folder kept as it
    // is: what an admin's recovery refused, saying a restart opens it, the restart opens (keptAside: the
    // journal stays). What is in the folder is read from its listing: a folder that can't be listed (no permission, a
    // file in its place) is an obstacle, never "nothing in it" (File.Exists is false for a file it may not see). A -wal
    // or -shm in the folder without its database (an admin took the database out of the folder by hand) never goes
    // back: beside any database SQLite would replay the damaged one's log into it (in a repro the admin's database went
    // from 0 rows to 7, quick_check still passing). What the recovery put in the place is deleted as usual and the log
    // files stay where they are (an ordinary quarantine folder then, and the open goes on as for a missing database);
    // someone else's readable database in the place is used as it is, the journal kept, and Notice says to delete or
    // move the whole folder. Returns null when the open goes on, else why not.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private string? PutBackFromJournal(SqliteRecoveryJournalEntry entry, out KeptAsideFolder? keptAside)
    {
        keptAside = null;
        var folderPath = entry.Folder is null ? null : Path.Combine(Folder, entry.Folder);
        List<string> inFolder;
        try
        {
            inFolder = folderPath is null ? [] : ListJournalFolder(folderPath, entry.Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "the folder couldn't be read (" + ex.Message + ")";
        }

        var databaseInFolder = inFolder.Any(IsDatabaseName);
        var ours = entry.Placed is { } placed && SqliteRecoveryJournal.Matches(FilePath, placed);
        var logFilesAlone = inFolder.Count > 0 && !databaseInFolder;
        if (ours && (inFolder.Count == 0 || databaseInFolder || logFilesAlone))
        {
            try
            {
                SqliteFiles.DeleteWithSidecars(FilePath);
                _logger.LogInformation("Deleted what the recovery of {File} that didn't finish had put in its place (the snapshot {Snapshot}'s copy, or a new empty database: {Size} bytes, its SHA-256 as recorded)", FilePath, entry.Snapshot ?? "(none)", entry.Placed!.Size);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return "what it had put in the database's place couldn't be deleted (" + ex.Message + ")";
            }
        }
        else if ((databaseInFolder || logFilesAlone) && File.Exists(FilePath))
        {
            bool readable;
            try
            {
                readable = ReadsAsADatabase();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                return "another file is in the database's place, and it couldn't be checked (" + ex.Message + ")";
            }

            if (!readable)
            {
                return "another file, which isn't a readable database, is in the database's place";
            }

            if (logFilesAlone)
            {
                _logger.LogWarning(
                    "The recovery of {File} begun {Utc} UTC didn't finish, a readable database that isn't the one it put there is in its place now (put back by hand), and {Folder} holds the damaged database's {Files} without the database itself: they stay there, never next to a database, and the one in place is opened as it is; Diagnostics and the settings page say so until that folder is deleted or moved",
                    FilePath,
                    entry.Utc,
                    folderPath,
                    string.Join(", ", inFolder));
            }
            else
            {
                _logger.LogWarning(
                    "The recovery of {File} begun {Utc} UTC didn't finish, and a readable database that isn't the one it put there is in its place now (put back by hand): it is opened as it is, and what the recovery had moved stays in {Folder} ({Files}); Diagnostics and the settings page say so until that folder is deleted or moved",
                    FilePath,
                    entry.Utc,
                    folderPath,
                    string.Join(", ", inFolder));
            }

            keptAside = new KeptAsideFolder(entry.Folder!, logFilesAlone);
            return null;
        }

        if (logFilesAlone)
        {
            // Only the damaged database's log files are left, and nothing of the recovery's is in the place (deleted above, or
            // nothing there): they stay in their folder, an ordinary quarantine folder now, and the open goes on as for a
            // missing database.
            _logger.LogWarning(
                "The recovery of {File} begun {Utc} UTC didn't finish, and {Folder} holds the damaged database's {Files} without the database itself (taken out of the folder by hand): they stay there, never next to a database, and the open goes on as for a missing database",
                FilePath,
                entry.Utc,
                folderPath,
                string.Join(", ", inFolder));
            return null;
        }

        if (inFolder.Count > 0)
        {
            if (MoveBack(folderPath!, inFolder) is { } why)
            {
                return why;
            }

            RemoveQuarantineFolder(folderPath!);
        }
        else if (folderPath is not null && Directory.Exists(folderPath))
        {
            // Nothing of it is in the folder: nothing had been moved yet, or all of it is back.
            RemoveQuarantineFolder(folderPath);
        }

        _logger.LogWarning(
            "The recovery of {File} begun {Utc} UTC ({Step}) didn't finish; what it had moved aside ({Files}) is back in place, and the database is opened as usual",
            FilePath,
            entry.Utc,
            entry.Step,
            inFolder.Count == 0 ? "nothing" : entry.Folder + ": " + string.Join(", ", inFolder));
        return null;
    }

    // The files a journal names that are in its folder, from the folder's own listing: none when there is no such folder.
    // IOException when a file is in the folder's place, or when the data folder lists the folder but it can't be looked at
    // (Directory.Exists is false then too); the listing's own exception when it can't be listed.
    private List<string> ListJournalFolder(string folderPath, IReadOnlyList<string> names)
    {
        if (!Directory.Exists(folderPath))
        {
            if (File.Exists(folderPath))
            {
                throw new IOException("a file is in its place");
            }

            if (Directory.EnumerateFileSystemEntries(Folder, Path.GetFileName(folderPath)).Any())
            {
                throw new IOException("it is there but can't be looked at");
            }

            return [];
        }

        RecoveryStepFault?.Invoke("resume:list");
        var present = Directory.EnumerateFiles(folderPath).Select(p => Path.GetFileName(p)).ToHashSet(StringComparer.Ordinal);
        return names.Where(present.Contains).ToList();
    }

    // The open's read-only state while what a recovery that didn't finish moved can't be put back.
    private OpenState StuckAtOpen(SqliteRecoveryJournalEntry entry, string obstacle)
    {
        var folder = entry.Folder ?? string.Empty;
        _logger.LogError(
            "The recovery of {File} begun {Utc} UTC didn't finish, and what it moved into {Folder} couldn't be put back: {Obstacle}. The database is read-only and nothing creates it until that is put back by hand with the server stopped, or an admin restores a snapshot or starts empty; the next start tries again",
            FilePath,
            entry.Utc,
            Path.Combine(Folder, folder),
            obstacle);
        var what = (entry.Damaged ? "The data failed its integrity check (" : "The data wasn't there (") + entry.Reason + "). ";
        return new OpenState(
            SqliteDatabaseStatus.ReadOnlyCorrupt,
            null,
            null,
            null,
            what + SqliteMessages.RecoveryNotPutBack(entry.Failure ?? SqliteMessages.InterruptedRecovery, folder),
            true,
            StuckProblem(entry.Reason, folder),
            Missing: true);
    }

    // At every open: what the process before left half done: a snapshot's copy, or a new empty database, a recovery
    // made and didn't rename into place; the copy of the database an admin's recovery checks first; each with its
    // sidecars; and the journal's own temporary file. All of them are ours and hold nothing that isn't elsewhere (in a
    // snapshot, or in the database itself).
    private void DeleteLeftoversOfAStop()
    {
        foreach (var leftover in new[] { FilePath + RestoreTempSuffix, FilePath + RecheckSuffix })
        {
            if (File.Exists(leftover) || SqliteFiles.ListSidecars(leftover).Count > 0)
            {
                _logger.LogInformation("Deleted {Leftover}, which the process before left half done", leftover);
                TryDeleteWithSidecars(leftover);
            }
        }

        var journalTemp = RecoveryJournalPath + TempSuffix;
        try
        {
            File.Delete(journalTemp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", journalTemp);
        }
    }

    // Newest first, but after a restore the snapshots taken since come first, then the restored snapshot, then the
    // rest: the "restore" pre-operation snapshot taken just before a restore holds the state that was replaced
    // (LastRestoreMarkerPath). Snapshots dated more than a day after now go last, in that order: their names say
    // nothing about their age.
    private List<StoreSnapshot> RecoveryCandidates()
    {
        var all = SnapshotRotation.List(SnapshotDirectory, StoreName, Extension)
            .Concat(SnapshotRotation.ListPreOp(SnapshotDirectory, StoreName, Extension))
            .OrderByDescending(s => s.Utc)
            .ThenByDescending(s => s.Sequence)
            .ToList();
        List<StoreSnapshot> ordered;
        if (ReadLastRestore() is not { } last)
        {
            ordered = all;
        }
        else
        {
            var second = new DateTime(last.Utc.Ticks - (last.Utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            ordered = all.Where(s => s.Utc > second)
                .Concat(all.Where(s => string.Equals(s.Name, last.Name, StringComparison.OrdinalIgnoreCase)))
                .Concat(all)
                .Distinct()
                .ToList();
        }

        var now = UtcNow();
        return ordered.Where(s => !SnapshotRotation.IsFutureDated(s, now)).Concat(ordered.Where(s => SnapshotRotation.IsFutureDated(s, now))).ToList();
    }

    private void RememberRestore(StoreSnapshot snapshot)
    {
        try
        {
            File.WriteAllText(LastRestoreMarkerPath, snapshot.Name + "\n" + UtcNow().ToString("O", CultureInfo.InvariantCulture) + "\n");
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
            if (!File.Exists(LastRestoreMarkerPath))
            {
                return null;
            }

            var lines = File.ReadAllLines(LastRestoreMarkerPath);
            if (lines.Length >= 2
                && !string.IsNullOrWhiteSpace(lines[0])
                && DateTime.TryParse(lines[1].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
            {
                return (lines[0].Trim(), utc);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read {Marker}", LastRestoreMarkerPath);
        }

        return null;
    }

    // At open the check's own connection creates an empty -wal and a -shm beside a database a clean stop left alone; they
    // hold nothing of it, so they don't go into the quarantine as if they had been found. A -wal that was there (a crash),
    // or one with frames, always goes.
    private void DeleteSidecarsOfTheCheck(IReadOnlySet<string> foundBeforeCheck)
    {
        var wal = FilePath + "-wal";
        if (foundBeforeCheck.Contains(Path.GetFileName(wal)) || (File.Exists(wal) && new FileInfo(wal).Length > 0))
        {
            return;
        }

        foreach (var created in SqliteFiles.ListSidecars(FilePath).Where(p => !foundBeforeCheck.Contains(Path.GetFileName(p))))
        {
            try
            {
                File.Delete(created);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not delete {Path}, which the integrity check created; it goes into the quarantine folder", created);
            }
        }
    }

    // Renames the checked copy (or start empty's new database) into place, three tries; the last failure throws, and the
    // recovery then puts the database back (PutBack), so it is never left missing (a new, empty one would be created at the
    // next use) while its data waits in the quarantine folder.
    private void InstallRestoreCopy(string temp) => MoveWithRetries(temp, FilePath, "install");

    // Start empty's new database, made beside the database as a snapshot's copy is (RestoreTempSuffix): an empty file in WAL
    // mode with nothing beside it, so what takes the database's place has an identity the journal holds before the rename,
    // and the open after it writes only to the -wal while it gives it its schema.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void PrepareEmptyDatabase(string temp)
    {
        SqliteFiles.DeleteWithSidecars(temp);
        using (var connection = OpenConnection(FileConnectionString(temp, readOnly: false), null))
        {
            Scalar(connection, null, "PRAGMA journal_mode=WAL;");
        }

        foreach (var sidecar in SqliteFiles.ListSidecars(temp))
        {
            File.Delete(sidecar);
        }
    }

    // Copies a snapshot next to the database and checks the copy: from this or an older version, integrity_check ok, in
    // rollback mode (a WAL-mode copy is switched first, so nothing is left beside it). Unusable: it isn't such a database
    // or it is damaged (SQLITE_CORRUPT, SQLITE_NOTADB, a failed check), so the next one is tried. Unreadable: it couldn't
    // be copied, opened or checked for any other reason (in use, a read error, a full disk), which says nothing about it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private RestoreCopy PrepareRestoreCopy(StoreSnapshot snapshot, string temp, out string? detail)
    {
        detail = null;

        // A snapshot this server may not read (an ACL) stays so whatever the retries, unlike one in use: it is passed
        // over, named in the banner with one Warning, as the JSON store does. It used to keep the database Unavailable,
        // retried every 5 s with a Warning and a stack trace each time, and no banner.
        if (MayNotRead(snapshot.Path))
        {
            detail = NoPermission;
            _logger.LogWarning("Passed over the snapshot {Snapshot} for the restore of {File}: {Reason}", snapshot.Name, FilePath, NoPermission);
            return RestoreCopy.Unusable;
        }

        try
        {
            SqliteFiles.DeleteWithSidecars(temp);
            File.Copy(snapshot.Path, temp);
            if (!SqliteFiles.TryReadHeader(temp, out var header))
            {
                detail = "its copy couldn't be read";
                LogSnapshotInUse(snapshot.Name, detail);
                SqliteFiles.DeleteWithSidecars(temp);
                return RestoreCopy.Unreadable;
            }

            if (header is null || header.Value.UserVersion > CurrentVersion)
            {
                _logger.LogWarning("Skipped the snapshot {Snapshot} for the restore of {File}: it isn't a database of this or an older version", snapshot.Name, FilePath);
                SqliteFiles.DeleteWithSidecars(temp);
                return RestoreCopy.Unusable;
            }

            if (header.Value.IsWal)
            {
                using var connection = OpenConnection(FileConnectionString(temp, readOnly: false), null);
                Scalar(connection, null, "PRAGMA journal_mode=DELETE;");
            }

            var check = CheckFile(temp);
            if (check.IsInconclusive)
            {
                detail = check.IoError;
                LogSnapshotInUse(snapshot.Name, check.IoError);
                SqliteFiles.DeleteWithSidecars(temp);
                return RestoreCopy.Unreadable;
            }

            if (!check.Ok)
            {
                _logger.LogWarning("Skipped the snapshot {Snapshot} for the restore of {File}: it failed its integrity check ({Check})", snapshot.Name, FilePath, check.FirstMessage);
                SqliteFiles.DeleteWithSidecars(temp);
                return RestoreCopy.Unusable;
            }

            // Checked, then switched to WAL as the live database is (its last connection closing leaves nothing beside it):
            // the open after the rename then changes nothing in the file, so it stays the one whose identity the recovery's
            // journal holds until the recovery is done.
            using (var wal = OpenConnection(FileConnectionString(temp, readOnly: false), null))
            {
                Scalar(wal, null, "PRAGMA journal_mode=WAL;");
            }

            foreach (var sidecar in SqliteFiles.ListSidecars(temp))
            {
                File.Delete(sidecar);
            }

            return RestoreCopy.Ready;
        }
        catch (SqliteException ex) when (IsCorruption(ex))
        {
            _logger.LogWarning(ex, "Skipped the snapshot {Snapshot} for the restore of {File}: it is damaged", snapshot.Name, FilePath);
            TryDeleteWithSidecars(temp);
            return RestoreCopy.Unusable;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted since it was listed: there is nothing to try again.
            _logger.LogWarning(ex, "Skipped the snapshot {Snapshot} for the restore of {File}: it is gone", snapshot.Name, FilePath);
            TryDeleteWithSidecars(temp);
            return RestoreCopy.Unusable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            // In use (a sharing or lock violation), a full disk, or no access to write the copy: tried again later.
            detail = ex.Message;
            LogSnapshotInUse(snapshot.Name, ex.Message);
            TryDeleteWithSidecars(temp);
            return RestoreCopy.Unreadable;
        }
    }

    // A snapshot that couldn't be read just then: a Warning the first time, Debug while the same one is tried again (every
    // OpenRetryInterval at open), until a restore or an open succeeds.
    private void LogSnapshotInUse(string name, string? error)
    {
        if (string.Equals(Interlocked.Exchange(ref _snapshotInUseLogged, name), name, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("The snapshot {Snapshot} still couldn't be read for the restore of {File} ({Error})", name, FilePath, error);
            return;
        }

        _logger.LogWarning(
            "The snapshot {Snapshot} couldn't be read for the restore of {File} just now ({Error}); it is tried again, and the next attempts are logged at Debug",
            name,
            FilePath,
            error);
    }

    // Moves the database, -wal, -shm and -journal that exist together into a new quarantine folder, the database first;
    // with none of them there, no folder is made (null). The journal, when one is given (a recovery's), comes first:
    // with the folder's name and the files about to move, flushed to the disk, before the folder is made (a server
    // stopped between the folder and the journal would leave a folder no open would ever remove, whose note says files
    // have been moved into it), so a server stopped at any point leaves what the next open needs to put them back
    // together and to remove a folder nothing was moved into. Then the folder and its note, before the first move, so
    // the folder holds a file only with its note; the note says what is moved there, and that a file it names that
    // isn't there was never moved (true whenever the server stops). The journal is written even with nothing to move,
    // since a recovery then still puts a file in the database's place. If the folder can't be made, or one file can't
    // be moved, the ones already moved go back (MoveBack), the folder goes, the journal is as it was, and IOException
    // says why: a database must never be left beside another one's WAL. If they can't go back either,
    // RecoveryFailedException says where they are, and the journal stays, as stuck.
    private (string? Folder, IReadOnlyList<string> Moved, SqliteRecoveryJournalEntry? Journal) Quarantine(string reason, string why, DateTime utc, SqliteRecoveryJournalEntry? journal)
    {
        var files = SqliteFiles.SidecarSuffixes.Select(s => FilePath + s).Prepend(FilePath).Where(File.Exists).ToList();
        string? folder = null;
        if (files.Count > 0)
        {
            var stem = Path.Combine(Folder, QuarantinePrefix + utc.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            folder = stem;
            for (var n = 1; Directory.Exists(folder) || File.Exists(folder); n++)
            {
                folder = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}");
            }
        }

        var names = files.Select(f => Path.GetFileName(f)).ToList();
        if (journal is not null)
        {
            journal = journal with { Folder = folder is null ? null : Path.GetFileName(folder), Files = names };
            try
            {
                WriteJournal(journal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Could not write the journal " + RecoveryJournalPath + " before anything of " + FilePath + " was moved: " + ex.Message, ex);
            }

            RecoveryStepFault?.Invoke("journal");
        }

        if (folder is null)
        {
            return (null, [], journal);
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (journal is not null)
            {
                TryRestoreEarlierJournal(journal.Earlier);
            }

            throw new IOException("Could not make the quarantine folder " + folder + " for " + FilePath + ": " + ex.Message, ex);
        }

        try
        {
            File.WriteAllText(
                Path.Combine(folder, QuarantineNoteName),
                string.Create(CultureInfo.InvariantCulture, $"On {utc:O}, {string.Join(", ", names)} of {FilePath} were to be moved into this folder because {why}. Each of them that is in this folder was moved here; one that isn't was never moved (the server stopped first) and is still next to the database.{Environment.NewLine}{reason}{Environment.NewLine}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not write the note in {Folder}", folder);
        }

        RecoveryStepFault?.Invoke("folder");

        var moved = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                File.Move(file, Path.Combine(folder, name));
                moved.Add(name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (MoveBack(folder, moved) is { } stuck)
                {
                    _logger.LogError(ex, "Could not move {Path} into {Folder}, nor put back what was moved before it: {Why}", file, folder, stuck);
                    if (journal is not null)
                    {
                        TryWriteJournal(journal with { Step = SqliteRecoveryJournal.Stuck, Failure = "The database couldn't be moved aside" });
                    }

                    throw new RecoveryFailedException("The database couldn't be moved aside", PluginErrorKind.Unavailable, Path.GetFileName(folder), moved.Count, moved, ex);
                }

                RemoveQuarantineFolder(folder);
                if (journal is not null)
                {
                    TryRestoreEarlierJournal(journal.Earlier);
                }

                throw new IOException("Could not move " + file + " into " + folder + ": " + ex.Message, ex);
            }

            RecoveryStepFault?.Invoke("moved:" + name);
        }

        return (Path.GetFileName(folder), moved, journal);
    }

    private void TryRestoreEarlierJournal(SqliteRecoveryJournalEntry? earlier)
    {
        try
        {
            RestoreEarlierJournal(earlier);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not update {Journal}; the next open finds what it names back in place", RecoveryJournalPath);
        }
    }

    private void TryWriteJournal(SqliteRecoveryJournalEntry entry)
    {
        try
        {
            WriteJournal(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not update {Journal}; the next start reads it as it was", RecoveryJournalPath);
        }
    }

    // -wal or -shm without their database (the file deleted by hand, or moved away), at every open that finds the database
    // missing, before anything else: never let a database start, or be copied back, beside them, where
    // SQLite would replay another database's log. Returns where they went, or null when there were none.
    private (string Folder, IReadOnlyList<string> Files)? QuarantineOrphanSidecars()
    {
        var sidecars = SqliteFiles.ListSidecars(FilePath);
        if (sidecars.Count == 0)
        {
            return null;
        }

        var (folder, moved, _) = Quarantine("the database file was missing, but its " + string.Join(", ", sidecars.Select(p => Path.GetFileName(p))) + " were there", "they were found without their database", UtcNow(), journal: null);
        _logger.LogWarning("{File} was missing but its sidecars were there; they were moved to {Folder} ({Files}) before anything else", FilePath, folder, string.Join(", ", moved));
        return folder is null ? null : (folder, moved);
    }

    // A quarantine folder that a recovery that didn't finish left while someone else's readable database is used as it
    // is, and whether it holds only the damaged database's log files.
    private sealed record KeptAsideFolder(string Folder, bool LogFilesOnly);

    // Missing: ReadOnlyCorrupt because the data wasn't there (the file missing or empty) and no snapshot could be restored;
    // reads are refused, so nothing creates the file.
    private sealed record OpenState(
        SqliteDatabaseStatus Status,
        int? LoadedVersion,
        int? SchemaVersion,
        int? MigratedFrom,
        string? Message,
        bool ReadOnlyConnections,
        string? Problem = null,
        bool Missing = false,
        bool IndexesOnly = false);

    // What a recovery captured before it moved anything, for PutBack: each marker's bytes (Known false when it couldn't be
    // read, and then it is left as the attempt wrote it; Bytes null when there was none), the kept snapshots, and the names
    // of the pre-operation snapshots there were.
    private sealed record RecoveryUndo(
        (bool Known, byte[]? Bytes) KeepMarker,
        (bool Known, byte[]? Bytes) LastRestore,
        KeptSnapshots<SqliteDatabaseStatus>? Kept,
        IReadOnlySet<string> PreOp);

    // A recovery moved the database aside and a later step failed: Failure says what (path-free), Moved how many files had
    // been moved, and Stuck the names that couldn't be moved back from Folder (none when everything is as it was).
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Never leaves SqliteDatabase: the recovery turns it into its answer or banner.")]
    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private; always made with what failed.")]
    private sealed class RecoveryFailedException(string failure, PluginErrorKind kind, string? folder, int moved, IReadOnlyList<string> stuck, Exception? inner)
        : Exception(failure, inner)
    {
        public string Failure { get; } = failure;

        public PluginErrorKind Kind { get; } = kind;

        public string? Folder { get; } = folder;

        public int Moved { get; } = moved;

        public IReadOnlyList<string> Stuck { get; } = stuck;
    }

    // The open can't decide yet (a check couldn't read part of the file, or a snapshot a restore needs couldn't be read):
    // nothing was moved, the database is Unavailable, and the next use after OpenRetryInterval tries again.
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Never leaves SqliteDatabase: EnsureOpen turns it into the 503 of an Unavailable database.")]
    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private; always made with its message.")]
    private sealed class RetryLaterException(string message) : IOException(message)
    {
        // The 503's sentence for clients (path-free), when it says more than SqliteMessages.Unavailable.
        public string? PublicMessage { get; init; }
    }
}
