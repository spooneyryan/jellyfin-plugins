namespace Jellyfin.Plugin.TrailersFixture;

/// <summary>
/// Stands in for a plugin controller that takes one of Jellyfin's controllers and calls it in process, as Jellyfin's own
/// <c>TrailersController</c> takes <c>ItemsController</c>: the rule table's self-check flags it.
/// </summary>
internal sealed class TrailersLikeController
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TrailersLikeController"/> class.
    /// </summary>
    /// <param name="users">The Jellyfin controller it would call.</param>
    public TrailersLikeController(Jellyfin.Api.Controllers.UserController users)
    {
        Users = users;
    }

    /// <summary>Gets the Jellyfin controller.</summary>
    public Jellyfin.Api.Controllers.UserController Users { get; }
}
