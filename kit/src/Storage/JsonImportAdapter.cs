using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// A plugin's part of the kit's import into a <see cref="JsonDocumentStore{TDoc}"/>: which users the
/// records belong to, and how the export's document combines with the stored one. <see cref="JsonStoreImport"/> does
/// the rest: the envelope checks, the migrations, the user map, the dry run, the snapshot and the write.
/// </summary>
/// <typeparam name="TDoc">The store's document.</typeparam>
internal abstract class JsonImportAdapter<TDoc>
    where TDoc : class, IVersionedDocument
{
    /// <summary>
    /// Counts a document's records per user id.
    /// </summary>
    /// <param name="document">The document (the export's, at the current version).</param>
    /// <returns>User id to number of records.</returns>
    public abstract IReadOnlyDictionary<Guid, int> CountRecordsByUser(TDoc document);

    /// <summary>
    /// Counts the export's archived records per user id, so that their users are matched too, also a user with no live
    /// record in the export (Bookmarks was the first plugin that imports an archive). The default, for plugins without
    /// an archive, counts none.
    /// </summary>
    /// <param name="records">The archived records, in the records' shape at <paramref name="schemaVersion"/>.</param>
    /// <param name="schemaVersion">The export's schema version.</param>
    /// <returns>User id to number of archived records.</returns>
    public virtual IReadOnlyDictionary<Guid, int> CountArchivedRecordsByUser(IReadOnlyList<JsonObject> records, int schemaVersion)
        => new Dictionary<Guid, int>();

    /// <summary>
    /// Combines the stored document with the export's. Pure: it runs once for a dry run on the stored document, and once
    /// more inside the store's writer lock for the real import, on the document stored then. It keeps only records of
    /// users in <paramref name="users"/>, with their ids rewritten through <see cref="ImportUserMap.TryMap"/>, and never
    /// trusts paths from another server.
    /// </summary>
    /// <param name="current">The stored document.</param>
    /// <param name="incoming">The export's document, at the current version.</param>
    /// <param name="mode">Merge or Replace (<see cref="ImportMerge"/> has helpers for both).</param>
    /// <param name="users">The export's users found on this server.</param>
    /// <returns>The document to store, and what changed.</returns>
    public abstract ImportApplied<TDoc> Apply(TDoc current, TDoc incoming, ImportMode mode, ImportUserMap users);

    /// <summary>
    /// Checks a real import on the document stored now, before <see cref="JsonStoreImport"/> takes its pre-operation
    /// snapshot: a refusal thrown here costs no snapshot (a snapshot rotates out an older one) and writes nothing.
    /// <see cref="Apply"/> runs afterwards under the writer lock on the document stored then, and may still refuse there
    /// when the store changed in between. The default checks nothing.
    /// </summary>
    /// <param name="current">The document stored now.</param>
    /// <param name="incoming">The export's document, at the current version.</param>
    /// <param name="mode">Merge or Replace.</param>
    /// <param name="users">The export's users found on this server.</param>
    public virtual void CheckBeforeWrite(TDoc current, TDoc incoming, ImportMode mode, ImportUserMap users)
    {
    }

    /// <summary>
    /// Imports the export's archived records. Plugins without an archive keep this default, which
    /// imports none and says so in the report.
    /// </summary>
    /// <param name="records">The archived records, in the records' shape at <paramref name="schemaVersion"/>.</param>
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
