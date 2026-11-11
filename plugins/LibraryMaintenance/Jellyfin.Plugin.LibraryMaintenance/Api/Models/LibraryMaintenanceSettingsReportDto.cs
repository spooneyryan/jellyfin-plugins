using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The answer to a settings save or import (<c>POST /Admin/Settings</c>, <c>POST /Admin/Import</c>): what changes,
/// what that does to the index, and whether it was saved.
/// </summary>
public sealed class LibraryMaintenanceSettingsReportDto
{
    /// <summary>
    /// Gets or sets a value indicating whether nothing was saved (<c>dryRun</c>, the default).
    /// </summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the settings were saved.
    /// </summary>
    public bool Applied { get; set; }

    /// <summary>
    /// Gets or sets the version of the settings saved now: the new one after a save, else the current one.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the settings the change gives (after a save, the saved ones), every field given.
    /// </summary>
    public LibraryMaintenanceSettingsDto Settings { get; set; } = new();

    /// <summary>
    /// Gets or sets each setting that changes; none when the settings are the same.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceSettingsChangeDto> Changes { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the index's scope changes: the libraries it reads or the folders it leaves
    /// out. Such a change is previewed (<see cref="Preview"/>) before it is saved.
    /// </summary>
    public bool ScopeChanged { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether saving builds the index again (its scope changes, or Duplicates is turned
    /// on).
    /// </summary>
    public bool RebuildsIndex { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the title rules change (the groups are worked out again; the index isn't
    /// built again).
    /// </summary>
    public bool TitleRulesChanged { get; set; }

    /// <summary>
    /// Gets or sets the preview of a scope change, or null.
    /// </summary>
    public LibraryMaintenanceSettingsPreviewDto? Preview { get; set; }

    /// <summary>
    /// Gets or sets what an import left out or couldn't use, as sentences.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; set; } = [];

    /// <summary>
    /// Gets or sets where an imported file came from; null for a save.
    /// </summary>
    public LibraryMaintenanceSettingsSourceDto? Source { get; set; }
}
