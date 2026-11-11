using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// What a recovery journal records (<see cref="SqliteRecoveryJournal"/>).
/// </summary>
/// <param name="Folder">The quarantine folder's name next to the database; <c>null</c> when there was nothing to move.</param>
/// <param name="Files">The names of the files moved (or being moved) into it, the database first.</param>
/// <param name="Step"><see cref="SqliteRecoveryJournal.Quarantining"/>, <see cref="SqliteRecoveryJournal.Installing"/> or <see cref="SqliteRecoveryJournal.Stuck"/>.</param>
/// <param name="Utc">When the recovery began.</param>
/// <param name="Damaged">Whether the database failed its check (else it was missing or empty).</param>
/// <param name="Reason">Why, as the recovery's banner says it.</param>
/// <param name="Snapshot">The snapshot being restored; <c>null</c> to start empty.</param>
/// <param name="Placed">What takes the database's place, once known: the checked copy of the snapshot, or the new empty database.</param>
/// <param name="Failure">What failed, path-free, when the put back couldn't finish (<see cref="SqliteRecoveryJournal.Stuck"/>).</param>
/// <param name="Earlier">The journal of an earlier recovery whose put back couldn't finish, still to be put back after this one.</param>
internal sealed record SqliteRecoveryJournalEntry(
    string? Folder,
    IReadOnlyList<string> Files,
    string Step,
    DateTime Utc,
    bool Damaged,
    string Reason,
    string? Snapshot,
    SqliteRecoveryJournalPlaced? Placed,
    string? Failure,
    SqliteRecoveryJournalEntry? Earlier);
