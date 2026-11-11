using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Implements <see cref="IBookmarkClips"/> over <see cref="ClipQueryService"/> (the list and what each clip is),
/// <see cref="ClipLibraryLocations"/> (the In library badge), <see cref="ClipFileOps"/> (the delete) and
/// <see cref="ClipFileRenamer"/> (the renames), and maps the records to DTOs itself.
/// </summary>
/// <remarks>
/// <para><b>Only the caller's clips</b>: every list and lookup starts from the caller's records; an id from the
/// client that names another user's record, a record without a clip or nothing at all is the same everywhere (left out, or
/// <c>NotFound</c>).</para>
/// <para><b>What comes from the library follows the viewer</b>: <c>ItemAvailable</c> is Jellyfin's own check as the
/// caller (<c>GetItemById&lt;Video&gt;(id, user)</c>) once per distinct video on the page, never a user-less lookup,
/// never kept past the request, and never made for a detached record or an adopted clip (no library item); a library's
/// name is given only when the caller can open that library (the libraries they may open, read once per request when a
/// clip on the page is in one). A clip keeps its own stored name and file whatever the caller may open.</para>
/// </remarks>
internal sealed class ClipService : IBookmarkClips
{
    /// <summary>
    /// The most ids one status request or bulk delete takes.
    /// </summary>
    public const int MaxIds = 200;

    /// <summary>
    /// The 400 message of a status request without ids.
    /// </summary>
    public const string StatusNoIdsMessage = "Ask for at least one clip.";

    /// <summary>
    /// The 400 message of a status request of more than <see cref="MaxIds"/> ids.
    /// </summary>
    public const string StatusTooManyMessage = "At most 200 clips can be asked for at once.";

    /// <summary>
    /// The 400 message of a bulk delete without ids.
    /// </summary>
    public const string BulkDeleteNoIdsMessage = "Choose at least one clip to delete.";

    /// <summary>
    /// The 400 message of a bulk delete of more than <see cref="MaxIds"/> ids.
    /// </summary>
    public const string BulkDeleteTooManyMessage = "At most 200 clips can be deleted at once.";

    private readonly IBookmarkStore _store;
    private readonly ClipQueryService _query;
    private readonly ClipFileOps _files;
    private readonly IClipExtractionService _clips;
    private readonly ClipLibraryLocations _locations;
    private readonly ILibraryManager _library;
    private readonly LibraryAccess _libraries;
    private readonly ClipFileRenamer _renamer;
    private readonly ILogger<ClipService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipService"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="query">The clip list.</param>
    /// <param name="files">The clip file operations.</param>
    /// <param name="clips">The clip worker, for the progress of running clips.</param>
    /// <param name="locations">The libraries' folders, for the In library badge.</param>
    /// <param name="library">The library, asked only for <c>ItemAvailable</c>, as the caller.</param>
    /// <param name="libraries">The libraries the caller may open, for a library's name.</param>
    /// <param name="logger">The logger.</param>
    public ClipService(
        IBookmarkStore store,
        ClipQueryService query,
        ClipFileOps files,
        IClipExtractionService clips,
        ClipLibraryLocations locations,
        ILibraryManager library,
        LibraryAccess libraries,
        ILogger<ClipService> logger)
    {
        _store = store;
        _query = query;
        _files = files;
        _clips = clips;
        _locations = locations;
        _library = library;
        _libraries = libraries;
        _logger = logger;
        _renamer = new ClipFileRenamer(store, files, logger);
    }

    /// <inheritdoc />
    public QueryResult<BookmarkClipItemDto> GetClips(User user, BookmarkClipQueryFilter filter, BookmarkClipSortBy? sortBy)
    {
        ArgumentNullException.ThrowIfNull(user);

        var page = _query.Query(_query.Rows(user.Id), filter, sortBy);
        var viewer = new ClipViewer(_library, _libraries, _logger, user);
        var items = page.Rows.Select(row => ToItemDto(row, viewer)).ToList();
        return new QueryResult<BookmarkClipItemDto>(page.StartIndex, page.Total, items);
    }

    /// <inheritdoc />
    public BookmarkClipSummaryDto GetSummary(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _query.Summarize(_query.Rows(user.Id));
    }

    /// <inheritdoc />
    public IReadOnlyList<BookmarkClipStatusDto> GetStatus(User user, IReadOnlyList<Guid>? ids)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (ids is not { Count: > 0 })
        {
            throw BookmarkException.Invalid(StatusNoIdsMessage);
        }

        if (ids.Count > MaxIds)
        {
            throw BookmarkException.Invalid(StatusTooManyMessage);
        }

