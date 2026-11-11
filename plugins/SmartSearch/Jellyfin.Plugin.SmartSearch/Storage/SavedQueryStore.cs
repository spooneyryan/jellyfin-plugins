using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.Storage;

/// <summary>
/// The saved queries, <c>data/smartsearch/saved-queries.json</c>, through the kit's JSON store (atomic writes, a
/// <c>.bak</c>, snapshots, read-only on a newer version), and the kit's owner of their users' data.
/// </summary>
/// <remarks>
/// <para><b>A deleted user</b> (<see cref="HandleUserDeletedAsync"/>): under <c>Delete</c> (the default) their private
/// queries that nothing references go (none while the references can't be checked,
/// <see cref="ISavedQueryReferenceGuard.ReferencesUnknown"/>); a shared or referenced query stays under the deleted id as an orphan, shown as
/// "Deleted user", until an administrator gives it to another user or deletes it. <c>KeepAsOrphan</c> and
/// <c>Anonymize</c> keep every query that way (a query has no author beyond its owner).</para>
/// <para><b>Remap by name</b> (<see cref="RemapUsersAsync"/>) runs the check of its <see cref="CheckRemaps"/> scope (the
/// administrator's tools set one: references, administrator-only fields, the quota) under the writer lock.</para>
/// <para>Writes take the store's writer lock, the last in the lock order: the kit's reconciler, then whatever references
/// saved queries, then this store (<see cref="ISavedQueryReferenceGuard"/>). Nothing here calls the reconciler or another
/// owner.</para>
/// </remarks>
internal sealed class SavedQueryStore : IUserDataOwner, IDisposable
{
    /// <summary>The store's file name.</summary>
    public const string FileName = "saved-queries.json";

    private static readonly AsyncLocal<Func<SavedQueriesDocument, SavedQueriesDocument, Exception?>?> RemapCheck = new();

    private readonly Func<ISavedQueryReferenceGuard> _guard;

