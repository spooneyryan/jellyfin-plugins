using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Configuration;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Implements <see cref="IBookmarkAdminClips"/> (since 1.2.0): the admins' "All users"
/// scope of the My Clips page over the same parts as the owner's own (<see cref="ClipService"/>): the list
/// (<see cref="ClipQueryService"/>, without notes), the library as the caller (<see cref="ClipViewer"/>), the clip file
/// operations (<see cref="ClipFileOps"/>, through <see cref="ClipFileSingleDelete"/> and <see cref="ClipFileBatchDelete"/>,
/// the owner's own code) and the clip worker's cancel.
/// </summary>
/// <remarks>
/// <para><b>The gate</b> (a single list of routes): every member but <see cref="GetSummary"/> answers 403
/// <see cref="TurnedOffMessage"/> while <c>AdminsCanManageAllClips</c> is off, read on each request. Who may call at all is
/// Jellyfin's <c>RequiresElevation</c> policy on the controller, evaluated on every request (a demoted admin's token loses
/// it at the next request, a disabled user's token isn't authenticated; an API key passes it).</para>
/// <para><b>Privacy.</b> A clip's note never reaches an answer (the admin DTO has no member for it), nor takes part in a search
/// or an order (<see cref="ClipQueryService.Query"/> without notes). A clip file's name is the file's own, as the clip folder
/// shows it (admins can read the clip folder anyway); since 1.2.0 a new clip's name never takes its bookmark's note
/// (<see cref="BookmarkNames.ClipFileLabel"/>), though a file made before then for a bookmark without a name still carries
/// the beginning of its note until its owner renames it (Rename file).</para>
/// <para><b>Library access</b> (since Bookmarks 1.2.1; the kit's privacy rule, the caller's own library access, which
/// admin endpoints aren't exempt from): a signed-in admin sees and acts on the clips of the videos they can open (their
/// libraries and parental limit, <see cref="LibraryAccess.Visibility"/>, asked once per video and request), and the clips
/// with no video to check (detached, not in the library, or of an item that is gone) only with access to every library
/// (<c>EnableAllFolders</c>); any other clip is as if it weren't there (404, left out of the list, its paging and its
/// states). An API key sees every clip; a user token whose user no longer exists, none. <see cref="GetSummary"/> still
/// counts every clip: numbers per user, no title, name or path.</para>
/// <para><b>The file actions</b> are the owner's own code with a target of any user's clip: the file first, a rename that isn't
/// finished finished first (refused with nothing touched while it can't be), a clip folder that is offline or not recognised
/// left alone, a clip being made refused (409; cancel it first), a file another record still names kept for that one
/// (Bookmarks 1.1.3), and a path outside the clip folders 409 (the admins' answer).</para>
/// <para><b>The log</b> names each action at Information with who acted (an admin by id; an API key by its name, never its
/// token; a user token whose user no longer exists by that id, never taken for an API key), the clip's id and
/// its owner's id, never a clip's name or a path; the path is at Debug. A delete that kept the file because another
/// clip still names it says so ("let go of"), never "deleted". A download is logged each time; a preview once per caller and
/// clip in <see cref="PreviewLogWindow"/> (a looping video asks for its first bytes again on every loop), its repeats at
/// Debug. Reading the list and the states is logged at Debug.</para>
/// </remarks>
internal sealed class AdminClipService : IBookmarkAdminClips
{
    /// <summary>
    /// The 403 message of the gated routes while <c>AdminsCanManageAllClips</c> is off.
    /// </summary>
    public const string TurnedOffMessage = "Admins' access to other users' clips is turned off in the Bookmarks settings (\"Admins can see and manage every user's clips\").";

    /// <summary>
    /// The 404 message of an id that is no clip.
    /// </summary>
    public const string NotFoundMessage = "Clip not found.";

