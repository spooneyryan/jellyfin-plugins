using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Bookmarks.Tests.TestSupport;

/// <summary>
/// A real <see cref="BookmarkStore"/> with a hook that runs once, just before the next write of one record
/// (<see cref="IBookmarkStore.UpdateAsync"/> or <see cref="IBookmarkStore.UpdateOrRemoveAsync"/>) takes the store's lock: the
/// moment another request, the worker or a remap changes the record after the caller looked it up (what the
/// store's race guards are for). Everything else goes straight to the real store.
/// </summary>
internal sealed class InterferingBookmarkStore : IBookmarkStore
{
    private readonly BookmarkStore _inner;
    private Func<Guid, Task>? _beforeNextWrite;
    private Func<Task>? _beforeNextDocumentWrite;

    public InterferingBookmarkStore(BookmarkStore inner)
    {
        _inner = inner;
    }

    /// <summary>Gets how many single-record writes went through (the interfering one not counted).</summary>
    public int RecordWrites { get; private set; }

    /// <summary>Gets how many document writes were asked for (<see cref="IBookmarkStore.UpdateDocumentAsync"/>; the batches).</summary>
    public int DocumentWrites { get; private set; }

    /// <summary>Gets or sets what runs once before the next document write takes the store's lock (a user's edit meanwhile).</summary>
    public Func<Task>? BeforeNextDocumentWrite
    {
        get => _beforeNextDocumentWrite;
        set => _beforeNextDocumentWrite = value;
    }

    public bool IsReadOnly => _inner.IsReadOnly;

    public BookmarkStoreReadOnlyReason? ReadOnlyReason => _inner.ReadOnlyReason;

    public JsonDocumentStore<BookmarkStoreDocument> Documents => _inner.Documents;

    public IReadOnlyList<string> ClipRoots => _inner.ClipRoots;

    public IReadOnlyDictionary<string, BookmarkImportLedgerEntry> ImportLedger => _inner.ImportLedger;

    public IReadOnlyDictionary<string, BookmarkImportRun> ImportRuns => _inner.ImportRuns;

    public KnownUsers KnownUsers => _inner.KnownUsers;

    public IReadOnlyList<PendingFileDelete> PendingFileDeletes => _inner.PendingFileDeletes;

    /// <summary>
    /// Runs <paramref name="interference"/> (given the record's id) before the next single-record write, once.
    /// </summary>
    /// <param name="interference">What happens in between, typically a write through the real store.</param>
    public void BeforeNextWriteRun(Func<Guid, Task> interference) => _beforeNextWrite = interference;

    /// <summary>
    /// Changes the record through the real store before the next single-record write, once.
    /// </summary>
    /// <param name="change">The change another party makes; <c>null</c> removes the record.</param>
    public void BeforeNextWrite(Func<Bookmark, Bookmark?> change)
        => BeforeNextWriteRun(id => _inner.UpdateOrRemoveAsync(id, change, CancellationToken.None));

    public void ThrowIfReadOnly() => _inner.ThrowIfReadOnly();

    public IReadOnlyList<Bookmark> GetAll() => _inner.GetAll();

    public IReadOnlyList<Bookmark> GetLive(Guid userId, Guid? itemId) => _inner.GetLive(userId, itemId);

    public IReadOnlyList<Bookmark> GetClipRecords(Guid? userId) => _inner.GetClipRecords(userId);

    public Bookmark? Get(Guid id) => _inner.Get(id);

    public Task AddAsync(Bookmark bookmark, CancellationToken cancellationToken, User? writer = null) => _inner.AddAsync(bookmark, cancellationToken, writer);

    public async Task<Bookmark?> UpdateAsync(Guid id, Func<Bookmark, Bookmark> update, CancellationToken cancellationToken, User? writer = null)
    {
        await InterfereAsync(id).ConfigureAwait(false);
        RecordWrites++;
        return await _inner.UpdateAsync(id, update, cancellationToken, writer).ConfigureAwait(false);
    }

    public async Task<BookmarkChange> UpdateOrRemoveAsync(Guid id, Func<Bookmark, Bookmark?> change, CancellationToken cancellationToken, User? writer = null)
    {
        await InterfereAsync(id).ConfigureAwait(false);
        RecordWrites++;
        return await _inner.UpdateOrRemoveAsync(id, change, cancellationToken, writer).ConfigureAwait(false);
    }

    public Task<Bookmark?> RemoveAsync(Guid id, CancellationToken cancellationToken) => _inner.RemoveAsync(id, cancellationToken);

    public async Task<BookmarkStoreDocument> UpdateDocumentAsync(Func<BookmarkStoreDocument, BookmarkStoreDocument> change, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _beforeNextDocumentWrite, null) is { } interference)
        {
            await interference().ConfigureAwait(false);
        }

        DocumentWrites++;
        return await _inner.UpdateDocumentAsync(change, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AddClipRootAsync(string folder, CancellationToken cancellationToken) => _inner.AddClipRootAsync(folder, cancellationToken);

    public string? GetClipRootToken(string root) => _inner.GetClipRootToken(root);

    public BookmarkUserPreferences? GetPreferences(Guid userId) => _inner.GetPreferences(userId);

    public Task SetPreferencesAsync(Guid userId, BookmarkUserPreferences? preferences, CancellationToken cancellationToken)
        => _inner.SetPreferencesAsync(userId, preferences, cancellationToken);

    public Task<bool> RefreshKnownUsersAsync(IReadOnlyDictionary<Guid, string> currentNames, CancellationToken cancellationToken)
        => _inner.RefreshKnownUsersAsync(currentNames, cancellationToken);

    public Task<bool> AddPendingFileDeleteAsync(string path, CancellationToken cancellationToken) => _inner.AddPendingFileDeleteAsync(path, cancellationToken);

    public Task<IReadOnlyList<PendingFileDelete>> UpdatePendingFileDeletesAsync(Func<PendingFileDelete, PendingFileDelete?> retry, CancellationToken cancellationToken)
        => _inner.UpdatePendingFileDeletesAsync(retry, cancellationToken);

    private async Task InterfereAsync(Guid id)
    {
        var interference = Interlocked.Exchange(ref _beforeNextWrite, null);
        if (interference is not null)
        {
            await interference(id).ConfigureAwait(false);
        }
    }
}
