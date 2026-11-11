namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// How <see cref="LibraryAccess.EligibleIds"/> counts owned items (extras, local trailers, local alternate versions:
/// <c>OwnerId</c> set, no top parent).
/// </summary>
internal enum OwnedItems
{
    /// <summary>Never eligible: they are no results of their own. The default.</summary>
    Exclude,

    /// <summary>
    /// Eligible when their first owner that isn't owned is eligible and they pass their own checks: for ids a client
    /// sent, where a user who can see a movie keeps its trailer (Access Roles' check of <c>ids</c>).
    /// </summary>
    FollowOwner,
}
