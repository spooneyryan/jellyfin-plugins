using System;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// One schema step of a <see cref="SqliteDatabase"/>: SQL that brings the database to
/// <see cref="Version"/>, its <c>PRAGMA user_version</c> afterwards. The steps of a database run in order, every pending
/// one in a single transaction, so a failing step rolls back its DDL and <c>user_version</c> with every step before it.
/// </summary>
/// <remarks>
/// <para>Steps are SQL only (no SQLite type lives in a plugin's step list, which DI-built services hold; see
/// <see cref="SqliteDatabase"/>'s isolation). Growable enums are validated in code rather than with <c>CHECK</c>
/// constraints, so adding a value never needs a table rebuild.</para>
/// <para><b>Table rebuilds.</b> With <c>foreign_keys=ON</c>, <c>DROP TABLE</c> does an implicit <c>DELETE</c> that cascades
/// into child tables, and <c>PRAGMA foreign_keys</c> has no effect inside a transaction (sqlite.org/foreignkeys.html §5,
/// §2). A step that rebuilds a table sets <see cref="RequiresRebuild"/>, and the database then runs the whole upgrade as:
/// <c>PRAGMA foreign_keys=OFF</c> on an unpooled connection before <c>BEGIN</c>; a check that foreign keys really are off;
/// the steps, each following sqlite.org/lang_altertable.html §7 (create <c>new_X</c>, copy the rows, drop <c>X</c>, rename
/// <c>new_X</c> to <c>X</c>, recreate the indexes, triggers and views: never rename <c>X</c> away first, which would
/// point the child tables' foreign keys at the old name); <c>PRAGMA foreign_key_check</c>, where any row rolls everything
/// back; <c>COMMIT</c>; <c>PRAGMA foreign_keys=ON</c>. The child rows are kept.</para>
/// </remarks>
/// <param name="Version">The <c>user_version</c> after this step: 1 for the first, then one more each.</param>
/// <param name="Name">What the step does, for logs and the status message ("create Ratings").</param>
/// <param name="Sql">One or more statements. No <c>BEGIN</c>/<c>COMMIT</c> and no <c>PRAGMA user_version</c>: the database runs them in its transaction and sets the version.</param>
internal sealed record SqliteMigration(int Version, string Name, string Sql)
{
    /// <summary>
    /// Gets a value indicating whether the step rebuilds a table, so the upgrade runs with foreign keys off and checked
    /// before it commits (see the remarks).
    /// </summary>
    public bool RequiresRebuild { get; init; }

    /// <summary>
    /// Gets a value indicating whether the database runs <c>VACUUM</c> once the upgrade has committed: it rebuilds the
    /// file with only what is live, so what earlier deletes left in its free space is gone, then checkpoints the
    /// <c>-wal</c> and cuts it. For a one-time clean-up of files written before
    /// <see cref="SqliteDatabase.SecureDelete"/> was on; its step may hold no schema change (<c>SELECT 1;</c>). Best
    /// effort: a <c>VACUUM</c> that fails (it needs room for a copy of the file) leaves the upgrade done, with a
    /// Warning, and is tried again at each later open until one succeeds
    /// (<see cref="SqliteDatabase.VacuumPendingPath"/>). The pre-operation snapshot before the upgrade is an old copy
    /// of the file, kept as its class keeps it.
    /// </summary>
    public bool VacuumAfter { get; init; }

    /// <summary>
    /// Checks the step's own values.
    /// </summary>
    /// <exception cref="ArgumentException">The version is below 1, or the name or SQL is empty.</exception>
    public void Validate()
    {
        if (Version < 1)
        {
            throw new ArgumentException($"A SQLite migration's version must be 1 or more; '{Name}' has {Version}.", nameof(Version));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException($"The SQLite migration to version {Version} has no name.", nameof(Name));
        }

        if (string.IsNullOrWhiteSpace(Sql))
        {
            throw new ArgumentException($"The SQLite migration '{Name}' has no SQL.", nameof(Sql));
        }
    }
}
