using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage.Migrations;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// Bookmarks keyed by id, and the rest of schema 2's document, on top of the kit's <see cref="JsonDocumentStore{TDoc}"/>
/// atomic writes with a <c>.bak</c>, corrupt-file recovery, the 1→2 migration with its
/// <c>.v1-*</c> copy, read-only refusal of newer files and of a failed upgrade, and snapshots.
/// </summary>
/// <remarks>
/// Records are written ordered by creation time and then id, as 1.0 wrote them, with the kit's serializer options, which
/// are 1.0's. Reads are served from an index of the current document that is rebuilt only when the document changes.
/// Every write keeps the document's invariants: ordered records with unique ids, at most
/// <see cref="BookmarkStoreDocument.MaxClipRoots"/> clip folders and <see cref="BookmarkStoreDocument.MaxPendingFileDeletes"/>
/// pending deletes.
/// </remarks>
internal sealed class BookmarkStore : IBookmarkStore, IDisposable
{
    private readonly JsonDocumentStore<BookmarkStoreDocument> _store;
    private volatile BookmarkIndex? _index;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkStore"/> class.
    /// </summary>
    /// <param name="store">The document store; the bookmark store owns and disposes it.</param>
    public BookmarkStore(JsonDocumentStore<BookmarkStoreDocument> store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public JsonDocumentStore<BookmarkStoreDocument> Documents => _store;

    /// <inheritdoc />
    public bool IsReadOnly => _store.IsReadOnly;

    /// <inheritdoc />
    public BookmarkStoreReadOnlyReason? ReadOnlyReason => ReadOnlyReasonOf(_store.Status);

    /// <inheritdoc />
    public IReadOnlyList<string> ClipRoots => _store.Current.ClipRoots;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, BookmarkImportLedgerEntry> ImportLedger => _store.Current.ImportLedger;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, BookmarkImportRun> ImportRuns => _store.Current.ImportRuns;

    /// <inheritdoc />
    public KnownUsers KnownUsers => _store.Current.KnownUsers;

    /// <inheritdoc />
    public IReadOnlyList<PendingFileDelete> PendingFileDeletes => _store.Current.PendingFileDeletes;

    /// <summary>
    /// Gets the schema upgrade steps of <c>bookmarks.json</c> as the plugin runs them.
    /// </summary>
    /// <param name="migration1To2">The 1→2 step, built with the plugin's paths, settings and users.</param>
    /// <returns>One step from each older version.</returns>
    public static IReadOnlyList<IJsonMigration> Migrations(BookmarkStoreMigration1To2 migration1To2) => [migration1To2];

    /// <summary>
    /// Gets why a store with the given status is read-only, or <c>null</c> when it is writable.
    /// </summary>
    /// <param name="status">The kit store's status.</param>
    /// <returns>The reason.</returns>
    public static BookmarkStoreReadOnlyReason? ReadOnlyReasonOf(StoreStatus status) => status switch
    {
        StoreStatus.ReadOnlyNewerVersion => BookmarkStoreReadOnlyReason.NewerVersion,
        StoreStatus.ReadOnlyMigrationFailed => BookmarkStoreReadOnlyReason.MigrationFailed,
        StoreStatus.ReadOnlyDataMissing => BookmarkStoreReadOnlyReason.DataMissing,
        StoreStatus.ReadOnlyDataUnreadable => BookmarkStoreReadOnlyReason.DataUnreadable,
        _ => null
    };

    /// <summary>
    /// Creates the store over a file, with its migrations.
    /// </summary>
    /// <param name="file">The bookmark file, normally <see cref="BookmarkPaths.StoreFile"/>.</param>
    /// <param name="migrations">The upgrade steps (<see cref="Migrations"/>).</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock for file time stamps; the system clock by default.</param>
    /// <returns>The store. Nothing is read until it is first used.</returns>
    public static BookmarkStore Create(string file, IReadOnlyList<IJsonMigration> migrations, ILogger logger, TimeProvider? timeProvider = null)
        => new(CreateDocumentStore(file, migrations, logger, timeProvider));

    /// <summary>
    /// Creates the store over a file outside the plugin's services (tests, tools): a 1.0 file is upgraded with the clip
    /// folder <c>clips</c> next to it as the default, no configured folder and no server users to name.
    /// </summary>
    /// <param name="file">The bookmark file.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock for file time stamps; the system clock by default.</param>
    /// <returns>The store. Nothing is read until it is first used.</returns>
    public static BookmarkStore Create(string file, ILogger logger, TimeProvider? timeProvider = null)
        => Create(file, Migrations(StandaloneMigration(file, logger)), logger, timeProvider);

    /// <summary>
    /// Creates the 1→2 step for a file outside the plugin's services (see <see cref="Create(string, ILogger, TimeProvider)"/>).
    /// </summary>
    /// <param name="file">The bookmark file.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The step.</returns>
    public static BookmarkStoreMigration1To2 StandaloneMigration(string file, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        var folder = Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".";
        return new BookmarkStoreMigration1To2(BookmarkPaths.ForRoot(folder).DefaultClipsDirectory, () => null, _ => null, logger);
    }

    /// <summary>
    /// Creates the kit document store exactly as Bookmarks uses it: the current schema version, the given migrations, the
    /// kit's serializer options, and <see cref="AfterRestoreAtLoad"/> for data restored from a snapshot at load. The store
    /// conformance tests use it too.
    /// </summary>
    /// <param name="file">The bookmark file.</param>
    /// <param name="migrations">The upgrade steps.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock for file time stamps; the system clock by default.</param>
    /// <returns>The document store.</returns>
    public static JsonDocumentStore<BookmarkStoreDocument> CreateDocumentStore(
        string file,
        IReadOnlyList<IJsonMigration> migrations,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var folder = Path.GetDirectoryName(Path.GetFullPath(file));
        return new(
            file,
            BookmarkStoreDocument.CurrentVersion,
            BookmarkStoreDocument.CreateEmpty,
            migrations,
            logger,
            JsonStoreFormat.Options,
            timeProvider,
            afterRestoreAtLoad: document => AfterRestoreAtLoad(document, clock.GetUtcNow().UtcDateTime, logger, folder));
    }

    /// <summary>
    /// The step after <c>bookmarks.json</c> is restored from a snapshot at load because it was missing, empty or unreadable,
    /// or by an admin while it was (the kit's <see cref="StoreStatus.RestoredFromSnapshot"/>, <c>RecoverAsync</c>), the same
    /// as after an admin's <i>Restore snapshot</i>
    /// (<see cref="Services.BookmarkAdminTools.AfterRestore"/>, with no data before it): a clip job the snapshot holds
    /// queued or being made is ended as Failed, to be made again, so the clip worker never resumes it at start (it may
    /// have finished after the snapshot: a second clip, or a file no record names). Since 1.2.0 a record naming a
    /// path its clip file left in a rename since names the renamed file again, from the renames' history in the data folder
    /// (<see cref="ClipRenameRelink"/>; a clip folder that is away now is put right at the next start or daily maintenance).
    /// </summary>
    /// <param name="restored">The restored document.</param>
    /// <param name="endedUtc">When the jobs are ended.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="folder">The data folder, whose renames' history is read; none reads none.</param>
    /// <returns>The document to save.</returns>
    internal static BookmarkStoreDocument AfterRestoreAtLoad(BookmarkStoreDocument restored, DateTime endedUtc, ILogger logger, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var document = Services.BookmarkAdminTools.AfterRestore(restored, BookmarkStoreDocument.CreateEmpty(), endedUtc, out var jobsEnded);
        if (jobsEnded > 0)
        {
            logger.LogWarning("The snapshot restored at load had {Count} clip jobs queued or being made; they were marked failed, to be made again", jobsEnded);
        }

        if (string.IsNullOrEmpty(folder))
        {
            return document;
        }

        try
        {
            var history = ClipRenameHistory.Read(folder, PhysicalClipFileSystem.Instance, logger);
            return history.Count == 0 ? document : ClipRenameRelink.Apply(document, null, history, PhysicalClipFileSystem.Instance, logger, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The relink is put right at the next start or daily maintenance (ClipFileOps.RelinkRenamedAsync).
            logger.LogDebug(ex, "Could not name the renamed clip files of the restored data again at load");
            return document;
        }
    }

    /// <summary>
    /// Returns the document with the given records, in the order Bookmarks has always written them.
    /// </summary>
    /// <param name="document">The current document.</param>
    /// <param name="bookmarks">The records by id.</param>
    /// <returns>The new document.</returns>
    public static BookmarkStoreDocument WithBookmarks(BookmarkStoreDocument document, IEnumerable<Bookmark> bookmarks)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { Bookmarks = bookmarks.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).ToList() };
    }

