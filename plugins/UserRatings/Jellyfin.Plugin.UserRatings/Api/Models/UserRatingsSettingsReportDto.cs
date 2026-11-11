using System.Collections.Generic;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The answer of <c>POST /UserRatings/Admin/Settings</c>: what a change changes (or, a dry run, would change), what
/// that means for the members and the data, and the settings' version after it.
/// </summary>
public sealed class UserRatingsSettingsReportDto
{
    /// <summary>Gets or sets a value indicating whether nothing was saved (a dry run).</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets a value indicating whether the change was saved (false for a dry run, or a change that changes
    /// nothing).</summary>
    public bool Applied { get; set; }

    /// <summary>Gets or sets the settings' version now: after a save, the new one.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the settings after the change.</summary>
    public UserRatingsSettingsDto Settings { get; set; } = new();

    /// <summary>Gets or sets the settings the change changes, in the Settings tab's order; empty when none.</summary>
    public IReadOnlyList<UserRatingsSettingsChangeDto> Changes { get; set; } = [];

    /// <summary>Gets or sets what the change means for the members and the data, in sentences (members who can no longer
    /// rate, ratings a kind's removal hides, log rows the next maintenance deletes, …); the Settings tab asks before it saves
    /// a change that has any.</summary>
    public IReadOnlyList<string> Consequences { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether a change takes effect only after the server restarts.</summary>
    public bool RestartNeeded { get; set; }
}
