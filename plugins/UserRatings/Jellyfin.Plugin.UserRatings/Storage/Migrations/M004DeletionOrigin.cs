using Jellyfin.Plugin.Kit.Sqlite;

namespace Jellyfin.Plugin.UserRatings.Storage.Migrations;

/// <summary>
/// <c>M004_DeletionOrigin</c>, schema version 4 of <c>userratings.db</c> (since 0.3.20): where a
/// tombstone or a former member was made (<see cref="RatingValues.DeletionOrigin"/>), so a restore re-applies only this
/// server's own deletions and undoes an import's with the rest of the import. Two columns added in place, with a default:
/// no table rebuild, and every row there is reads <c>Local</c>, as it was made here or by an import before this version.
/// </summary>
internal static class M004DeletionOrigin
{
    /// <summary>The schema version this step leaves (<c>PRAGMA user_version</c>).</summary>
    public const int Version = 4;

    /// <summary>The step's name, in logs and in Diagnostics.</summary>
    public const string Name = "M004_DeletionOrigin";

    /// <summary>The step's SQL, run in the kit's upgrade transaction.</summary>
    public const string Sql = "ALTER TABLE Tombstones ADD COLUMN Origin TEXT NOT NULL DEFAULT 'Local'; ALTER TABLE FormerUsers ADD COLUMN Origin TEXT NOT NULL DEFAULT 'Local';";

    /// <summary>
    /// Gets the step for the kit's runner.
    /// </summary>
    public static SqliteMigration Migration { get; } = new(Version, Name, Sql);
}
