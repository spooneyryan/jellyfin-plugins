using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The Settings tab's data (<c>GET /Admin/Settings</c>): the saved settings, their version, and what the fields
/// offer.
/// </summary>
public sealed class LibraryMaintenanceSettingsViewDto
{
    /// <summary>
    /// Gets or sets the saved settings, every field given.
    /// </summary>
    public LibraryMaintenanceSettingsDto Settings { get; set; } = new();

    /// <summary>
    /// Gets or sets the saved settings' version: 16 hex digits of a SHA-256 of their values. A save sends it back, and is
    /// refused (409) when the settings were saved since.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the server's <c>movies</c> and mixed libraries, in the server's order, then any included id that is
    /// no longer one.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceSettingsLibraryDto> Libraries { get; set; } = [];

    /// <summary>
    /// Gets or sets the languages this version knows, each with the articles it drops.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ArticlesByLanguage { get; set; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Gets or sets the default edition phrases.
    /// </summary>
    public IReadOnlyList<string> DefaultEditionPhrases { get; set; } = [];
}
