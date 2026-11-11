using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AccessRoles.Api.Models;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Services;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Diagnostics;
using Jellyfin.Plugin.Kit.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AccessRoles.Api;

/// <summary>
/// Access Roles' admin endpoints besides roles and users: Diagnostics, the preview, the libraries (with "grant it
/// again" and the purge), the library sync, the audit log, and the kit's <i>Remap by name</i> and
/// <i>Apply deleted-user policy</i>. <c>RequiresElevation</c>, API keys allowed (they are the break-glass path).
/// </summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("AccessRoles/Admin")]
[Produces(MediaTypeNames.Application.Json)]
[KitAbiGate]
[AccessRolesInertGate]
[PluginExceptionFilter]
[AccessRolesProblemFilter]
[StrictEnumQuery]
public class AccessRolesAdminController : ControllerBase
{
    private readonly IKitDiagnostics _diagnostics;
    private readonly IAccessRolesAdmin _admin;
    private readonly IAccessRolesAudit _audit;
    private readonly IAccessRolesSettings _settings;
    private readonly IAccessRolesRecovery _recovery;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccessRolesAdminController"/> class.
    /// </summary>
    /// <param name="diagnostics">The kit's diagnostics.</param>
    /// <param name="admin">The roles API.</param>
    /// <param name="audit">The audit log's reads.</param>
    /// <param name="settings">The Settings tab's API.</param>
    /// <param name="recovery">Lockout and recovery.</param>
    public AccessRolesAdminController(IKitDiagnostics diagnostics, IAccessRolesAdmin admin, IAccessRolesAudit audit, IAccessRolesSettings settings, IAccessRolesRecovery recovery)
    {
        _diagnostics = diagnostics;
        _admin = admin;
        _audit = audit;
        _settings = settings;
        _recovery = recovery;
    }

