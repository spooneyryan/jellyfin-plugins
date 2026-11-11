namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What happened to a library item, as <see cref="LibraryChangeMonitor"/> saw it.
/// </summary>
internal enum LibraryChangeKind
{
    /// <summary><c>ILibraryManager.ItemAdded</c>.</summary>
    Added,

    /// <summary><c>ILibraryManager.ItemUpdated</c>.</summary>
    Updated,

    /// <summary><c>ILibraryManager.ItemRemoved</c>: only the removed item itself, never the descendants a folder took with it.</summary>
    Removed,

    /// <summary><c>IUserDataManager.UserDataSaved</c> (opt-in, <see cref="LibraryChangeFilter.IncludeUserData"/>).</summary>
    UserData,
}
