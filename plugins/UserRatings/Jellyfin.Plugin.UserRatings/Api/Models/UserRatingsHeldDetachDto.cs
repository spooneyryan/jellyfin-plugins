namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The answer of <c>POST /UserRatings/Admin/Held/Detach</c>: what an admin's "Detach now" detached.
/// </summary>
public sealed class UserRatingsHeldDetachDto
{
    /// <summary>
    /// Gets or sets how many of the items had kept ratings that are now detached (Missing).
    /// </summary>
    public int Items { get; set; }

    /// <summary>
    /// Gets or sets how many ratings were detached.
    /// </summary>
    public int Ratings { get; set; }

    /// <summary>
    /// Gets or sets how many of the ids had no kept ratings: never held, or their item is back in the library (then they
    /// stay on it).
    /// </summary>
    public int NotHeld { get; set; }
}
