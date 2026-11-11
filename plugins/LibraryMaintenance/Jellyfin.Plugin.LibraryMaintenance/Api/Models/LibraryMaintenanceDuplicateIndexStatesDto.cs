namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The number of index entries in each broken version-set state (<c>GET /Index</c>); an entry can be in
/// several, so the counts don't add up to the broken entries.
/// </summary>
public sealed class LibraryMaintenanceDuplicateIndexStatesDto
{
    /// <summary>
    /// Gets or sets the entries whose root points at a primary that no longer exists.
    /// </summary>
    public int Orphaned { get; set; }

    /// <summary>
    /// Gets or sets the entries whose primary lists versions that resolve to nothing.
    /// </summary>
    public int StaleLinks { get; set; }

    /// <summary>
    /// Gets or sets the entries with a member that points at another member instead of the primary.
    /// </summary>
    public int Chained { get; set; }

    /// <summary>
    /// Gets or sets the entries with a member the primary doesn't list.
    /// </summary>
    public int Unlisted { get; set; }

    /// <summary>
    /// Gets or sets the entries whose primary lists an item that doesn't point back.
    /// </summary>
    public int ListedNotPointing { get; set; }

    /// <summary>
    /// Gets or sets the entries whose pointers go round in a circle.
    /// </summary>
    public int Cycle { get; set; }
}
