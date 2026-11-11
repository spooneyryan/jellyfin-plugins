using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.UserRatings.Api;

/// <summary>
/// User Ratings' admin endpoints: <c>RequiresElevation</c>, admin sessions and API keys. In the development
/// builds 0.3.x Diagnostics, the Detached &amp; ambiguous banner's "seen", "Detach now" for the ratings kept on
/// items that are gone, the kit's <i>Remap by name</i> and <i>Apply deleted-user policy</i>, and
/// moderation (the Moderation list, Remove text only and Remove rating and review, the moderation log), and the Settings
/// tab; then the Data tab's detached list, data jobs, export and import. Enum query values are names, in
/// any case, or a 400.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("UserRatings/Admin")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[PluginExceptionFilter]
[StrictEnumQuery(NamesOnly = true)]
public class UserRatingsAdminController : ControllerBase
{
    /// <summary>The <c>Code</c> of DELETE Jobs/{id}'s 202: the export's file withdrawn, its delete pending.</summary>
    public const string FileDeletePendingCode = "FileDeletePending";

    /// <summary>
    /// The import route's request limit (<c>[RequestSizeLimit]</c>): <see cref="ExportEnvelope.MaxImportBytes"/> and 1 MiB
    /// more, since Kestrel counts a chunked body's framing toward it (found while verifying 0.3.19: an export of
    /// exactly 100 MiB sent chunked was a 413). The import counts the body's own bytes against the 100 MiB exactly, so
    /// 100 MiB passes however it is sent and one byte more is a 413 <c>TooLarge</c>. A declared <c>Content-Length</c> over
    /// this limit Kestrel refuses before the import sees it, with the connection closed unread (on Windows the client sees
    /// a reset, not the 413).
    /// </summary>
    public const long ImportRequestLimitBytes = ExportEnvelope.MaxImportBytes + (1L * 1024 * 1024);

    private readonly IKitDiagnostics _diagnostics;
    private readonly IUserRatingsAdminApi _api;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserRatingsAdminController"/> class.
    /// </summary>
    /// <param name="diagnostics">The kit's diagnostics.</param>
    /// <param name="api">The admin endpoints' service.</param>
    public UserRatingsAdminController(IKitDiagnostics diagnostics, IUserRatingsAdminApi api)
    {
        _diagnostics = diagnostics;
        _api = api;
    }

    /// <summary>
    /// Gets the plugin's diagnostics: <c>PluginVersion</c>, <c>KitVersion</c>, <c>Abi</c> (the
    /// Jellyfin line the build is for and whether it runs), <c>WebAnchors</c> and <c>ClientHealth</c>; the store, the
    /// counts and Jellyfin Enhanced's state join it with the tasks that bring them. For an admin without access to every
    /// library <c>Users.OrphanUsers</c> is empty (<c>Users.OrphanUsersHidden</c>: <c>AllLibrariesNeeded</c>),
    /// as the unknown users' tools aren't theirs to use.
    /// </summary>
    /// <response code="200">The diagnostics, as JSON.</response>
    /// <returns>The diagnostics.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetUserRatingsDiagnostics() => Ok(_api.ScopeDiagnostics(Caller(), _diagnostics.Describe()));

