using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// Degraded mode (since 0.9.13; it replaced the interim rule of 0.9.5 that a store started empty locked every non-admin
/// until the next restart). A start is degraded when the store recovered empty (<see cref="StoreStatus.RecoveredEmpty"/>)
/// or started read-only without a usable document (a failed migration, a newer version's file this build can't read, data
/// missing or unreadable with nothing to take its place: read-only with no assignment). The managed users are then read
/// leniently (<see cref="DegradedEvidence"/>) from the stored file itself, else the newest readable copy of it; with
/// nothing readable a read-only start locks every non-admin (the Overview says so as a server-wide behaviour), and a store
/// recovered empty locks nobody (no evidence of managed users).
/// </summary>
/// <remarks>
/// <para><b>Durable</b>: a degraded start that locks anyone writes
/// <see cref="MarkerFileName"/> next to the data file. A later start whose document is usable reads it, and the users it
/// lists that the document doesn't manage stay locked until an admin confirms <i>Stop locking them</i>
/// (<c>POST /AccessRoles/Admin/Degraded/Resolve</c>), so a store put back empty or deleted never switches their locks off
/// unseen; the users the document manages again are its own and leave the marker, which goes when nobody is left. A
/// marker that can't be read locks every non-admin (fail closed), as one that locked every non-admin does until a usable
/// document holds any assignment; then it ends, logged as a warning and audited <c>MarkerUnreadable</c>: the document is
/// the only evidence left of whom to lock, and every non-admin kept locked on a store that works would lock users nobody
/// ever managed.</para>
/// <para><b>A copy that took the file's place</b>: a store restored from a snapshot
/// (<see cref="StoreStatus.RestoredFromSnapshot"/>) or its <c>.bak</c> (<see cref="StoreStatus.RecoveredFromBackup"/>) is
/// usable, but older than the file moved aside as unreadable. The users that copy lists as managed and the copy used
/// doesn't manage are locked as an earlier start's marker locks them (resolvable, written to the marker). A newer
/// version's file whose assignments this build reads (<see cref="StoreStatus.ReadOnlyNewerVersion"/>) is read leniently
/// too: users it names that the parse doesn't manage are locked (fail closed against a structure renamed), until the file
/// is put right.</para>
/// <para><b>The mirror's records</b>: the users whose native preference access the optional
/// mirror recorded turning off (<c>PreferenceMirror</c>) in the copy read, the copy moved aside, or the marker, and not in
/// the document, are <see cref="MirrorEvidence"/>: kept in the marker until the startup takes them back into a writable
/// store (<see cref="ForgetMirrorEvidence"/>), and read by <i>Release all locks</i> and the uninstall meanwhile, so no flag
/// the mirror turned off is left without a record.</para>
/// <para>Evaluated once per process, on first use (the filter's first request or the startup), after the store loaded;
/// <see cref="Resolve"/> changes it. Locks only ever add: a degraded user keeps the document's own locks too.</para>
/// </remarks>
internal sealed class DegradedMode
{
    /// <summary>The marker's file name in the data folder.</summary>
    public const string MarkerFileName = "degraded.json";

    /// <summary>The 503 code of a confirmation whose marker couldn't be deleted (nothing changed).</summary>
    public const string MarkerNotDeleted = "MarkerNotDeleted";

    // The Because of a marker that holds only the mirror's records.
    private const string MirrorRecordsOnly = "MirrorRecords";

    private readonly JsonDocumentStore<AccessRolesDocument> _store;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();
    private Evaluation? _evaluation;

