namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;

/// <summary>
/// How <c>GET /Groups</c> orders the groups (<c>sort</c>). Every order ends with the title, then the key and
/// the group id, so a page never changes between requests with the same snapshot.
/// </summary>
internal enum GroupSort
{
    /// <summary>Confirmed, Likely, Possible, then Conflict; then the title (the default).</summary>
    Confidence = 0,

    /// <summary>The title key (case, accents and a leading article set aside), then the year.</summary>
    Title,

    /// <summary>The most reclaimable bytes first.</summary>
    Reclaimable,

    /// <summary>The most entries first.</summary>
    Size,

    /// <summary>The group with the most recently added movie first.</summary>
    Newest,
}