        // The caller's own clips only; anything else is left out, so nothing is learned about other users' records. The state
        // only: a clip being made (what the page follows) asks the file system nothing.
        var rows = _query.Rows(ids.Distinct()
            .Select(_store.Get)
            .Where(record => record is { Clip: not null } && record.UserId.Equals(user.Id))
            .Select(record => record!));
        return rows.Select(row => BookmarkDtoMapper.ToClipStatusDto(row.Record, row.State, _clips.GetProgress(row.Record.Id))).ToList();
    }

    /// <inheritdoc />
    public async Task<BookmarkClipBulkDeleteResultDto> BulkDeleteAsync(User user, BookmarkClipBulkDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Ids is not { Count: > 0 } given)
        {
            throw BookmarkException.Invalid(BulkDeleteNoIdsMessage);
        }

        if (given.Count > MaxIds)
        {
            throw BookmarkException.Invalid(BulkDeleteTooManyMessage);
        }

        // Read-only data is the store's 503 before anything is looked up or touched, as for every change.
        _store.ThrowIfReadOnly();

        // Each clip as DELETE /Bookmarks/{id}/Clip deletes a finished one: the file first; then a live bookmark keeps
        // no clip, and a tombstone, which is only its clip, goes. A clip being made (a job waiting or running, as the
        // store's lock sees it) is left exactly as it is, its job and its earlier file too, and reported InProgress
        // (cancel it, or wait for it, first), so no job is stopped and none can finish after the answer said its clip
        // was gone. A record that is no longer the caller's or has no clip any more when the write sees it is left
        // alone: not found.
        var ids = given.Distinct().ToList();

        // A rename of one of their files not finished is finished first; one still waiting holds its paths, and
        // its clip is reported Failed with nothing touched.
        if (ids.Exists(_files.HasUnfinishedRename))
        {
            await _files.FinishRenamesAsync(null, cancellationToken).ConfigureAwait(false);
        }

        var outcome = await ClipFileBatchDelete.RunAsync(
            _store,
            _files,
            ids,
            b => b.UserId.Equals(user.Id) && b.Clip is not null,
            static b => BookmarkRecordRules.IsTombstone(b) ? null : b with { Clip = null },
            static _ =>
            {
                // No job to stop: a clip being made is never changed here.
            },
            static b => b.Clip is { Status: BookmarkClipStatus.Queued or BookmarkClipStatus.Running },
            requireRecognisedFolder: true,
            cancellationToken).ConfigureAwait(false);

        var result = Result(ids, outcome);
        _logger.LogDebug(
            "User {UserId} deleted {Deleted} clips at once, {Removed} of them of deleted bookmarks; {NotFound} not found, {InProgress} being made, {InUse} in use, {Offline} in a clip folder that isn't available, {Failed} that couldn't be deleted",
            user.Id,
            result.FilesDeleted.Count + result.RecordsRemoved.Count,
            result.RecordsRemoved.Count,
            result.NotFound.Count,
            result.InProgress.Count,
            result.InUse.Count,
            result.FolderUnavailable.Count,
            result.Failed.Count);
        return result;
    }

    /// <inheritdoc />
    public async Task<BookmarkClipItemDto> RenameAsync(User user, Guid clipId, BookmarkClipRenameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        // Read-only data first (503, the same for every id), then the name's 400, then the clip.
        _store.ThrowIfReadOnly();
        var name = BookmarkNames.NormalizeName(request.Name);
        var record = await _renamer.RenameAsync(user, clipId, changeName: true, name, request.RenameFile, request.IfUpdatedAt, cancellationToken).ConfigureAwait(false);
        return ItemOf(record, user);
    }

    /// <inheritdoc />
    public async Task<BookmarkClipItemDto> RenameFileAsync(User user, Guid clipId, DateTime? ifUpdatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var record = await _renamer.RenameAsync(user, clipId, changeName: false, name: null, renameFile: true, ifUpdatedAt, cancellationToken).ConfigureAwait(false);
        return ItemOf(record, user);
    }

    // One clip as the list maps it, judged afresh (the rename forgot its paths' verdicts).
    private BookmarkClipItemDto ItemOf(Bookmark record, User user)
        => ToItemDto(_query.Rows([record])[0], new ClipViewer(_library, _libraries, _logger, user));

    // Every distinct id in exactly one list, in the order the request gave them (the admins' bulk delete answers the
    // same way).
    internal static BookmarkClipBulkDeleteResultDto Result(IReadOnlyList<Guid> ids, ClipFileBatchOutcome outcome)
    {
        bool Changed(Guid id, bool tombstone) => outcome.Changed.TryGetValue(id, out var before) && BookmarkRecordRules.IsTombstone(before) == tombstone;
        bool Refused(Guid id, Func<ClipFileResult, bool> kind) => outcome.Refused.TryGetValue(id, out var refused) && kind(refused);
        static bool InUse(ClipFileResult r) => r.Outcome == ClipFileOutcome.InUse;
        // A clip folder that isn't there, or one that is there but isn't recognised as the clips' own.
        static bool Offline(ClipFileResult r) => r.Outcome == ClipFileOutcome.Failed && (r.Check.Verdict == ClipPathVerdict.FolderUnavailable || r.Check.RootUnrecognised);

        return new BookmarkClipBulkDeleteResultDto
        {
            FilesDeleted = ids.Where(id => Changed(id, tombstone: false)).ToList(),
            RecordsRemoved = ids.Where(id => Changed(id, tombstone: true)).ToList(),
            InProgress = ids.Where(outcome.InProgress.Contains).ToList(),
            InUse = ids.Where(id => Refused(id, InUse)).ToList(),
            FolderUnavailable = ids.Where(id => Refused(id, Offline)).ToList(),
            Failed = ids.Where(id => Refused(id, r => !InUse(r) && !Offline(r))).ToList(),
            NotFound = ids.Where(id => !outcome.Changed.ContainsKey(id) && !outcome.Refused.ContainsKey(id) && !outcome.InProgress.Contains(id)).ToList()
        };
    }

    private BookmarkClipItemDto ToItemDto(ClipRow row, ClipViewer viewer)
    {
        // The In library badge from the libraries' folders; the library's name only for a library the viewer may
        // open.
        var location = row.Clip.FilePath is { Length: > 0 } path ? _locations.Find(path) : null;
        var libraryName = location is { LibraryId: Guid libraryId } && viewer.CanOpenLibrary(libraryId) ? location.LibraryName : null;
        return BookmarkDtoMapper.ToClipItemDto(row.Record, row.Facts, _clips.GetProgress(row.Record.Id), viewer.CanOpen(row.Record), location is not null, libraryName);
    }
}
