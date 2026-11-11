using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A warning or blocker of a plan (a <c>move.*</c> code).
/// </summary>
public sealed class LibraryMaintenanceMoveIssueDto
{
    /// <summary>
    /// Gets or sets the code.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the sentence shown.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the paths it is about.
    /// </summary>
    public IReadOnlyList<string>? Paths { get; set; }
}
