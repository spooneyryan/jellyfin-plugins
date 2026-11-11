using System.Collections.Generic;
using System.Net.Mime;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// The developer demo of the kit's library-change monitor, for sandbox checks only: admins, and only while the
/// developer setting <c>EnableLibraryChangeDemo</c> is on (403 "turned off" otherwise). It shows the batches the demo
/// consumer received (ids, kinds, flags and counts; no names or paths) and writes nothing.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Bookmarks/Admin/LibraryChangeDemo")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class BookmarksLibraryChangeDemoController : ControllerBase
{
    private readonly IBookmarkLibraryChangeDemo _demo;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksLibraryChangeDemoController"/> class.
    /// </summary>
    /// <param name="demo">The library change demo.</param>
    public BookmarksLibraryChangeDemoController(IBookmarkLibraryChangeDemo demo)
    {
        _demo = demo;
    }

    /// <summary>
    /// Lists the batches of library changes the demo consumer received, oldest first: <c>{Received, Batches:[{Seq,
    /// ReceivedUtc, FullPass, DuringPostScan, AfterFullPass, ScanRunning, Kinds, Counts, Added, Updated, Removed,
    /// RemovedFolders, UserData}]}</c>.
    /// </summary>
    /// <response code="200">The batches.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>What the demo consumer received.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksLibraryChangeDemo() => Ok(_demo.Describe());

    /// <summary>
    /// Forgets the batches received so far.
    /// </summary>
    /// <response code="204">Forgotten.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>No content.</returns>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult DeleteBookmarksLibraryChangeDemo()
    {
        _demo.Clear();
        return NoContent();
    }
}