    /// <summary>
    /// Initializes a new instance of the <see cref="DegradedMode"/> class.
    /// </summary>
    /// <param name="store">The kit store.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    public DegradedMode(JsonDocumentStore<AccessRolesDocument> store, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        MarkerPath = Path.Combine(Path.GetDirectoryName(store.FilePath)!, MarkerFileName);
    }

    /// <summary>
    /// Raised after an admin ended it (<see cref="Resolve"/>): the library sync runs, so the mirror puts back the flags of the
    /// users it no longer locks. Handlers must be quick and never throw.
    /// </summary>
    public event EventHandler? Resolved;

    /// <summary>Gets the marker's path.</summary>
    public string MarkerPath { get; }

    /// <summary>Gets the degraded state, or <c>null</c> when nobody is locked by it.</summary>
    public DegradedState? Current => Evaluated().State;

    /// <summary>
    /// Gets a value indicating whether this start began it, or added users to it: a degraded start without an earlier
    /// marker, a restore that lost managed users, a newer version's file naming users its parse doesn't manage (audited
    /// <c>DegradedModeEntered</c>).
    /// </summary>
    public bool EnteredNow => Evaluated().Entered;

    /// <summary>
    /// Gets the users an earlier start's marker listed that this start's usable document manages again, when that ended it
    /// (audited <c>DegradedModeEnded</c>), else <c>null</c>.
    /// </summary>
    public IReadOnlySet<Guid>? EndedNow => Evaluated().Ended?.Users;

    /// <summary>Gets why this start ended it (<c>TheDataFileManagesThemAgain</c>, <c>MarkerUnreadable</c>), else <c>null</c>.</summary>
    public string? EndedBecause => Evaluated().Ended?.Reason;

    /// <summary>Gets a value indicating whether what this start ended locked every non-admin.</summary>
    public bool EndedEveryNonAdmin => Evaluated().Ended?.EveryNonAdmin == true;

    /// <summary>
    /// Gets the users whose native preference access the mirror recorded turning off in a copy the store couldn't use or in
    /// the marker, and that the document doesn't record (the class remarks).
    /// </summary>
    public IReadOnlySet<Guid> MirrorEvidence => Evaluated().Mirrored;

    /// <summary>
    /// Gets a value indicating whether an admin can end it now: the store is usable (an earlier start's marker, or a copy a
    /// restore moved aside) or recovered empty (the confirmation called <i>start empty</i>). A read-only store's way
    /// out is a readable copy put back or the plugin version that wrote it.
    /// </summary>
    public bool CanResolve => Current is { } state && (state.FromEarlierStart || state.Because == nameof(StoreStatus.RecoveredEmpty));

    /// <summary>
    /// Ends it in this process and deletes the marker (an admin's confirmation); a marker that still holds mirror records
    /// is written without the users instead.
    /// </summary>
    /// <returns>The state it ended, or <c>null</c> when there was none.</returns>
    /// <exception cref="IOException">The marker couldn't be deleted or written; nothing changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of access; nothing changed.</exception>
    public DegradedState? Resolve()
    {
        DegradedState? before;
        lock (_lock)
        {
            var evaluation = Evaluated();
            before = evaluation.State;
            if (before is null)
            {
                return null;
            }

            if (evaluation.Mirrored.Count > 0)
            {
                WriteMarker(null, evaluation.Mirrored, strict: true);
            }
            else
            {
                DeleteMarker();
            }

            _evaluation = evaluation with { State = null, Entered = false, Ended = null };
        }

        Resolved?.Invoke(this, EventArgs.Empty);
        return before;
    }

    /// <summary>
    /// Drops mirror records from <see cref="MirrorEvidence"/> and the marker: the startup took them back into the document,
    /// or the flag is on again (<i>Release all locks</i> or the uninstall on a read-only store).
    /// </summary>
    /// <param name="ids">The users.</param>
    public void ForgetMirrorEvidence(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            var evaluation = Evaluated();
            var left = evaluation.Mirrored.Except(ids).ToHashSet();
            if (left.Count == evaluation.Mirrored.Count)
            {
                return;
            }

            if (evaluation.State is null && left.Count == 0)
            {
                DeleteMarkerQuietly();
            }
            else
            {
                WriteMarker(evaluation.State, left);
            }

            _evaluation = evaluation with { Mirrored = left };
        }
    }

    /// <summary>
    /// Describes it for Diagnostics.
    /// </summary>
    /// <returns>The description, or <c>null</c> when nobody is locked by it.</returns>
    public IDictionary<string, object?>? Describe()
    {
        var state = Current;
        return state is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Because"] = state.Because,
            ["EveryNonAdmin"] = state.EveryNonAdmin,
            ["Users"] = state.Users.Order().ToList(),
            ["Source"] = state.Source,
            ["SinceUtc"] = state.SinceUtc,
            ["FromEarlierStart"] = state.FromEarlierStart,
            ["CanResolve"] = CanResolve,
            ["MirrorEvidence"] = MirrorEvidence.Order().ToList(),
            ["Marker"] = MarkerFileName
        };
    }

    // The managed users of a usable document: an assignment that isn't Excluded (an unknown state counts as managed).
    internal static HashSet<Guid> ManagedIn(AccessRolesDocument document)
        => [.. document.Assignments.Where(a => !a.State.IsKnown || !a.State.Is(AccessRolesAssignmentState.Excluded)).Select(a => a.UserId)];

    private Evaluation Evaluated()
    {
        var evaluation = Volatile.Read(ref _evaluation);
        if (evaluation is not null)
        {
            return evaluation;
        }

        lock (_lock)
        {
            _evaluation ??= Evaluate();
            return _evaluation;
        }
    }

    private Evaluation Evaluate()
    {
        var status = _store.Status;
        var document = _store.Current;
        var readOnly = _store.IsReadOnly;
        var marker = ReadMarker(out var markerUnreadable);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var mirrored = new HashSet<Guid>(marker?.MirroredIds ?? []);

        if (status == StoreStatus.RecoveredEmpty || (readOnly && document.Assignments.Count == 0))
        {
            var evidence = DegradedEvidence.Find(_store, _logger);
            var users = new HashSet<Guid>(evidence?.Users ?? new HashSet<Guid>());
            users.UnionWith(marker?.Ids ?? []);
            mirrored.UnionWith(evidence?.Mirrored ?? new HashSet<Guid>());
            mirrored.ExceptWith(document.PreferenceMirror.Keys);
            var everyone = (readOnly && evidence is null) || marker?.EveryNonAdmin == true || markerUnreadable;
            if (users.Count == 0 && !everyone)
            {
                _logger.LogWarning(
                    "Access Roles' data file is {Status} and nothing lists a managed user (the file, its .bak, snapshots and kept copies): Degraded mode locks nobody",
                    status);
                KeepMirrorRecords(marker, mirrored);
                return new Evaluation(null, false, null, mirrored);
            }

            var state = new DegradedState(status.ToString(), everyone, users, evidence?.Source ?? marker?.Source, marker?.SinceUtc ?? now, FromEarlierStart: false);
            WriteMarker(state, mirrored);
            _logger.LogWarning(
                "Access Roles is in Degraded mode ({Status}): every server-enforced settings section is locked for {Who}, read from {Source}, until it is put right",
                status,
                everyone ? "every non-admin" : users.Count.ToString(CultureInfo.InvariantCulture) + " managed users",
                state.Source ?? "nothing readable");
            return new Evaluation(state, marker is null && !markerUnreadable, null, mirrored);
        }

        // A usable document (or a newer version's, read-only, whose assignments could be read): the users it manages are its
        // own; the others an earlier start locked stay locked, and so do those a copy moved aside at this start lists.
        var managed = ManagedIn(document);
        var locking = marker is { } m && (m.Ids.Count > 0 || m.EveryNonAdmin);
        var listed = locking ? marker!.Ids : new HashSet<Guid>();
        var found = status switch
        {
            StoreStatus.RestoredFromSnapshot or StoreStatus.RecoveredFromBackup => DegradedEvidence.MovedAside(_store, _logger),
            StoreStatus.ReadOnlyNewerVersion => DegradedEvidence.Read(_store.FilePath, _logger),
            _ => null
        };
        var lost = (found?.Users ?? new HashSet<Guid>()).Where(id => !managed.Contains(id) && !listed.Contains(id)).ToHashSet();
        mirrored.UnionWith(found?.Mirrored ?? new HashSet<Guid>());
        mirrored.ExceptWith(document.PreferenceMirror.Keys);
        var remaining = listed.Where(id => !managed.Contains(id)).Concat(lost).ToHashSet();
        var stillEveryone = (marker?.EveryNonAdmin == true || markerUnreadable) && document.Assignments.Count == 0;
        if (remaining.Count == 0 && !stillEveryone)
        {
            KeepMirrorRecords(marker, mirrored);
            if (!locking && !markerUnreadable)
            {
                return new Evaluation(null, false, null, mirrored);
            }

            if (markerUnreadable)
            {
                // Not silent: the marker can't say whom it locked, and the document holds assignments.
                _logger.LogWarning(
                    "Access Roles left Degraded mode: its marker {File} couldn't be read, and the data file manages users again, so every non-admin's lock ends; the users it locked aren't known",
                    MarkerPath);
                return new Evaluation(null, false, new Ending(listed, "MarkerUnreadable", true), mirrored);
            }

            _logger.LogInformation("Access Roles left Degraded mode: the data file manages every user the earlier degraded start locked");
            return new Evaluation(null, false, new Ending(listed, "TheDataFileManagesThemAgain", marker!.EveryNonAdmin), mirrored);
        }

        var because = (locking ? marker!.Because : null) ?? (markerUnreadable && lost.Count == 0 ? "UnreadableMarker" : status.ToString());
        var lingering = new DegradedState(because, stillEveryone, remaining, (locking ? marker!.Source : null) ?? found?.Source, locking ? marker!.SinceUtc : now, FromEarlierStart: !readOnly);
        if (marker is null || !remaining.SetEquals(listed) || stillEveryone != marker.EveryNonAdmin || !mirrored.SetEquals(marker.MirroredIds))
        {
            WriteMarker(lingering, mirrored);
        }

        if (lost.Count > 0)
        {
            _logger.LogWarning(
                "Access Roles' data file is {Status}: {Count} users {Source} lists as managed aren't managed by the data now, so every server-enforced settings section stays locked for them until {WayOut}",
                status,
                lost.Count,
                found!.Source,
                readOnly ? "the file is put right" : "an admin confirms");
        }
        else
        {
            _logger.LogWarning(
                "Access Roles stays in Degraded mode from an earlier start ({Because}): {Who} the data file doesn't manage keep every server-enforced settings section locked until an admin confirms",
                lingering.Because,
                stillEveryone ? "every non-admin" : remaining.Count.ToString(CultureInfo.InvariantCulture) + " users");
        }

        return new Evaluation(lingering, lost.Count > 0, null, mirrored);
    }

    // Nobody locked: a marker holding only the mirror's records the document lacks, or none.
    private void KeepMirrorRecords(Marker? marker, IReadOnlySet<Guid> mirrored)
    {
        if (mirrored.Count > 0)
        {
            if (marker is null || marker.Ids.Count > 0 || marker.EveryNonAdmin || !mirrored.SetEquals(marker.MirroredIds))
            {
                WriteMarker(null, mirrored);
            }
        }
        else if (File.Exists(MarkerPath))
        {
            DeleteMarkerQuietly();
        }
    }

    private Marker? ReadMarker(out bool unreadable)
    {
        unreadable = false;
        try
        {
            if (!File.Exists(MarkerPath))
            {
                return null;
            }

            // The users in any Guid format (a marker an admin wrote or edited by hand); one that isn't an id fails it closed.
            // A mirror record that isn't an id is only left out: it locks nobody.
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(MarkerPath), JsonStoreFormat.Options);
            if (marker is { Because: not null, Users: not null } && marker.Users.All(u => Guid.TryParse(u, out _)))
            {
                if ((marker.Mirrored ?? []).Any(u => !Guid.TryParse(u, out _)))
                {
                    _logger.LogWarning("Access Roles' Degraded mode marker {File} has mirror records that aren't user ids; they are left out", MarkerPath);
                }

                return marker;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't read its Degraded mode marker {File}: every non-admin stays locked (fail closed)", MarkerPath);
        }

        unreadable = true;
        return null;
    }

    // The state's users, or none (a marker of the mirror's records only); strict throws what couldn't be written.
    private void WriteMarker(DegradedState? state, IReadOnlySet<Guid> mirrored, bool strict = false)
    {
        var marker = new Marker
        {
            Because = state?.Because ?? MirrorRecordsOnly,
            SinceUtc = state?.SinceUtc ?? _timeProvider.GetUtcNow().UtcDateTime,
            Source = state?.Source,
            EveryNonAdmin = state?.EveryNonAdmin == true,
            Users = [.. (state?.Users ?? new HashSet<Guid>()).Order().Select(u => u.ToString("D", CultureInfo.InvariantCulture))],
            Mirrored = mirrored.Count == 0 ? null : [.. mirrored.Order().Select(u => u.ToString("D", CultureInfo.InvariantCulture))]
        };
        var temporary = MarkerPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(marker, JsonStoreFormat.Options));
            File.Move(temporary, MarkerPath, overwrite: true);
        }
        catch (Exception ex) when (!strict && ex is IOException or UnauthorizedAccessException)
        {
            // The lock holds in this process; the next start reads the evidence again, but a store put right meanwhile won't
            // remember these users.
            _logger.LogError(ex, "Access Roles couldn't write its Degraded mode marker {File}: the next start may not remember the locked users", MarkerPath);
        }
    }

    private void DeleteMarker()
    {
        if (File.Exists(MarkerPath))
        {
            File.Delete(MarkerPath);
        }
    }

    private void DeleteMarkerQuietly()
    {
        try
        {
            DeleteMarker();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Access Roles couldn't delete its Degraded mode marker {File}", MarkerPath);
        }
    }

    // What this start ended: the users the marker listed, why, and whether it locked every non-admin.
    private sealed record Ending(IReadOnlySet<Guid> Users, string Reason, bool EveryNonAdmin);

    private sealed record Evaluation(DegradedState? State, bool Entered, Ending? Ended, IReadOnlySet<Guid> Mirrored);

    /// <summary>The marker file, <c>degraded.json</c>.</summary>
    internal sealed class Marker
    {
        /// <summary>Gets or sets the marker format's version.</summary>
        public int Version { get; set; } = 1;

        /// <summary>Gets or sets the store status of the degraded start.</summary>
        public string? Because { get; set; }

        /// <summary>Gets or sets when it began.</summary>
        public DateTime SinceUtc { get; set; }

        /// <summary>Gets or sets the file the users were read from.</summary>
        public string? Source { get; set; }

        /// <summary>Gets or sets a value indicating whether every non-admin is locked.</summary>
        public bool EveryNonAdmin { get; set; }

        /// <summary>Gets or sets the users locked, as ids in any format.</summary>
        public List<string>? Users { get; set; }

        /// <summary>
        /// Gets or sets the users whose native preference access the mirror recorded turning off, read from a copy the store
        /// couldn't use and not yet taken back into the document (since 0.9.14; an older version ignores it).
        /// </summary>
        public List<string>? Mirrored { get; set; }

        /// <summary>Gets the users as ids.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public HashSet<Guid> Ids => [.. (Users ?? []).Select(u => Guid.Parse(u, CultureInfo.InvariantCulture))];

        /// <summary>Gets the mirror records as ids (those that are ids).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public HashSet<Guid> MirroredIds => [.. (Mirrored ?? []).Select(u => Guid.TryParse(u, CultureInfo.InvariantCulture, out var id) ? id : Guid.Empty).Where(id => !id.Equals(Guid.Empty))];
    }
}
