using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Clips;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Implements bookmark operations on top of the store, the library and the clip service, and maps the records it
/// returns to DTOs.
/// </summary>
/// <remarks>
/// <para>Clip files: every delete route acts on the file first, through <see cref="ClipFileOps"/>, and
/// changes the record only once the file is gone. A file in use answers 409 with the record and the file unchanged; a path
/// outside the clip folders 404, and nothing is touched; the clip file is served only from the clip folders.</para>
/// <para>Free space: a clip request is planned, checked by <see cref="ClipStorageGuard"/> and only
/// then queued, with its estimate.</para>
/// </remarks>
internal sealed class BookmarkManager : IBookmarkManager
{
    /// <summary>
    /// The 409 message of an edit or rename whose <c>IfUpdatedAt</c> is out of date.
    /// </summary>
    public const string ChangedElsewhereMessage = "Changed elsewhere. Reload to see the latest.";

    /// <summary>
    /// The 409 message of a clip forgotten as missing whose file is there (<c>DELETE /Bookmarks/{id}/Clip?onlyIfMissing=true</c>,
    /// the My Clips page's Forget and Dismiss).
    /// </summary>
    public const string ClipFileIsThereMessage = "The clip file is there after all (made again or put back), so nothing was deleted. Reload to see it.";

    /// <summary>
    /// The 404 message of a clip file route whose clip has no file to serve (none, gone, or outside the clip folders); the
    /// admins' file route answers the same.
    /// </summary>
    public const string NoClipFileMessage = "This bookmark has no clip file.";

    /// <summary>
    /// The longest note, in UTF-16 code units (the capabilities' <c>MaxNoteLength</c>).
    /// </summary>
    public const int MaxNoteLength = 500;

    /// <summary>
    /// The most ids one bulk delete takes.
    /// </summary>
    public const int MaxBulkDelete = 200;

    /// <summary>
    /// The 400 message of a bulk delete without ids.
    /// </summary>
    public const string BulkDeleteNoIdsMessage = "Choose at least one bookmark to delete.";

    /// <summary>
    /// The 400 message of a bulk delete of more than <see cref="MaxBulkDelete"/> ids.
    /// </summary>
    public const string BulkDeleteTooManyMessage = "At most 200 bookmarks can be deleted at once.";

    // Reported positions can land slightly past an item's stated runtime.
    private static readonly long _runtimeToleranceTicks = TimeSpan.FromSeconds(5).Ticks;

    private readonly IBookmarkStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IClipExtractionService _clips;
    private readonly IClipPlanner _planner;
    private readonly ClipFileOps _files;
    private readonly ClipStorageGuard _guard;
    private readonly IPluginConfigurationProvider _configurationProvider;
    private readonly ILogger<BookmarkManager> _logger;
    private readonly ShortcutPolicy? _shortcuts;
    private readonly JellyfinEnhancedBookmarkImporter? _jeImport;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkManager"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="clips">The clip extraction service.</param>
    /// <param name="planner">The clip planner, used to validate clip requests up front.</param>
    /// <param name="files">The clip file operations.</param>
    /// <param name="guard">The free-space guard.</param>
    /// <param name="configurationProvider">The plugin configuration.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="shortcuts">The kit's shortcut policy, for the capabilities' reserved keys; without
    /// one (older tests) none are reported.</param>
    /// <param name="jeImport">The Jellyfin Enhanced import, for the capabilities' hint to admins; without one
    /// (older tests) there is none.</param>
    public BookmarkManager(
        IBookmarkStore store,
        ILibraryManager libraryManager,
        IClipExtractionService clips,
        IClipPlanner planner,
        ClipFileOps files,
        ClipStorageGuard guard,
        IPluginConfigurationProvider configurationProvider,
        ILogger<BookmarkManager> logger,
        ShortcutPolicy? shortcuts = null,
        JellyfinEnhancedBookmarkImporter? jeImport = null)
    {
        _store = store;
        _libraryManager = libraryManager;
        _clips = clips;
        _planner = planner;
        _files = files;
        _guard = guard;
        _configurationProvider = configurationProvider;
        _logger = logger;
        _shortcuts = shortcuts;
        _jeImport = jeImport;
    }

