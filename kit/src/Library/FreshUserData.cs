using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// The only way a plugin reads <c>UserItemData</c> before a read-modify-write or for a read that decides something (on
/// 10.11.5 core drops the cached item's user data after a change to a child, and <c>GetUserData</c> then answers with a
/// blank record): it reloads the item's user-data rows from the database and puts them on core's cached instance first.
/// </summary>
/// <remarks>
/// <para>On 10.11.5 <c>IUserDataManager.GetUserData(user, item)</c> reads only the item's in-memory snapshot
/// (<c>item.UserData</c>, <c>UserDataManager.cs:239-245</c>) and answers with a blank record when it is null. Core sets it to
/// null on the cached parent whenever a child is created, updated or deleted (<c>LibraryManager.cs:463, 2006, 2169</c>; the
/// parent is the cached instance, <c>BaseItem.cs:1008-1017, 2053-2054</c>), so after a scan or a refresh the cached Season,
/// Series or MusicAlbum reads as unplayed, not a favourite, play count 0, no position. Saving that record writes every
/// column (<c>UserDataManager.cs:58-72</c>) and would reset them. Upstream fixed it in 10.11.10 (<c>d2df6adc16</c>); there
/// and on 12 the extra read is harmless.</para>
/// <para>The read has no user: it isn't an access filter. Check access first (<c>LibraryAccess.GetItem</c>, or
/// <c>GetItemById&lt;T&gt;(id, user)</c>). When the item can't be loaded the answer is null ("UserDataUnavailable"), and
/// nothing may be written from an unknown state. A core write between the read and a save (a playback-progress save) can
/// still be lost; core's own DTO update does the same read-modify-write (<c>UserDataManager.cs:96-137</c>).</para>
/// </remarks>
internal sealed class FreshUserData
{
    private readonly ILibraryManager _library;
    private readonly IUserDataManager _userData;
    private readonly IItemRepository? _repository;
    private readonly ILogger _logger;
    private long _reads;
    private long _blankSnapshots;
    private long _fallbacks;
    private long _unavailable;
    private int _fallbackLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="FreshUserData"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="userData">The user data manager.</param>
    /// <param name="repository">The item repository, for the <see cref="FreshUserDataReadPath.Repository"/> path; null
    /// leaves only the query path.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="readPath">The read path to use first.</param>
    public FreshUserData(
        ILibraryManager library,
        IUserDataManager userData,
        IItemRepository? repository = null,
        ILogger? logger = null,
        FreshUserDataReadPath readPath = FreshUserDataReadPath.Query)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(userData);
        _library = library;
        _userData = userData;
        _repository = repository;
        _logger = logger ?? NullLogger.Instance;
        ReadPath = readPath;
    }

    /// <summary>
    /// Gets the read path used first. The other one is the fallback: the query path falls back to the repository when it
    /// returns the item without its user data, or no item at all.
    /// </summary>
    public FreshUserDataReadPath ReadPath { get; }

    /// <summary>
    /// Reloads an item's user-data rows from the database onto core's cached instance (<c>GetItemById</c>).
    /// </summary>
    /// <param name="itemId">The item id; the empty GUID is never looked up.</param>
    /// <param name="readPath">The read path, or null for <see cref="ReadPath"/>.</param>
    /// <returns>The cached instance with fresh rows, the path that loaded them, whether it had a snapshot before and the row
    /// count; null when the item can't be loaded.</returns>
    public (BaseItem Item, FreshUserDataReadPath Source, bool HadSnapshot, int Rows)? Refresh(Guid itemId, FreshUserDataReadPath? readPath = null)
    {
        Interlocked.Increment(ref _reads);
        if (itemId == Guid.Empty || _library.GetItemById(itemId) is not { } cached)
        {
            Interlocked.Increment(ref _unavailable);
            return null;
        }

        var path = readPath ?? ReadPath;
        var fresh = Load(itemId, path);
        if (fresh?.UserData is null && path == FreshUserDataReadPath.Query && _repository is not null)
        {
            Interlocked.Increment(ref _fallbacks);
            if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
            {
                _logger.LogWarning(
                    "Reading user data through GetItemList returned {What}; reading it through the item repository instead",
                    fresh is null ? "no item" : "the item without its user data");
            }

            path = FreshUserDataReadPath.Repository;
            fresh = Load(itemId, path);
        }

        if (fresh?.UserData is not { } rows)
        {
            Interlocked.Increment(ref _unavailable);
            return null;
        }

        var hadSnapshot = cached.UserData is not null;
        if (!hadSnapshot)
        {
            Interlocked.Increment(ref _blankSnapshots);
        }

        // The rows of every user, as core's own rehydration after a save sets them (UserDataManager.cs:83).
        cached.UserData = rows;
        return (cached, path, hadSnapshot, rows.Count);
    }

    /// <summary>
    /// Reads a user's data for an item from the database: the rows are reloaded onto core's cached instance,
    /// which also repairs its stale snapshot, then read through <c>IUserDataManager.GetUserData</c>.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="user">The user.</param>
    /// <param name="readPath">The read path, or null for <see cref="ReadPath"/>.</param>
    /// <returns>The data, or null when the item can't be loaded (never write then).</returns>
    public FreshUserDataResult? Read(Guid itemId, User user, FreshUserDataReadPath? readPath = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (Refresh(itemId, readPath) is not { } fresh)
        {
            return null;
        }

        var data = _userData.GetUserData(user, fresh.Item) ?? new UserItemData { Key = fresh.Item.GetUserDataKeys().First() };
        return new FreshUserDataResult { Item = fresh.Item, Data = data, Source = fresh.Source, HadSnapshot = fresh.HadSnapshot, Rows = fresh.Rows };
    }

    /// <summary>
    /// Changes a user's data for an item from its stored state: a fresh <see cref="Read"/>, the change, then
    /// <c>SaveUserData</c> through core's cached instance, so the user's other fields (favourite, played, play count,
    /// resume position) are saved as stored.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="user">The user.</param>
    /// <param name="change">Changes the record; returns false to write nothing.</param>
    /// <param name="reason">The save reason (User Ratings uses <c>UpdateUserData</c>, which writes no NFO).</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>The data as read and changed, or null when the item can't be loaded (nothing written).</returns>
    public FreshUserDataResult? Update(Guid itemId, User user, Func<UserItemData, bool> change, UserDataSaveReason reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(change);
        if (Read(itemId, user) is not { } fresh)
        {
            return null;
        }

        if (change(fresh.Data))
        {
            _userData.SaveUserData(user, fresh.Item, fresh.Data, reason, cancellationToken);
        }

        return fresh;
    }

    /// <summary>
    /// Describes the reads so far, for a plugin's Diagnostics.
    /// </summary>
    /// <returns>The counts.</returns>
    public IDictionary<string, object?> Describe() => new Dictionary<string, object?>
    {
        ["ReadPath"] = ReadPath.ToString(),
        ["Reads"] = Interlocked.Read(ref _reads),
        ["BlankSnapshots"] = Interlocked.Read(ref _blankSnapshots),
        ["Fallbacks"] = Interlocked.Read(ref _fallbacks),
        ["Unavailable"] = Interlocked.Read(ref _unavailable)
    };

    private BaseItem? Load(Guid itemId, FreshUserDataReadPath path)
    {
        if (path == FreshUserDataReadPath.Repository)
        {
            return _repository?.RetrieveItem(itemId);
        }

        // No user: this isn't an access filter (the caller checked access), and no grouping or user filter may drop it.
        return _library.GetItemList(new InternalItemsQuery
        {
            ItemIds = [itemId],
            DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = true }
        }).FirstOrDefault(i => i is not null && i.Id == itemId);
    }
}
