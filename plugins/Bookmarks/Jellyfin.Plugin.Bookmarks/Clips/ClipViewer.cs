using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Model;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What one request of the clip lists asks of the library as its caller: whether the caller can open each distinct
/// video, asked of Jellyfin once per video, and the libraries the caller may open, read once; never kept past the request,
/// never a user-less lookup. The owner's own list (<see cref="ClipService"/>) and the admins' "All users" scope
/// (<see cref="AdminClipService"/>, the admin as the caller) share it. A request without a user (an API key on an admin
/// route) can open nothing, so it gets no poster, no Play source and no library's name.
/// </summary>
internal sealed class ClipViewer
{
    private readonly ILibraryManager _library;
    private readonly LibraryAccess _libraries;
    private readonly ILogger _logger;
    private readonly User? _user;
    private readonly Dictionary<Guid, bool> _items = [];
    private HashSet<Guid>? _openLibraries;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipViewer"/> class.
    /// </summary>
    /// <param name="library">The library, asked as the caller.</param>
    /// <param name="libraries">The libraries the caller may open.</param>
    /// <param name="logger">The logger, for a look-up that fails (at Debug).</param>
    /// <param name="user">The caller, or <c>null</c> for an API key.</param>
    public ClipViewer(ILibraryManager library, LibraryAccess libraries, ILogger logger, User? user)
    {
        _library = library;
        _libraries = libraries;
        _logger = logger;
        _user = user;
    }

    /// <summary>
    /// Whether the caller can open a record's video now: attached to a library item that may be looked up (not detached, "Not in
    /// library", and not an adopted clip), and Jellyfin's own check as the caller says so. A lookup that fails counts
    /// as "can't", so one broken item costs its rows the poster and Play source, never the page.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <returns>Whether the caller can open the video.</returns>
    public bool CanOpen(Bookmark record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (_user is null || record.DetachedUtc is not null || !BookmarkRecordRules.HasLibraryItem(record))
        {
            return false;
        }

        if (!_items.TryGetValue(record.ItemId, out var canOpen))
        {
            canOpen = CanOpenItem(record.ItemId, _user);
            _items[record.ItemId] = canOpen;
        }

        return canOpen;
    }

    /// <summary>
    /// Whether the caller may open a library, for its name on the In library badge.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>Whether the caller may open it.</returns>
    public bool CanOpenLibrary(Guid libraryId) => _user is not null && (_openLibraries ??= OpenLibraries(_user)).Contains(libraryId);

    private bool CanOpenItem(Guid itemId, User user)
    {
        try
        {
            return _library.GetItemById<Video>(itemId, user) is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not look up item {ItemId} for user {UserId}'s clips", itemId, user.Id);
            return false;
        }
    }

    // The libraries the user may open: a failure gives none, so no library's name is shown.
    private HashSet<Guid> OpenLibraries(User user)
    {
        try
        {
            return _libraries.VisibleLibraries(user).Select(library => library.Id).ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the libraries user {UserId} may open", user.Id);
            return [];
        }
    }
}