    /// <summary>
    /// The claim in which Jellyfin 10.11.5 puts an API key's name for a request made with it
    /// (<c>Jellyfin.Api.Constants.InternalClaimTypes.Client</c>; <c>AuthorizationContext</c> sets the client to the key's
    /// <c>Name</c>). The audit names an API key by it, never by its token.
    /// </summary>
    public const string ClientClaimType = "Jellyfin-Client";

    /// <summary>
    /// How many (caller, clip) pairs the preview's audit keeps in memory at most; past that the oldest window's are dropped.
    /// </summary>
    public const int MaxPreviewLogEntries = 1000;

    /// <summary>
    /// How long a preview of one clip by one caller is logged once at Information: a <c>&lt;video&gt;</c> that
    /// loops asks for the file from byte 0 again on every loop, and each of those requests is the preview's first.
    /// </summary>
    public static readonly TimeSpan PreviewLogWindow = TimeSpan.FromMinutes(10);

    private readonly IBookmarkStore _store;
    private readonly ClipQueryService _query;
    private readonly ClipFileOps _files;
    private readonly IClipExtractionService _clips;
    private readonly ClipLibraryLocations _locations;
    private readonly ILibraryManager _library;
    private readonly LibraryAccess _libraries;
    private readonly IUserManager _users;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly ILogger<AdminClipService> _logger;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<(string Caller, Guid ClipId), DateTimeOffset> _previews = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AdminClipService"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="query">The clip list.</param>
    /// <param name="files">The clip file operations.</param>
    /// <param name="clips">The clip worker: the progress of running clips and the cancel.</param>
    /// <param name="locations">The libraries' folders, for the In library badge.</param>
    /// <param name="library">The library, asked as the caller.</param>
    /// <param name="libraries">The libraries the caller may open, for a library's name.</param>
    /// <param name="users">The users: the caller, and the owners' names.</param>
    /// <param name="configuration">The settings: <c>AdminsCanManageAllClips</c>.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="clock">The clock of the preview's audit window.</param>
    public AdminClipService(
        IBookmarkStore store,
        ClipQueryService query,
        ClipFileOps files,
        IClipExtractionService clips,
        ClipLibraryLocations locations,
        ILibraryManager library,
        LibraryAccess libraries,
        IUserManager users,
        IPluginConfigurationProvider configuration,
        ILogger<AdminClipService> logger,
        TimeProvider clock)
    {
        _store = store;
        _query = query;
        _files = files;
        _clips = clips;
        _locations = locations;
        _library = library;
        _libraries = libraries;
        _users = users;
        _configuration = configuration;
        _logger = logger;
        _clock = clock;
    }

    // What an admin did to a user's clip, for its log line (Audit).
    private enum AuditAction
    {
        Downloaded,
        Previewed,
        Cancelled,
        Deleted,
        DeletedWithTheRecord,
        LetGo,
        LetGoWithTheRecord,
        Forgot,
        ForgotWithTheRecord
    }

    // Who acted: an admin, an API key, or a user token whose user no longer exists (never "an API key").
    private enum CallerKind
    {
        Admin,
        ApiKey,
        GoneUser
    }

    /// <inheritdoc />
    public QueryResult<BookmarkAdminClipItemDto> GetClips(ClaimsPrincipal caller, BookmarkClipQueryFilter? filter, Guid? userId, BookmarkClipSortBy? sortBy)
    {
        EnsureOn();
        var who = Resolve(caller);
        var listable = Listable(who);
        var page = _query.Query(_query.Rows(userId).Where(row => listable(row.Record)).ToList(), filter, sortBy, withNotes: false);
        var owners = Owners();
        var viewer = new ClipViewer(_library, _libraries, _logger, who.User);
        var items = page.Rows.Select(row => ToItemDto(row, viewer, owners)).ToList();
        _logger.LogDebug("{Caller} listed {Count} of {Total} clips of {Owner}", who.Describe(), items.Count, page.Total, userId?.ToString("N", CultureInfo.InvariantCulture) ?? "every user");
        return new QueryResult<BookmarkAdminClipItemDto>(page.StartIndex, page.Total, items);
    }