    /// <inheritdoc />
    public void ThrowIfReadOnly() => _store.ThrowIfReadOnly();

    /// <inheritdoc />
    public IReadOnlyList<Bookmark> GetAll() => Index().Values.ToList();

    /// <inheritdoc />
    public IReadOnlyList<Bookmark> GetLive(Guid userId, Guid? itemId)
        => Index().Values
            .Where(b => b.UserId.Equals(userId) && BookmarkRecordRules.IsLive(b) && (itemId is null || b.ItemId.Equals(itemId.Value)))
            .ToList();

    /// <inheritdoc />
    public IReadOnlyList<Bookmark> GetClipRecords(Guid? userId)
        => Index().Values
            .Where(b => b.Clip is not null && (userId is null || b.UserId.Equals(userId.Value)))
            .ToList();

    /// <inheritdoc />
    public Bookmark? Get(Guid id) => Index().GetValueOrDefault(id);

    /// <inheritdoc />
    public async Task AddAsync(Bookmark bookmark, CancellationToken cancellationToken, User? writer = null)
    {
        ArgumentNullException.ThrowIfNull(bookmark);

        ImmutableDictionary<Guid, Bookmark>? written = null;
        var document = await _store.UpdateAsync(
            current =>
            {
                var bookmarks = IndexOf(current);
                if (bookmarks.ContainsKey(bookmark.Id))
                {
                    throw new ArgumentException("A bookmark with this id already exists.", nameof(bookmark));
                }

                written = bookmarks.Add(bookmark.Id, bookmark);
                return WithBookmarks(current, written.Values) with { KnownUsers = KnownUsersAfter(current.KnownUsers, bookmark, writer) };
            },
            cancellationToken).ConfigureAwait(false);
        Remember(document, written);
    }