    /// <summary>
    /// Initializes a new instance of the <see cref="SavedQueryStore"/> class.
    /// </summary>
    /// <param name="file">The store file.</param>
    /// <param name="snapshotDirectory">The snapshot folder.</param>
    /// <param name="guard">What references saved queries (a shared query or a referenced one outlives its deleted owner).</param>
    /// <param name="logger">The store's logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public SavedQueryStore(string file, string snapshotDirectory, Func<ISavedQueryReferenceGuard> guard, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(guard);
        SnapshotDirectory = snapshotDirectory;
        _guard = guard;
        Documents = CreateStore(file, logger, timeProvider, snapshotDirectory);
    }

    /// <summary>
    /// Raised after a write that replaced, removed or added queries (an edit, a forced or bulk change, a deletion, a
    /// user's queries moved or removed), with their ids. Handlers only queue work; they run on the writer's thread.
    /// </summary>
    public event EventHandler<SavedQueriesChangedEventArgs>? QueriesChanged;

    /// <summary>Gets the kit store.</summary>
    public JsonDocumentStore<SavedQueriesDocument> Documents { get; }

    /// <summary>Gets the snapshot folder.</summary>
    public string SnapshotDirectory { get; }

    /// <inheritdoc />
    public string Name => Documents.StoreName;

    /// <inheritdoc />
    public bool IsReadOnly => Documents.IsReadOnly;

    /// <inheritdoc />
    public KnownUsers KnownUsers => Documents.Current.KnownUsers ?? KnownUsers.Empty;

    /// <summary>Gets the current document (never blocks).</summary>
    public SavedQueriesDocument Current => Documents.Current;

    /// <summary>
    /// Creates the store: schema version 1, no migrations, the kit's format.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="snapshotDirectory">The snapshot folder; <c>snapshots</c> next to the file by default.</param>
    /// <returns>The store.</returns>
    public static JsonDocumentStore<SavedQueriesDocument> CreateStore(string file, ILogger logger, TimeProvider? timeProvider = null, string? snapshotDirectory = null)
        => new(file, SavedQueriesDocument.CurrentVersion, SavedQueriesDocument.CreateEmpty, [], logger, JsonStoreFormat.Options, timeProvider, snapshotDirectory);

    /// <summary>Counts a document's queries per owner.</summary>
    /// <param name="document">The document.</param>
    /// <returns>Owner id to number of queries.</returns>
    public static IReadOnlyDictionary<Guid, int> CountRecordsByUser(SavedQueriesDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (document.Queries ?? []).Where(q => q is not null).GroupBy(q => q.OwnerUserId).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Returns the document with the given queries, ordered by creation time and id.</summary>
    /// <param name="document">The document.</param>
    /// <param name="queries">The queries.</param>
    /// <returns>The new document.</returns>
    public static SavedQueriesDocument WithQueries(SavedQueriesDocument document, IEnumerable<SavedQueryRecord> queries)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Queries = queries.OrderBy(q => q.CreatedUtc).ThenBy(q => q.Id).ToList() };
    }

    /// <summary>Finds a query by id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The query, or <c>null</c>.</returns>
    public SavedQueryRecord? Find(Guid id) => Current.Queries.FirstOrDefault(q => q.Id.Equals(id));

    /// <summary>Changes the document under the writer lock (<see cref="JsonDocumentStore{TDoc}.UpdateAsync"/>).</summary>
    /// <param name="change">The change; it may throw to refuse, and then nothing is written.</param>
    /// <param name="cancellationToken">Cancels waiting for the lock.</param>
    /// <returns>The document after the change.</returns>
    public Task<SavedQueriesDocument> UpdateAsync(Func<SavedQueriesDocument, SavedQueriesDocument> change, CancellationToken cancellationToken)
        => ChangeAsync(change, cancellationToken);

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, int> CountRecordsByUser() => CountRecordsByUser(Current);

    /// <inheritdoc />
    public async Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        await ChangeAsync(
            document =>
            {
                var known = document.KnownUsers ?? KnownUsers.Empty;
                var refreshed = known.Refresh(CountRecordsByUser(document).Keys, currentNames);
                return ReferenceEquals(refreshed, known) ? document : document with { KnownUsers = refreshed };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string?> SnapshotBeforeAsync(string operation, CancellationToken cancellationToken)
        => await Documents.PreOpSnapshotAsync(SnapshotDirectory, operation, cancellationToken).ConfigureAwait(false) is { } path
            ? Path.GetFileName(path)
            : null;

    /// <inheritdoc />
    public bool WouldChangeRecords(Guid userId, DeletedUserPolicy policy)
        => policy == DeletedUserPolicy.Delete && Current.Queries.Any(q => q.OwnerUserId.Equals(userId) && GoesWithOwner(q));

    /// <inheritdoc />
    public async Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken)
    {
        if (policy != DeletedUserPolicy.Delete)
        {
            return 0; // every query stays, as an orphan
        }

        var removed = 0;
        await ChangeAsync(
            document =>
            {
                var kept = document.Queries.Where(q => !q.OwnerUserId.Equals(userId) || !GoesWithOwner(q)).ToList();
                removed = document.Queries.Count - kept.Count;
                if (removed == 0)
                {
                    return document;
                }

                var known = document.KnownUsers ?? KnownUsers.Empty;
                return WithQueries(document, kept) with
                {
                    KnownUsers = kept.Exists(q => q.OwnerUserId.Equals(userId)) ? known : known.Without(userId)
                };
            },
            cancellationToken).ConfigureAwait(false);

        // The write's .bak still holds the deleted queries; written once more, it doesn't.
        if (removed > 0)
        {
            await Documents.RefreshBackupAsync(cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    /// Has the remaps run in this flow until the result is disposed (the kit's reconciler calls
    /// <see cref="RemapUsersAsync"/>) checked by <paramref name="check"/>: it is given the document before and after, under
    /// the writer lock, and returns the refusal to throw, or <c>null</c> to write. A remap outside such a scope is refused
    /// when it would break references (<see cref="ISavedQueryReferenceGuard.FindBrokenReferences"/>).
    /// </summary>
    /// <param name="check">The check.</param>
    /// <returns>Ends the scope.</returns>
    public static IDisposable CheckRemaps(Func<SavedQueriesDocument, SavedQueriesDocument, Exception?> check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var previous = RemapCheck.Value;
        RemapCheck.Value = check;
        return new Scope(() => RemapCheck.Value = previous);
    }

    /// <summary>
    /// Gives the queries of old owner ids to new ones, as <see cref="RemapUsersAsync"/> writes it: a name the new owner
    /// already uses gets a number on the moved query, as an import's does; the queries that stay keep their names.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="map">Old id to new id.</param>
    /// <param name="moved">How many queries changed owner.</param>
    /// <returns>The new document; the same instance when none moved.</returns>
    public static SavedQueriesDocument Remap(SavedQueriesDocument document, IReadOnlyDictionary<Guid, Guid> map, out int moved)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(map);
        moved = 0;
        var queries = new List<SavedQueryRecord>();
        foreach (var query in document.Queries)
        {
            var owner = map.TryGetValue(query.OwnerUserId, out var newOwner) ? newOwner : query.OwnerUserId;
            var editor = map.TryGetValue(query.UpdatedByUserId, out var newEditor) ? newEditor : query.UpdatedByUserId;
            if (!owner.Equals(query.OwnerUserId))
            {
                moved++;
            }

            queries.Add(owner.Equals(query.OwnerUserId) && editor.Equals(query.UpdatedByUserId)
                ? query
                : query with { OwnerUserId = owner, UpdatedByUserId = editor });
        }

        if (moved == 0)
        {
            return document;
        }

        var ordered = queries.Select((q, i) => (Query: q, Moved: !document.Queries[i].OwnerUserId.Equals(q.OwnerUserId)))
            .OrderBy(p => p.Moved)
            .Select(p => p.Query)
            .ToList();
        return WithQueries(document, SavedQueryNames.MakeUnique(ordered)) with
        {
            KnownUsers = (document.KnownUsers ?? KnownUsers.Empty).Remap(map)
        };
    }

    /// <inheritdoc />
    public async Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        var check = RemapCheck.Value ?? DefaultRemapCheck;
        var moved = 0;
        await ChangeAsync(
            document =>
            {
                var remapped = Remap(document, map, out moved);
                if (moved > 0 && check(document, remapped) is { } refusal)
                {
                    throw refusal;
                }

                return remapped;
            },
            cancellationToken).ConfigureAwait(false);
        return moved;
    }

    /// <inheritdoc />
    public void Dispose() => Documents.Dispose();

    /// <summary>
    /// Tells whoever follows the queries which ones a write that didn't go through this store's own changes (an import, a
    /// snapshot restore, both on <see cref="Documents"/>) replaced, removed or added. Called after the write, outside
    /// the locks.
    /// </summary>
    /// <param name="before">The document before the write.</param>
    /// <param name="after">The document after it.</param>
    public void NotifyChanged(SavedQueriesDocument before, SavedQueriesDocument after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        Raise(before, after);
    }

    // Every change of the document goes through here, so that whoever follows the queries hears of each one: the ids
    // of the queries replaced (a new query text, owner, name or sharing), removed or added (one coming back), after the
    // write.
    private async Task<SavedQueriesDocument> ChangeAsync(Func<SavedQueriesDocument, SavedQueriesDocument> change, CancellationToken cancellationToken)
    {
        SavedQueriesDocument? before = null;
        var after = await Documents.UpdateAsync(
            document =>
            {
                before = document;
                return change(document);
            },
            cancellationToken).ConfigureAwait(false);
        if (before is not null)
        {
            Raise(before, after);
        }

        return after;
    }

    private void Raise(SavedQueriesDocument before, SavedQueriesDocument after)
    {
        if (!ReferenceEquals(before, after) && QueriesChanged is { } handler)
        {
            var now = new Dictionary<Guid, SavedQueryRecord>();
            foreach (var query in (after.Queries ?? []).Where(q => q is not null))
            {
                now.TryAdd(query.Id, query);
            }

            // Replaced or removed, and added: a query that comes back (an import, a restore) is news to whatever still
            // follows it and stopped when it went.
            var was = (before.Queries ?? []).Where(q => q is not null).Select(q => q.Id).ToHashSet();
            var changed = (before.Queries ?? []).Where(q => q is not null && (!now.TryGetValue(q.Id, out var current) || !ReferenceEquals(current, q))).Select(q => q.Id)
                .Concat(now.Keys.Where(id => !was.Contains(id)))
                .Distinct()
                .ToList();
            if (changed.Count > 0)
            {
                handler(this, new SavedQueriesChangedEventArgs(changed));
            }
        }
    }

    // Outside a scope of CheckRemaps: a remap that breaks references, or one that moves queries while the references
    // can't be checked, is refused.
    private Exception? DefaultRemapCheck(SavedQueriesDocument before, SavedQueriesDocument after)
    {
        var guard = _guard();
        if (guard.ReferencesUnknown)
        {
            return Services.SavedQueryErrors.ReferencesUnknown;
        }

        return guard.FindBrokenReferences(SavedQueryBulkChange.Between(before, after)) is { Count: > 0 } broken
            ? Services.SavedQueryErrors.BreaksReferences(broken)
            : null;
    }

    // A private query nothing references goes with its deleted owner; any other stays as an orphan, and so does every
    // query while the references can't be checked.
    private bool GoesWithOwner(SavedQueryRecord query)
    {
        var guard = _guard();
        return !query.Shared && !guard.ReferencesUnknown && guard.CountReferences(query.Id) == 0;
    }

    private sealed class Scope(Action end) : IDisposable
    {
        public void Dispose() => end();
    }
}