    /// <inheritdoc />
    public BookmarkAdminClipSummaryDto GetSummary()
    {
        var owners = Owners();
        var users = new List<BookmarkAdminClipUserDto>();
        foreach (var group in _query.Rows((Guid?)null).GroupBy(row => row.Record.UserId))
        {
            var rows = group.ToList();
            var dates = rows.Select(row => row.Clip.CompletedAt ?? row.Clip.RequestedAt).ToList();
            var name = owners.GetValueOrDefault(group.Key);
            users.Add(new BookmarkAdminClipUserDto
            {
                UserId = group.Key,
                UserName = name ?? BookmarkDtoMapper.DeletedUserName(group.Key),
                UserDeleted = name is null,
                Count = rows.Count,
                TotalBytes = _query.Summarize(rows).TotalBytes,
                OldestUtc = dates.Count > 0 ? dates.Min() : null,
                NewestUtc = dates.Count > 0 ? dates.Max() : null
            });
        }

        var ordered = users
            .OrderBy(u => u.UserDeleted ? 1 : 0)
            .ThenBy(u => u.UserName, StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase))
            .ThenBy(u => u.UserId)
            .ToList();
        return new BookmarkAdminClipSummaryDto
        {
            Users = ordered,
            Count = ordered.Sum(u => u.Count),
            TotalBytes = ordered.Sum(u => u.TotalBytes)
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<BookmarkClipStatusDto> GetStatus(ClaimsPrincipal caller, IReadOnlyList<Guid>? ids)
    {
        EnsureOn();
        var listable = Listable(Resolve(caller));
        if (ids is not { Count: > 0 })
        {
            throw BookmarkException.Invalid(ClipService.StatusNoIdsMessage);
        }

        if (ids.Count > ClipService.MaxIds)
        {
            throw BookmarkException.Invalid(ClipService.StatusTooManyMessage);
        }

        var rows = _query.Rows(ids.Distinct()
            .Select(_store.Get)
            .Where(record => record is { Clip: not null } && listable(record))
            .Select(record => record!));
        return rows.Select(row => BookmarkDtoMapper.ToClipStatusDto(row.Record, row.State, _clips.GetProgress(row.Record.Id))).ToList();
    }

    /// <inheritdoc />
    public string GetClipFilePath(ClaimsPrincipal caller, Guid clipId, bool download, bool logAccess)
    {
        EnsureOn();
        var who = Resolve(caller);
        var record = Find(clipId, who);

        // As the owner's file route (GET /Bookmarks/{id}/Clip/File): only from the clip folders, a file that is there (also
        // while a rename moves it); a path the guard refuses is 404 as a missing file, nothing touched.
        var path = _files.FindServable(record.Clip!.FilePath, clipId) ?? throw BookmarkException.NotFound(BookmarkManager.NoClipFileMessage);
        if (logAccess && download)
        {
            Audit(who, AuditAction.Downloaded, clipId, record.UserId);
        }
        else if (logAccess && FirstPreviewInWindow(who, clipId))
        {
            Audit(who, AuditAction.Previewed, clipId, record.UserId);
        }
        else if (logAccess)
        {
            _logger.LogDebug("{Caller} previewed the clip of bookmark {BookmarkId} of user {UserId} again (a video looping, or played again)", who.Describe(), clipId, record.UserId);
        }

        _logger.LogDebug("{Caller} served {Path} for bookmark {BookmarkId}", who.Describe(), path, clipId);
        return path;
    }

    /// <inheritdoc />
    public async Task<BookmarkAdminClipItemDto> CancelAsync(ClaimsPrincipal caller, Guid clipId, CancellationToken cancellationToken)
    {
        EnsureOn();
        var who = Resolve(caller);
        _store.ThrowIfReadOnly();
        var record = Find(clipId, who);
        if (BookmarkRecordRules.HasClipJob(record))
        {
            // As the owner's cancel: a waiting job ends at once, a running one reports its end on the record; any earlier file
            // stays.
            await _clips.CancelAsync(clipId, cancellationToken).ConfigureAwait(false);
            Audit(who, AuditAction.Cancelled, clipId, record.UserId);
        }

        var now = _store.Get(clipId) is { Clip: not null } current ? current : throw BookmarkException.NotFound(NotFoundMessage);
        return ToItemDto(_query.Rows([now])[0], new ClipViewer(_library, _libraries, _logger, who.User), Owners());
    }

    /// <inheritdoc />
    public async Task DeleteAsync(ClaimsPrincipal caller, Guid clipId, bool onlyIfMissing, CancellationToken cancellationToken)
    {
        EnsureOn();
        var who = Resolve(caller);
        _store.ThrowIfReadOnly();
        var owner = Find(clipId, who).UserId;

        // The owner's own delete (ClipFileSingleDelete), with any user's clip as its target, the owner as it was read (a clip
        // given to another user meanwhile is left alone: 404), and a clip being made refused, never cancelled here.
        ClipFileOutcome? file = null;
        var change = await ClipFileSingleDelete.RunAsync(
            _store,
            _files,
            clipId,
            onlyIfMissing,
            requireRecognisedFolder: true,
            b => b.Clip is not null && b.UserId.Equals(owner),
            BookmarkRecordRules.HasClipJob,
            static b => BookmarkRecordRules.IsTombstone(b) ? null : b with { Clip = null },
            writer: null,
            forAdmin: true,
            outcome => file = outcome,
            cancellationToken).ConfigureAwait(false);
        if (!change.Changed)
        {
            throw BookmarkException.NotFound(NotFoundMessage);
        }

        // A file another clip still names was kept (Bookmarks 1.1.3's rule): the clip let go of it, nothing was deleted.
        var kept = file == ClipFileOutcome.NamedByAnotherRecord;
        Audit(
            who,
            onlyIfMissing
                ? change.Removed ? AuditAction.ForgotWithTheRecord : AuditAction.Forgot
                : kept
                    ? change.Removed ? AuditAction.LetGoWithTheRecord : AuditAction.LetGo
                    : change.Removed ? AuditAction.DeletedWithTheRecord : AuditAction.Deleted,
            clipId,
            owner);
        _logger.LogDebug("{Caller} removed the clip {Path} of bookmark {BookmarkId}: its file {File}", who.Describe(), change.Before?.Clip?.FilePath, clipId, file?.ToString() ?? "wasn't asked about");
    }

    /// <inheritdoc />
    public async Task<BookmarkClipBulkDeleteResultDto> BulkDeleteAsync(ClaimsPrincipal caller, BookmarkClipBulkDeleteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureOn();
        var who = Resolve(caller);
        if (request.Ids is not { Count: > 0 } given)
        {
            throw BookmarkException.Invalid(ClipService.BulkDeleteNoIdsMessage);
        }

        if (given.Count > ClipService.MaxIds)
        {
            throw BookmarkException.Invalid(ClipService.BulkDeleteTooManyMessage);
        }

        _store.ThrowIfReadOnly();
        var ids = given.Distinct().ToList();

        // A rename of one of their files not finished is finished first; one still waiting holds its paths, and its clip is
        // reported Failed with nothing touched (as the owner's bulk delete).
        if (ids.Exists(_files.HasUnfinishedRename))
        {
            await _files.FinishRenamesAsync(null, cancellationToken).ConfigureAwait(false);
        }

        // The owner's bulk delete of the My Clips page with any user's clip as a target: a clip being made is left as it is
        // (InProgress), a folder offline or not recognised (FolderUnavailable), a file in use (InUse) or refused (Failed) too.
        var listable = Listable(who);
        var outcome = await ClipFileBatchDelete.RunAsync(
            _store,
            _files,
            ids,
            b => b.Clip is not null && listable(b),
            static b => BookmarkRecordRules.IsTombstone(b) ? null : b with { Clip = null },
            static _ =>
            {
                // No job to stop: a clip being made is never changed here.
            },
            static b => b.Clip is { Status: BookmarkClipStatus.Queued or BookmarkClipStatus.Running },
            requireRecognisedFolder: true,
            cancellationToken).ConfigureAwait(false);

        var result = ClipService.Result(ids, outcome);
        AuditBulk(who, result, outcome);
        return result;
    }

    // Who called (from the kit's claims): an admin; an API key (RequiresElevation admits it), named by its key's name;
    // or a user token whose user no longer exists (deleted between the authentication and this look), never an API key.
    private AdminCaller Resolve(ClaimsPrincipal caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.IsApiKey())
        {
            var name = caller.FindFirst(ClientClaimType)?.Value;
            return new AdminCaller(CallerKind.ApiKey, null, Guid.Empty, string.IsNullOrWhiteSpace(name) ? "(no name)" : name.Trim());
        }

        var userId = caller.GetUserId();
        var user = userId.Equals(Guid.Empty) ? null : _users.GetUserById(userId);
        return user is null
            ? new AdminCaller(CallerKind.GoneUser, null, userId, null)
            : new AdminCaller(CallerKind.Admin, user, user.Id, null);
    }

