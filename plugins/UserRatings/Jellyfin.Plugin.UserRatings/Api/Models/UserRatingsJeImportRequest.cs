using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The body of <c>POST /UserRatings/Admin/Import/JellyfinEnhanced</c>. Every field may be left out.
/// </summary>
public sealed class UserRatingsJeImportRequest
{
    /// <summary>Gets or sets <c>First</c> (the default: one copy per review) or <c>All</c> (a rating on every copy with the TMDb id).</summary>
    public string? Copies { get; set; }

    /// <summary>Gets or sets a value indicating whether a review whose item isn't in the library is stored detached (default on).</summary>
    public bool? StoreUnmatched { get; set; }

    /// <summary>Gets or sets a value indicating whether a review an admin purged after an earlier import is made again (default off).</summary>
    public bool? RecreatePurged { get; set; }

    /// <summary>Gets or sets the map of Jellyfin Enhanced user ids this server doesn't know to its own users' ids.</summary>
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "A request body the JSON reader fills.")]
    public Dictionary<string, string>? UserMap { get; set; }
}
