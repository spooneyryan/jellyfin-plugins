using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryMaintenance.Settings;

/// <summary>
/// The settings' endpoints' service: the Settings tab's data, a save with its preview, and the settings'
/// export and import.
/// </summary>
public interface ILibraryMaintenanceSettings
{
    /// <summary>
    /// Gets the saved settings, their version, the server's movie and mixed libraries, the article languages and the
    /// default edition phrases.
    /// </summary>
    /// <returns>The Settings tab's data.</returns>
    LibraryMaintenanceSettingsViewDto Read();

    /// <summary>
    /// Changes the settings: checks the values, reports what changes and previews a change to the index's scope; saves
    /// unless it is a dry run.
    /// </summary>
    /// <param name="change">The fields to change; a null field keeps its value.</param>
    /// <param name="basedOn">The version the change was made from; null skips the check.</param>
    /// <param name="dryRun">Whether nothing is saved.</param>
    /// <param name="caller">The administrator, or null for an API key.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <returns>The report.</returns>
    Task<LibraryMaintenanceSettingsReportDto> ChangeAsync(LibraryMaintenanceSettingsDto change, string? basedOn, bool dryRun, User? caller, CancellationToken cancellationToken);

    /// <summary>
    /// Answers the settings' export: the kit's envelope as an attachment, <c>no-store</c>.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The file.</returns>
    IActionResult Export(HttpContext context);

    /// <summary>
    /// Imports the settings of the export in the request's body, as a lenient change.
    /// </summary>
    /// <param name="request">The request; its body is the file.</param>
    /// <param name="basedOn">The version the import was checked against; null skips the check.</param>
    /// <param name="dryRun">Whether nothing is saved.</param>
    /// <param name="caller">The administrator, or null for an API key.</param>
    /// <param name="cancellationToken">Cancels the read and the preview.</param>
    /// <returns>The report.</returns>
    Task<LibraryMaintenanceSettingsReportDto> ImportAsync(HttpRequest request, string? basedOn, bool dryRun, User? caller, CancellationToken cancellationToken);
}
