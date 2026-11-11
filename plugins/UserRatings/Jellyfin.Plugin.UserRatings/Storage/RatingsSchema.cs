using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Sqlite;
using Jellyfin.Plugin.UserRatings.Storage.Migrations;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// The database of User Ratings: <c>&lt;data&gt;/userratings/userratings.db</c> and its schema steps, one per
/// version from 1. The kit's <see cref="SqliteDatabase"/> runs them; a table rebuild says so with
/// <see cref="SqliteMigration.RequiresRebuild"/> and the kit's FK-safe procedure keeps the child rows.
/// </summary>
internal static class RatingsSchema
{
    /// <summary>The database's file name in the plugin's data folder.</summary>
    public const string DatabaseFile = "userratings.db";

    /// <summary>
    /// Gets the schema steps: version 1 is <c>M001_Initial</c>, version 2 <c>M002_AttachedUtc</c> (when a
    /// row was relinked, for the move journal), version 3 <c>M003_Vacuum</c> (no schema change, the file
    /// rebuilt once so deletions from before <c>secure_delete</c> leave nothing), version 4 <c>M004_DeletionOrigin</c> (0.3.20:
    /// where a tombstone or a former member was made, for the restore's rule). No 1.1 step is planned.
    /// </summary>
    public static IReadOnlyList<SqliteMigration> Migrations { get; } = [M001Initial.Migration, M002AttachedUtc.Migration, M003Vacuum.Migration, M004DeletionOrigin.Migration];

    /// <summary>Gets the schema version this build reads and writes.</summary>
    public static int CurrentVersion => Migrations[^1].Version;
}
