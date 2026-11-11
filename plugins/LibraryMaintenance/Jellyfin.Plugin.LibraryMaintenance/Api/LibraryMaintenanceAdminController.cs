using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Settings;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.LibraryMaintenance.Api;

/// <summary>
/// Library Maintenance's admin endpoints: Diagnostics, the settings and their export and import.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("LibraryMaintenance/Admin")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class LibraryMaintenanceAdminController : ControllerBase
{
    /// <summary>The largest settings body: 64 KB.</summary>
    internal const long MaxSettingsBytes = 64 * 1024;

    /// <summary>The largest import file: 1 MB (the third review; the kit's default is 100 MB, and 1.0's settings are a few KB).</summary>
    internal const long MaxImportBytes = 1024 * 1024;

    /// <summary>The Diagnostics entries whose numbers cover every library the movie index read.</summary>
    internal static readonly string[] ServerWideSources = ["DuplicatesIndex", "DuplicateGroups"];

    private readonly IKitDiagnostics _diagnostics;
    private readonly IDuplicatesIndex _index;
    private readonly IUserManager _users;
    private readonly ILibraryMaintenanceSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryMaintenanceAdminController"/> class.
    /// </summary>
    /// <param name="diagnostics">The kit's diagnostics.</param>
    /// <param name="index">The movie index, for whether the caller can see every library it read.</param>
    /// <param name="users">The user manager, for the caller's library access.</param>
    /// <param name="settings">The settings, their export and import.</param>
    public LibraryMaintenanceAdminController(IKitDiagnostics diagnostics, IDuplicatesIndex index, IUserManager users, ILibraryMaintenanceSettings settings)
    {
        _diagnostics = diagnostics;
        _index = index;
        _users = users;
        _settings = settings;
    }

    /// <summary>
    /// Gets the plugin's diagnostics: <c>PluginVersion</c>, <c>KitVersion</c>, <c>Abi</c> (the Jellyfin line the
    /// build is for and whether it runs), and the entries of the kit and of Library Maintenance. The movie index's and the
    /// groups' entries count every library the index read, so an administrator whose access is limited to some libraries
    /// gets a note in their place.
    /// </summary>
    /// <response code="200">The diagnostics, as JSON.</response>
    /// <returns>The diagnostics.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetLibraryMaintenanceDiagnostics()
    {
        var described = _diagnostics.Describe();
        if (!_index.SeesEveryLibrary(Caller()))
        {
            foreach (var key in ServerWideSources)
            {
                if (described.ContainsKey(key))
                {
                    described[key] = new Dictionary<string, object?>
                    {
                        ["Scope"] = "Hidden: these numbers cover every library the index read, and your access is limited to some of them."
                    };
                }
            }
        }

        return Ok(described);
    }

    /// <summary>
    /// Gets the settings: the 1.0 fields, their version (a save sends it back), the server's movie and
    /// mixed libraries, the article languages this version knows and the default edition phrases.
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>The Settings tab's data.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<LibraryMaintenanceSettingsViewDto> GetLibraryMaintenanceSettings()
    {
        _ = Caller();
        return Ok(_settings.Read());
    }

    /// <summary>
    /// Changes the settings: each field given replaces the saved one. A dry run (the default) only reports what
    /// changes; a change to the libraries the index reads or the folders it leaves out comes with a preview of the movies
    /// before and after. With <c>dryRun=false</c> the settings are saved, as the Dashboard's form saves them.
    /// </summary>
    /// <param name="settings">The fields to change; a field left out keeps its value.</param>
    /// <param name="version">The version of the settings the change was made from (<c>GET …/Settings</c>); a save needs
    /// it, and is refused when they were saved since.</param>
    /// <param name="dryRun">Whether only to report; true when left out.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">A value the settings can't take, saying which, or a save without its version.</response>
    /// <response code="409">The settings were saved since <paramref name="version"/> (<c>code</c> <c>SettingsChanged</c>).</response>
    /// <returns>What changes, and whether it was saved.</returns>
    [HttpPost("Settings")]
    [RequestSizeLimit(MaxSettingsBytes)]
    [Consumes(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<LibraryMaintenanceSettingsReportDto>> SaveLibraryMaintenanceSettings(
        [FromBody, Required] LibraryMaintenanceSettingsDto settings,
        [FromQuery] string? version,
        [FromQuery] bool? dryRun,
        CancellationToken cancellationToken)
        => Ok(await _settings.ChangeAsync(settings, version, dryRun ?? true, Caller(), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Exports the settings: the kit's envelope, as an attachment
    /// <c>librarymaintenance-export-&lt;time&gt;.json</c>, with the settings in <c>Settings</c> and no data (1.0 keeps none).
    /// It holds the excluded folders' paths: admin-only and <c>no-store</c>.
    /// </summary>
    /// <response code="200">The file.</response>
    /// <returns>The export.</returns>
    [HttpGet("Export")]
    [Produces("application/json")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult ExportLibraryMaintenance()
    {
        _ = Caller();
        return _settings.Export(HttpContext);
    }

    /// <summary>
    /// Imports the settings of an export: the body is the file, <c>application/json</c>. A dry run (the
    /// default) reports what would change, with the preview of a change to the index's scope and what the file holds that
    /// this server can't use (libraries not on it, folders that aren't full paths here, values this version doesn't know),
    /// which is left out. With <c>dryRun=false</c> the 1.0 settings are saved.
    /// </summary>
    /// <param name="version">The version of the settings the dry run was checked against; a save needs it, and is refused
    /// when they were saved since.</param>
    /// <param name="dryRun">Whether only to report; true when left out.</param>
    /// <param name="cancellationToken">Cancels the read and the preview.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Not an export of Library Maintenance, settings it can't read, or a save without its version.</response>
    /// <response code="409">A newer schema (<c>code</c> <c>NewerSchema</c>), or the settings were saved since
    /// <paramref name="version"/> (<c>SettingsChanged</c>).</response>
    /// <response code="413">Over 1 MB.</response>
    /// <returns>What changes, and whether it was saved.</returns>
    [HttpPost("Import")]
    [RequestSizeLimit(MaxImportBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<LibraryMaintenanceSettingsReportDto>> ImportLibraryMaintenance([FromQuery] string? version, [FromQuery] bool? dryRun, CancellationToken cancellationToken)
    {
        var caller = Caller();
        return Ok(await _settings.ImportAsync(Request, version, dryRun ?? true, caller, cancellationToken).ConfigureAwait(false));
    }

    // The caller whose library access applies: none for an API key (every library); a user that no longer exists is a 403.
    private User? Caller() => User.IsApiKey() ? null : User.RequireUser(_users);
}
