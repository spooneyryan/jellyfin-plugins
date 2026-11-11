namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// The identity of what a recovery put in the database's place (<see cref="SqliteRecoveryJournal.Identify"/>).
/// </summary>
/// <param name="Size">Its size in bytes.</param>
/// <param name="Sha256">Its SHA-256, upper-case hex.</param>
internal sealed record SqliteRecoveryJournalPlaced(long Size, string Sha256);