    /// <summary>
    /// Gets the plugin's diagnostics: <c>PluginVersion</c>, <c>KitVersion</c>, <c>Abi</c> (the Jellyfin line the
    /// build is for and whether it runs), <c>Store</c> (<c>accessroles.json</c>, its schema version and status),
    /// <c>Records</c> (the stored roles, assignments, libraries and ledger entries, counted), <c>Enforcement</c> (what is
    /// enforced now), <c>ActionManifest</c> (the reviewed list of Jellyfin.Api's actions, compared with the
    /// running server's), <c>ServerUsers</c> (the server's users counted, as the kit lists them), <c>Audit</c> (the audit
    /// log's files), <c>Users</c> (the kit's user reconciliation), <c>Maintenance</c>, <c>WebAnchors</c> and
    /// <c>ClientHealth</c>.
    /// </summary>
    /// <response code="200">The diagnostics, as JSON.</response>
    /// <returns>The diagnostics.</returns>
    [HttpGet("Diagnostics")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetAccessRolesDiagnostics() => Ok(_diagnostics.Describe());

    /// <summary>
    /// Previews a proposed state of the roles and assignments against the stored one: per user it
    /// affects, the sections newly locked or unlocked, the libraries added or removed, whether a native policy write will
    /// follow, <c>NoLibraries</c> and leaving management with what the access becomes; and the refusals saving it would
    /// meet. Nothing is saved.
    /// </summary>
    /// <param name="request">The proposal.</param>
    /// <response code="200">The preview.</response>
    /// <response code="400">An invalid proposal.</response>
    /// <response code="404">A role or user it names doesn't exist.</response>
    /// <returns>What the proposal does.</returns>
    [HttpPost("Preview")]
    [RequestSizeLimit(AccessRolesRolesController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<AccessRolesPreviewDto> PreviewAccessRoles([FromBody, Required] AccessRolesPreviewRequest request) => Ok(_admin.Preview(request));

    /// <summary>
    /// Lists the libraries (every one the server lists, disabled ones and the Collections library included) with the roles
    /// that grant them, and the ones the roles and overrides still refer to that the server no longer lists.
    /// </summary>
    /// <response code="200">The libraries.</response>
    /// <returns>The libraries with their roles.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<AccessRolesLibraryDto>> GetAccessRolesLibraries() => Ok(_admin.GetLibraries());

    /// <summary>
    /// Grants a library deleted and created again under the same id again (the Libraries tab's Grant again): its recorded
    /// created time becomes its folder's and the roles' references to it are no longer orphaned, so its earlier grants (a
    /// role's <c>Only</c> list, a user's <c>Add</c>, the original access) give it again; the library sync then writes it.
    /// </summary>
    /// <param name="libraryId">The library's id.</param>
    /// <param name="options">The dry run and lockout confirmation.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <response code="200">The change, with its preview.</response>
    /// <response code="404">The server lists no such library.</response>
    /// <response code="409">The library wasn't created again and no reference to it is orphaned.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpPost("Libraries/{libraryId}/GrantAgain")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> GrantAccessRolesLibraryAgain(
        [FromRoute, Required] Guid libraryId,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.GrantLibraryAgainAsync(libraryId, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Purges a deleted library's references: its grants, from the <i>Only…</i> roles' lists, the
    /// users' <c>Add</c>, the ledger's original access and pending releases. Since 1.0.1 its exclusions stay (an
    /// <i>All except…</i> role's list, a user's <c>Remove</c>), and the known library with them, so a library created again
    /// under its id is re-created and stays left out; with no exclusion left the known library goes too.
    /// </summary>
    /// <param name="libraryId">The library's id.</param>
    /// <param name="options">The dry run and lockout confirmation.</param>
    /// <param name="cancellationToken">Cancels waiting for the store.</param>
    /// <response code="200">The change, with its preview.</response>
    /// <response code="404">Nothing refers to the library.</response>
    /// <response code="409">The server still lists the library, or only its exclusions refer to it (1.0.1): nothing to purge.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The change.</returns>
    [HttpPost("Libraries/{libraryId}/Purge")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesChangeDto>> PurgeAccessRolesLibrary(
        [FromRoute, Required] Guid libraryId,
        [FromQuery] AccessRolesWriteOptions options,
        CancellationToken cancellationToken)
        => Ok(await _admin.PurgeLibraryAsync(libraryId, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Runs the library sync now: the library lifecycle, then every managed user's library access written
    /// where it differs from the rules (drift re-applied or only reported, per <c>DriftPolicy</c>), pending releases written
    /// or dropped. With <c>dryRun=true</c> it only reports. Paused (SAFE-MODE, enforcement or the sync off, a read-only
    /// store, the hold after a restore) it writes nothing and says why; <c>confirmRestore=true</c> lifts the hold once a dry
    /// run was made since it began.
    /// </summary>
    /// <param name="options">
    /// The dry run, the lockout confirmation, the drift reapply, the restore's confirmation, and the dry run's
    /// <c>PlanToken</c> the admin confirmed (<c>ifPlan</c>).
    /// </param>
    /// <param name="cancellationToken">Cancels the run between writes.</param>
    /// <response code="200">What the run found and did.</response>
    /// <response code="400">
    /// A <c>userId</c> with no value (or not a GUID), or <c>userId</c> with <c>confirmRestore=true</c>. <c>IfPlanRequired</c>:
    /// <c>confirmRestore=true</c> without <c>ifPlan</c> (the dry run's <c>PlanToken</c>) while the sync is held.
    /// </response>
    /// <response code="404">The server has no user <c>userId</c>.</response>
    /// <response code="409">
    /// <c>DryRunRequired</c>: <c>confirmRestore=true</c> before the dry run of the hold, or a restore began since (or is in
    /// progress), so the hold is that restore's now and nothing was lifted. <c>Changed</c>: the run would write other than the
    /// confirmed dry run (<c>ifPlan</c>), so it wrote nothing.
    /// </response>
    /// <response code="503">A real run while the data is read-only.</response>
    /// <returns>The run's report.</returns>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesSyncDto>> SyncAccessRolesLibraries([FromQuery] AccessRolesSyncOptions options, CancellationToken cancellationToken)
    {
        // MVC binds "userId=" (no value) as no userId, which would make a run for one user a run for everyone):
        // a userId that is in the query must name a user.
        if (options?.UserId is null && Request.Query.ContainsKey(nameof(AccessRolesSyncOptions.UserId)))
        {
            throw PluginException.Invalid("userId has no value: name the user the run is for, or leave userId out for a run for everyone. Nothing was changed.");
        }

        return Ok(await _admin.SyncAsync(options ?? new AccessRolesSyncOptions(), User, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Ends Degraded mode: the settings locks of the users a degraded start locked then come from their
    /// roles alone, and the library sync runs, so the optional mirror puts back the native preference access it turned off
    /// for those whose roles no longer lock all five sections; library access stays as last written. Offered while the data
    /// file is usable and an earlier degraded start's marker, or the copy a restore moved aside, keeps users locked, or after
    /// a store recovered empty (the kit's "start empty"); a read-only store's way out is a readable copy put back. A dry run
    /// first (<c>dryRun=true</c>); the confirmation sends its <c>PreviewToken</c> as <c>ifPreview</c>. Audited
    /// <c>DegradedModeEnded</c>.
    /// </summary>
    /// <param name="options">The dry run and <c>ifPreview</c>.</param>
    /// <param name="cancellationToken">Cancels the audit write.</param>
    /// <response code="200">Whom it locks, ended unless a dry run.</response>
    /// <response code="409"><c>NotDegraded</c>: nothing to end. <c>StoreReadOnly</c>: the data file is read-only.
    /// <c>Changed</c>: it locks other users than the preview showed.</response>
    /// <response code="503"><c>MarkerNotDeleted</c>: the marker couldn't be deleted; nothing changed.</response>
    /// <returns>What it locked.</returns>
    [HttpPost("Degraded/Resolve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesDegradedDto>> ResolveAccessRolesDegraded([FromQuery] AccessRolesWriteOptions options, CancellationToken cancellationToken)
        => Ok(await _recovery.ResolveDegradedAsync(options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Releases the native locks, before the plugin is disabled or deleted: the
    /// <c>EnableUserPreferenceAccess</c> flags the optional mirror turned off go back on; with <c>restoreLibraries=true</c> the
    /// ledger's original library access is written back too (deleted libraries and ones created again since left out); the
    /// users released become <c>Excluded</c>, roles and ledger kept, so neither the library sync nor the mirror writes the
    /// locks again. The mirror's records are the document's and Degraded mode's evidence (a copy the store couldn't use). A
    /// read-only data file releases too, but nobody becomes <c>Excluded</c> (<c>StoreReadOnly</c> in the answer).
    /// A dry run first (<c>dryRun=true</c>); the confirmation sends its <c>PreviewToken</c> as
    /// <c>ifPreview</c>. Audited <c>LocksReleased</c>.
    /// </summary>
    /// <param name="body">The users (<c>UserIds</c>); none or empty: everyone with a native lock to release.</param>
    /// <param name="restoreLibraries">Whether the original library access is restored too.</param>
    /// <param name="options">The dry run, <c>ifPreview</c> and <c>confirmNoFullAdmin</c>.</param>
    /// <param name="cancellationToken">Cancels before the first write.</param>
    /// <response code="200">Who is released, and how.</response>
    /// <response code="404">A user id the server doesn't have.</response>
    /// <response code="409"><c>Changed</c>: the locks to release aren't the preview's any more (naming whom).</response>
    /// <response code="503">The data file couldn't be saved (nobody was released).</response>
    /// <returns>The users released.</returns>
    [HttpPost("ReleaseLocks")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesReleaseLocksDto>> ReleaseAccessRolesLocks([FromBody] AccessRolesReleaseLocksRequest? body, [FromQuery] bool restoreLibraries, [FromQuery] AccessRolesWriteOptions options, CancellationToken cancellationToken)
        => Ok(await _recovery.ReleaseLocksAsync(body, restoreLibraries, options ?? new AccessRolesWriteOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <i>Import current setup</i>: one role per distinct library access among the non-admin users,
    /// named after its libraries, with no locks, and the membership, so that nobody's access changes ("no change for 7
    /// users"); administrators, users who can't be managed or have no library, and users no role would give exactly their
    /// access aren't imported (each with a warning). Saved, the roles and assignments are audited (<c>RoleCreated</c>,
    /// <c>AssignmentChanged</c>, <c>ImportApplied</c>) and no policy is written, since nothing differs.
    /// </summary>
    /// <param name="options">The dry run (<c>dryRun=true</c> shows the proposal and saves nothing).</param>
    /// <param name="manageUsersWithEveryLibrary">Whether users who see every library get an <c>All libraries</c> role
    /// (their later edits on the stock page are then overridden); by default they are left unmanaged, with a warning.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The proposal, saved unless a dry run.</response>
    /// <response code="409"><c>AlreadyManaging</c>: Access Roles manages users already.</response>
    /// <response code="503">The data is read-only.</response>
    /// <returns>The import.</returns>
    [HttpPost("ImportExisting")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesImportDto>> ImportExistingAccessRoles([FromQuery] AccessRolesWriteOptions options, [FromQuery] bool? manageUsersWithEveryLibrary, CancellationToken cancellationToken)
        => Ok(await _admin.ImportExistingAsync(options ?? new AccessRolesWriteOptions(), manageUsersWithEveryLibrary ?? false, User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Reads the audit log, newest first.
    /// </summary>
    /// <param name="startIndex">The first line of the page.</param>
    /// <param name="limit">The page's size: 1 to 200, 50 when left out.</param>
    /// <param name="userId">Only lines about this user or by this admin.</param>
    /// <param name="action">Only lines of this action (<c>RoleCreated</c>, <c>AssignmentChanged</c>, …).</param>
    /// <param name="fromUtc">Only lines since then; a time without a zone is UTC.</param>
    /// <param name="toUtc">Only lines before then; a time without a zone is UTC.</param>
    /// <param name="before">
    /// Only lines older than the line of this <c>Seq</c>: the next page's cursor, the <c>Seq</c> of the
    /// last line shown, which lines written meanwhile don't shift as they shift <c>startIndex</c>. The total then counts the
    /// matching lines older than it.
    /// </param>
    /// <param name="rule">
    /// Only lines of this rule (<c>RequestsBlocked</c>, <c>FilterErrors</c>), ignoring letter case: the rule as
    /// the line names it, or one of the rules a name joined with <c>+</c> names (<c>ServerHardening.RuleNames</c>: an action
    /// with more than one hardening rule), so <c>LibraryVisibility</c> finds <c>LibraryVisibility+ItemIds</c> too.
    /// </param>
    /// <response code="200">The page.</response>
    /// <response code="400">A negative start or cursor, a limit out of range, or a range that ends before it starts.</response>
    /// <returns>The page of audit lines.</returns>
    [HttpGet("Audit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<QueryResult<AccessRolesAuditEntryDto>> GetAccessRolesAudit(
        [FromQuery] int? startIndex,
        [FromQuery] int? limit,
        [FromQuery] Guid? userId,
        [FromQuery] string? action,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] long? before,
        [FromQuery] string? rule)
        => Ok(_audit.Query(startIndex, limit, userId, action, fromUtc, toUtc, rule, before));

    /// <summary>
    /// Gets Access Roles' settings as this build reads them (the Settings tab).
    /// </summary>
    /// <response code="200">The settings.</response>
    /// <returns>Every setting the Settings tab shows.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AccessRolesSettingsDto> GetAccessRolesSettings() => Ok(_settings.Get());

    /// <summary>
    /// Changes the settings the body sets (a value left out stays), audited <c>SettingsChanged</c>. A dry run
    /// (<c>dryRun=true</c>) lists each setting the change changes, before and after, with what it means and its
    /// <c>PreviewToken</c>; the save sends that token as <c>ifPreview</c>.
    /// </summary>
    /// <param name="body">The values to set.</param>
    /// <param name="options">The dry run, its token and <c>confirmSecretKeys</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">What the change does, saved unless a dry run.</response>
    /// <response code="400">A value out of range or not one of a setting's values, or <c>SecretKeys</c>: a key whose type carries secrets added to the allow-list without <c>confirmSecretKeys=true</c>.</response>
    /// <response code="409"><c>Changed</c>: the change would do other than the dry run said (a setting changed meanwhile); nothing was saved.</response>
    /// <returns>The change.</returns>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AccessRolesSettingsChangeDto>> UpdateAccessRolesSettings([FromBody, Required] AccessRolesSettingsDto body, [FromQuery] AccessRolesSettingsOptions options, CancellationToken cancellationToken)
        => Ok(await _settings.UpdateAsync(body, options ?? new AccessRolesSettingsOptions(), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Exports the audit log's lines of a time range, oldest first, as they are in the files: a JSON Lines download
    /// (<c>Cache-Control: no-store</c>; it holds user and library names).
    /// </summary>
    /// <param name="fromUtc">Only lines since then.</param>
    /// <param name="toUtc">Only lines before then.</param>
    /// <response code="200">The file.</response>
    /// <response code="400">A range that ends before it starts.</response>
    /// <returns>The JSON Lines file.</returns>
    [HttpGet("Audit/Export")]
    [Produces("application/x-ndjson")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ExportAccessRolesAudit([FromQuery] DateTime? fromUtc, [FromQuery] DateTime? toUtc) => _audit.Export(HttpContext, fromUtc, toUtc);

    /// <summary>
    /// <i>Remap by name</i> (the kit's): each user id Diagnostics lists under <c>Users.OrphanUsers</c> whose last seen
    /// name is one user's name on this server gets that user's id, assignment and ledger entry, after a snapshot, audited
    /// (<c>UsersRemapped</c>) as the caller's. A dry run (the default) only reports, with its <c>PlanToken</c>; a remap that
    /// writes sends it as <c>ifPlan</c>, and is refused when it would now do anything else.
    /// </summary>
    /// <param name="by">How to match: <c>Name</c> (the only way).</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="ifPlan">The dry run's <c>PlanToken</c>; a remap that writes needs it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">Another way to match, or a remap that writes without <c>ifPlan</c>.</response>
    /// <response code="409">The data is read-only, or the remap would do other than its dry run said (<c>ifPlan</c>).</response>
    /// <returns>The remap report.</returns>
    [HttpPost("Users/Remap")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> RemapAccessRolesUsers([FromQuery] string? by, [FromQuery] bool? dryRun, [FromQuery] string? ifPlan, CancellationToken cancellationToken)
        => Ok(await _admin.RemapUsersAsync(by, dryRun, ifPlan, User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// <i>Apply deleted-user policy</i> (the kit's) to unknown user ids from <c>Users.OrphanUsers</c>: Access Roles'
    /// policy is <c>Delete</c>, so their assignments and ledger entries go, after a snapshot, audited
    /// (<c>UserDeleted</c>) as the caller's.
    /// </summary>
    /// <param name="request">The user ids.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">No ids, too many, or a current user's id.</response>
    /// <response code="409">The data is read-only.</response>
    /// <returns>The policy report.</returns>
    [HttpPost("Users/ApplyPolicy")]
    [RequestSizeLimit(AccessRolesRolesController.MaxBodyBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IDictionary<string, object?>>> ApplyAccessRolesDeletedUserPolicy([FromBody, Required] AccessRolesApplyPolicyRequest request, CancellationToken cancellationToken)
        => Ok(await _admin.ApplyDeletedUserPolicyAsync(request, User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Exports the data: roles, assignments with their overrides, default roles, known libraries, the ledger,
    /// the mirror's records and the settings, as the kit's envelope (<c>jellyfin-plugin-export/1</c>), an attachment that
    /// isn't cached. It holds user and library names: keep it safe.
    /// </summary>
    /// <response code="200">The export, as an attachment.</response>
    /// <response code="503">The data file can't be read, or its upgrade failed.</response>
    /// <returns>The file.</returns>
    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult ExportAccessRolesData() => _admin.ExportData(HttpContext);

    /// <summary>
    /// Imports an export: the request body, <c>application/json</c>, at most 100 MB. A dry run (the default)
    /// reports what changes for each user. <c>Merge</c> (the default) adds the export's roles and assignments and replaces
    /// older copies by id; <c>Replace</c> makes them the export's. Users are matched by <c>Id</c> (the default) or
    /// <c>Name</c>, never a deleted user; libraries by id, then folders, then name, and one that matches nothing is left out
    /// and listed. The save takes a snapshot first and is refused as any change would be; settings are never applied.
    /// </summary>
    /// <param name="mode"><c>Merge</c> or <c>Replace</c>.</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="mapUsersBy"><c>Id</c> or <c>Name</c>.</param>
    /// <param name="onUnmanage">What the library access of users the import takes out of management becomes.</param>
    /// <param name="confirmNoFullAdmin">Confirms leaving no enabled administrator with every library.</param>
    /// <param name="ifPreview">The dry run's <c>PreviewToken</c>.</param>
    /// <response code="200">The report, saved unless a dry run.</response>
    /// <response code="400"><c>NotAnExport</c>, <c>ExportFormatNotSupported</c>, <c>NotThisPlugin</c>, an export that can't be read, a bad <c>mode</c> or <c>mapUsersBy</c>, or <c>UnmanageChoiceRequired</c>.</response>
    /// <response code="409"><c>NewerSchemaVersion</c>, <c>Changed</c>, <c>NoLibraries</c>, <c>NoLedgerEntry</c>, <c>NoFullAdministrator</c>, or the data is read-only.</response>
    /// <response code="413">The body is over 100 MB.</response>
    /// <returns>The report.</returns>
    [HttpPost("Import")]
    [RequestSizeLimit(ExportEnvelope.MaxImportBytes)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<ActionResult<AccessRolesDataImportDto>> ImportAccessRolesData(
        [FromQuery] string? mode,
        [FromQuery] bool? dryRun,
        [FromQuery] string? mapUsersBy,
        [FromQuery] AccessRolesUnmanageChoice? onUnmanage,
        [FromQuery] bool? confirmNoFullAdmin,
        [FromQuery] string? ifPreview)
        => Ok(await _admin.ImportDataAsync(Request, mode, mapUsersBy, Options(dryRun, onUnmanage, confirmNoFullAdmin, ifPreview), User).ConfigureAwait(false));

    /// <summary>
    /// Lists the data's snapshots, newest first: the daily ones (<c>SnapshotsToKeep</c>) and those taken
    /// before an operation (an import, a restore, a deleted user's policy, <i>Remap by name</i>), and whether the library
    /// sync is held after a restore.
    /// </summary>
    /// <response code="200">The snapshots.</response>
    /// <returns>The list.</returns>
    [HttpGet("Snapshots")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IDictionary<string, object?>> GetAccessRolesSnapshots() => Ok(_admin.GetSnapshots());

    /// <summary>
    /// <i>Restore snapshot</i>: the roles, assignments and default roles become the snapshot's (a
    /// <c>Replace</c> import of it, users by id, this server's bookkeeping kept), after a snapshot of the state it replaces.
    /// A dry run (the default) reports what changes for each user. After the save the library sync is held until an admin
    /// has seen its dry run and confirmed it (<c>POST Sync?dryRun=true</c>, then <c>confirmRestore=true</c>); settings locks
    /// apply at once.
    /// </summary>
    /// <param name="name">The snapshot's name, as <c>GET Snapshots</c> lists it.</param>
    /// <param name="dryRun">Whether to only report; true when left out.</param>
    /// <param name="onUnmanage">What the library access of users the restore takes out of management becomes.</param>
    /// <param name="confirmNoFullAdmin">Confirms leaving no enabled administrator with every library.</param>
    /// <param name="ifPreview">The dry run's <c>PreviewToken</c>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <response code="200">The report, restored unless a dry run.</response>
    /// <response code="400">No name, or <c>UnmanageChoiceRequired</c>.</response>
    /// <response code="404">No snapshot has that name.</response>
    /// <response code="409"><c>NewerSchemaVersion</c>, a snapshot that can't be read, <c>Changed</c>, <c>NoLibraries</c>, <c>NoLedgerEntry</c>, <c>NoFullAdministrator</c>, <c>RestoreInProgress</c> (another restore is between its start and the end of its save: restores run one at a time), or the data is read-only.</response>
    /// <response code="503">The snapshot, or the sync's hold file, couldn't be read or written; nothing was restored.</response>
    /// <returns>The report.</returns>
    [HttpPost("Snapshots/Restore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AccessRolesDataImportDto>> RestoreAccessRolesSnapshot(
        [FromQuery] string? name,
        [FromQuery] bool? dryRun,
        [FromQuery] AccessRolesUnmanageChoice? onUnmanage,
        [FromQuery] bool? confirmNoFullAdmin,
        [FromQuery] string? ifPreview,
        CancellationToken cancellationToken)
        => Ok(await _admin.RestoreSnapshotAsync(name, Options(dryRun, onUnmanage, confirmNoFullAdmin, ifPreview), User, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Queues the daily "Access Roles: Maintenance" task to run now.
    /// </summary>
    /// <response code="200">Queued.</response>
    /// <response code="503">The server's scheduled tasks aren't available.</response>
    /// <returns>What was queued.</returns>
    [HttpPost("Maintenance/Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<IDictionary<string, object?>> RunAccessRolesMaintenance() => Ok(_admin.RunMaintenance());

    // A dry run unless the request says otherwise (dryRun=true by default for an import and a restore).
    private static AccessRolesWriteOptions Options(bool? dryRun, AccessRolesUnmanageChoice? onUnmanage, bool? confirmNoFullAdmin, string? ifPreview) => new()
    {
        DryRun = dryRun ?? true,
        OnUnmanage = onUnmanage,
        ConfirmNoFullAdmin = confirmNoFullAdmin ?? false,
        IfPreview = ifPreview
    };
}
