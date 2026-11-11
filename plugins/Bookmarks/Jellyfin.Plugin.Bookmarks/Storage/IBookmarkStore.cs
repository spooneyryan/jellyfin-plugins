using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Bookmarks.Storage;

/// <summary>
/// Persistent storage for bookmarks, schema 2: keyed access to the records, and the document's other
/// fields. Reads are served from memory; writes are serialized, one file write each.
/// </summary>
/// <remarks>
/// Records are live bookmarks or clip-only tombstones (<see cref="BookmarkRecordRules.IsTombstone"/>). Bookmark
/// lists (<see cref="GetLive"/>) leave tombstones out; clip lists (<see cref="GetClipRecords"/>) and <see cref="Get"/>
/// include them, so callers apply the tombstone rules themselves.
/// </remarks>
internal interface IBookmarkStore
{
    /// <summary>
    /// Gets a value indicating whether the store refuses every write: the file was written by a newer plugin version,
    /// or upgrading it failed. Writes then throw a 503 <c>PluginException</c>, and background writers stay idle
    /// (<see cref="BackgroundWriteGate"/>). Decided when the store first loads; it stays until the server restarts.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets why the store is read-only (<see cref="IsReadOnly"/>), or <c>null</c> while it is writable.
    /// </summary>
    BookmarkStoreReadOnlyReason? ReadOnlyReason { get; }

    /// <summary>
    /// Gets the kit document store underneath, for its status on the Diagnostics tab and its snapshots.
    /// </summary>
    JsonDocumentStore<BookmarkStoreDocument> Documents { get; }

    /// <summary>
    /// Gets every clip folder recorded.
    /// </summary>
    IReadOnlyList<string> ClipRoots { get; }

    /// <summary>
    /// Gets the import ledger.
    /// </summary>
    IReadOnlyDictionary<string, BookmarkImportLedgerEntry> ImportLedger { get; }

    /// <summary>
    /// Gets the applied imports by source. The Jellyfin Enhanced import writes them, with the
    /// records and the ledger, in one <see cref="UpdateDocumentAsync"/>.
    /// </summary>
    IReadOnlyDictionary<string, BookmarkImportRun> ImportRuns { get; }

    /// <summary>
    /// Gets the last seen names of the users who own records.
    /// </summary>
    KnownUsers KnownUsers { get; }

    /// <summary>
    /// Gets the old clip files waiting to be deleted.
    /// </summary>
    IReadOnlyList<PendingFileDelete> PendingFileDeletes { get; }

    /// <summary>
    /// Throws the 503 <c>PluginException</c> every write throws while the store is read-only (<see cref="IsReadOnly"/>),
    /// with the store's message, and does nothing while it is writable. A request's write that looks its record up first
    /// calls it before the lookup: after a failed upgrade the store serves an empty stand-in, so the lookup would answer
    /// 404 for a bookmark the untouched file still holds (a read-only store's writes answer 503).
    /// </summary>
    void ThrowIfReadOnly();

    /// <summary>
    /// Gets every stored record, tombstones included.
    /// </summary>
    /// <returns>All records, in no particular order.</returns>
    IReadOnlyList<Bookmark> GetAll();

    /// <summary>
    /// Gets a user's live bookmarks, optionally on one item; tombstones are left out.
    /// </summary>
    /// <param name="userId">The owning user.</param>
    /// <param name="itemId">The item to filter by, or <c>null</c> for all items.</param>
    /// <returns>The matching bookmarks, in no particular order.</returns>
    IReadOnlyList<Bookmark> GetLive(Guid userId, Guid? itemId);

    /// <summary>
    /// Gets the records that have a clip, live bookmarks and tombstones alike (the clip lists).
    /// </summary>
    /// <param name="userId">The owning user, or <c>null</c> for every user's.</param>
    /// <returns>The records, in no particular order.</returns>
    IReadOnlyList<Bookmark> GetClipRecords(Guid? userId);

    /// <summary>
    /// Gets a record by id, a tombstone included.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <returns>The record, or <c>null</c> if it doesn't exist.</returns>
    Bookmark? Get(Guid id);

    /// <summary>
    /// Adds a new record.
    /// </summary>
    /// <param name="bookmark">The record; its id must be unused.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <param name="writer">The user who makes it, when that is its owner: their name goes into <see cref="KnownUsers"/>
    /// in the same write ("refreshed on writes").</param>
    /// <returns>A task that completes once the record is persisted.</returns>
    Task AddAsync(Bookmark bookmark, CancellationToken cancellationToken, User? writer = null);

    /// <summary>
    /// Atomically replaces a record with the result of <paramref name="update"/>.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <param name="update">Produces the new version from the current one. Returning the same instance skips the write.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <param name="writer">The user who changes it, when that is its owner (see <see cref="AddAsync"/>).</param>
    /// <returns>The stored record after the update, or <c>null</c> if it doesn't exist.</returns>
    Task<Bookmark?> UpdateAsync(Guid id, Func<Bookmark, Bookmark> update, CancellationToken cancellationToken, User? writer = null);