    // Whether this preview of a clip by this caller is the first in PreviewLogWindow (then noted), the cache kept bounded.
    private bool FirstPreviewInWindow(AdminCaller who, Guid clipId)
    {
        var now = _clock.GetUtcNow();
        var key = (who.Key, clipId);
        if (_previews.TryGetValue(key, out var logged) && now - logged < PreviewLogWindow && now >= logged)
        {
            return false;
        }

        if (_previews.Count >= MaxPreviewLogEntries)
        {
            foreach (var entry in _previews)
            {
                if (now - entry.Value >= PreviewLogWindow || now < entry.Value)
                {
                    _previews.TryRemove(entry.Key, out _);
                }
            }

            if (_previews.Count >= MaxPreviewLogEntries)
            {
                _previews.Clear();
            }
        }

        _previews[key] = now;
        return true;
    }

    // An admin action on a user's clip at Information (audited with the ids, never a name or a path), each its own
    // sentence per kind of caller, so the log reads it without quotes around its words.
    private void Audit(AdminCaller who, AuditAction action, Guid clipId, Guid ownerId)
    {
        switch (who.Kind)
        {
            case CallerKind.Admin:
                AuditAdmin(who.UserId, action, clipId, ownerId);
                break;
            case CallerKind.ApiKey:
                AuditApiKey(who.KeyName!, action, clipId, ownerId);
                break;
            default:
                AuditGoneUser(who.UserId, action, clipId, ownerId);
                break;
        }
    }

