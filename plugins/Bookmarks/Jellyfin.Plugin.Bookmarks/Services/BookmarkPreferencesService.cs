using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Bookmarks.Storage;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Implements <see cref="IBookmarkPreferences"/> on the store's <c>UserPreferences</c>, mapping the record to its
/// DTO.
/// </summary>
internal sealed class BookmarkPreferencesService : IBookmarkPreferences
{
    private readonly IBookmarkStore _store;
    private readonly ILogger<BookmarkPreferencesService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkPreferencesService"/> class.
    /// </summary>
    /// <param name="store">The bookmark store.</param>
    /// <param name="logger">The logger.</param>
    public BookmarkPreferencesService(IBookmarkStore store, ILogger<BookmarkPreferencesService> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public BookmarkPreferencesDto Get(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var stored = _store.GetPreferences(user.Id);
        return new BookmarkPreferencesDto { AskForName = stored?.AskForName, PauseWhileNaming = stored?.PauseWhileNaming ?? false };
    }

    /// <inheritdoc />
    public async Task SetAsync(User user, BookmarkPreferencesDto preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(preferences);

        // Read-only data is the store's 503, before anything else.
        _store.ThrowIfReadOnly();

        // The defaults are no entry at all (the store keeps an entry with fields of a later version).
        var value = preferences.AskForName is null && !preferences.PauseWhileNaming
            ? null
            : new BookmarkUserPreferences { AskForName = preferences.AskForName, PauseWhileNaming = preferences.PauseWhileNaming };
        await _store.SetPreferencesAsync(user.Id, value, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("User {UserId} set their naming preferences", user.Id);
    }
}
