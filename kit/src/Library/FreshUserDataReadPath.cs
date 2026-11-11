namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// How <see cref="FreshUserData"/> loads an item's user-data rows from the database.
/// </summary>
internal enum FreshUserDataReadPath
{
    /// <summary>
    /// <c>ILibraryManager.GetItemList</c> with <c>ItemIds = [id]</c> and <c>DtoOptions.EnableUserData</c>, no user
    /// (<c>Include(e =&gt; e.UserData)</c>, <c>BaseItemRepository.cs:428-431</c>). The default; a sandbox check of the
    /// fresh read chose it.
    /// </summary>
    Query,

    /// <summary>
    /// <c>IItemRepository.RetrieveItem(id)</c>, which always includes the user data (<c>BaseItemRepository.cs:763-781</c>):
    /// the fallback when the query path returns the item without its rows.
    /// </summary>
    Repository
}