    private void AuditAdmin(Guid admin, AuditAction action, Guid clipId, Guid ownerId)
    {
        switch (action)
        {
            case AuditAction.Downloaded:
                _logger.LogInformation("Admin {AdminId} downloaded the clip of bookmark {BookmarkId} of user {UserId}", admin, clipId, ownerId);
                break;
            case AuditAction.Previewed:
                _logger.LogInformation("Admin {AdminId} previewed the clip of bookmark {BookmarkId} of user {UserId}", admin, clipId, ownerId);
                break;
            case AuditAction.Cancelled:
                _logger.LogInformation("Admin {AdminId} cancelled the clip being made of bookmark {BookmarkId} of user {UserId}", admin, clipId, ownerId);
                break;
            case AuditAction.Deleted:
                _logger.LogInformation("Admin {AdminId} deleted the clip of bookmark {BookmarkId} of user {UserId}; the bookmark stays", admin, clipId, ownerId);
                break;
            case AuditAction.DeletedWithTheRecord:
                _logger.LogInformation("Admin {AdminId} deleted the clip of bookmark {BookmarkId} of user {UserId}; the deleted bookmark's record is gone with it", admin, clipId, ownerId);
                break;
            case AuditAction.LetGo:
                _logger.LogInformation("Admin {AdminId} let go of the clip of bookmark {BookmarkId} of user {UserId}; its file stays, as another clip still names it, and the bookmark stays", admin, clipId, ownerId);
                break;
            case AuditAction.LetGoWithTheRecord:
                _logger.LogInformation("Admin {AdminId} let go of the clip of bookmark {BookmarkId} of user {UserId}; its file stays, as another clip still names it, and the deleted bookmark's record is gone", admin, clipId, ownerId);
                break;
            case AuditAction.Forgot:
                _logger.LogInformation("Admin {AdminId} forgot the clip of bookmark {BookmarkId} of user {UserId}; the bookmark stays", admin, clipId, ownerId);
                break;
            default:
                _logger.LogInformation("Admin {AdminId} forgot the clip of bookmark {BookmarkId} of user {UserId}; the deleted bookmark's record is gone with it", admin, clipId, ownerId);
                break;
        }
    }

