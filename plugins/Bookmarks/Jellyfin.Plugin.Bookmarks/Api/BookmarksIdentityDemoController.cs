using System;
using System.Collections.Generic;
using System.Net.Mime;
using System.Text.Json;
using Jellyfin.Plugin.Bookmarks.Services;
using Jellyfin.Plugin.Kit.Api;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.Bookmarks.Api;

/// <summary>
/// The developer demo of the kit's item identity, for sandbox checks only: admins, and only while the developer
/// setting <c>EnableIdentityDemo</c> is on (403 "turned off" otherwise). It reads the library and the move journal and
/// writes nothing. Snapshots include server paths, which is why every action needs an admin. Admins have no library
/// bypass: ids resolve as the signed-in admin, and nothing names an item that admin can't see; only an
/// API key works over the whole server.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Bookmarks/Admin/IdentityDemo")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
public class BookmarksIdentityDemoController : ControllerBase
{
    private readonly IBookmarkIdentityDemo _demo;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarksIdentityDemoController"/> class.
    /// </summary>
    /// <param name="demo">The identity demo.</param>
    /// <param name="userManager">The user manager.</param>
    public BookmarksIdentityDemoController(IBookmarkIdentityDemo demo, IUserManager userManager)
    {
        _demo = demo;
        _userManager = userManager;
    }

    /// <summary>
    /// Takes the identity snapshot of a library item: <c>{Identity, StrongKeys}</c>, the snapshot as a record would store
    /// it and its lookup keys.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <response code="200">The snapshot.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <response code="404">No library item the caller can see has this id.</response>
    /// <returns>The item's snapshot and keys.</returns>
    [HttpGet("Items/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksIdentityDemoItem([FromRoute] Guid itemId) => Ok(_demo.Capture(itemId, GetCaller()));

    /// <summary>
    /// Resolves an identity snapshot (the body, as <c>Items/{itemId}</c> returns it) against the library and the move journal:
    /// id, then journal, then path, then provider ids, as a record of the caller would be.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="policy"><c>Default</c>, <c>Bookmarks</c> (runtime within 2 %, at most 60 s) or <c>ExactOnly</c>.</param>
    /// <param name="attachedUtc">When the record was attached to the snapshot's item; journal lines committed at or before
    /// it are not followed. Omitted: the whole journal.</param>
    /// <response code="200">The result.</response>
    /// <response code="400">The body isn't a snapshot, or the policy is unknown.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>What a consumer would do with the snapshot.</returns>
    [HttpPost("Resolve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IDictionary<string, object?>> ResolveBookmarksIdentityDemo([FromBody] JsonElement snapshot, [FromQuery] string? policy, [FromQuery] DateTime? attachedUtc)
        => Ok(_demo.Resolve(snapshot, policy, attachedUtc, GetCaller()));

    /// <summary>
    /// Reads the move journal: its state, the catch-up of a consumer at <c>(journalId, lastMoveSeq)</c>, and the pending
    /// relinks.
    /// </summary>
    /// <param name="journalId">The stored <c>MoveJournalId</c>, if any.</param>
    /// <param name="lastMoveSeq">The stored <c>LastMoveSeq</c>.</param>
    /// <response code="200">The journal.</response>
    /// <response code="403">The demo is turned off.</response>
    /// <returns>The journal's state and the catch-up.</returns>
    [HttpGet("Journal")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IDictionary<string, object?>> GetBookmarksIdentityDemoJournal([FromQuery] Guid? journalId, [FromQuery] long lastMoveSeq = 0)
        => Ok(_demo.Journal(journalId, lastMoveSeq, GetCaller()));

    // The signed-in admin, whose ids resolve as that admin; null for an API key, which works over the whole server.
    private JellyfinUser? GetCaller() => User.IsApiKey() ? null : User.RequireUser(_userManager);
}
