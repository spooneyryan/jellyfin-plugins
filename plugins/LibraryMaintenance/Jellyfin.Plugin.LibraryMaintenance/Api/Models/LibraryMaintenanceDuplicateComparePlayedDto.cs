namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// How the server's users played a copy: counts only, no user is named. Jellyfin keeps watch data per item, so another
/// copy doesn't share it.
/// </summary>
public sealed class LibraryMaintenanceDuplicateComparePlayedDto
{
    /// <summary>
    /// Gets or sets how many users have it marked as played.
    /// </summary>
    public int UsersPlayed { get; set; }

    /// <summary>
    /// Gets or sets how many users have a resume point in it.
    /// </summary>
    public int UsersInProgress { get; set; }

    /// <summary>
    /// Gets or sets every user's plays added up.
    /// </summary>
    public int PlayCount { get; set; }
}