    private void AuditApiKey(string key, AuditAction action, Guid clipId, Guid ownerId)
    {
        switch (action)
        {
            case AuditAction.Downloaded:
                _logger.LogInformation("The API key {ApiKey} downloaded the clip of bookmark {BookmarkId} of user {UserId}", key, clipId, ownerId);
                break;
            case AuditAction.Previewed:
                _logger.LogInformation("The API key {ApiKey} previewed the clip of bookmark {BookmarkId} of user {UserId}", key, clipId, ownerId);
                break;
            case AuditAction.Cancelled:
                _logger.LogInformation("The API key {ApiKey} cancelled the clip being made of bookmark {BookmarkId} of user {UserId}", key, clipId, ownerId);
                break;
            case AuditAction.Deleted:
                _logger.LogInformation("The API key {ApiKey} deleted the clip of bookmark {BookmarkId} of user {UserId}; the bookmark stays", key, clipId, ownerId);
                break;
            case AuditAction.DeletedWithTheRecord:
                _logger.LogInformation("The API key {ApiKey} deleted the clip of bookmark {BookmarkId} of user {UserId}; the deleted bookmark's record is gone with it", key, clipId, ownerId);
                break;
            case AuditAction.LetGo:
                _logger.LogInformation("The API key {ApiKey} let go of the clip of bookmark {BookmarkId} of user {UserId}; its file stays, as another clip still names it, and the bookmark stays", key, clipId, ownerId);
                break;
            case AuditAction.LetGoWithTheRecord:
                _logger.LogInformation("The API key {ApiKey} let go of the clip of bookmark {BookmarkId} of user {UserId}; its file stays, as another clip still names it, and the deleted bookmark's record is gone", key, clipId, ownerId);
                break;
            case AuditAction.Forgot:
                _logger.LogInformation("The API key {ApiKey} forgot the clip of bookmark {BookmarkId} of user {UserId}; the bookmark stays", key, clipId, ownerId);
                break;
            default:
                _logger.LogInformation("The API key {ApiKey} forgot the clip of bookmark {BookmarkId} of user {UserId}; the deleted bookmark's record is gone with it", key, clipId, ownerId);
                break;
        }
    }