    /// <summary>
    /// Atomically replaces or removes a record with the result of <paramref name="change"/>: the one write for an operation
    /// that decides between the two only once it sees the record under the lock, such as a delete that keeps a clip-only
    /// tombstone when the record has a clip file and removes it otherwise.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <param name="change">Produces the new version from the current one: the same instance writes nothing, <c>null</c>
    /// removes the record.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <param name="writer">The user who changes it, when that is its owner (see <see cref="AddAsync"/>); not used when the
    /// record is removed.</param>
    /// <returns>The record before and after; both <c>null</c> when it doesn't exist.</returns>
    Task<BookmarkChange> UpdateOrRemoveAsync(Guid id, Func<Bookmark, Bookmark?> change, CancellationToken cancellationToken, User? writer = null);

    /// <summary>
    /// Removes a record.
    /// </summary>
    /// <param name="id">The record id.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <returns>The removed record, or <c>null</c> if it didn't exist.</returns>
    Task<Bookmark?> RemoveAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Changes several records and document fields in one write (a batch of the identity service, a bulk delete, a
    /// deleted user, a move-journal catch-up). The store then orders the records, keeps the last of a repeated id, and
    /// refuses a document over the caps of <see cref="BookmarkStoreDocument"/>.
    /// </summary>
    /// <param name="change">Returns the new document; the same instance writes nothing.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <returns>The document after the change.</returns>
    Task<BookmarkStoreDocument> UpdateDocumentAsync(Func<BookmarkStoreDocument, BookmarkStoreDocument> change, CancellationToken cancellationToken);

    /// <summary>
    /// Records a clip folder, normalised; nothing is written when it is already recorded.
    /// </summary>
    /// <param name="folder">The folder clips are written under (the configured or default one, before the per-user folder).</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <returns><c>true</c> when it was added, <c>false</c> when it was already there.</returns>
    /// <exception cref="Jellyfin.Plugin.Kit.Api.PluginException">400 for a path that can't be a folder; 409 when
    /// <see cref="BookmarkStoreDocument.MaxClipRoots"/> folders are recorded already.</exception>
    Task<bool> AddClipRootAsync(string folder, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the token recorded for a clip folder's marker file (<see cref="BookmarkStoreDocument.ClipRootTokens"/>), or
    /// <c>null</c> when none is. Recorded through <see cref="UpdateDocumentAsync"/>
    /// (<see cref="BookmarkStoreDocument.WithClipRootToken"/>), with the marker file.
    /// </summary>
    /// <param name="root">The clip folder; any spelling of it.</param>
    /// <returns>The token, or <c>null</c>.</returns>
    string? GetClipRootToken(string root);

    /// <summary>
    /// Gets a user's naming preferences.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The preferences, or <c>null</c> when the user has none (the server defaults apply).</returns>
    BookmarkUserPreferences? GetPreferences(Guid userId);

    /// <summary>
    /// Sets or clears a user's naming preferences. Fields a later version stored (<see cref="BookmarkUserPreferences.Extra"/>)
    /// are kept either way: clearing then leaves an entry with only them.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="preferences">The preferences, or <c>null</c> to go back to the server defaults (the entry is removed).</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <returns>A task that completes once they are persisted.</returns>
    Task SetPreferencesAsync(Guid userId, BookmarkUserPreferences? preferences, CancellationToken cancellationToken);

    /// <summary>
    /// The reconciler's refresh of <see cref="KnownUsers"/> (<see cref="KnownUsers.Refresh"/> over the ids that own a
    /// record, tombstones included); nothing is written when no name changed.
    /// </summary>
    /// <param name="currentNames">The server's users, id to name.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <returns>Whether a name changed.</returns>
    Task<bool> RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a file an automatic deletion couldn't delete, or counts one more attempt when it is listed already.
    /// </summary>
    /// <param name="path">The file's absolute path.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <returns><c>false</c> when the list is full (<see cref="BookmarkStoreDocument.MaxPendingFileDeletes"/>) and the path
    /// wasn't listed: the file then stays until something deletes it.</returns>
    Task<bool> AddPendingFileDeleteAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Goes through the pending deletes once (the janitor's retry): <paramref name="retry"/> returns <c>null</c> to drop an
    /// entry (the file is gone) or the entry to keep, for example with one more attempt counted.
    /// </summary>
    /// <param name="retry">Decides each entry.</param>
    /// <param name="cancellationToken">Cancels waiting for the write lock.</param>
    /// <returns>The entries left.</returns>
    Task<IReadOnlyList<PendingFileDelete>> UpdatePendingFileDeletesAsync(Func<PendingFileDelete, PendingFileDelete?> retry, CancellationToken cancellationToken);
}
