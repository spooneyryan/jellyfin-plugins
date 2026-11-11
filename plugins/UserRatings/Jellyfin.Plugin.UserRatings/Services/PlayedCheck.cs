using System;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// <c>RequirePlayedToRate</c>'s check, the plugin's first use of the kit's <see cref="FreshUserData"/>. A leaf item (a movie, an episode, a video, a book…) is played when
/// its user data, read fresh from the database, says so: on 10.11.5 <c>GetUserData</c> on core's cached instance can answer
/// with a blank record (core sets a parent's snapshot to null when a child changes), and the fresh read repairs that
/// snapshot too. A Series, Season or MusicAlbum is played when any of its episodes or tracks is, which a count query with
/// the user's <c>IsPlayed</c> filter answers in SQL, never the container's own cached flag.
/// </summary>
internal sealed class PlayedCheck
{
    private readonly ILibraryManager _library;
    private readonly FreshUserData _userData;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayedCheck"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="userData">The kit's fresh user-data reader.</param>
    /// <param name="logger">The logger; none by default.</param>
    public PlayedCheck(ILibraryManager library, FreshUserData userData, ILogger<PlayedCheck>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(userData);
        _library = library;
        _userData = userData;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Tells whether the user has played the item (or, for a series, season or album, any of its episodes or tracks). The
    /// caller checked access first: the fresh read is no access filter.
    /// </summary>
    /// <param name="item">The item, as <c>GetItemById&lt;BaseItem&gt;(id, user)</c> returned it.</param>
    /// <param name="user">The user.</param>
    /// <returns>Whether it is played; <c>null</c> when the user data couldn't be read (decide nothing from it).</returns>
    public bool? HasPlayed(BaseItem item, User user)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(user);
        BaseItemKind[]? children = ItemAccess.KindOf(item) switch
        {
            BaseItemKind.Series or BaseItemKind.Season => [BaseItemKind.Episode],
            BaseItemKind.MusicAlbum => [BaseItemKind.Audio],
            _ => null
        };

        if (children is not null)
        {
            return _library.GetCount(new InternalItemsQuery(user)
            {
                AncestorIds = [item.Id],
                IsPlayed = true,
                Recursive = true,
                IncludeItemTypes = children,
                Limit = 1
            }) > 0;
        }

        if (_userData.Read(item.Id, user) is not { } fresh)
        {
            _logger.LogWarning("The user data of item {ItemId} couldn't be read from the database, so whether it was played isn't known", item.Id);
            return null;
        }

        return fresh.Data.Played;
    }
}
