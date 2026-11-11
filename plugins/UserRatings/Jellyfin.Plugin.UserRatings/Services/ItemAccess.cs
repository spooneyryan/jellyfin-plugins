using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// Which items a member may read and rate. One id is checked with
/// <c>GetItemById&lt;BaseItem&gt;(id, user)</c> (through the kit's <see cref="LibraryAccess"/>): parental rules, parents and
/// the user's libraries, so an item the user can't open and one that isn't rateable get the same 404, and existence doesn't
/// leak. A list goes through the kit's <see cref="LibraryAccess.EligibleIds"/> (one <c>GetItemList</c> per 500 ids with the
/// user's libraries, then every item's parents checked as <c>GetItemById</c> checks them, each parent once), with a
/// per-item check for the ids it drops, since it leaves owned items out by design; the batch measured about 15 times faster
/// than per item at 5,000 ids, with identical answers. Its answers were identical only for items no parent hides: before
/// the kit's parent check a TV-PG episode of a TV-MA series reached <c>OtherCopies</c> and stayed
/// out of <c>Mine/Inaccessible</c> while its own page was a 404. Never <c>InternalItemsQuery.ItemIds</c> alone and never a
/// user-less <c>GetItemById&lt;T&gt;(id, null)</c> as the access check. A member's write also asks whether the library still
/// has the item (<see cref="GetWritable"/>): reads answer a cache ghost as core does.
/// </summary>
internal sealed class ItemAccess
{
    /// <summary>The one 404 for an item that doesn't exist, isn't visible to the caller or can't be rated.</summary>
    public const string NotFoundMessage = "This item wasn't found, or it can't be rated.";

    /// <summary>The most ids of one list checked one by one after the batch dropped them; the rest stay out (never shown).</summary>
    public const int PerItemFallbackLimit = 200;

    private readonly LibraryAccess _access;
    private readonly ILibraryManager _library;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemAccess"/> class.
    /// </summary>
    /// <param name="access">The kit's library access.</param>
    /// <param name="library">The library manager, for <see cref="IsInLibrary"/>.</param>
    /// <param name="logger">The logger; none by default.</param>
    public ItemAccess(LibraryAccess access, ILibraryManager library, ILogger<ItemAccess>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(library);
        _access = access;
        _library = library;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Gets an item's kind as <see cref="BaseItemKind"/>, or <c>null</c> for a type the enum doesn't name.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The kind.</returns>
    public static BaseItemKind? KindOf(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            return item.GetBaseItemKind();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells whether items of this one's kind can be rated (the <c>RateableItemTypes</c> setting).
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="settings">The settings of the request.</param>
    /// <returns>Whether it is rateable.</returns>
    public static bool IsRateable(BaseItem item, RatingsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return KindOf(item) is { } kind && settings.IsRateable(kind);
    }

    /// <summary>
    /// Gets an item as the user sees it: <c>GetItemById&lt;BaseItem&gt;(id, user)</c>.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="user">The user.</param>
    /// <returns>The item, or <c>null</c> when it doesn't exist or the user can't open it.</returns>
    public BaseItem? GetVisible(Guid itemId, User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return _access.GetItem<BaseItem>(itemId, user);
    }

    /// <summary>
    /// Gets an item the user can open and rate, or the one 404.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="user">The user.</param>
    /// <param name="settings">The settings of the request.</param>
    /// <returns>The item as the user sees it.</returns>
    /// <exception cref="PluginException">404 (<see cref="NotFoundMessage"/>) when it doesn't exist, isn't visible or isn't rateable.</exception>
    public BaseItem GetRateable(Guid itemId, User user, RatingsSettings settings)
    {
        var item = GetVisible(itemId, user);
        if (item is null || !IsRateable(item, settings))
        {
            throw PluginException.NotFound(NotFoundMessage);
        }

        return item;
    }

    /// <summary>
    /// Gets an item the user can open and rate, and that the library still has, for a member's write (a rating, a review,
    /// clearing or deleting their own), or the one 404. 10.11.5's <c>GetItemById</c> still answers a deleted music artist
    /// folder's albums from its cache, and a rating written on one would be detached by the next
    /// maintenance. Reads take <see cref="GetRateable"/> and answer such an item as core does, with
    /// <c>Can.Reason</c> <see cref="RatingsService.NotInLibraryReason"/>.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="user">The user.</param>
    /// <param name="settings">The settings of the request.</param>
    /// <returns>The item as the user sees it.</returns>
    /// <exception cref="PluginException">404 (<see cref="NotFoundMessage"/>) when it doesn't exist, isn't visible, isn't
    /// rateable or isn't in the library any more.</exception>
    public BaseItem GetWritable(Guid itemId, User user, RatingsSettings settings)
    {
        var item = GetRateable(itemId, user, settings);
        if (!IsInLibrary(item))
        {
            throw PluginException.NotFound(NotFoundMessage);
        }

        return item;
    }

    /// <summary>
    /// Tells whether the library still has the item and every parent up its chain (the kit's <see cref="LibraryPresence"/>):
    /// false for a cache ghost of a deleted folder.
    /// </summary>
    /// <param name="item">The item, as <c>GetItemById</c> gave it.</param>
    /// <returns>Whether it is in the library.</returns>
    public bool IsInLibrary(BaseItem item) => LibraryPresence.IsInLibrary(_library, item);

    /// <summary>
    /// Tells which of the ids the user can open (Summaries, <c>OtherCopies</c>, <c>Mine/Inaccessible</c>, Browse): the kit's
    /// batch, then a per-item check of the ids it dropped (at most <see cref="PerItemFallbackLimit"/>).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="itemIds">The ids.</param>
    /// <returns>The ids the user can open.</returns>
    public IReadOnlySet<Guid> VisibleIds(User user, IReadOnlyCollection<Guid> itemIds)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(itemIds);
        var wanted = itemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var visible = new HashSet<Guid>(_access.EligibleIds(user, _access.VisibleLibraries(user), wanted));
        var dropped = wanted.Where(id => !visible.Contains(id)).ToList();
        foreach (var id in dropped.Take(PerItemFallbackLimit))
        {
            // An owned item (an extra, a local alternate version) has no top parent, so the batch leaves it out; core's own
            // check is the details page's.
            if (_access.GetItem<BaseItem>(id, user) is not null)
            {
                visible.Add(id);
            }
        }

        if (dropped.Count > PerItemFallbackLimit)
        {
            _logger.LogDebug("Checked {Checked} of {Dropped} ids the library batch left out one by one; the others stay out", PerItemFallbackLimit, dropped.Count);
        }

        return visible;
    }

    /// <summary>
    /// Gets the libraries the user can open (for a sibling copy's library name).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<CollectionFolder> VisibleLibraries(User user) => _access.VisibleLibraries(user);

    /// <summary>
    /// Gets every library of the server, whoever can open it (the JE export's restricted check, 0.3.24).
    /// </summary>
    /// <returns>The libraries, each once.</returns>
    public IReadOnlyList<CollectionFolder> AllLibraries() => _access.VisibleLibraries(null);
}
