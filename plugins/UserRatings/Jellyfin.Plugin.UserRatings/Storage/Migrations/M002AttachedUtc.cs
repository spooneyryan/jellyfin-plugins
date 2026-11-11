using Jellyfin.Plugin.Kit.Sqlite;

namespace Jellyfin.Plugin.UserRatings.Storage.Migrations;

/// <summary>
/// <c>M002_AttachedUtc</c>, schema version 2 of <c>userratings.db</c>: when a row was last attached to its
/// item by a relink (<see cref="Domain.RatingRecord.AttachedUtc"/>), so the move journal and the kit's resolver follow only
/// the journal lines after it. A nullable column added in place: no table rebuild, every row keeps its
/// values and reads null (attached since it was created).
/// </summary>
internal static class M002AttachedUtc
{
    /// <summary>The schema version this step leaves (<c>PRAGMA user_version</c>).</summary>
    public const int Version = 2;

    /// <summary>The step's name, in logs and in Diagnostics.</summary>
    public const string Name = "M002_AttachedUtc";

    /// <summary>The step's SQL, run in the kit's upgrade transaction.</summary>
    public const string Sql = "ALTER TABLE Ratings ADD COLUMN AttachedUtc TEXT NULL;";

    /// <summary>
    /// Gets the step for the kit's runner.
    /// </summary>
    public static SqliteMigration Migration { get; } = new(Version, Name, Sql);
}
