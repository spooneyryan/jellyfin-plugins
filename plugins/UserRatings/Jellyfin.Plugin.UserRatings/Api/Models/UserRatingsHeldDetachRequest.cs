using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The body of <c>POST /UserRatings/Admin/Held/Detach</c>: the items whose kept ratings the admin
/// detaches, as Diagnostics' <c>Lifecycle.Held</c> lists them: <c>{"ItemIds": ["…"]}</c>.
/// </summary>
public sealed class UserRatingsHeldDetachRequest
{
    /// <summary>
    /// The most item ids one request takes.
    /// </summary>
    public const int MaxItemIds = 1000;

    /// <summary>
    /// Gets or sets the item ids: 1 to <see cref="MaxItemIds"/>. None, an empty list or more is a 400; an id whose ratings
    /// aren't kept (never held, or back in the library since) is counted in <c>NotHeld</c> and changes nothing.
    /// </summary>
    public IReadOnlyList<Guid>? ItemIds { get; set; }
}