    private void AuditGoneUser(Guid caller, AuditAction action, Guid clipId, Guid ownerId)
    {
        switch (action)
        {
            case AuditAction.Downloaded:
                _logger.LogInformation("A user that no longer exists ({CallerId}) downloaded the clip of bookmark {BookmarkId} of user {UserId}", caller, clipId, ownerId);
                break;
            case AuditAction.Previewed:
                _logger.LogInformation("A user that no longer exists ({CallerId}) previewed the clip of bookmark {BookmarkId} of user {UserId}", caller, clipId, ownerId);
                break;
            case AuditAction.Cancelled:
                _logger.LogInformation("A user that no longer exists ({CallerId}) cancelled the clip being made of bookmark {BookmarkId} of user {UserId}", caller, clipId, ownerId);
                break;
            case AuditAction.Deleted:
                _logger.LogInformation("A user that no longer exists ({CallerId}) deleted the clip of bookmark {BookmarkId} of user {UserId}; the bookmark stays", caller, clipId, ownerId);
                break;
            case AuditAction.DeletedWithTheRecord:
                _logger.LogInformation("A user that no longer exists ({CallerId}) deleted the clip of bookmark {BookmarkId} of user {UserId}; the deleted bookmark's record is gone with it", caller, clipId, ownerId);
                break;
            case AuditAction.LetGo:
                _logger.LogInformation("A user that no longer exists ({CallerId}) let go of the clip of bookmark {BookmarkId} of user {UserId}; its file stays, as another clip still names it, and the bookmark stays", caller, clipId, ownerId);
                break;
            case AuditAction.LetGoWithTheRecord:
                _logger.LogInformation("A user that no longer exists ({CallerId}) let go of the clip of bookmark {BookmarkId} of user {UserId}; its file stays, as another clip still names it, and the deleted bookmark's record is gone", caller, clipId, ownerId);
                break;
            case AuditAction.Forgot:
                _logger.LogInformation("A user that no longer exists ({CallerId}) forgot the clip of bookmark {BookmarkId} of user {UserId}; the bookmark stays", caller, clipId, ownerId);
                break;
            default:
                _logger.LogInformation("A user that no longer exists ({CallerId}) forgot the clip of bookmark {BookmarkId} of user {UserId}; the deleted bookmark's record is gone with it", caller, clipId, ownerId);
                break;
        }
    }

    // The bulk delete's one line: how many clips went, those whose file another clip still names (kept, let go of), and each
    // clip with its owner; never a name or a path.
    private void AuditBulk(AdminCaller who, BookmarkClipBulkDeleteResultDto result, ClipFileBatchOutcome outcome)
    {
        var deleted = result.FilesDeleted.Count + result.RecordsRemoved.Count;
        var clips = string.Join(", ", outcome.Changed.Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} of user {pair.Value.UserId}")));
        var kept = outcome.FilesKept.Count;
        switch (who.Kind)
        {
            case CallerKind.Admin:
                _logger.LogInformation(
                    "Admin {AdminId} deleted {Deleted} clips at once ({Removed} of them of deleted bookmarks; {Kept} let go of a file another clip still names, which stays): {Clips}; {NotFound} not found, {InProgress} being made, {InUse} in use, {Offline} in a clip folder that isn't available, {Failed} that couldn't be deleted",
                    who.UserId,
                    deleted,
                    result.RecordsRemoved.Count,
                    kept,
                    clips,
                    result.NotFound.Count,
                    result.InProgress.Count,
                    result.InUse.Count,
                    result.FolderUnavailable.Count,
                    result.Failed.Count);
                break;
            case CallerKind.ApiKey:
                _logger.LogInformation(
                    "The API key {ApiKey} deleted {Deleted} clips at once ({Removed} of them of deleted bookmarks; {Kept} let go of a file another clip still names, which stays): {Clips}; {NotFound} not found, {InProgress} being made, {InUse} in use, {Offline} in a clip folder that isn't available, {Failed} that couldn't be deleted",
                    who.KeyName,
                    deleted,
                    result.RecordsRemoved.Count,
                    kept,
                    clips,
                    result.NotFound.Count,
                    result.InProgress.Count,
                    result.InUse.Count,
                    result.FolderUnavailable.Count,
                    result.Failed.Count);
                break;
            default:
                _logger.LogInformation(
                    "A user that no longer exists ({CallerId}) deleted {Deleted} clips at once ({Removed} of them of deleted bookmarks; {Kept} let go of a file another clip still names, which stays): {Clips}; {NotFound} not found, {InProgress} being made, {InUse} in use, {Offline} in a clip folder that isn't available, {Failed} that couldn't be deleted",
                    who.UserId,
                    deleted,
                    result.RecordsRemoved.Count,
                    kept,
                    clips,
                    result.NotFound.Count,
                    result.InProgress.Count,
                    result.InUse.Count,
                    result.FolderUnavailable.Count,
                    result.Failed.Count);
                break;
        }
    }

