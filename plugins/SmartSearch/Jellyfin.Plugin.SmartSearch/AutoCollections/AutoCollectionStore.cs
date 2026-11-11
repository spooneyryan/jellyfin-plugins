using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The auto-collection definitions, <c>data/smartsearch/auto-collections.json</c>, through the kit's JSON store, and the
/// kit's owner of the users they name.
/// </summary>
/// <remarks>
/// <para><b>Locks.</b> Every change of the definitions goes through <see cref="UpdateAsync"/> (or
/// <see cref="UpdateHeldAsync"/> under <see cref="AcquireAsync"/>), which hold the store's own gate, taken before the saved
/// queries' writer lock whenever both are needed: the saved queries' reference guard checks the definitions under this
/// gate and writes the query from inside it, so nothing can start referencing a query between the check and the
/// write.</para>
/// <para><b>Deleted users.</b> A definition is never deleted with a user, whatever the policy: a definition whose creator
/// is gone stops (<c>CreatorMissing</c>, decided at each run by looking the creator up, so a user deleted while the server
/// was down counts too) until an administrator gives it to someone else; one whose evaluation user is gone stops as
/// <c>EvaluationUserMissing</c>. The collections stay as they are meanwhile.</para>
/// <para><b>Read-only.</b> A file of a newer schema version, or one whose upgrade failed, leaves the store read-only, and
/// a read-only store is inert: nothing evaluates or writes a collection (<see cref="AutoCollectionChecks"/>). So does a
/// file of this schema version with a document-level field this version doesn't know: a newer Smart Search may have
/// put a setting there that changes what every definition does, so nothing runs on it and nothing is written over it.</para>
/// <para><b>Missing parts.</b> A file edited by hand can hold a null where a definition, a part of one or a list should
/// be. Such a document is read as if the nulls were the defaults (a null definition or entry is dropped, with a
/// warning), so the rest keeps working; the next write stores it that way.</para>
/// </remarks>
internal sealed class AutoCollectionStore : IUserDataOwner, IDisposable
{
    /// <summary>The store's file name.</summary>
    public const string FileName = "auto-collections.json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger;
    private Normalised? _normalised;
    private int _newerUnreadable;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionStore"/> class.
    /// </summary>
    /// <param name="file">The store file.</param>
    /// <param name="snapshotDirectory">The snapshot folder.</param>
    /// <param name="logger">The store's logger.</param>
    /// <param name="timeProvider">The clock; the system clock by default.</param>
    public AutoCollectionStore(string file, string snapshotDirectory, ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        _logger = logger;
        SnapshotDirectory = snapshotDirectory;
        Documents = CreateStore(file, logger, timeProvider, snapshotDirectory);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionStore"/> class over a kit store made elsewhere (one with
    /// upgrade steps, for the state a failed upgrade leaves).
    /// </summary>
    /// <param name="documents">The kit store.</param>
    /// <param name="snapshotDirectory">The snapshot folder.</param>
    /// <param name="logger">The store's logger.</param>
    internal AutoCollectionStore(JsonDocumentStore<AutoCollectionsDocument> documents, string snapshotDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentException.ThrowIfNullOrEmpty(snapshotDirectory);
        _logger = logger;
        SnapshotDirectory = snapshotDirectory;
        Documents = documents;
    }

    /// <summary>Gets the kit store.</summary>
    public JsonDocumentStore<AutoCollectionsDocument> Documents { get; }

    /// <summary>Gets the snapshot folder.</summary>
    public string SnapshotDirectory { get; }

    /// <inheritdoc />
    public string Name => Documents.StoreName;

    /// <inheritdoc />
    /// <remarks>Also read-only while the document has fields this version doesn't know (see the class).</remarks>
    public bool IsReadOnly => Documents.IsReadOnly || HasNewerFields;

    /// <summary>Gets a value indicating whether the document has fields of its own this version doesn't know.</summary>
    public bool HasNewerFields => Documents.Current.Extra is { Count: > 0 };

    /// <summary>
    /// Gets a value indicating whether the store is read-only without the definitions in memory: the file couldn't be read
    /// or is missing with nothing to take its place, its upgrade failed, or a newer version's file couldn't be read at all.
    /// The kit then serves an empty document, so nothing here can tell which saved queries are followed: the saved
    /// queries' reference guard refuses every change that could break a reference (see
    /// <see cref="DefinitionReferenceGuard"/>). Memory only after the first call.
    /// </summary>
    public bool DefinitionsUnknown => Documents.Status switch
    {
        StoreStatus.ReadOnlyDataUnreadable or StoreStatus.ReadOnlyDataMissing or StoreStatus.ReadOnlyMigrationFailed => true,
        StoreStatus.ReadOnlyNewerVersion => NewerFileUnreadable(),
        _ => false
    };

    /// <inheritdoc />
    public KnownUsers KnownUsers => Documents.Current.KnownUsers ?? KnownUsers.Empty;

    /// <summary>Gets the current document (never blocks), with any nulls read as defaults (see the class).</summary>
    public AutoCollectionsDocument Current => Normalise(Documents.Current);

    /// <summary>
    /// Creates the store: schema version 1, no migrations, the kit's format.
    /// </summary>
    /// <param name="file">The file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="snapshotDirectory">The snapshot folder; <c>snapshots</c> next to the file by default.</param>
    /// <returns>The store.</returns>
    public static JsonDocumentStore<AutoCollectionsDocument> CreateStore(string file, ILogger logger, TimeProvider? timeProvider = null, string? snapshotDirectory = null)
        => new(file, AutoCollectionsDocument.CurrentVersion, AutoCollectionsDocument.CreateEmpty, [], logger, JsonStoreFormat.Options, timeProvider, snapshotDirectory);

    /// <summary>Counts the definitions that name each user, as creator or evaluation user.</summary>
    /// <param name="document">The document.</param>
    /// <returns>User id to number of definitions.</returns>
    public static IReadOnlyDictionary<Guid, int> CountRecordsByUser(AutoCollectionsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Users(document).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>Returns the document with the given definitions, ordered by creation time and id.</summary>
    /// <param name="document">The document.</param>
    /// <param name="definitions">The definitions.</param>
    /// <returns>The new document.</returns>
    public static AutoCollectionsDocument WithDefinitions(AutoCollectionsDocument document, IEnumerable<AutoCollectionDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Definitions = definitions.OrderBy(d => d.CreatedUtc).ThenBy(d => d.Id).ToList() };
    }

    /// <summary>Finds a definition by id.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The definition, or <c>null</c>.</returns>
    public AutoCollectionDefinition? Find(Guid id) => Definitions().FirstOrDefault(d => d.Id.Equals(id));

    /// <summary>Finds the definition whose collection is this item.</summary>
    /// <param name="collectionId">The collection's item id.</param>
    /// <returns>The definition, or <c>null</c>.</returns>
    public AutoCollectionDefinition? FindByCollection(Guid collectionId)
        => collectionId == Guid.Empty ? null : Definitions().FirstOrDefault(d => d.CollectionId == collectionId);

    /// <summary>Gets every definition's collection id (what <c>InCollection IsEmpty</c> ignores, and what no rule may name).</summary>
    /// <returns>The ids.</returns>
    public IReadOnlySet<Guid> CollectionIds()
        => Definitions().Where(d => d.CollectionId is { } id && id != Guid.Empty).Select(d => d.CollectionId!.Value).ToHashSet();

    /// <summary>
    /// Gets the definitions that follow the saved query and whose creator owns it: the references the query's changes are
    /// checked against. One whose creator doesn't own the query (after an owner change that was forced, a remap refused
    /// part-way, or an import) has already stopped (<c>SourceOwnerChanged</c>) and runs again only once it follows a query
    /// of its creator's, so it holds nothing of the query's owner back. Lock-free (it reads the current document only), so
    /// the saved queries may call it under their own writer lock. Nothing about a referenced query is stored with the
    /// definition beyond its id: every run resolves the query again, so a bulk change of the saved queries (an import, a
    /// restore, a remap) is reconciled by the next run (<c>QueryMissing</c>, or the changed query run as it now is).
    /// </summary>
    /// <param name="query">The saved query as stored.</param>
    /// <returns>The definitions.</returns>
    public IReadOnlyList<AutoCollectionDefinition> ReferencesTo(Storage.SavedQueryRecord query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Definitions().Where(d => Follows(d, query)).ToList();
    }

    /// <summary>Whether a definition follows the saved query and its creator owns it (see <see cref="ReferencesTo"/>).</summary>
    /// <param name="definition">The definition.</param>
    /// <param name="query">The saved query.</param>
    /// <returns>Whether it is a reference.</returns>
    public static bool Follows(AutoCollectionDefinition definition, Storage.SavedQueryRecord query)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(query);
        return definition.Source is { } s
            && s.Mode.Value != SmartSearchAutoCollectionSourceMode.Snapshot
            && s.SavedQueryId == query.Id
            && definition.CreatedByUserId.Equals(query.OwnerUserId);
    }