    /// <inheritdoc />
    public IReadOnlyList<BookmarkDto> GetBookmarks(User user, Guid? itemId)
    {
        ArgumentNullException.ThrowIfNull(user);

        var bookmarks = _store.GetLive(user.Id, itemId);
        var ordered = itemId is null
            ? bookmarks.OrderByDescending(b => b.UpdatedAt)
            : bookmarks.OrderBy(b => b.StartTicks).ThenBy(b => b.EndTicks ?? b.StartTicks);
        return ordered.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public BookmarkDto GetBookmark(User user, Guid bookmarkId) => ToDto(GetOwnedBookmark(user, bookmarkId));

    /// <inheritdoc />
    public async Task<BookmarkDto> CreateBookmarkAsync(User user, BookmarkCreateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ItemId.Equals(Guid.Empty))
        {
            throw BookmarkException.Invalid("An item id is required.");
        }

        var item = FindVideo(request.ItemId, user)
            ?? throw BookmarkException.NotFound("Video not found.");
        ValidateRange(item, request.StartTicks, request.EndTicks);
        var name = BookmarkNames.NormalizeName(request.Name);
        var note = NormalizeNote(request.Note);

        var now = DateTime.UtcNow;
        var bookmark = new Bookmark
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ItemId = item.Id,
            MediaSourceId = string.IsNullOrWhiteSpace(request.MediaSourceId) ? null : request.MediaSourceId.Trim(),
            AudioStreamIndex = request.AudioStreamIndex >= 0 ? request.AudioStreamIndex : null,
            StartTicks = request.StartTicks,
            EndTicks = request.EndTicks,
            Name = name,
            Note = note,
            ItemName = GetDisplayName(item),

            // What the bookmark remembers about its video, and since when it is on it.
            Identity = ItemIdentity.Capture(item, _libraryManager),
            Origin = BookmarkOrigin.Native,
            AttachedUtc = now,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _store.AddAsync(bookmark, cancellationToken, user).ConfigureAwait(false);

        // Ids and positions only: names and notes never reach the log at any level.
        _logger.LogDebug("User {UserId} bookmarked {ItemId} at {StartTicks}{Named}", user.Id, item.Id, bookmark.StartTicks, name is null ? string.Empty : ", named");
        return ToDto(bookmark);
    }

