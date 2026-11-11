using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// <c>POST /&lt;Route&gt;/Admin/Import</c> for a <see cref="JsonDocumentStore{TDoc}"/>.
/// </summary>
/// <remarks>
/// In order: the envelope must be this plugin's (400) at a schema this version reads (409); its data goes through the
/// store's migrations; its users are matched with this server's by id or name (never by name for a user deleted on this
/// server or on the exporting one, <see cref="ImportUserMap"/>), and the records of users not found are skipped and
/// reported; then the plugin's <see cref="JsonImportAdapter{TDoc}"/> combines the documents. A dry run (the
/// default) stops there. Otherwise a read-only store refuses (409), the adapter's
/// <see cref="JsonImportAdapter{TDoc}.CheckBeforeWrite"/> may refuse, a pre-operation snapshot is taken, and the adapter
/// runs again inside the store's writer lock on the document stored at that moment, so a write in between is never lost.
/// Settings are never applied.
/// </remarks>
internal static class JsonStoreImport
{
    /// <summary>
    /// Runs an import.
    /// </summary>
    /// <typeparam name="TDoc">The store's document.</typeparam>
    /// <param name="store">The store.</param>
    /// <param name="envelope">The export (<see cref="ExportEnvelope.ReadAsync"/>).</param>
    /// <param name="request">Mode, dry run and user matching.</param>
    /// <param name="adapter">The plugin's part.</param>
    /// <param name="options">The plugin's kit options.</param>
    /// <param name="serverUsers">This server's users, id to name.</param>
    /// <param name="deletedUsers">The users deleted on this server (<see cref="Users.UserReconciler.DeletedUserIds"/>).</param>
    /// <param name="snapshotDirectory">The store's snapshot folder.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>The report.</returns>
    /// <exception cref="Api.PluginException">400, 409 as above; 503 the store can't be read or written.</exception>
    public static async Task<ImportReport> RunAsync<TDoc>(
        JsonDocumentStore<TDoc> store,
        ExportEnvelope envelope,
        ImportRequest request,
        JsonImportAdapter<TDoc> adapter,
        KitOptions options,
        IReadOnlyDictionary<Guid, string> serverUsers,
        IReadOnlySet<Guid> deletedUsers,
        string snapshotDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
        where TDoc : class, IVersionedDocument
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serverUsers);
        ArgumentNullException.ThrowIfNull(deletedUsers);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        envelope.EnsureFor(options, store.CurrentVersion);
        var incoming = store.ReadData(envelope.Data, envelope.SchemaVersion);

        // The users of the live records and of the archived ones are matched together (a user may have only archived
        // records); the live records of users not found are the ones counted as skipped.
        var live = adapter.CountRecordsByUser(incoming);
        var all = live.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (userId, count) in adapter.CountArchivedRecordsByUser(envelope.Archive, envelope.SchemaVersion))
        {
            all[userId] = all.GetValueOrDefault(userId) + count;
        }

        var users = ImportUserMap.Build(all, envelope.Users, serverUsers, deletedUsers, request.MapUsersBy);
        var skipped = users.Unknown.Sum(u => live.GetValueOrDefault(u.Id));

        ImportApplied<TDoc> applied;
        string? safety = null;
        if (request.DryRun)
        {
            applied = adapter.Apply(store.Current, incoming, request.Mode, users);
        }
        else
        {
            store.ThrowIfReadOnlyForAdmin();
            adapter.CheckBeforeWrite(store.Current, incoming, request.Mode, users);
            safety = await store.PreOpSnapshotAsync(snapshotDirectory, SnapshotOperations.Import, cancellationToken).ConfigureAwait(false);
            ImportApplied<TDoc>? result = null;
            await store.UpdateAsync(
                current =>
                {
                    result = adapter.Apply(current, incoming, request.Mode, users);
                    return result.Document;
                },
                cancellationToken).ConfigureAwait(false);
            applied = result!;
        }

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
                store.FilePath,
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
            MigratedFrom = envelope.SchemaVersion < store.CurrentVersion ? envelope.SchemaVersion : null,
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