    /// <summary>
    /// Takes the store's gate: the caller may then read <see cref="Current"/>, call other stores' writers, and change the
    /// definitions with <see cref="UpdateHeldAsync"/>, with nothing else changing them meanwhile.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the gate.</param>
    /// <returns>The held gate; dispose it to let go.</returns>
    public async Task<Held> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Held(this);
    }

    /// <summary>Changes the document under the gate and the kit's writer lock.</summary>
    /// <param name="change">The change; it may throw to refuse, and then nothing is written.</param>
    /// <param name="cancellationToken">Cancels waiting for the locks.</param>
    /// <returns>The document after the change.</returns>
    /// <exception cref="Jellyfin.Plugin.Kit.Api.PluginException">409 <c>StoreReadOnly</c> while the document has fields this version doesn't know.</exception>
    public async Task<AutoCollectionsDocument> UpdateAsync(Func<AutoCollectionsDocument, AutoCollectionsDocument> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var held = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await WriteAsync(change, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Changes the document while the caller holds the gate.</summary>
    /// <param name="held">The gate, from <see cref="AcquireAsync"/>.</param>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">Cancels waiting for the kit's writer lock.</param>
    /// <returns>The document after the change.</returns>
    public Task<AutoCollectionsDocument> UpdateHeldAsync(Held held, Func<AutoCollectionsDocument, AutoCollectionsDocument> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(change);
        if (!ReferenceEquals(held.Store, this) || held.Released)
        {
            throw new InvalidOperationException("The gate isn't held.");
        }

        return WriteAsync(change, cancellationToken);
    }

    /// <summary>Changes one definition under the gate; nothing is written when it is gone or the change returns it unchanged.</summary>
    /// <param name="id">The definition's id.</param>
    /// <param name="change">The change.</param>
    /// <param name="cancellationToken">Cancels waiting for the locks.</param>
    /// <returns>The definition after the change, or <c>null</c> when it is gone.</returns>
    public async Task<AutoCollectionDefinition?> UpdateDefinitionAsync(Guid id, Func<AutoCollectionDefinition, AutoCollectionDefinition> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        AutoCollectionDefinition? result = null;
        await UpdateAsync(
            document =>
            {
                var list = document.Definitions.ToList();
                var index = list.FindIndex(d => d is not null && d.Id.Equals(id));
                if (index < 0)
                {
                    result = null;
                    return document;
                }

                result = change(list[index]);
                if (ReferenceEquals(result, list[index]))
                {
                    return document;
                }

                list[index] = result;
                return WithDefinitions(document, list);
            },
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<Guid, int> CountRecordsByUser() => CountRecordsByUser(Current);

    /// <inheritdoc />
    public async Task RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        if (IsReadOnly)
        {
            return;
        }

        await UpdateAsync(
            document =>
            {
                var known = document.KnownUsers ?? KnownUsers.Empty;
                var refreshed = known.Refresh(Users(document).Distinct(), currentNames);
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
    public bool WouldChangeRecords(Guid userId, DeletedUserPolicy policy) => false;

    /// <inheritdoc />
    /// <remarks>Nothing changes: a definition whose creator or evaluation user is deleted pauses at its next run (see the class).</remarks>
    public Task<int> HandleUserDeletedAsync(Guid userId, DeletedUserPolicy policy, CancellationToken cancellationToken) => Task.FromResult(0);

    /// <inheritdoc />
    public async Task<int> RemapUsersAsync(IReadOnlyDictionary<Guid, Guid> map, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        var moved = 0;
        await UpdateAsync(
            document =>
            {
                moved = 0;
                Guid Map(Guid id) => map.TryGetValue(id, out var to) ? to : id;
                var definitions = new List<AutoCollectionDefinition>();
                foreach (var definition in document.Definitions)
                {
                    var changed = definition with
                    {
                        CreatedByUserId = Map(definition.CreatedByUserId),
                        UpdatedByUserId = Map(definition.UpdatedByUserId),
                        EvaluationUserId = definition.EvaluationUserId is { } e ? Map(e) : null
                    };
                    if (changed == definition)
                    {
                        definitions.Add(definition);
                        continue;
                    }

                    moved++;
                    definitions.Add(changed);
                }

                return moved == 0
                    ? document
                    : WithDefinitions(document, definitions) with { KnownUsers = (document.KnownUsers ?? KnownUsers.Empty).Remap(map) };
            },
            cancellationToken).ConfigureAwait(false);
        return moved;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Documents.Dispose();
        _gate.Dispose();
    }

    private static IEnumerable<Guid> Users(AutoCollectionsDocument document)
        => (document.Definitions ?? []).Where(d => d is not null).SelectMany(d =>
            d.EvaluationUserId is { } e && e != d.CreatedByUserId ? new[] { d.CreatedByUserId, e } : [d.CreatedByUserId]);

    private IReadOnlyList<AutoCollectionDefinition> Definitions() => Current.Definitions;

    // A newer version's file that this version couldn't read at all: the kit serves an empty document for it. Worked out
    // once (the status holds until the plugin restarts) the way the kit reads it.
    private bool NewerFileUnreadable()
    {
        var known = Volatile.Read(ref _newerUnreadable);
        if (known != 0)
        {
            return known == 1;
        }

        bool unreadable;
        try
        {
            unreadable = System.Text.Json.JsonSerializer.Deserialize<AutoCollectionsDocument>(Documents.ExportData().Data, JsonStoreFormat.Options) is null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException or ArgumentException or Jellyfin.Plugin.Kit.Api.PluginException)
        {
            unreadable = true;
        }

        Volatile.Write(ref _newerUnreadable, unreadable ? 1 : 2);
        return unreadable;
    }

    // A null part read as its default, a null list as empty, and a null entry dropped.
    private static AutoCollectionDefinition NormaliseDefinition(AutoCollectionDefinition definition)
    {
        var edits = definition.ManualEdits ?? new AutoCollectionManualEdits();
        var pinned = edits.Pinned is null || edits.Pinned.Any(e => e is null) ? (edits.Pinned ?? []).Where(e => e is not null).ToList() : edits.Pinned;
        var excluded = edits.Excluded is null || edits.Excluded.Any(e => e is null) ? (edits.Excluded ?? []).Where(e => e is not null).ToList() : edits.Excluded;
        if (definition.Name is not null
            && definition.Source is not null
            && definition.Presentation is not null
            && definition.Safety is not null
            && definition.ManualEdits is not null
            && ReferenceEquals(pinned, edits.Pinned)
            && ReferenceEquals(excluded, edits.Excluded))
        {
            return definition;
        }

        return definition with
        {
            Name = definition.Name ?? string.Empty,
            Source = definition.Source ?? new AutoCollectionSource(),
            Presentation = definition.Presentation ?? new AutoCollectionPresentation(),
            Safety = definition.Safety ?? new AutoCollectionSafety(),
            ManualEdits = edits with { Pinned = pinned, Excluded = excluded }
        };
    }

    private Task<AutoCollectionsDocument> WriteAsync(Func<AutoCollectionsDocument, AutoCollectionsDocument> change, CancellationToken cancellationToken)
    {
        if (!Documents.IsReadOnly && HasNewerFields)
        {
            throw AutoCollectionErrors.StoreReadOnly;
        }

        return Documents.UpdateAsync(d => change(Normalise(d)), cancellationToken);
    }

    // The document with its nulls read as defaults; the same instance when it has none. Kept for the loaded document, so
    // the warning is logged once.
    private AutoCollectionsDocument Normalise(AutoCollectionsDocument document)
    {
        var cached = Volatile.Read(ref _normalised);
        if (cached is not null && ReferenceEquals(cached.Raw, document))
        {
            return cached.Document;
        }

        var raw = document.Definitions ?? [];
        var dropped = raw.Count(d => d is null);
        var definitions = raw.Where(d => d is not null).Select(NormaliseDefinition).ToList();
        var same = document.Definitions is not null && dropped == 0 && definitions.Where((d, i) => !ReferenceEquals(d, raw[i])).Any() == false;
        var result = same && document.KnownUsers is not null
            ? document
            : document with { Definitions = same ? raw : definitions, KnownUsers = document.KnownUsers ?? KnownUsers.Empty };
        if (dropped > 0)
        {
            _logger.LogWarning("The auto-collections file has {Count} empty definition entries; they are left out.", dropped);
        }

        Volatile.Write(ref _normalised, new Normalised(document, result));
        return result;
    }

    private sealed record Normalised(AutoCollectionsDocument Raw, AutoCollectionsDocument Document);

    /// <summary>The store's gate, held until disposed.</summary>
    internal sealed class Held : IDisposable
    {
        private int _released;

        /// <summary>
        /// Initializes a new instance of the <see cref="Held"/> class.
        /// </summary>
        /// <param name="store">The store.</param>
        public Held(AutoCollectionStore store)
        {
            Store = store;
        }

        /// <summary>Gets the store whose gate this is.</summary>
        public AutoCollectionStore Store { get; }

        /// <summary>Gets a value indicating whether the gate was let go.</summary>
        public bool Released => Volatile.Read(ref _released) == 1;

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Store._gate.Release();
            }
        }
    }
}
