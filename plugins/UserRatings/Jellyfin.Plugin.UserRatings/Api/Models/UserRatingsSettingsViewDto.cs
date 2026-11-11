using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The answer of <c>GET /UserRatings/Admin/Settings</c>: the settings, their defaults and version, and what
/// the Settings tab offers.
/// </summary>
public sealed class UserRatingsSettingsViewDto
{
    /// <summary>Gets or sets the saved settings, every value set (a name as saved, even one this version doesn't know).</summary>
    public UserRatingsSettingsDto Settings { get; set; } = new();

    /// <summary>Gets or sets every setting's default.</summary>
    public UserRatingsSettingsDto Defaults { get; set; } = new();

    /// <summary>Gets or sets the settings' version: a save sends it back as <c>version</c>, and is refused (409
    /// <c>SettingsChanged</c>) when the settings were saved since.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the users the picker offers: the server's users by name, then the ids the settings hold that
    /// are no longer users (<c>Missing</c>).</summary>
    public IReadOnlyList<UserRatingsSettingsUserDto> Users { get; set; } = [];

    /// <summary>Gets or sets the item kinds <c>RateableItemTypes</c> offers, then any other kind the settings hold.</summary>
    public IReadOnlyList<string> ItemKinds { get; set; } = [];

    /// <summary>Gets or sets the settings that need a restart to take effect.</summary>
    public IReadOnlyList<string> RestartFields { get; set; } = [];

    /// <summary>Gets or sets the saved values the plugin can't use as they are, each with what it uses instead (the log's
    /// warnings).</summary>
    public IReadOnlyList<string> Problems { get; set; } = [];
}