    /// <inheritdoc />
    public async Task<Bookmark?> UpdateAsync(Guid id, Func<Bookmark, Bookmark> update, CancellationToken cancellationToken, User? writer = null)
    {
        ArgumentNullException.ThrowIfNull(update);

        var change = await UpdateOrRemoveAsync(
            id,
            b => update(b) ?? throw new InvalidOperationException("A bookmark update returned no record; use RemoveAsync to remove one."),
            cancellationToken,
            writer).ConfigureAwait(false);
        return change.After;
    }

    /// <inheritdoc />
    public async Task<BookmarkChange> UpdateOrRemoveAsync(Guid id, Func<Bookmark, Bookmark?> change, CancellationToken cancellationToken, User? writer = null)
    {
        ArgumentNullException.ThrowIfNull(change);

        Bookmark? before = null;
        Bookmark? after = null;
        ImmutableDictionary<Guid, Bookmark>? written = null;
        var document = await _store.UpdateAsync(
            current =>
            {
                before = null;
                after = null;
                written = null;
                var bookmarks = IndexOf(current);
                if (!bookmarks.TryGetValue(id, out var existing))
                {
                    return current;
                }

                before = existing;
                after = change(existing);
                if (ReferenceEquals(after, existing))
                {
                    return current;
                }

                if (after is null)
                {
                    written = bookmarks.Remove(id);
                    return WithBookmarks(current, written.Values);
                }

                if (!after.Id.Equals(id))
                {
                    throw new InvalidOperationException("A bookmark's id can't be changed.");
                }

                written = bookmarks.SetItem(id, after);
                return WithBookmarks(current, written.Values) with { KnownUsers = KnownUsersAfter(current.KnownUsers, after, writer) };
            },
            cancellationToken).ConfigureAwait(false);
        Remember(document, written);
        return new BookmarkChange(before, after);
    }

