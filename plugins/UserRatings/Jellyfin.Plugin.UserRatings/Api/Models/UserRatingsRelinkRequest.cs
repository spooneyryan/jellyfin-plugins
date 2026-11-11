using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The body of <c>POST Admin/Detached/{ratingId}/Relink</c>.
/// </summary>
public sealed class UserRatingsRelinkRequest
{
    /// <summary>Gets or sets the item to relink to.</summary>
    public Guid? ItemId { get; set; }
}