    /// <inheritdoc />
    public async Task<BookmarkDto> UpdateBookmarkAsync(User user, Guid bookmarkId, BookmarkUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var existing = GetOwnedBookmarkToChange(user, bookmarkId);

        // The item may have left the library, or never been in it (an adopted clip); editing is still allowed, just
        // without the runtime check.
        var item = BookmarkRecordRules.HasLibraryItem(existing) ? FindVideo(existing.ItemId, user) : null;
        ValidateRange(item, request.StartTicks, request.EndTicks);

        // Replace semantics: a name or note left out is cleared, and null never means "keep". Except in
        // Bookmarks 1.0's own body, which has neither Name nor IfUpdatedAt (every 1.1 client sends Name): a browser tab still
        // running 1.0's script after the upgrade doesn't show the name the upgrade made from the note, so saving there would
        // clear it, and after a reload of its list the text with it. Such a body keeps the stored name.
        var keepName = !request.NameSent && request.IfUpdatedAt is null;
        var name = keepName ? null : BookmarkNames.NormalizeName(request.Name);
        var note = NormalizeNote(request.Note);

        var updated = await WriteEditAsync(
            user,
            bookmarkId,
            request.IfUpdatedAt,
            (b, updatedAt) => b with
            {
                StartTicks = request.StartTicks,
                EndTicks = request.EndTicks,
                Name = keepName ? b.Name : name,
                Note = note,
                RelinkedUtc = null,
                RelinkStrength = null,
                UpdatedAt = updatedAt
            },
            cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("User {UserId} updated bookmark {BookmarkId}", user.Id, bookmarkId);
        return ToDto(updated);
    }

    /// <inheritdoc />
    public async Task<BookmarkDto> RenameBookmarkAsync(User user, Guid bookmarkId, BookmarkRenameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        GetOwnedBookmarkToChange(user, bookmarkId);
        var name = BookmarkNames.NormalizeName(request.Name);

        // Only the name, and always a change: the same name again is how the "Relinked, check position" badge's "Looks
        // right" clears it. A clip file keeps its name.
        var renamed = await WriteEditAsync(
            user,
            bookmarkId,
            request.IfUpdatedAt,
            (b, updatedAt) => b with { Name = name, RelinkedUtc = null, RelinkStrength = null, UpdatedAt = updatedAt },
            cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("User {UserId} {Action} bookmark {BookmarkId}", user.Id, name is null ? "unnamed" : "renamed", bookmarkId);
        return ToDto(renamed);
    }

    /// <inheritdoc />
    public async Task DeleteBookmarkAsync(User user, Guid bookmarkId, bool deleteClipFile, CancellationToken cancellationToken)
    {
        GetOwnedBookmarkToChange(user, bookmarkId);

        BookmarkChange change;
        if (deleteClipFile)
        {
            // With its clip, the record goes (as in 1.0), but the file first: a file in use is 409, and the
            // bookmark, its clip job and its file stay as they were. A queued job goes with the record; a running one is
            // stopped below. A file gone from a clip folder that is there but isn't recognised as the clips' own is 503 with
            // nothing changed, as on the clip routes: taking it for gone removed the only record of a file that is
            // probably fine in the real folder, which then came back with nothing pointing at it (1.0's defect: it lost the
            // record but kept the file).
            change = await DeleteClipFileAndWriteAsync(
                user,
                bookmarkId,
                onlyIfMissing: false,
                requireRecognisedFolder: true,
                b => BookmarkRecordRules.IsLiveBookmarkOf(b, user.Id),
                static _ => null,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Tombstone creation. 1: cancel the clip job.
            await _clips.CancelAsync(bookmarkId, cancellationToken).ConfigureAwait(false);

            // 2 and 3, in one write, with the record as the store's lock sees it: a record with a clip file becomes a clip-only
            // tombstone, one without is removed. A record that became a tombstone (another delete) or another user's since
            // the check above is left as it is: 404.
            var now = DateTime.UtcNow;
            change = await _store.UpdateOrRemoveAsync(
                bookmarkId,
                b => BookmarkRecordRules.IsLiveBookmarkOf(b, user.Id) ? BookmarkRecordRules.ToTombstone(b, now) : b,
                cancellationToken,
                user).ConfigureAwait(false);
        }

        if (!change.Changed)
        {
            throw BookmarkException.NotFound("Bookmark not found.");
        }

        // A job claimed between the cancellation and the write still runs: stop it. Its worker records nothing on a
        // tombstone or a removed record, and discards the file it may still finish.
        _clips.StopRunning(bookmarkId);
        _logger.LogDebug(
            "User {UserId} deleted bookmark {BookmarkId}{Outcome}",
            user.Id,
            bookmarkId,
            change.Removed ? string.Empty : "; its clip file is kept");
    }

    /// <inheritdoc />
    public async Task<BookmarkBulkDeleteResultDto> BulkDeleteAsync(User user, BookmarkBulkDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Ids is not { Count: > 0 } given)
        {
            throw BookmarkException.Invalid(BulkDeleteNoIdsMessage);
        }

        if (given.Count > MaxBulkDelete)
        {
            throw BookmarkException.Invalid(BulkDeleteTooManyMessage);
        }

        // Read-only data is the store's 503 before anything is looked up or touched, as for every change (see
        // GetOwnedBookmarkToChange).
        _store.ThrowIfReadOnly();

        // Each write stops the jobs of the bookmarks it removed or made tombstones (StopRunningAfterWrite), so a request
        // aborted between the writes of a delete with the clips leaves no job running for a bookmark already gone.
        var ids = given.Distinct().ToList();
        var result = request.DeleteClips
            ? await BulkDeleteWithClipsAsync(user, ids, cancellationToken).ConfigureAwait(false)
            : await BulkDeleteKeepingClipsAsync(user, ids, cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "User {UserId} deleted {Deleted} bookmarks at once, {Tombstoned} of them keeping their clip files; {NotFound} not found, {InUse} with a clip file in use, {Failed} with a clip file that couldn't be deleted",
            user.Id,
            result.Deleted.Count + result.Tombstoned.Count,
            result.Tombstoned.Count,
            result.NotFound.Count,
            result.InUse.Count,
            result.Failed.Count);
        return result;
    }

    /// <inheritdoc />
    public async Task<BookmarkDto> StartClipAsync(User user, Guid bookmarkId, BookmarkClipMode? mode, bool isAdmin, CancellationToken cancellationToken)
    {
        EnsureCanCreateClips(user);

        var bookmark = GetOwnedBookmarkToChange(user, bookmarkId);
        if (bookmark.EndTicks is null)
        {
            throw BookmarkException.Invalid("Set an end time on the bookmark to make a clip.");
        }

        // A rename of its clip file a crash or a failed write left half done is finished first: a remake deletes
        // the file its record names afterwards, and the rename's file would be no clip's.
        await FinishClipRenameAsync(bookmarkId, cancellationToken).ConfigureAwait(false);

        var item = (BookmarkRecordRules.HasLibraryItem(bookmark) ? FindVideo(bookmark.ItemId, user) : null)
            ?? throw BookmarkException.NotFound("The bookmarked video is no longer in the library.");
        if (!item.CanDownload(user))
        {
            throw BookmarkException.Invalid("Clips can only be made from videos that can be downloaded.");
        }

        var clipMode = mode ?? _configurationProvider.GetConfiguration().DefaultClipMode;
        ClipPlan plan;
        try
        {
            // Plan now so problems (too long, missing file, disc image, no ffmpeg) are reported straight
            // away instead of surfacing later as a failed job.
            plan = _planner.Plan(bookmark, clipMode);
        }
        catch (ClipExtractionException ex)
        {
            // A clip folder that can't be reached while the file's name is chosen is the server's 503, not the request's 400,
            // and is said once, without the folder (a Debug line names the error).
            if (ex.FolderUnreachable)
            {
                _logger.LogWarning("Could not plan the clip of bookmark {BookmarkId}: its clip folder can't be reached ({Error})", bookmarkId, ex.InnerException?.InnerException?.GetType().Name ?? "IOException");
                _logger.LogDebug(ex.InnerException, "Could not plan the clip of bookmark {BookmarkId}", bookmarkId);
                throw new BookmarkException(PluginErrorKind.Unavailable, ex.Message);
            }

            throw new BookmarkException(ex.Message, ex);
        }

        // The free-space guard, with the plan kept: the clip's estimate, and the folder it goes to (a
        // per-user folder that doesn't exist yet is measured by the nearest folder above it). Refused: 503, with the drive and
        // the numbers for an admin only, and nothing queued or written.
        var space = await _guard.CheckBeforeQueueAsync(plan.OutputFolder, plan.EstimatedBytes, bookmarkId, cancellationToken).ConfigureAwait(false);
        if (space.Refused)
        {
            throw new BookmarkException(PluginErrorKind.Unavailable, isAdmin ? space.AdminMessage : space.UserMessage);
        }

        // The queue checks again under the store's lock: a bookmark deleted (made a tombstone) or given to another user
        // since the check above gets no job, and is 404. The job keeps the estimate.
        var queued = await _clips.EnqueueAsync(bookmarkId, user.Id, clipMode, plan.EstimatedBytes, cancellationToken).ConfigureAwait(false);
        return queued is not null ? ToDto(queued) : throw BookmarkException.NotFound("Bookmark not found.");
    }

    /// <inheritdoc />
    public async Task<BookmarkDto> CancelClipAsync(User user, Guid bookmarkId, CancellationToken cancellationToken)
    {
        GetOwnedBookmarkToChange(user, bookmarkId);

        // The previous clip file, if any, stays; a running job reports the cancellation asynchronously. A tombstone is 404
        // too, also when the bookmark became one meanwhile; the service never changes a tombstone's clip.
        await _clips.CancelAsync(bookmarkId, cancellationToken).ConfigureAwait(false);
        return _store.Get(bookmarkId) is { } bookmark && BookmarkRecordRules.IsLiveBookmarkOf(bookmark, user.Id)
            ? ToDto(bookmark)
            : throw BookmarkException.NotFound("Bookmark not found.");
    }

    /// <inheritdoc />
    public Task<BookmarkDto?> DeleteClipAsync(User user, Guid bookmarkId, CancellationToken cancellationToken)
        => DeleteClipAsync(user, bookmarkId, onlyIfMissing: false, cancellationToken);

    /// <inheritdoc />
    public Task<BookmarkDto?> ForgetClipAsync(User user, Guid bookmarkId, CancellationToken cancellationToken)
        => DeleteClipAsync(user, bookmarkId, onlyIfMissing: true, cancellationToken);

    // DELETE /Bookmarks/{id}/Clip, and with onlyIfMissing its Forget (My Clips' Forget and Dismiss: never a file that is there).
    private async Task<BookmarkDto?> DeleteClipAsync(User user, Guid bookmarkId, bool onlyIfMissing, CancellationToken cancellationToken)
    {
        // Read-only data first, as for every change (see GetOwnedBookmarkToChange).
        _store.ThrowIfReadOnly();
        GetOwnedRecord(user, bookmarkId);

        // The file first: a file in use is 409, with the record, its clip job and the file as they were. Then a live
        // bookmark keeps its record without the clip (as in 1.0); a clip-only tombstone is only its clip, so
        // the record goes with the file. Decided under the store's lock, so a bookmark deleted with its clip kept since the
        // check above goes too. A record that became another user's is left as it is: 404. A file gone from a clip folder that
        // isn't recognised as the clips' own (no marker of its own) isn't taken for gone: 503, nothing changed.
        var change = await DeleteClipFileAndWriteAsync(
            user,
            bookmarkId,
            onlyIfMissing,
            requireRecognisedFolder: true,
            b => b.UserId.Equals(user.Id),
            b =>
            {
                if (BookmarkRecordRules.IsTombstone(b))
                {
                    return null;
                }

                if (b.Clip is null)
                {
                    return b;
                }

                // A queued job goes with the clip; a running job keeps its record so the worker can report the cancellation.
                return b with
                {
                    Clip = b.Clip.Status == BookmarkClipStatus.Running ? b.Clip with { FilePath = null, FileSize = null } : null
                };
            },
            cancellationToken).ConfigureAwait(false);

        if (change.Removed)
        {
            return null;
        }

        if (change.After is not { } after || !BookmarkRecordRules.IsLiveBookmarkOf(after, user.Id))
        {
            throw BookmarkException.NotFound("Bookmark not found.");
        }

        // A running job is stopped now that its clip is gone; it reports the cancellation on the record.
        await _clips.CancelAsync(bookmarkId, cancellationToken).ConfigureAwait(false);
        return ToDto(after);
    }

    /// <inheritdoc />
    public string GetClipFilePath(User user, Guid bookmarkId)
    {
        // A clip-only tombstone's file downloads too (clip operations include tombstones). Only from the clip
        // folders: a path the guard refuses is 404 like a missing file.
        var bookmark = GetOwnedRecord(user, bookmarkId);
        return _files.FindServable(bookmark.Clip?.FilePath, bookmarkId)
            ?? throw BookmarkException.NotFound(NoClipFileMessage);
    }

    /// <inheritdoc />
    public BookmarkCapabilitiesDto GetCapabilities(User user, bool isAdmin)
    {
        ArgumentNullException.ThrowIfNull(user);

        var configuration = _configurationProvider.GetConfiguration();
        var readOnly = _store.ReadOnlyReason;

        // The user's own naming choices: AskForName falls back to the server's default when they made none.
        var preferences = _store.GetPreferences(user.Id);
        return new BookmarkCapabilitiesDto
        {
            ClipsEnabled = configuration.EnableClips,
            CanCreateClips = configuration.EnableClips && user.HasPermission(PermissionKind.EnableContentDownloading),

            // The clip file route's own permission (Policies.Download), with clips turned off too.
            CanDownloadClips = user.HasPermission(PermissionKind.EnableContentDownloading),

            // The "All users" scope of My Clips: an admin on this request, with the setting on.
            CanManageAllClips = isAdmin && configuration.AdminsCanManageAllClips,
            MaxClipDurationSeconds = Math.Max(0, configuration.MaxClipDurationSeconds),
            DefaultClipMode = configuration.DefaultClipMode,
            IsAdmin = isAdmin,
            StoreReadOnly = readOnly is not null,
            StoreReadOnlyReason = readOnly,
            MaxNameLength = BookmarkNames.MaxNameLength,
            MaxNoteLength = MaxNoteLength,
            Shortcuts = new BookmarkShortcutsDto { BookmarkNow = configuration.BookmarkNowShortcut?.Trim() ?? string.Empty },

            // jellyfin-web's player keys and Jellyfin Enhanced's configured ones, read again on each request, for the
            // warning next to a user's own override. This user's own JE overrides are in it, nobody else's: any signed-in
            // user may call this.
            ReservedShortcuts = _shortcuts?.ReservedCombos(user.Id) ?? [],
            ShowPlayerButtons = configuration.ShowPlayerButtons,
            AskForName = preferences?.AskForName ?? configuration.AskForNameDefault,
            AskForNameIsDefault = preferences?.AskForName is null,
            AskForNameDefault = configuration.AskForNameDefault,
            PauseWhileNaming = preferences?.PauseWhileNaming ?? false,
            SuggestChapterNames = configuration.SuggestChapterNames,

            // Each page once the build has it (My Bookmarks since 1.1.0, My Clips since 1.2.0) and the admin didn't turn
            // it off. Pages.Clips also decides the delete dialogs' default: kept clips are listed in My
            // Clips, so the clip stays unless the user ticks "Also delete its clip".
            Pages = new BookmarkPagesDto
            {
                Bookmarks = BookmarksKit.HasPage(BookmarksKit.BookmarksPage) && configuration.EnableBookmarksPage,
                Clips = BookmarksKit.HasPage(BookmarksKit.ClipsPage) && configuration.EnableClipsPage
            },

            // An admin's empty page points at the import while Jellyfin Enhanced's bookmarks wait for it. Only an
            // admin's request reads JE's folder.
            JellyfinEnhancedImportPending = isAdmin && _jeImport is not null && _jeImport.HasPendingImport()
        };
    }

    /// <summary>
    /// Builds the display name stored with a bookmark, such as <c>Heat (1995)</c> or <c>Show S01E02 - Title</c>.
    /// </summary>
    /// <param name="item">The bookmarked item.</param>
    /// <returns>The display name.</returns>
    internal static string GetDisplayName(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item is Episode episode)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(episode.SeriesName))
            {
                builder.Append(episode.SeriesName.Trim());
            }

            if (episode.ParentIndexNumber is int season && episode.IndexNumber is int number)
            {
                if (builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(CultureInfo.InvariantCulture, $"S{season:00}E{number:00}");
            }

            if (!string.IsNullOrWhiteSpace(episode.Name))
            {
                if (builder.Length > 0)
                {
                    builder.Append(" - ");
                }

                builder.Append(episode.Name.Trim());
            }

            return builder.Length > 0 ? builder.ToString() : "Episode";
        }

        var name = string.IsNullOrWhiteSpace(item.Name) ? "Video" : item.Name.Trim();
        if (item.ProductionYear is not int year)
        {
            return name;
        }

        // Without a metadata provider the name comes from the folder and may already end in the year.
        var yearSuffix = string.Create(CultureInfo.InvariantCulture, $"({year})");
        return name.EndsWith(yearSuffix, StringComparison.Ordinal) ? name : name + " " + yearSuffix;
    }

