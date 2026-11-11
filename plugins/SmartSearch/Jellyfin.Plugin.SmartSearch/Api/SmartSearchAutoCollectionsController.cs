using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SmartSearch.Api;

/// <summary>
/// Auto-collections: Jellyfin collections whose members follow a saved query. Every endpoint needs a user who manages
/// collections (an administrator, or a user allowed to manage collections); anyone else gets 403 before anything is
/// looked at. The two a collection's page reads (<c>Ids</c> and <c>ByCollection</c>) are open to any user, about the
/// collections they can open. An API key may only list and refresh.
/// </summary>
[ApiController]
[Authorize]
[Route("SmartSearch/AutoCollections")]
[KitAbiGate]
[PluginExceptionFilter]
public class SmartSearchAutoCollectionsController : ControllerBase
{
    private readonly ISmartSearchAutoCollections _autoCollections;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchAutoCollectionsController"/> class.
    /// </summary>
    /// <param name="autoCollections">The auto-collections.</param>
    /// <param name="userManager">The user manager.</param>
    public SmartSearchAutoCollectionsController(ISmartSearchAutoCollections autoCollections, IUserManager userManager)
    {
        _autoCollections = autoCollections;
        _userManager = userManager;
    }

    /// <summary>
    /// Checks whether a new collection can take a name. A collection of the same name (ignoring case) or on the same
    /// folder, a folder left on disk or a taken id is in the way; the answer then suggests free names. Writes nothing.
    /// </summary>
    /// <param name="request">The name, and the auto-collection it is for when it exists.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">Whether the name is free, what is in the way, warnings and suggestions.</response>
    /// <response code="400">The body or the name isn't valid (<c>InvalidRequest</c>, with <c>errors</c>).</response>
    /// <response code="403">An API key, a user who no longer exists, or a user who doesn't manage collections (<c>ManagerRequired</c>).</response>
    /// <returns>The outcome.</returns>
    [HttpPost("CheckName")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SmartSearchAutoCollectionNameCheckDto>> CheckSmartSearchAutoCollectionName([SmartSearchBody] SmartSearchAutoCollectionCheckNameRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _autoCollections.CheckNameAsync(user, isAdmin, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Shows what an auto-collection would hold now, without saving or writing anything: an unsaved one (the caller's own),
    /// or a saved one with changes. The members are worked out in the collection's scope (its creator's libraries); the
    /// samples show only items the caller may open, the others are counted.
    /// </summary>
    /// <param name="request">The auto-collection, or the saved one's id with changes, read strictly.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The members' count, what would change, samples and warnings.</response>
    /// <response code="400">The body, the definition or its query isn't valid, the query is too broad
    /// (<c>QueryTooBroad</c>), or the collection would be too large (<c>TooManyMembers</c>).</response>
    /// <response code="403">An API key, a user who no longer exists, a user who doesn't manage collections, a change of
    /// someone else's source (<c>NotCreator</c>), or an evaluation user the caller may not choose.</response>
    /// <response code="404">No auto-collection has the id (<c>AutoCollectionNotFound</c>).</response>
    /// <response code="409">The auto-collection was saved by a newer version (<c>NewerDefinition</c>, <c>StoreReadOnly</c>).</response>
    /// <response code="413">The request is too large.</response>
    /// <response code="503">The search engine is busy or the preview timed out.</response>
    /// <returns>The preview.</returns>
    [HttpPost("Preview")]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxSaveBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<SmartSearchAutoCollectionPreviewDto>> PreviewSmartSearchAutoCollection([SmartSearchBody] SmartSearchAutoCollectionPreviewRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _autoCollections.PreviewAsync(user, isAdmin, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets an auto-collection with its last run's state.</summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The auto-collection and its last run.</response>
    /// <response code="403">An API key, a user who no longer exists, or a user who doesn't manage collections.</response>
    /// <response code="404">No auto-collection has the id (<c>AutoCollectionNotFound</c>).</response>
    /// <returns>The auto-collection.</returns>
    [HttpGet("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SmartSearchAutoCollectionDto>> GetSmartSearchAutoCollection([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _autoCollections.GetAsync(user, isAdmin, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saves a new auto-collection and creates its collection with its first members, waiting for it up to a minute. The
    /// members must still match the preview it was saved from (<c>AcceptedPreviewHash</c>, within 5%). A saved query of
    /// someone else's is copied into the caller's queries first when <c>DuplicateSourceQuery</c> is true. The collection
    /// has no image until an administrator uploads one.
    /// </summary>
    /// <param name="request">The auto-collection, read strictly.</param>
    /// <param name="cancellationToken">Cancels the request (once saved, the collection is still made).</param>
    /// <response code="201">The auto-collection, its collection made.</response>
    /// <response code="202">The auto-collection, saved; its collection is still on its way (<c>Queued</c> or <c>Running</c>).</response>
    /// <response code="400">The body, the definition or its query isn't valid, or the collection would be too large.</response>
    /// <response code="403">An API key, a user who no longer exists, a user who doesn't manage collections, an evaluation
    /// user the caller may not choose, or someone else's saved query without <c>DuplicateSourceQuery</c> (<c>NotQueryOwner</c>).</response>
    /// <response code="409">A collection uses the name (<c>NameTaken</c>, with <c>conflicts</c> and <c>suggestions</c>), the
    /// members moved since the preview (<c>PreviewStale</c>), the server has the most auto-collections allowed, or a newer
    /// version wrote them (<c>StoreReadOnly</c>).</response>
    /// <response code="413">The request is too large.</response>
    /// <returns>The auto-collection.</returns>
    [HttpPost]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxSaveBodyBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<SmartSearchAutoCollectionDto>> CreateSmartSearchAutoCollection([SmartSearchBody] SmartSearchAutoCollectionRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        var created = await _autoCollections.CreateAsync(user, isAdmin, request, cancellationToken).ConfigureAwait(false);
        return Made(created);
    }

    /// <summary>Lists the auto-collections with their state; cheap enough to poll while some are queued or running.</summary>
    /// <param name="status">Only those with this status.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The auto-collections, by name.</response>
    /// <response code="400">The status isn't one.</response>
    /// <response code="403">A user who no longer exists, or a user who doesn't manage collections.</response>
    /// <returns>The auto-collections.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SmartSearchAutoCollectionDto>>> GetSmartSearchAutoCollections([FromQuery] string? status, CancellationToken cancellationToken)
    {
        var (user, isAdmin) = await CallerOrApiKeyAsync().ConfigureAwait(false);
        var list = await _autoCollections.ListAsync(user, isAdmin, status, cancellationToken).ConfigureAwait(false);
        return Ok(list);
    }

    /// <summary>
    /// Changes an auto-collection; its collection is updated next. What the body leaves out stays. Only the creator or an
    /// administrator changes the name, the source, the evaluation user or whether it is on; an administrator switches
    /// someone else's source only to a copy of a query.
    /// </summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">The change, with <c>IfUpdatedAt</c>: the <c>UpdatedUtc</c> the caller read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The auto-collection, changed; its update is queued.</response>
    /// <response code="400">The body, the definition or its query isn't valid.</response>
    /// <response code="403">As for a create, a change only the creator may make (<c>NotCreator</c>), or an
    /// administrator pointing someone else's auto-collection at a saved query (<c>SnapshotOnly</c>).</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <response code="409">It changed since it was read (<c>AutoCollectionChanged</c>), the new name is in use, or a
    /// newer version wrote it.</response>
    /// <response code="413">The request is too large.</response>
    /// <returns>The auto-collection.</returns>
    [HttpPut("{id}")]
    [RequestSizeLimit(SmartSearchRequestLimits.MaxSaveBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<SmartSearchAutoCollectionDto>> UpdateSmartSearchAutoCollection([FromRoute] Guid id, [SmartSearchBody] SmartSearchAutoCollectionRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _autoCollections.UpdateAsync(user, isAdmin, id, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Asks for an update of an auto-collection's collection now, even during a library scan. Writes only what differs.</summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="202">The auto-collection; its update is queued.</response>
    /// <response code="403">A user who no longer exists, a user who doesn't manage collections, or auto-collections are
    /// off (<c>AutoCollectionsDisabled</c>).</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <response code="409">It is off (<c>AutoCollectionDisabled</c>), or a newer version wrote it.</response>
    /// <returns>The auto-collection.</returns>
    [HttpPost("{id}/Refresh")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SmartSearchAutoCollectionDto>> RefreshSmartSearchAutoCollection([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var (user, isAdmin) = await CallerOrApiKeyAsync().ConfigureAwait(false);
        var queued = await _autoCollections.RefreshAsync(user, isAdmin, id, cancellationToken).ConfigureAwait(false);
        return Accepted(queued);
    }

    /// <summary>Asks for an update of every enabled auto-collection now.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="202">The updates are queued.</response>
    /// <response code="403">A user who no longer exists, a user who doesn't manage collections, or auto-collections are off.</response>
    /// <returns>Nothing.</returns>
    [HttpPost("Refresh")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> RefreshSmartSearchAutoCollections(CancellationToken cancellationToken)
    {
        var (user, isAdmin) = await CallerOrApiKeyAsync().ConfigureAwait(false);
        await _autoCollections.RefreshAllAsync(user, isAdmin, cancellationToken).ConfigureAwait(false);
        return Accepted();
    }

    /// <summary>
    /// Creates the collection again of an auto-collection whose collection was deleted, never made, or replaced by another
    /// collection (which stays the user's), after the name check. An empty body keeps the name.
    /// </summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">A new name, or none.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="201">The auto-collection, its collection made.</response>
    /// <response code="202">The auto-collection; its collection is still on its way.</response>
    /// <response code="400">The body or the name isn't valid.</response>
    /// <response code="403">An API key, a user who no longer exists, a user who doesn't manage collections, or a rename
    /// only the creator may make.</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <response code="409">Its collection is there (<c>NotRecreatable</c>), the name is in use (<c>NameTaken</c>), or a
    /// newer version wrote it.</response>
    /// <returns>The auto-collection.</returns>
    [HttpPost("{id}/Recreate")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SmartSearchAutoCollectionDto>> RecreateSmartSearchAutoCollection([FromRoute] Guid id, [SmartSearchBody] SmartSearchAutoCollectionRecreateRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        var made = await _autoCollections.RecreateAsync(user, isAdmin, id, request, cancellationToken).ConfigureAwait(false);
        return Made(made);
    }

    /// <summary>Applies the removals the guard holds back, once: the next run makes them whatever the guard says.</summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">The held removals' <c>DetectedUtc</c>, as read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="202">The auto-collection; its run is queued.</response>
    /// <response code="400">The body isn't valid.</response>
    /// <response code="403">An API key, a user who no longer exists, a user who doesn't manage collections, or
    /// auto-collections are off.</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <response code="409">Nothing is held (<c>GuardNotPending</c>), what is held changed since it was read
    /// (<c>PreviewStale</c>: read and review it again), or a newer version wrote it.</response>
    /// <returns>The auto-collection.</returns>
    [HttpPost("{id}/Guard/Apply")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SmartSearchAutoCollectionDto>> ApplySmartSearchAutoCollectionGuard([FromRoute] Guid id, [SmartSearchBody] SmartSearchAutoCollectionGuardRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return Accepted(await _autoCollections.ApplyGuardAsync(user, isAdmin, id, request, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Keeps the members the guard holds back: they become pinned items, and the next run is queued.</summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">The held removals' <c>DetectedUtc</c>, as read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="204">Pinned.</response>
    /// <response code="400">The body isn't valid.</response>
    /// <response code="403">An API key, a user who no longer exists, a user who doesn't manage collections, or
    /// auto-collections are off.</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <response code="409">Nothing is held (<c>GuardNotPending</c>), what is held changed since it was read
    /// (<c>PreviewStale</c>), more is held than can be pinned with the pins already there (<c>TooManyToKeep</c>: nothing
    /// changes, the hold stays), or a newer version wrote it.</response>
    /// <returns>Nothing.</returns>
    [HttpPost("{id}/Guard/Keep")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> KeepSmartSearchAutoCollectionGuard([FromRoute] Guid id, [SmartSearchBody] SmartSearchAutoCollectionGuardRequestDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        await _autoCollections.KeepGuardAsync(user, isAdmin, id, request, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets an auto-collection's last runs (at most 20), newest first: why each ran, what it changed and how it ended. The
    /// removed members are listed only when the caller can open them; the others are counted.
    /// </summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The runs.</response>
    /// <response code="403">An API key, a user who no longer exists, or a user who doesn't manage collections.</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <returns>The auto-collection's runs.</returns>
    [HttpGet("{id}/History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<SmartSearchAutoCollectionHistoryDto>>> GetSmartSearchAutoCollectionHistory([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return Ok(await _autoCollections.GetHistoryAsync(user, isAdmin, id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists the collections that auto-collections fill and the caller can open, so a collection's page knows whether to
    /// say it updates automatically. Any user; empty while auto-collections are off.
    /// </summary>
    /// <response code="200">The collections' ids.</response>
    /// <response code="403">An API key, or a user who no longer exists.</response>
    /// <returns>The ids of the collections the caller can open that auto-collections fill.</returns>
    [HttpGet("Ids")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<Guid>> GetSmartSearchAutoCollectionIds()
    {
        var user = User.RequireUser(_userManager);
        return Ok(_autoCollections.GetCollectionIds(user));
    }

    /// <summary>
    /// Tells a collection's page what fills the collection: the auto-collection's name, when it last updated and, for a
    /// user who manages collections, its id to edit or refresh it. Any user.
    /// </summary>
    /// <param name="boxSetId">The collection's id.</param>
    /// <response code="200">The note.</response>
    /// <response code="403">An API key, or a user who no longer exists.</response>
    /// <response code="404">No auto-collection fills the collection, the caller can't open it, or auto-collections are off.</response>
    /// <returns>What fills the collection.</returns>
    [HttpGet("ByCollection/{boxSetId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SmartSearchAutoCollectionNoteDto>> GetSmartSearchAutoCollectionByCollection([FromRoute] Guid boxSetId)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return Ok(_autoCollections.GetNote(user, isAdmin, boxSetId));
    }

    /// <summary>Gives an auto-collection its own copy of its saved query: later changes to the saved query no longer reach it.</summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The auto-collection, following its copy.</response>
    /// <response code="403">An API key, a user who no longer exists, a user who doesn't manage collections, or a manager
    /// other than its creator (<c>NotCreator</c>).</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <response code="409">It already has its own copy (<c>AlreadySnapshot</c>), the saved query is gone
    /// (<c>QueryMissing</c>), it now belongs to another user (<c>SourceOwnerChanged</c>), or a newer version wrote it.</response>
    /// <returns>The auto-collection.</returns>
    [HttpPost("{id}/Detach")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SmartSearchAutoCollectionDto>> DetachSmartSearchAutoCollection([FromRoute] Guid id, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _autoCollections.DetachAsync(user, isAdmin, id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives an auto-collection whose creator was deleted to another user, an administrator or a user allowed to manage
    /// collections; the saved query it follows moves with it (with every other auto-collection of the same creator that
    /// follows that query). Administrators only.
    /// </summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="request">The new creator's <c>UserId</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The auto-collection, with its new creator; its update is queued.</response>
    /// <response code="400">No such user, or one who doesn't manage collections.</response>
    /// <response code="403">An API key, a user who no longer exists, or a caller who isn't an administrator.</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <response code="409">Its creator still exists (<c>CreatorNotMissing</c>), the query uses an administrator-only field
    /// and the new creator isn't an administrator (<c>AdminOnlyField</c>), the new creator has as many saved queries as
    /// allowed (<c>QuotaReached</c>), or a newer version wrote it.</response>
    /// <returns>The auto-collection.</returns>
    [HttpPost("{id}/Reassign")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SmartSearchAutoCollectionDto>> ReassignSmartSearchAutoCollection([FromRoute] Guid id, [SmartSearchBody] SmartSearchSavedQueryOwnerDto request, CancellationToken cancellationToken)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        return await _autoCollections.ReassignAsync(user, isAdmin, id, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes an auto-collection. Its collection stays as an ordinary collection; with <c>deleteCollection=true</c> it is
    /// deleted too, but only while it is still this auto-collection's and the caller could delete it on the server (they
    /// can see it and may manage collections); otherwise nothing is deleted.
    /// </summary>
    /// <param name="id">The auto-collection's id.</param>
    /// <param name="deleteCollection">Whether to delete the collection too.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="204">Deleted.</response>
    /// <response code="403">An API key, a user who no longer exists, a user who doesn't manage collections, or a
    /// collection the caller can't delete (<c>CannotDeleteCollection</c>).</response>
    /// <response code="404">No auto-collection has the id.</response>
    /// <response code="409">A newer version wrote the auto-collections (<c>StoreReadOnly</c>).</response>
    /// <returns>Nothing.</returns>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteSmartSearchAutoCollection([FromRoute] Guid id, [FromQuery] bool deleteCollection = false, CancellationToken cancellationToken = default)
    {
        var user = User.RequireUser(_userManager);
        var isAdmin = await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false);
        await _autoCollections.DeleteAsync(user, isAdmin, id, deleteCollection, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    // 201 once the collection is made; 202 while it is still on its way.
    private ActionResult<SmartSearchAutoCollectionDto> Made(SmartSearchAutoCollectionDto dto)
        => dto.State.Status is "Queued" or "Running"
            ? Accepted(dto)
            : CreatedAtAction(nameof(GetSmartSearchAutoCollection), new { id = dto.Id }, dto);

    // The list and the refreshes also take an API key (no user).
    private async Task<(User? User, bool IsAdmin)> CallerOrApiKeyAsync()
    {
        if (User.IsApiKey())
        {
            return (null, true);
        }

        var user = User.RequireUser(_userManager);
        return (user, await CurrentUser.IsElevatedAsync(HttpContext).ConfigureAwait(false));
    }
}
