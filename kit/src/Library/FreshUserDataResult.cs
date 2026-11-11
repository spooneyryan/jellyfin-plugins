using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// A user's data for an item as stored in the database, read by <see cref="FreshUserData"/>.
/// </summary>
internal sealed record FreshUserDataResult
{
    /// <summary>
    /// Gets core's cached instance of the item, which now holds the rows just read. Save through it:
    /// <c>SaveUserData</c> rehydrates the snapshot of the instance it is given (<c>UserDataManager.cs:83</c>), so core's
    /// later reads of that instance see the change.
    /// </summary>
    public required BaseItem Item { get; init; }

    /// <summary>
    /// Gets the user's data: the stored row, or a true blank (unplayed, not a favourite, no position) when the user has
    /// none.
    /// </summary>
    public required UserItemData Data { get; init; }

    /// <summary>
    /// Gets the read path that loaded the rows.
    /// </summary>
    public required FreshUserDataReadPath Source { get; init; }

    /// <summary>
    /// Gets a value indicating whether the cached instance had a user-data snapshot before the read. False is the
    /// 10.11.5 case this exists for: core set it to null after a change to a child, and <c>GetUserData</c> would have
    /// answered with a blank record.
    /// </summary>
    public bool HadSnapshot { get; init; }

    /// <summary>
    /// Gets how many user-data rows the item has in the database, every user's.
    /// </summary>
    public int Rows { get; init; }
}
