using Jellyfin.Plugin.Kit.Sqlite;

namespace Jellyfin.Plugin.UserRatings.Storage.Migrations;

/// <summary>
/// <c>M003_Vacuum</c>, schema version 3 of <c>userratings.db</c>: no schema change, a
/// <c>VACUUM</c> after the kit's upgrade transaction (<see cref="SqliteMigration.VacuumAfter"/>) that rebuilds the file, so
/// what deletions before the kit's <c>secure_delete</c> (0.3.14.0) left in its free space, deleted reviews among it, is gone.
/// The kit's <c>migrate</c> pre-operation snapshot is taken first; it is the old file as it was, kept by age like every
/// pre-operation snapshot (7 days, <c>RatingsUserDataOwner.PreOpKeepWithin</c>), then deleted.
/// </summary>
internal static class M003Vacuum
{
    /// <summary>The schema version this step leaves (<c>PRAGMA user_version</c>).</summary>
    public const int Version = 3;

    /// <summary>The step's name, in logs and in Diagnostics.</summary>
    public const string Name = "M003_Vacuum";

    /// <summary>The step's SQL, run in the kit's upgrade transaction: nothing; the work is the vacuum after it.</summary>
    public const string Sql = "SELECT 1;";

    /// <summary>
    /// Gets the step for the kit's runner.
    /// </summary>
    public static SqliteMigration Migration { get; } = new(Version, Name, Sql) { VacuumAfter = true };
}
