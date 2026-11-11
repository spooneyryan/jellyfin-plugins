using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// A plugin's part of the kit's import into a <see cref="SqliteDatabase"/>, the SQLite twin of
/// <see cref="JsonImportAdapter{TDoc}"/>: which users the export's records belong to, and how they go into the tables.
/// <see cref="SqliteStoreImport"/> does the rest: the envelope checks, the user map, the pre-operation snapshot, the
/// transaction, and the dry run, which is the same <see cref="Apply"/> rolled back.
/// </summary>
internal abstract class SqliteImportAdapter
{
    /// <summary>
    /// Counts the export's records per user id.
    /// </summary>
    /// <param name="data">The export's <c>Data</c>, in the plugin's export shape at <paramref name="schemaVersion"/>.</param>
    /// <param name="schemaVersion">The export's schema version, this build's or an older one.</param>
    /// <returns>User id to number of records.</returns>
    public abstract IReadOnlyDictionary<Guid, int> CountRecordsByUser(JsonObject data, int schemaVersion);

    /// <summary>
    /// Counts the export's archived records per user id, so their users are matched too. The default,
    /// for plugins without an archive, counts none.
    /// </summary>
    /// <param name="records">The archived records.</param>
    /// <param name="schemaVersion">The export's schema version.</param>
    /// <returns>User id to number of archived records.</returns>
    public virtual IReadOnlyDictionary<Guid, int> CountArchivedRecordsByUser(IReadOnlyList<JsonObject> records, int schemaVersion)
        => new Dictionary<Guid, int>();

    /// <summary>
    /// Writes the export's records into the tables, inside the import's transaction (one writer, <c>BEGIN IMMEDIATE</c>).
    /// It keeps only records of users in <paramref name="users"/>, with their ids rewritten through
    /// <see cref="ImportUserMap.TryMap"/>, reads an older <paramref name="schemaVersion"/> itself, and never trusts paths
    /// from another server. A dry run calls it the same way and rolls the transaction back, so the counts are exactly what
    /// the import would do; it must therefore change nothing outside the transaction.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="transaction">The transaction every command must be given.</param>
    /// <param name="data">The export's <c>Data</c>.</param>
    /// <param name="schemaVersion">The export's schema version.</param>
    /// <param name="mode">Merge or Replace.</param>
    /// <param name="users">The export's users found on this server.</param>
    /// <returns>What changed.</returns>
    public abstract SqliteImportApplied Apply(SqliteConnection connection, SqliteTransaction transaction, JsonObject data, int schemaVersion, ImportMode mode, ImportUserMap users);

    /// <summary>
    /// Imports the export's archived records. Plugins without an archive keep this default, which
    /// imports none and says so in the report.
    /// </summary>
    /// <param name="records">The archived records.</param>
    /// <param name="schemaVersion">The export's schema version.</param>
    /// <param name="users">The export's users found on this server.</param>
    /// <param name="mode">Merge or Replace.</param>
    /// <param name="dryRun">Whether nothing is written.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>What was, or would be, imported.</returns>
    public virtual Task<ArchiveImportResult> ImportArchiveAsync(
        IReadOnlyList<JsonObject> records,
        int schemaVersion,
        ImportUserMap users,
        ImportMode mode,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Task.FromResult(new ArchiveImportResult(
            records.Count,
            0,
            records.Count == 0 ? null : "This version keeps no archive of detached records, so the export's archived records were not imported."));
    }
}