    // The AdminsCanManageAllClips gate, read on each request.
    private void EnsureOn()
    {
        if (!_configuration.GetConfiguration().AdminsCanManageAllClips)
        {
            throw BookmarkException.Forbidden(TurnedOffMessage);
        }
    }

    // A record with a clip, any user's, a deleted bookmark's kept clip included, that the caller may see (Listable); else 404.
    private Bookmark Find(Guid clipId, AdminCaller who)
        => _store.Get(clipId) is { Clip: not null } record && Listable(who)(record) ? record : throw BookmarkException.NotFound(NotFoundMessage);

    // Which clips the caller may see and act on (since 1.2.1, the class's Library access): an API key every one; an admin those
    // of the videos they can open, and those with no video to check only with every library; a user that no longer exists
    // none. Asked of the library once per video for the request; a look-up that fails counts as "can't".
    private Func<Bookmark, bool> Listable(AdminCaller who)
    {
        if (who.Kind == CallerKind.ApiKey)
        {
            return static _ => true;
        }

        if (who.User is not { } admin)
        {
            return static _ => false;
        }

        var everyLibrary = admin.HasPermission(PermissionKind.EnableAllFolders);
        var seen = new Dictionary<Guid, ItemVisibility>();
        return record =>
        {
            if (record.DetachedUtc is not null || !BookmarkRecordRules.HasLibraryItem(record))
            {
                return everyLibrary;
            }

            if (!seen.TryGetValue(record.ItemId, out var visibility))
            {
                try
                {
                    visibility = _libraries.Visibility(record.ItemId, admin);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not look up item {ItemId} for admin {UserId}'s view of every user's clips", record.ItemId, admin.Id);
                    visibility = ItemVisibility.Hidden;
                }

                seen[record.ItemId] = visibility;
            }

            return visibility switch
            {
                ItemVisibility.Visible => true,
                ItemVisibility.Unknown => everyLibrary,
                _ => false
            };
        };
    }

    // The users of this server now, by id (KitUsers, never IUserManager.Users directly), read once per request.
    private Dictionary<Guid, string> Owners()
        => KitUsers.GetAll(_users).GroupBy(u => u.Id).ToDictionary(g => g.Key, g => g.First().Username);

    private BookmarkAdminClipItemDto ToItemDto(ClipRow row, ClipViewer viewer, Dictionary<Guid, string> owners)
    {
        // As the owner's list (ClipService): the In library badge from the libraries' folders, a library's name only for one the
        // caller may open.
        var location = row.Clip.FilePath is { Length: > 0 } path ? _locations.Find(path) : null;
        var libraryName = location is { LibraryId: Guid libraryId } && viewer.CanOpenLibrary(libraryId) ? location.LibraryName : null;
        return BookmarkDtoMapper.ToAdminClipItemDto(
            row.Record,
            row.Facts,
            _clips.GetProgress(row.Record.Id),
            viewer.CanOpen(row.Record),
            location is not null,
            libraryName,
            owners.GetValueOrDefault(row.Record.UserId));
    }

    // Who called, as the audit names them: the admin (the library is asked as them), an API key by its name, or a user that no
    // longer exists by its id; Key tells callers apart for the preview's window.
    private readonly record struct AdminCaller(CallerKind Kind, User? User, Guid UserId, string? KeyName)
    {
        public string Key => Kind switch
        {
            CallerKind.ApiKey => "key:" + KeyName,
            _ => UserId.ToString("N", CultureInfo.InvariantCulture)
        };

        // For the Debug lines.
        public string Describe() => Kind switch
        {
            CallerKind.Admin => "Admin " + UserId.ToString("N", CultureInfo.InvariantCulture),
            CallerKind.ApiKey => "The API key " + KeyName,
            _ => "A user that no longer exists (" + UserId.ToString("N", CultureInfo.InvariantCulture) + ")"
        };
    }
}
