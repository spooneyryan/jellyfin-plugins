using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// One user of the Settings tab's user picker (<c>RaterUserIds</c>): a user of this server, or an id the
/// settings hold that is no longer one.
/// </summary>
public sealed class UserRatingsSettingsUserDto
{
    /// <summary>Gets or sets the user's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the user's name; empty for an id that isn't a user of this server.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the id is in the settings but no longer a user of this server.</summary>
    public bool Missing { get; set; }
}