    private static void ValidateRange(BaseItem? item, long startTicks, long? endTicks)
    {
        if (startTicks < 0)
        {
            throw BookmarkException.Invalid("The start time can't be negative.");
        }

        if (endTicks <= startTicks)
        {
            throw BookmarkException.Invalid("The end time must be after the start time.");
        }

        if (item?.RunTimeTicks is long runtime && runtime > 0)
        {
            var limit = runtime + _runtimeToleranceTicks;
            if (startTicks > limit || endTicks > limit)
            {
                throw BookmarkException.Invalid("The bookmark is past the end of the video.");
            }
        }
    }

    private static string? NormalizeNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        note = note.Trim();
        if (note.Length > MaxNoteLength)
        {
            throw BookmarkException.Invalid(string.Format(
                CultureInfo.InvariantCulture,
                "Notes can be at most {0} characters long.",
                MaxNoteLength));
        }

        return note;
    }

    // A user's edit of their own live bookmark (update, rename) in one write, decided under the store's lock:
    // a record that became a tombstone or another user's since the request looked it up is left exactly as it is, with
    // nothing written, and reported as missing (404); so is one changed since the client's IfUpdatedAt, reported as changed
    // elsewhere (409). The edit's UpdatedAt, the next token, comes from NextUpdatedAt. A successful edit clears the
    // "Relinked, check position" badge, even when nothing else changed; `edit` does that.
    private async Task<Bookmark> WriteEditAsync(
        User user,
        Guid bookmarkId,
        DateTime? ifUpdatedAt,
        Func<Bookmark, DateTime, Bookmark> edit,
        CancellationToken cancellationToken)
    {
        var changedElsewhere = false;
        var written = await _store.UpdateAsync(
            bookmarkId,
            b =>
            {
                changedElsewhere = false;
                if (!BookmarkRecordRules.IsLiveBookmarkOf(b, user.Id))
                {
                    return b;
                }

                if (!BookmarkRecordRules.IsUnchangedSince(b, ifUpdatedAt))
                {
                    changedElsewhere = true;
                    return b;
                }

                return edit(b, BookmarkRecordRules.NextUpdatedAt(b.UpdatedAt, DateTime.UtcNow));
            },
            cancellationToken,
            user).ConfigureAwait(false);

        if (written is null || !BookmarkRecordRules.IsLiveBookmarkOf(written, user.Id))
        {
            throw BookmarkException.NotFound("Bookmark not found.");
        }

        if (changedElsewhere)
        {
            _logger.LogDebug("User {UserId}'s change of bookmark {BookmarkId} was refused: it was changed since {IfUpdatedAt}", user.Id, bookmarkId, ifUpdatedAt);
            throw BookmarkException.Conflict(ChangedElsewhereMessage);
        }

        return written;
    }

    // Every distinct id of a bulk delete in exactly one list, in the order the request gave them.
    private static BookmarkBulkDeleteResultDto BulkResult(
        IReadOnlyList<Guid> ids,
        IReadOnlySet<Guid> deleted,
        IReadOnlySet<Guid> tombstoned,
        IReadOnlySet<Guid> inUse,
        IReadOnlySet<Guid> failed)
        => new()
        {
            Deleted = ids.Where(deleted.Contains).ToList(),
            Tombstoned = ids.Where(tombstoned.Contains).ToList(),
            InUse = ids.Where(inUse.Contains).ToList(),
            Failed = ids.Where(failed.Contains).ToList(),
            NotFound = ids.Where(id => !deleted.Contains(id) && !tombstoned.Contains(id) && !inUse.Contains(id) && !failed.Contains(id)).ToList()
        };

    // A bulk delete that keeps clip files (as DeleteBookmarkAsync without deleteClip, for many ids in one write): each
    // of the caller's live bookmarks becomes a clip-only tombstone when it has a clip file and is removed otherwise;
    // everything else (unknown ids, other users' records, tombstones) is left exactly as it is. The tombstone's clip is
    // reduced to its file, so a queued job of it is gone with its record's job state, as the one delete's cancellation
    // leaves it.
    private async Task<BookmarkBulkDeleteResultDto> BulkDeleteKeepingClipsAsync(User user, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var targets = ids.ToHashSet();
        var removed = new HashSet<Guid>();
        var tombstoned = new HashSet<Guid>();
        var now = DateTime.UtcNow;
        await _store.UpdateDocumentAsync(
            document =>
            {
                removed.Clear();
                tombstoned.Clear();
                var records = new List<Bookmark>(document.Bookmarks.Count);
                foreach (var bookmark in document.Bookmarks)
                {
                    if (!targets.Contains(bookmark.Id) || !BookmarkRecordRules.IsLiveBookmarkOf(bookmark, user.Id))
                    {
                        records.Add(bookmark);
                    }
                    else if (BookmarkRecordRules.ToTombstone(bookmark, now) is { } tombstone)
                    {
                        records.Add(tombstone);
                        tombstoned.Add(bookmark.Id);
                    }
                    else
                    {
                        removed.Add(bookmark.Id);
                    }
                }

                if (removed.Count + tombstoned.Count == 0)
                {
                    return document;
                }

                // "Refreshed on writes": the owner of the tombstones, as one delete writes it.
                return document with { Bookmarks = records, KnownUsers = tombstoned.Count > 0 ? document.KnownUsers.With(user) : document.KnownUsers };
            },
            cancellationToken).ConfigureAwait(false);
        StopRunningAfterWrite(removed.Concat(tombstoned));
        return BulkResult(ids, removed, tombstoned, new HashSet<Guid>(), new HashSet<Guid>());
    }

    // A bulk delete with the clip files (file first, as DeleteClipFileAndWriteAsync for many ids; the loop is
    // ClipFileBatchDelete's, which the My Clips bulk delete shares): each of the caller's live bookmarks is removed once its
    // clip file is gone, the file deleted inside the store's write that removes the record. The files still in use are tried
    // again in the next write after the pause, outside the store's lock, up to ClipFileOps.Attempts times; then they are
    // InUse, and a file that can't be deleted otherwise (a path the guard refuses, a clip folder that isn't there,
    // a file gone from a clip folder that is there but isn't recognised as the clips' own, another failure) is
    // Failed; the bookmarks of both are left exactly as they were. The guard's refusals and missing folders are found before
    // the first write, so a slow drive never holds the store's lock. An id that is no longer the caller's live bookmark
    // (deleted meanwhile in another tab) is simply not found.
    private async Task<BookmarkBulkDeleteResultDto> BulkDeleteWithClipsAsync(User user, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var outcome = await ClipFileBatchDelete.RunAsync(
            _store,
            _files,
            ids,
            b => BookmarkRecordRules.IsLiveBookmarkOf(b, user.Id),
            static _ => null,
            gone => StopRunningAfterWrite(gone.Keys),
            isInProgress: null,
            requireRecognisedFolder: true,
            cancellationToken).ConfigureAwait(false);

        var inUse = outcome.Refused.Where(r => r.Value.Outcome == ClipFileOutcome.InUse).Select(r => r.Key).ToHashSet();
        var failed = outcome.Refused.Keys.Where(id => !inUse.Contains(id)).ToHashSet();
        return BulkResult(ids, outcome.Changed.Keys.ToHashSet(), new HashSet<Guid>(), inUse, failed);
    }

    // A job claimed before a bulk delete's write still runs: stop it, as one delete does. Its worker records nothing on a
    // removed record or a tombstone, and discards the file it may still finish.
    private void StopRunningAfterWrite(IEnumerable<Guid> written)
    {
        foreach (var id in written)
        {
            _clips.StopRunning(id);
        }
    }

    // File first, through the clip file operations' single delete (ClipFileSingleDelete, which the admins' delete
    // shares): the file inside the store's write of the record, the record changed only once the file is
    // gone, a file in use tried again with the pauses outside the store's lock, nothing written on a refusal. A read-only store
    // refuses before the file is touched (503, the callers' GetOwnedRecord and GetOwnedBookmarkToChange). `isTarget` says
    // whether the record is still one the caller may change. With `onlyIfMissing` (the My Clips page's Forget and Dismiss)
    // no file is deleted at all; with `requireRecognisedFolder` a file gone from a clip folder that isn't recognised as the
    // clips' own isn't gone either. A clip being made is `change`'s to handle: a queued job goes with the clip, a
    // running one is stopped by the caller.
    private Task<BookmarkChange> DeleteClipFileAndWriteAsync(
        User user,
        Guid bookmarkId,
        bool onlyIfMissing,
        bool requireRecognisedFolder,
        Func<Bookmark, bool> isTarget,
        Func<Bookmark, Bookmark?> change,
        CancellationToken cancellationToken)
        => ClipFileSingleDelete.RunAsync(
            _store,
            _files,
            bookmarkId,
            onlyIfMissing,
            requireRecognisedFolder,
            isTarget,
            isInProgress: null,
            change,
            user,
            forAdmin: false,
            fileOutcome: null,
            cancellationToken);

    // A rename of the bookmark's clip file not finished finished now, or 503 while it can't be (its clip folder
    // away, the data not writable): nothing that replaces or deletes the clip's file may run before it.
    private async Task FinishClipRenameAsync(Guid bookmarkId, CancellationToken cancellationToken)
    {
        if (!_files.HasUnfinishedRename(bookmarkId))
        {
            return;
        }

        await _files.FinishRenamesAsync(null, cancellationToken).ConfigureAwait(false);
        if (_files.HasUnfinishedRename(bookmarkId))
        {
            throw new BookmarkException(PluginErrorKind.Unavailable, ClipFileOps.RenameUnfinishedMessage);
        }
    }

    // The user's own live bookmark. Other users' bookmarks, and tombstones, are reported as missing, so ids
    // can't be probed.
    private Bookmark GetOwnedBookmark(User user, Guid bookmarkId)
    {
        ArgumentNullException.ThrowIfNull(user);

        var bookmark = _store.Get(bookmarkId);
        if (bookmark is null || !BookmarkRecordRules.IsLiveBookmarkOf(bookmark, user.Id))
        {
            throw BookmarkException.NotFound("Bookmark not found.");
        }

        return bookmark;
    }

    // GetOwnedBookmark for a request that changes the bookmark (update, rename, delete, a clip job): read-only data is 503
    // with the store's message before the bookmark is looked up. After a failed upgrade
    // the store serves an empty stand-in, so the lookup alone would answer 404 "Bookmark not found." for a bookmark the
    // untouched file still holds, and a page loaded before the upgrade would say it is gone. The answer
    // is the same for every id, so it tells nothing about other users' bookmarks either.
    private Bookmark GetOwnedBookmarkToChange(User user, Guid bookmarkId)
    {
        _store.ThrowIfReadOnly();
        return GetOwnedBookmark(user, bookmarkId);
    }

    // The user's own record, a clip-only tombstone included: only the clip operations (the clip file, deleting the
    // clip) see tombstones. Other users' records are missing, as above.
    private Bookmark GetOwnedRecord(User user, Guid bookmarkId)
    {
        ArgumentNullException.ThrowIfNull(user);

        var bookmark = _store.Get(bookmarkId);
        if (bookmark is null || !bookmark.UserId.Equals(user.Id))
        {
            throw BookmarkException.NotFound("Bookmark not found.");
        }

        return bookmark;
    }

    private BookmarkDto ToDto(Bookmark bookmark) => BookmarkDtoMapper.ToDto(bookmark, _clips.GetProgress(bookmark.Id));

    private Video? FindVideo(Guid itemId, User user)
    {
        try
        {
            return _libraryManager.GetItemById<Video>(itemId, user);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // Jellyfin throws for ids it can't materialize, such as its internal placeholder item.
            _logger.LogDebug(ex, "Could not load item {ItemId}", itemId);
            return null;
        }
    }

    private void EnsureCanCreateClips(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!_configurationProvider.GetConfiguration().EnableClips)
        {
            throw BookmarkException.Forbidden("Clip extraction is turned off on this server.");
        }

        if (!user.HasPermission(PermissionKind.EnableContentDownloading))
        {
            throw BookmarkException.Forbidden("Creating clips requires permission to download media.");
        }
    }
}