    /// <inheritdoc />
    public async Task<Bookmark?> RemoveAsync(Guid id, CancellationToken cancellationToken)
        => (await UpdateOrRemoveAsync(id, static _ => null, cancellationToken).ConfigureAwait(false)).Before;

    /// <inheritdoc />
    public Task<BookmarkStoreDocument> UpdateDocumentAsync(Func<BookmarkStoreDocument, BookmarkStoreDocument> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return _store.UpdateAsync(
            current =>
            {
                var next = change(current);
                if (ReferenceEquals(next, current) || next is null)
                {
                    return next!;
                }

                if (!ReferenceEquals(next.Bookmarks, current.Bookmarks))
                {
                    // The last record of an id wins, as when a hand-edited file is read.
                    var byId = new Dictionary<Guid, Bookmark>();
                    foreach (var bookmark in next.Bookmarks)
                    {
                        if (bookmark is not null)
                        {
                            byId[bookmark.Id] = bookmark;
                        }
                    }

                    next = WithBookmarks(next, byId.Values);
                }

                return CheckCaps(next);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AddClipRootAsync(string folder, CancellationToken cancellationToken)
    {
        if (!ClipRootPaths.TryNormalize(folder, out var root))
        {
            throw new PluginException(PluginErrorKind.Invalid, "The clip folder isn't a valid path.");
        }

        // Most calls find it recorded: answer from memory, without the writer lock.
        if (_store.Current.ClipRoots.Contains(root, ClipRootPaths.Comparer))
        {
            return false;
        }

        var added = false;
        await _store.UpdateAsync(
            current =>
            {
                added = false;
                if (current.ClipRoots.Contains(root, ClipRootPaths.Comparer))
                {
                    return current;
                }

                if (current.ClipRoots.Count >= BookmarkStoreDocument.MaxClipRoots)
                {
                    throw new PluginException(
                        PluginErrorKind.Conflict,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Clips have been written to {0} different folders, the most Bookmarks keeps track of. Remove clip folders that are no longer used first.",
                            BookmarkStoreDocument.MaxClipRoots));
                }

                added = true;
                return current with { ClipRoots = [.. current.ClipRoots, root] };
            },
            cancellationToken).ConfigureAwait(false);
        return added;
    }

    /// <inheritdoc />
    public string? GetClipRootToken(string root)
        => ClipRootPaths.TryNormalize(root, out var normalized) ? _store.Current.ClipRootTokens.GetValueOrDefault(normalized) : null;

    /// <inheritdoc />
    public BookmarkUserPreferences? GetPreferences(Guid userId) => _store.Current.UserPreferences.GetValueOrDefault(userId);

    /// <inheritdoc />
    public async Task SetPreferencesAsync(Guid userId, BookmarkUserPreferences? preferences, CancellationToken cancellationToken)
    {
        if (userId.Equals(Guid.Empty))
        {
            throw new ArgumentException("A user id is required.", nameof(userId));
        }

        await _store.UpdateAsync(
            current =>
            {
                // Preferences a later version stored (its Extra) stay when this version sets the ones it knows, and when it goes
                // back to the defaults: then the entry keeps only those, with this version's fields at their defaults.
                var stored = current.UserPreferences.GetValueOrDefault(userId);
                var next = stored?.Extra is null
                    ? preferences
                    : (preferences ?? new BookmarkUserPreferences()) is { Extra: null } known ? known with { Extra = stored.Extra } : preferences;
                if (Equals(stored, next))
                {
                    return current;
                }

                var map = current.UserPreferences.ToImmutableDictionary();
                return current with { UserPreferences = next is null ? map.Remove(userId) : map.SetItem(userId, next) };
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentNames);

        var changed = false;
        await _store.UpdateAsync(
            current =>
            {
                var owners = IndexOf(current).Values.Select(b => b.UserId).Distinct();
                var refreshed = current.KnownUsers.Refresh(owners, currentNames);
                changed = !ReferenceEquals(refreshed, current.KnownUsers);
                return changed ? current with { KnownUsers = refreshed } : current;
            },
            cancellationToken).ConfigureAwait(false);
        return changed;
    }

    /// <inheritdoc />
    public async Task<bool> AddPendingFileDeleteAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var listed = false;
        await _store.UpdateAsync(
            current =>
            {
                var entries = current.PendingFileDeletes.ToList();
                var index = entries.FindIndex(e => string.Equals(e.Path, path, ClipRootPaths.Comparison));
                if (index >= 0)
                {
                    entries[index] = entries[index] with { Attempts = entries[index].Attempts + 1 };
                }
                else if (entries.Count >= BookmarkStoreDocument.MaxPendingFileDeletes)
                {
                    listed = false;
                    return current;
                }
                else
                {
                    entries.Add(new PendingFileDelete { Path = path, SinceUtc = DateTime.UtcNow, Attempts = 1 });
                }

                listed = true;
                return current with { PendingFileDeletes = entries };
            },
            cancellationToken).ConfigureAwait(false);
        return listed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingFileDelete>> UpdatePendingFileDeletesAsync(Func<PendingFileDelete, PendingFileDelete?> retry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(retry);

        var document = await _store.UpdateAsync(
            current =>
            {
                if (current.PendingFileDeletes.Count == 0)
                {
                    return current;
                }

                var changed = false;
                var kept = new List<PendingFileDelete>(current.PendingFileDeletes.Count);
                foreach (var entry in current.PendingFileDeletes)
                {
                    var next = entry is null ? null : retry(entry);
                    changed |= entry is null || !ReferenceEquals(next, entry);
                    if (next is not null)
                    {
                        kept.Add(next);
                    }
                }

                return changed ? current with { PendingFileDeletes = kept } : current;
            },
            cancellationToken).ConfigureAwait(false);
        return document.PendingFileDeletes;
    }

    /// <inheritdoc />
    public void Dispose() => _store.Dispose();

    // "Refreshed on writes": the owner's current name, when the owner makes the change.
    private static KnownUsers KnownUsersAfter(KnownUsers known, Bookmark bookmark, User? writer)
        => writer is not null && writer.Id.Equals(bookmark.UserId) ? known.With(writer) : known;

    private static BookmarkStoreDocument CheckCaps(BookmarkStoreDocument document)
    {
        if (document.ClipRoots.Count > BookmarkStoreDocument.MaxClipRoots)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"A bookmark document records at most {BookmarkStoreDocument.MaxClipRoots} clip folders."));
        }

        if (document.PendingFileDeletes.Count > BookmarkStoreDocument.MaxPendingFileDeletes)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"A bookmark document lists at most {BookmarkStoreDocument.MaxPendingFileDeletes} pending file deletes."));
        }

        return document;
    }

    private ImmutableDictionary<Guid, Bookmark> Index() => IndexOf(_store.Current);

    // The index of a document, cached for the latest one. A file edited by hand may list an id twice; as in 1.0,
    // the last one wins and the next write drops the others.
    private ImmutableDictionary<Guid, Bookmark> IndexOf(BookmarkStoreDocument document)
    {
        var cached = _index;
        if (cached is not null && ReferenceEquals(cached.Document, document))
        {
            return cached.Bookmarks;
        }

        var builder = ImmutableDictionary.CreateBuilder<Guid, Bookmark>();
        foreach (var bookmark in document.Bookmarks)
        {
            if (bookmark is not null)
            {
                builder[bookmark.Id] = bookmark;
            }
        }

        var bookmarks = builder.ToImmutable();
        _index = new BookmarkIndex(document, bookmarks);
        return bookmarks;
    }

    private void Remember(BookmarkStoreDocument document, ImmutableDictionary<Guid, Bookmark>? bookmarks)
    {
        if (bookmarks is not null)
        {
            _index = new BookmarkIndex(document, bookmarks);
        }
    }

    private sealed record BookmarkIndex(BookmarkStoreDocument Document, ImmutableDictionary<Guid, Bookmark> Bookmarks);
}