    /// <summary>
    /// Records that the admin opened Data → Detached &amp; ambiguous: the dashboard's banner about ratings
    /// that several copies match ends until a library change creates new ones. The settings page sends it when the Data
    /// tab is opened while the banner shows.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Recorded.</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>No content.</returns>
    [HttpPost("Detached/Seen")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult> PostUserRatingsDetachedSeen(CancellationToken cancellationToken)
    {
        await _api.MarkDetachedSeenAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Detaches the ratings kept on items that are gone. A rating whose item left the library stays on it
    /// while its library folder is missing, empty or can't be read, its file is still on disk, or every rated item of its
    /// folder went at once, and that never ends by itself; Data → Detached &amp; ambiguous lists them with "Detach now". They
    /// detach as Missing, each in the moderation log with the caller, with one Activity Log entry; an item back in the
    /// library since keeps its ratings and is counted in <c>NotHeld</c>.
    /// </summary>
    /// <param name="request">The items, as Diagnostics' <c>Lifecycle.Held</c> lists them.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">What was detached.</response>
    /// <response code="400">No item ids, or more than 1000.</response>
    /// <response code="413">The body is larger than 64 KiB.</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>The counts.</returns>
    [HttpPost("Held/Detach")]
    [RequestSizeLimit(UserRatingsController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserRatingsHeldDetachDto>> PostUserRatingsHeldDetach(
        [FromBody, Required] UserRatingsHeldDetachRequest request,
        CancellationToken cancellationToken)
    {
        // An API key has no user: the moderation log's actor is then empty, as for the server's own actions.
        var caller = User.GetUserId();
        return await _api.DetachHeldAsync(request.ItemIds, caller == Guid.Empty ? null : caller, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <i>Remap by name</i>: each user id Diagnostics lists under <c>Users.OrphanUsers</c> whose
    /// last seen name is one user's name on this server gets that user's id, ratings and all, after a snapshot. On an item
    /// both rated, the newer rating stays attached and the other is detached as Ambiguous (the banner shows). Users deleted
    /// on this server and former members are never remapped. A dry run (the default) only reports, with a <c>PlanToken</c>;
    /// the real run must be given it back (<c>ifPlan</c>), and maps nothing that dry run didn't show. Both answer
    /// <c>Ambiguous</c> and <c>ArchivedRecords</c>.
    /// </summary>
    /// <param name="by">How to match: <c>Name</c> (the only way).</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="ifPlan">The dry run's <c>PlanToken</c>; required for a real run.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Another way to match, or a real run without ifPlan.</response>
    /// <response code="403">An admin without access to every library (<c>AllLibrariesNeeded</c>).</response>
    /// <response code="409">The plan changed since the dry run (nothing written), the ratings data is read-only (not in a
    /// dry run), or the list of users deleted on this server (kit-users.json) can't be read (a dry run too).</response>
    /// <response code="503">The ratings' archive can't be read (nothing written).</response>
    /// <returns>The remap report.</returns>
    [HttpPost("Users/Remap")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> PostUserRatingsUsersRemap([FromQuery] string? by, [FromQuery] bool? dryRun, [FromQuery] string? ifPlan, CancellationToken cancellationToken)
        => Ok(await _api.RemapUsersAsync(Caller(), by, dryRun, ifPlan, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <i>Apply deleted-user policy</i> to unknown user ids from <c>Users.OrphanUsers</c>:
    /// <c>DeletedUserPolicy</c> (<c>Delete</c>: the ratings deleted with tombstones; <c>Anonymize</c>: kept as a former
    /// member's; <c>KeepAsOrphan</c>: kept, counted nowhere), archived ratings included (<c>Archived</c>), after a snapshot
    /// when a rating changes, with a moderation-log entry for each user.
    /// </summary>
    /// <param name="request">The user ids.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">No ids, too many, an empty id or a current user's id.</response>
    /// <response code="403">An admin without access to every library (<c>AllLibrariesNeeded</c>).</response>
    /// <response code="409">The ratings data is read-only (or turned read-only part way: only the users done are marked).</response>
    /// <response code="413">The body is larger than 64 KiB.</response>
    /// <response code="503">The server's user list is unavailable, or the ratings' archive can't be read (nothing written).</response>
    /// <returns>The policy report.</returns>
    [HttpPost("Users/ApplyPolicy")]
    [RequestSizeLimit(UserRatingsController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> PostUserRatingsUsersApplyPolicy(
        [FromBody, Required] UserRatingsApplyPolicyRequest request,
        CancellationToken cancellationToken)
        => Ok(await _api.ApplyDeletedUserPolicyAsync(Caller(), request.UserIds, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The Moderation list: the members' ratings and reviews, the row changed last first, rows the
    /// Hide settings keep from members included. An API key sees every row; a signed-in admin the rows on items they can
    /// open, and the detached rows only with access to every library.
    /// </summary>
    /// <param name="userId">Only this member's rows.</param>
    /// <param name="itemId">Only the rows on this item; an item the admin can't open lists nothing.</param>
    /// <param name="since">Only rows changed at or after this time.</param>
    /// <param name="hasText">Only rows with (true) or without (false) a review.</param>
    /// <param name="state">Attached, Detached or All (the default).</param>
    /// <param name="searchTerm">A text the author's name, the item's or series' name, or the review holds (any case; up to
    /// 200 characters).</param>
    /// <param name="startIndex">The first row of the page (0 by default).</param>
    /// <param name="limit">The page size, 1 to 200 (50 by default).</param>
    /// <response code="200">The page, with the number of rows there are.</response>
    /// <response code="400">A paging value out of range, an unknown state, or a search term over 200 characters.</response>
    /// <response code="503">The ratings data can't be read now.</response>
    /// <returns>The page.</returns>
    [HttpGet("Reviews")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<QueryResult<UserRatingsAdminEntryDto>> GetUserRatingsModerationReviews(
        [FromQuery] Guid? userId,
        [FromQuery] Guid? itemId,
        [FromQuery] DateTime? since,
        [FromQuery] bool? hasText,
        [FromQuery] UserRatingsModerationState? state,
        [FromQuery] string? searchTerm,
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, 200)] int? limit = null)
        => _api.GetReviews(Caller(), userId, itemId, since, hasText, state, searchTerm, startIndex, limit);

    /// <summary>
    /// A moderator's <i>Remove text only</i> (the default: the stars stay) or <i>Remove rating and review</i>
    /// (<c>textOnly=false</c>; a <c>Moderator</c> tombstone, so no import brings it back). A dry run (the default) answers
    /// what goes and a <c>PreviewToken</c>; the real run (<c>dryRun=false</c>) must send it back as <c>ifPreview</c>, takes a
    /// pre-operation snapshot (kept 7 days), and writes one moderation-log row with ids, the reason and counts only. The
    /// rating's earlier lines in the archive go too, with no copy, and a checkpoint of the database's write-ahead log is
    /// asked for (it runs in the background; Diagnostics <c>Store.Checkpoint.Pending</c> is false once it is done), so the
    /// removed text is left in neither.
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <param name="textOnly">Whether only the review goes (true when left out).</param>
    /// <param name="reason">Spoiler, Offensive, Spam, OffTopic, Requested or Other.</param>
    /// <param name="dryRun">Whether to only report (true when left out).</param>
    /// <param name="ifPreview">The dry run's PreviewToken; required for a real run.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">What goes (a dry run) or went.</response>
    /// <response code="400">A real run without a well-formed ifPreview, or an unknown reason.</response>
    /// <response code="404">No such rating, or one the caller can't moderate (the same answer).</response>
    /// <response code="409">The rating has no review to remove (Remove text only), or it changed since the dry run
    /// (Changed: nothing was written).</response>
    /// <response code="503">The ratings data is read-only or busy; nothing changed.</response>
    /// <returns>The removal's report.</returns>
    [HttpDelete("Reviews/{ratingId:guid}")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserRatingsModerationDto>> DeleteUserRatingsModerationReview(
        [FromRoute, Required] Guid ratingId,
        [FromQuery] bool? textOnly,
        [FromQuery] UserRatingsModerationReason? reason,
        [FromQuery] bool? dryRun,
        [FromQuery] string? ifPreview,
        CancellationToken cancellationToken)
        => await _api.RemoveReviewAsync(Caller(), ratingId, textOnly, reason, dryRun, ifPreview, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// A page of the moderation log, newest first. The log holds ids, reason codes and counts, never review
    /// text or names; the names here are read now. A signed-in admin gets only the rows about items they can open (a row
    /// about an item the library no longer has only with every library); an API key every row.
    /// </summary>
    /// <param name="startIndex">The first row of the page (0 by default).</param>
    /// <param name="limit">The page size, 1 to 200 (50 by default).</param>
    /// <param name="action">Only rows of this action (RemoveText, RemoveEntry, UserDeleted, Detach …).</param>
    /// <response code="200">The page, with the number of rows there are.</response>
    /// <response code="400">A paging value out of range, or an unknown action.</response>
    /// <response code="503">The ratings data can't be read now.</response>
    /// <returns>The page.</returns>
    [HttpGet("ModerationLog")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<QueryResult<UserRatingsModerationLogDto>> GetUserRatingsModerationLog(
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, 200)] int? limit = null,
        [FromQuery] string? action = null)
        => _api.GetModerationLog(Caller(), startIndex, limit, action);

    /// <summary>
    /// The Settings tab's settings: every setting with its value (a name as saved), every default, the settings'
    /// <c>Version</c> (a save sends it back), the users the <c>RaterUserIds</c> picker offers (an id the settings hold that is
    /// no longer a user is <c>Missing</c>), the item kinds <c>RateableItemTypes</c> offers, the settings that need a
    /// restart, and the saved values the plugin can't use as they are.
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>The settings, their defaults and version, and what the tab offers.</returns>
    [HttpGet("Settings")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<UserRatingsSettingsViewDto> GetUserRatingsSettings() => _api.GetSettings();

    /// <summary>
    /// Changes the settings: the body holds the settings to change, by the names <c>GET Settings</c> answers (one
    /// left out keeps its value). A dry run (the default) answers what changes, what that means for the members and the data
    /// (<c>Consequences</c>) and whether a restart is needed; a save (<c>dryRun=false</c>) needs the <c>version</c> it was
    /// made from, and takes effect at once except where <c>RestartNeeded</c> says.
    /// </summary>
    /// <param name="request">The settings to change.</param>
    /// <param name="version">The settings' Version the change was made from; required for a save.</param>
    /// <param name="dryRun">Whether nothing is saved; true when left out.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">A key that names no setting (code UnknownSetting), a value the setting can't take (code
    /// InvalidSetting: and the setting's name), or a save without a version (code VersionNeeded); nothing saved.</response>
    /// <response code="409">The settings were saved since the version (code SettingsChanged, naming the settings that
    /// changed); nothing saved.</response>
    /// <response code="413">The body is larger than 64 KiB.</response>
    /// <response code="503">User Ratings isn't loaded; nothing saved.</response>
    /// <returns>What changes, what that means, and the settings' version after it.</returns>
    [HttpPost("Settings")]
    [NoStore]
    [RequestSizeLimit(UserRatingsController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<UserRatingsSettingsReportDto>> UpdateUserRatingsSettings(
        [FromBody, Required] UserRatingsSettingsDto request,
        [FromQuery] string? version,
        [FromQuery] bool? dryRun,
        CancellationToken cancellationToken)
        => await _api.ChangeSettingsAsync(request, version, dryRun, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The export, at once: every rating with its identity, the former members, the moderation log, the import
    /// ledger, the tombstones and the archive, in the kit's envelope, as a download. For an API key or an admin with every
    /// library.
    /// </summary>
    /// <response code="200">The envelope, as an attachment.</response>
    /// <response code="403">A signed-in admin without every library.</response>
    /// <response code="503">The ratings data or the archive can't be read now.</response>
    /// <returns>The file.</returns>
    [HttpGet("Export")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetUserRatingsExport() => _api.Export(Caller(), HttpContext);

    /// <summary>
    /// The ratings as Jellyfin Enhanced 12.14's <c>reviews.json</c>, as a download: the
    /// way back after switching JE's user reviews off. Movies, series, seasons and episodes with a TMDb id, keyed as JE keys
    /// them. Built in memory: nothing is written on the server, and never into JE's folder (the admin puts the file in
    /// place with the server stopped). <c>X-UserRatings-Exported</c>, <c>X-UserRatings-Skipped</c>,
    /// <c>X-UserRatings-Skipped-Detail</c>, <c>X-UserRatings-Merged-Copies</c> and <c>X-UserRatings-Over-Je-Limit</c> count
    /// what it holds and left out. JE 12.14 serves every review by TMDb id to any signed-in user, with no library check, so
    /// the ratings of titles not every member can open are left out (<c>RestrictedLibrary</c>) unless
    /// <c>leaveOutRestricted=false</c>. For an API key or an admin with every library.
    /// </summary>
    /// <param name="leaveOutRestricted">Whether to leave out the ratings of titles that not every member can open (true when
    /// left out).</param>
    /// <response code="200">The file, as an attachment.</response>
    /// <response code="403">A signed-in admin without every library.</response>
    /// <response code="503">The ratings data can't be read now.</response>
    /// <returns>The file.</returns>
    [HttpGet("Export/JellyfinEnhanced")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult GetUserRatingsJellyfinEnhancedExport([FromQuery] bool? leaveOutRestricted) => _api.ExportJellyfinEnhanced(Caller(), HttpContext, leaveOutRestricted);

    /// <summary>
    /// Starts the export as a data job; once it succeeded, <c>GET Admin/Jobs/{jobId}/File</c> downloads it.
    /// </summary>
    /// <response code="202">The job.</response>
    /// <response code="403">A signed-in admin without every library.</response>
    /// <response code="409">Another data job is running (<c>JobRunning</c>).</response>
    /// <returns>The job as it starts.</returns>
    [HttpPost("Export")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<UserRatingsJobDto> PostUserRatingsExport() => Accepted(_api.StartExport(Caller()));

    /// <summary>
    /// Imports an export as a data job: the body is stored in <c>tmp/</c> (at most 100 MiB, counted exactly,
    /// sent with a <c>Content-Length</c> or chunked) and the job runs in the background. The body is read as the bytes it is,
    /// whatever its <c>Content-Type</c> (<see cref="RawBodyAttribute"/>). A dry run by default; it answers a
    /// <c>PlanToken</c>, which the real run sends as <c>ifPlan</c>.
    /// </summary>
    /// <param name="mode">Merge (the default) or Replace.</param>
    /// <param name="dryRun">Whether nothing is written (true by default).</param>
    /// <param name="mapUsersBy">Id (the default) or Name.</param>
    /// <param name="ifPlan">The dry run's PlanToken; required when dryRun is false.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <response code="202">The job.</response>
    /// <response code="400">A bad mode or matching, or a real run without ifPlan.</response>
    /// <response code="403">A signed-in admin without every library.</response>
    /// <response code="409">Another data job is running.</response>
    /// <response code="413">Over 100 MiB (<c>TooLarge</c>). A declared <c>Content-Length</c> over 101 MiB is refused by
    /// Kestrel before the import (<see cref="ImportRequestLimitBytes"/>): on Windows the client sees a reset.</response>
    /// <returns>The job as it starts.</returns>
    [HttpPost("Import")]
    [NoStore]
    [RawBody]
    [RequestSizeLimit(ImportRequestLimitBytes)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<UserRatingsJobDto>> PostUserRatingsImport(
        [FromQuery] string? mode,
        [FromQuery] bool? dryRun,
        [FromQuery] string? mapUsersBy,
        [FromQuery] string? ifPlan,
        CancellationToken cancellationToken)
        => Accepted(await _api.StartImportAsync(Caller(), Request.Body, Request.ContentLength, mode, dryRun, mapUsersBy, ifPlan, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Imports Jellyfin Enhanced's reviews as a data job: its <c>reviews.json</c>, read-only (the
    /// plugin never writes JE's files or settings). A dry run by default; it answers a <c>PlanToken</c>, which the real run
    /// sends as <c>ifPlan</c>. The report has the copies note, the field names (<c>SourceFields</c>), JE's review settings
    /// and the unknown users to map; never review text.
    /// </summary>
    /// <param name="request">Copies (First or All), StoreUnmatched, RecreatePurged and the UserMap; may be left out.</param>
    /// <param name="dryRun">Whether nothing is written (true by default).</param>
    /// <param name="ifPlan">The dry run's PlanToken; required when dryRun is false.</param>
    /// <response code="202">The job.</response>
    /// <response code="400">A bad request, a real run without ifPlan, or a JE file of another shape (<c>UnsupportedFormat</c>).</response>
    /// <response code="403">A signed-in admin without every library.</response>
    /// <response code="404">Jellyfin Enhanced has no reviews here (<c>JeDataNotFound</c>).</response>
    /// <response code="409">Another data job is running.</response>
    /// <returns>The job as it starts.</returns>
    [HttpPost("Import/JellyfinEnhanced")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<UserRatingsJobDto> PostUserRatingsJellyfinEnhancedImport(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UserRatingsJeImportRequest? request,
        [FromQuery] bool? dryRun,
        [FromQuery] string? ifPlan)
        => Accepted(_api.StartJellyfinEnhancedImport(Caller(), request, dryRun, ifPlan));

    /// <summary>
    /// The data jobs: the running one and the last 10 finished, newest first.
    /// </summary>
    /// <response code="200">The jobs.</response>
    /// <response code="403">An admin without access to every library (<c>AllLibrariesNeeded</c>).</response>
    /// <returns>The running job and the finished ones kept.</returns>
    [HttpGet("Jobs")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IReadOnlyList<UserRatingsJobDto>> GetUserRatingsJobs() => Ok(_api.GetJobs(Caller()));

    /// <summary>
    /// A data job: its state, phase, progress and, once finished, its report or error.
    /// </summary>
    /// <param name="jobId">The job to read.</param>
    /// <response code="200">The job.</response>
    /// <response code="403">An admin without access to every library (<c>AllLibrariesNeeded</c>).</response>
    /// <response code="404">Unknown, or no longer kept.</response>
    /// <returns>The job as it is now.</returns>
    [HttpGet("Jobs/{jobId:guid}")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<UserRatingsJobDto> GetUserRatingsJob([FromRoute, Required] Guid jobId) => _api.GetJob(Caller(), jobId);

    /// <summary>
    /// A finished export job's file, as an attachment.
    /// </summary>
    /// <param name="jobId">The export job.</param>
    /// <response code="200">The file.</response>
    /// <response code="403">An admin without access to every library (<c>AllLibrariesNeeded</c>).</response>
    /// <response code="404">Unknown, not a finished export, or no longer kept.</response>
    /// <returns>The export.</returns>
    [HttpGet("Jobs/{jobId:guid}/File")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetUserRatingsJobFile([FromRoute, Required] Guid jobId)
    {
        var (path, name) = _api.GetExportFile(Caller(), jobId);

        // Opened with delete sharing (found while verifying 0.3.19): DELETE Jobs/{id} and the hour's expiry delete the file
        // while a download reads it, the download finishing from its open handle. PhysicalFile opens it without.
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            throw PluginException.NotFound("That job has no file to download: a finished export keeps one for an hour, until it is deleted or leaves the list of the last 10. Export again.");
        }

        return File(stream, MediaTypeNames.Application.Json, name);
    }

    /// <summary>
    /// Cancels a data job: between rows of its read and resolve phases; never once it writes, so a cancelled import
    /// writes nothing. On a finished export it deletes the export's file at once (the server keeps it an hour otherwise); a
    /// file still open is withdrawn at once and deleted within a minute of being closed (202).
    /// </summary>
    /// <param name="jobId">The job to cancel.</param>
    /// <response code="204">Asked to stop, or the finished export's file deleted.</response>
    /// <response code="202">The export's file is no longer offered but still open, so it isn't deleted yet (<c>FileDeletePending</c>).</response>
    /// <response code="403">An admin without access to every library (<c>AllLibrariesNeeded</c>).</response>
    /// <response code="404">Unknown, or no longer kept.</response>
    /// <response code="409">Already finished, without a file to delete (<c>JobFinished</c>), or writing (<c>JobWriting</c>).</response>
    /// <returns>Nothing.</returns>
    [HttpDelete("Jobs/{jobId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult DeleteUserRatingsJob([FromRoute, Required] Guid jobId)
    {
        if (_api.CancelJob(Caller(), jobId))
        {
            return NoContent();
        }

        // The delete didn't happen yet: never a 204 for it (found while verifying 0.3.19).
        return Accepted(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Deleted"] = false,
            ["Code"] = FileDeletePendingCode,
            ["Message"] = "The export's file is no longer offered, but it is still open (a download in progress), so it isn't deleted yet; it is deleted within a minute of being closed."
        });
    }

    /// <summary>
    /// The snapshots of both classes, newest first, and the database's integrity: its status, whether it is read-only or
    /// damaged, the last recovery and the quarantine folders (what the banner shows).
    /// </summary>
    /// <response code="200">The snapshots and the integrity.</response>
    /// <response code="403">An admin without access to every library (<c>AllLibrariesNeeded</c>); the Data tab then hides
    /// export, import and restore.</response>
    /// <returns>Both.</returns>
    [HttpGet("Snapshots")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<IDictionary<string, object?>> GetUserRatingsSnapshots() => Ok(_api.GetSnapshots(Caller()));

    /// <summary>
    /// Restores a snapshot: the logical restore, with a <c>restore</c> safety snapshot first, or the corruption
    /// restore when the database is damaged; a pre-operation snapshot's archive copy of its operation is put back too.
    /// </summary>
    /// <param name="name">The snapshot's file name.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <response code="200">What was restored.</response>
    /// <response code="400">No name.</response>
    /// <response code="403">A signed-in admin without every library.</response>
    /// <response code="404">No snapshot has that name.</response>
    /// <response code="409">A data job is running, or the snapshot can't be used (a newer version, it fails its check).</response>
    /// <response code="503">The database can't be opened or stayed in use.</response>
    /// <returns>The result.</returns>
    [HttpPost("Snapshots/Restore")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> PostUserRatingsSnapshotsRestore([FromQuery] string? name, CancellationToken cancellationToken)
        => Ok(await _api.RestoreSnapshotAsync(Caller(), name, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Starts the ratings empty, as only an admin can choose, while the database is damaged
    /// (<c>Snapshots</c>' <c>Integrity.Damaged</c>): when no snapshot passes its check, or the admin would rather not go back
    /// to one. The damaged database is moved to a quarantine folder and kept, a new empty one takes its place, every
    /// snapshot is kept until one of them is restored, and a <c>StartEmpty</c> row starts the moderation log.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <response code="200">The recovery: <c>StartedEmpty</c>, <c>QuarantineFolder</c>, <c>Reason</c>, <c>Utc</c>.</response>
    /// <response code="403">A signed-in admin without every library (<c>AllLibrariesNeeded</c>).</response>
    /// <response code="409">A data job is running, the database isn't damaged (restore a snapshot instead), or a readable
    /// database is in place now (restart the server to use it).</response>
    /// <response code="503">The damaged database couldn't be moved aside or the new one made; what was moved is put back.</response>
    /// <returns>The result.</returns>
    [HttpPost("Data/StartEmpty")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<IDictionary<string, object?>>> PostUserRatingsDataStartEmpty(CancellationToken cancellationToken)
        => Ok(await _api.StartEmptyAsync(Caller(), cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// The detached ratings, oldest detach first, each with up to five items it could be relinked to in its
    /// member's view; no review text. Empty for a signed-in admin without every library.
    /// </summary>
    /// <param name="reason">Removed, Missing, Unmatched or Ambiguous; all by default.</param>
    /// <param name="startIndex">The first row (0 by default).</param>
    /// <param name="limit">The page size, 1 to 50.</param>
    /// <response code="200">The page.</response>
    /// <response code="400">An unknown reason.</response>
    /// <returns>The ratings of the page.</returns>
    [HttpGet("Detached")]
    [NoStore]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<QueryResult<UserRatingsDetachedDto>> GetUserRatingsDetached(
        [FromQuery] string? reason,
        [FromQuery, Range(0, int.MaxValue)] int startIndex = 0,
        [FromQuery, Range(1, 50)] int? limit = null)
        => _api.GetDetached(Caller(), reason, startIndex, limit);

    /// <summary>
    /// Relinks a detached rating to an item its member can open.
    /// </summary>
    /// <param name="ratingId">The rating.</param>
    /// <param name="request">The item.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <response code="204">Relinked.</response>
    /// <response code="400">No item, one that can't be rated, or one the member can't open.</response>
    /// <response code="404">No detached rating the caller can change has that id.</response>
    /// <response code="409">The member already rates that item (<c>AlreadyRated</c>), or the data is read-only.</response>
    /// <returns>Nothing.</returns>
    [HttpPost("Detached/{ratingId:guid}/Relink")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> PostUserRatingsDetachedRelink([FromRoute, Required] Guid ratingId, [FromBody] UserRatingsRelinkRequest? request, CancellationToken cancellationToken)
    {
        await _api.RelinkDetachedAsync(Caller(), ratingId, request?.ItemId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Purges a detached rating: a <c>purge</c> pre-operation snapshot, then the row deleted with a <c>Purge</c>
    /// tombstone, its archive lines taken out and the checkpoint asked for, as a deletion leaves it.
    /// </summary>
    /// <param name="ratingId">The rating to purge.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <response code="204">Purged.</response>
    /// <response code="404">No detached rating the caller can change has that id.</response>
    /// <response code="409">The data is read-only.</response>
    /// <returns>Nothing.</returns>
    [HttpDelete("Detached/{ratingId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> DeleteUserRatingsDetached([FromRoute, Required] Guid ratingId, CancellationToken cancellationToken)
    {
        await _api.PurgeDetachedAsync(Caller(), ratingId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    // An API key has no user: the moderation log's actor is then empty, as for the server's own actions, and it works over
    // the whole server.
    private Guid? Caller()
    {
        var caller = User.GetUserId();
        return caller == Guid.Empty ? null : caller;
    }
}
