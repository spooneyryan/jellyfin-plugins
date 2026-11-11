using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Api.Models;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// A user's own naming preferences (the player's options, <c>UserPreferences</c>), kept in
/// <c>bookmarks.json</c> so they follow the user across devices. Only the caller's own; no user id is ever taken from a
/// request.
/// </summary>
/// <remarks>
/// Public because the public controller takes it: DTOs, Jellyfin's public types and BCL types only.
/// </remarks>
public interface IBookmarkPreferences
{
    /// <summary>
    /// Gets the user's preferences: the defaults (<c>AskForName</c> null, "Use server default"; no pause) when they chose
    /// none.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The preferences.</returns>
    BookmarkPreferencesDto Get(User user);

    /// <summary>
    /// Replaces the user's preferences. Setting the defaults removes the user's entry.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="preferences">The new preferences.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <returns>A task that completes once they are saved.</returns>
    /// <exception cref="Kit.Api.PluginException">503 while the bookmark data is read-only; nothing is written.</exception>
    Task SetAsync(User user, BookmarkPreferencesDto preferences, CancellationToken cancellationToken);
}
