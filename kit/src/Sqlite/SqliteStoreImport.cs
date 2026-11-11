using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Jellyfin.Plugin.Kit.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// <c>POST /&lt;Route&gt;/Admin/Import</c> for a <see cref="SqliteDatabase"/>, the SQLite twin of
/// <see cref="JsonStoreImport"/>.
/// </summary>
/// <remarks>
/// In order: the envelope must be this plugin's (400) at a schema this version reads (409); its users are matched with
/// this server's by id or name (<see cref="ImportUserMap"/>: never by name for a user deleted on either server), and the
/// records of users not found are skipped and reported; a read-only database refuses (409). A dry run (the default) runs
/// the plugin's <see cref="SqliteImportAdapter.Apply"/> in the writer's transaction and rolls it back, so its counts are
/// the import's own. An import that writes takes a pre-operation snapshot first (Merge too, as the JSON store does), in
/// the same hold of the writer lock as its transaction, so the snapshot holds exactly the state the import changes.
/// Settings are never applied.
/// </remarks>
internal static class SqliteStoreImport
{
    /// <summary>
    /// Runs an import.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="envelope">The export (<see cref="ExportEnvelope.ReadAsync"/>).</param>
    /// <param name="request">Mode, dry run and user matching.</param>
    /// <param name="adapter">The plugin's part.</param>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="serverUsers">This server's users, id to name.</param>
    /// <param name="deletedUsers">The users deleted on this server (<see cref="Users.UserReconciler.DeletedUserIds"/>).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels the import before it writes.</param>
    /// <returns>The report.</returns>
    /// <exception cref="Api.PluginException">400, 409 as above; 503 the database can't be opened or written.</exception>
    public static async Task<ImportReport> RunAsync(
        SqliteDatabase database,
        ExportEnvelope envelope,
        ImportRequest request,
        SqliteImportAdapter adapter,
        KitOptions options,
        IReadOnlyDictionary<Guid, string> serverUsers,
        IReadOnlySet<Guid> deletedUsers,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serverUsers);
        ArgumentNullException.ThrowIfNull(deletedUsers);
        ArgumentNullException.ThrowIfNull(logger);

        envelope.EnsureFor(options, database.CurrentVersion);
        database.ThrowIfReadOnlyForAdmin();

        var live = adapter.CountRecordsByUser(envelope.Data, envelope.SchemaVersion);
        var all = live.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (userId, count) in adapter.CountArchivedRecordsByUser(envelope.Archive, envelope.SchemaVersion))
        {
            all[userId] = all.GetValueOrDefault(userId) + count;
        }

        var users = ImportUserMap.Build(all, envelope.Users, serverUsers, deletedUsers, request.MapUsersBy);
        var skipped = users.Unknown.Sum(u => live.GetValueOrDefault(u.Id));

        // The pre-operation snapshot in the same hold of the writer lock as the import, so no write commits in between
        // and is in neither.
        var (applied, safety) = await database.RunWriteAsync(
            (connection, transaction) => adapter.Apply(connection, transaction, envelope.Data, envelope.SchemaVersion, request.Mode, users),
            commit: !request.DryRun,
            preOpOperation: request.DryRun ? null : SnapshotOperations.Import,
            cancellationToken).ConfigureAwait(false);

        var archive = await adapter.ImportArchiveAsync(envelope.Archive, envelope.SchemaVersion, users, request.Mode, request.DryRun, cancellationToken).ConfigureAwait(false);
        var counts = applied.Counts with { SkippedUnknownUser = applied.Counts.SkippedUnknownUser + skipped };
        if (!request.DryRun)
        {
            logger.LogInformation(
                "Imported an export of {Plugin} {Version} ({Mode}, users by {MapUsersBy}) into {File}: {Added} added, {Updated} updated, {Unchanged} unchanged, {KeptNewer} kept newer, {Removed} removed, {Skipped} skipped for {UnknownUsers} unknown users; snapshot before: {Snapshot}",
                envelope.Plugin,
                envelope.PluginVersion,
                request.Mode,
                request.MapUsersBy,
                database.FilePath,
                counts.Added,
                counts.Updated,
                counts.Unchanged,
                counts.KeptNewer,
                counts.Removed,
                counts.SkippedUnknownUser,
                users.Unknown.Count,
                safety is null ? "none" : Path.GetFileName(safety));
        }

        return new ImportReport
        {
            DryRun = request.DryRun,
            Mode = request.Mode,
            MapUsersBy = request.MapUsersBy,
            SourcePluginVersion = envelope.PluginVersion,
            SourceServerId = envelope.ServerId,
            ExportedUtc = envelope.ExportedUtc,
            SchemaVersion = envelope.SchemaVersion,
            MigratedFrom = envelope.SchemaVersion < database.CurrentVersion ? envelope.SchemaVersion : null,
            MappedUsers = users.Mapped,
            UnknownUsers = users.Unknown,
            Records = counts,
            Archive = archive,
            SettingsIncluded = envelope.Settings is not null,
            SafetySnapshot = safety is null ? null : Path.GetFileName(safety),
            Notes = applied.Notes
        };
    }
}
