namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// How an import finds the export's users on this server (<c>mapUsersBy=</c>).
/// </summary>
internal enum MapUsersBy
{
    /// <summary>The same user id (the same server, or a restore of it).</summary>
    Id,

    /// <summary>The export's <c>Users[]</c> name matched against this server's user names, ignoring case (another server).</summary>
    Name
}
