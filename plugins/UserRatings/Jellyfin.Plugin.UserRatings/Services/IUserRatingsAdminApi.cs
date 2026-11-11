using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.UserRatings.Api.Models;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// The admin endpoints' service: the admin controller's dependency besides the kit's Diagnostics, so the
/// controller names no internal type. Errors are the kit's <c>PluginException</c>s, which the controller's
/// filter turns into ProblemDetails. It also serves Moderation, the Settings tab, the detached list with its
/// relink and purge, and the data jobs.
/// </summary>
public interface IUserRatingsAdminApi
{
    /// <summary>
    /// Records that the admin opened Data → Detached &amp; ambiguous (<c>Meta.AmbiguousSeenUtc</c>), which
    /// ends the dashboard's Ambiguous banner until a library change creates new Ambiguous ratings.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for the database.</param>
    /// <returns>A task.</returns>
    Task MarkDetachedSeenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Detaches the ratings kept on items that are gone (Diagnostics' <c>Lifecycle.Held</c>):
    /// as Missing, each in the moderation log with the admin, and one Activity Log entry with their ids. An item
    /// back in the library since keeps its ratings.
    /// </summary>
    /// <param name="itemIds">The items, 1 to <see cref="UserRatingsHeldDetachRequest.MaxItemIds"/>.</param>
    /// <param name="adminId">The admin's user id, or null for an API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the database.</param>
    /// <returns>What was detached.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for no ids or too many; 503 while the data is read-only or busy.</exception>
    Task<UserRatingsHeldDetachDto> DetachHeldAsync(IReadOnlyCollection<Guid>? itemIds, Guid? adminId, CancellationToken cancellationToken);

    /// <summary>
    /// <i>Remap by name</i>: each unknown user id whose last seen name
    /// (<c>Meta.KnownUsers</c>) is one server user's name gets that user's id, ratings and all, after a snapshot, under the
    /// conflict rule (on an item both rated, the newer rating stays attached and the other is detached as Ambiguous).
    /// Users Jellyfin reported deleted here and former members are never remapped. A dry run only reports, with its
    /// <c>PlanToken</c>; the real run is bound to it: it must be given the token back and maps
    /// nothing the dry run didn't show. Both answer <c>Ambiguous</c> (the rows the remap would make, or made) and
    /// <c>ArchivedRecords</c> (archived ratings moved).
    /// </summary>
    /// <param name="adminId">The admin's user id, or null for an API key: an admin with
    /// access to every library only, as the Data tab's routes (a remap moves every library's ratings).</param>
    /// <param name="by">How to match: <c>Name</c> (the only way), or null.</param>
    /// <param name="dryRun">Whether to only report; true when null.</param>
    /// <param name="ifPlan">The dry run's <c>PlanToken</c>; required unless a dry run.</param>
    /// <param name="cancellationToken">Cancels waiting for the database.</param>
    /// <returns>The kit's remap report, with <c>Ambiguous</c> and <c>ArchivedRecords</c>.</returns>
    /// <exception cref="Kit.Api.PluginException">400 another way to match, or a real run without <paramref name="ifPlan"/>;
    /// 409 the plan changed since the dry run (<c>Changed</c>, nothing written), the data is read-only (not in a dry run),
    /// or the list of users deleted here (<c>kit-users.json</c>) can't be read; 503 the archive can't be read; 403
    /// <c>AllLibrariesNeeded</c> an admin without access to every library (before anything else).</exception>
    Task<IDictionary<string, object?>> RemapUsersAsync(Guid? adminId, string? by, bool? dryRun, string? ifPlan, CancellationToken cancellationToken);

    /// <summary>
    /// <i>Apply deleted-user policy</i> to unknown user ids, as Diagnostics lists them
    /// under <c>Users.OrphanUsers</c>: <c>DeletedUserPolicy</c>, after a snapshot, as when Jellyfin reports a deletion.
    /// </summary>
    /// <param name="adminId">The admin's user id, or null for an API key: an admin with access to every library only, as for
    /// a remap (Delete deletes every library's ratings).</param>
    /// <param name="userIds">The ids: 1 to 1000, none empty or a user of this server.</param>
    /// <param name="cancellationToken">Cancels waiting for the database.</param>
    /// <returns>The kit's policy report, with <c>Archived</c>: the archived ratings deleted or anonymized.</returns>
    /// <exception cref="Kit.Api.PluginException">400 no list, no ids, too many, an empty id or a current user's id; 409 the
    /// data is read-only (or turned read-only part way: only the ids done are marked); 503 the server's user list is
    /// unavailable or empty, or the archive can't be read (nothing written); 403 <c>AllLibrariesNeeded</c> an admin without
    /// access to every library (before anything else).</exception>
    Task<IDictionary<string, object?>> ApplyDeletedUserPolicyAsync(Guid? adminId, IReadOnlyCollection<Guid>? userIds, CancellationToken cancellationToken);

    /// <summary>
    /// The plugin's Diagnostics as the caller may see them: for an admin without access to
    /// every library, <c>Users.OrphanUsers</c> (the unknown users' ids and last seen names) is empty, <c>Users.Message</c>
    /// null and <c>Users.OrphanUsersHidden</c> is <c>AllLibrariesNeeded</c>; the counts stay.
    /// </summary>
    /// <param name="adminId">The admin's user id, or null for an API key.</param>
    /// <param name="described">The kit's Diagnostics.</param>
    /// <returns>The Diagnostics to answer.</returns>
    IDictionary<string, object?> ScopeDiagnostics(Guid? adminId, IDictionary<string, object?> described);

    /// <summary>
    /// The Moderation list (<c>Admin/Reviews</c>): every rating and review the filters match that
    /// the caller may moderate (an API key: all; a signed-in admin: those on items they can open, and the detached ones only
    /// with every library), the row changed last first.
    /// </summary>
    /// <param name="adminId">The admin's user id, or null for an API key.</param>
    /// <param name="userId">Only this member's rows.</param>
    /// <param name="itemId">Only the rows on this item (an item the admin can't open lists nothing).</param>
    /// <param name="since">Only rows changed at or after this time.</param>
    /// <param name="hasText">Only rows with (true) or without (false) a review.</param>
    /// <param name="state">Which rows by state; all when null.</param>
    /// <param name="searchTerm">A text the author's name, the item's or series' name, or the review holds (any case).</param>
    /// <param name="startIndex">Rows to skip.</param>
    /// <param name="limit">Rows to return (50 by default, at most 200).</param>
    /// <returns>The page and the total.</returns>
    /// <exception cref="Kit.Api.PluginException">400 a search term over 200 characters; 503.</exception>
    QueryResult<UserRatingsAdminEntryDto> GetReviews(Guid? adminId, Guid? userId, Guid? itemId, DateTime? since, bool? hasText, UserRatingsModerationState? state, string? searchTerm, int startIndex, int? limit);

    /// <summary>
    /// A moderator's <i>Remove text only</i> (the default) or <i>Remove rating and review</i>:
    /// a dry run by default, answering what goes and a <c>PreviewToken</c>; the real run must send the token back,
    /// takes a pre-operation snapshot, writes one moderation-log row (ids, the reason code and counts only), and deleting
    /// the rating writes a <c>Moderator</c> tombstone; the archive's earlier lines of the rating go with no copy, and a
    /// checkpoint of the database's write-ahead log is asked for (in the background).
    /// </summary>
    /// <param name="adminId">The admin's user id, or null for an API key.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="textOnly">Whether only the review goes (true when null).</param>
    /// <param name="reason">The reason, or null.</param>
    /// <param name="dryRun">Whether to only report (true when null).</param>
    /// <param name="ifPreview">The dry run's <c>PreviewToken</c>; required for a real run.</param>
    /// <param name="cancellationToken">Cancels waiting for the database.</param>
    /// <returns>What goes, or went.</returns>
    /// <exception cref="Kit.Api.PluginException">400 a real run without a well-formed token; 404; 409 no review to remove, or
    /// <c>Changed</c> (the rating changed since the dry run; nothing written); 503 read-only or busy (a real run).</exception>
    Task<UserRatingsModerationDto> RemoveReviewAsync(Guid? adminId, Guid ratingId, bool? textOnly, UserRatingsModerationReason? reason, bool? dryRun, string? ifPreview, CancellationToken cancellationToken);

    /// <summary>
    /// A page of the moderation log, newest first (<c>Admin/ModerationLog</c>), with the names read now.
    /// </summary>
    /// <param name="adminId">The admin's user id, or null for an API key.</param>
    /// <param name="startIndex">Rows to skip.</param>
    /// <param name="limit">Rows to return (50 by default, at most 200).</param>
    /// <param name="action">Only rows of this action, or null.</param>
    /// <returns>The page and the total.</returns>
    /// <exception cref="Kit.Api.PluginException">400 an action this version doesn't know; 503.</exception>
    QueryResult<UserRatingsModerationLogDto> GetModerationLog(Guid? adminId, int startIndex, int? limit, string? action);

    /// <summary>
    /// The Settings tab's settings: every setting, its default, the settings' version, the users and item
    /// kinds the tab offers, and the saved values the plugin can't use as they are.
    /// </summary>
    /// <returns>The settings.</returns>
    UserRatingsSettingsViewDto GetSettings();

    /// <summary>
    /// Changes the settings: a dry run by default, which answers what changes, what that means for the
    /// members and the data, and whether a restart is needed; a save needs the version the change was made from.
    /// </summary>
    /// <param name="change">The settings to change; one left out (or null) keeps its value.</param>
    /// <param name="version">The settings' version the change was made from; required for a save.</param>
    /// <param name="dryRun">Whether nothing is saved; true when null.</param>
    /// <param name="cancellationToken">Cancels waiting for another save.</param>
    /// <returns>The report.</returns>
    /// <exception cref="Kit.Api.PluginException">400 a key that names no setting (<c>UnknownSetting</c>), a value it can't
    /// use (<c>InvalidSetting:</c> and the setting's name), a save without a version (<c>VersionNeeded</c>); 409
    /// <c>SettingsChanged</c>, the settings saved since the version (nothing saved); 503 the plugin isn't loaded.</exception>
    Task<UserRatingsSettingsReportDto> ChangeSettingsAsync(UserRatingsSettingsDto? change, string? version, bool? dryRun, CancellationToken cancellationToken);

    /// <summary>Builds the export at once, as a download.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="context">The request.</param>
    /// <returns>The file.</returns>
    IActionResult Export(Guid? adminId, HttpContext context);

    /// <summary>
    /// The ratings as Jellyfin Enhanced 12.14's <c>reviews.json</c>, as a download: the
    /// switch-over's way back. Nothing is written on the server. The response's headers count what it holds and left out.
    /// </summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="context">The request.</param>
    /// <param name="leaveOutRestricted">Whether the ratings of titles not every member can open are left out (true when left
    /// out of the request; 0.3.24).</param>
    /// <returns>The file.</returns>
    IActionResult ExportJellyfinEnhanced(Guid? adminId, HttpContext context, bool? leaveOutRestricted = null);

    /// <summary>Starts the export as a job.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <returns>The job.</returns>
    UserRatingsJobDto StartExport(Guid? adminId);

    /// <summary>Gets a finished export job's file; for admins with every library and API keys.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="jobId">The job.</param>
    /// <returns>The path and the name to save it under.</returns>
    (string Path, string Name) GetExportFile(Guid? adminId, Guid jobId);

    /// <summary>Stores an import's body and starts its job.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="body">The body.</param>
    /// <param name="length">Its declared length.</param>
    /// <param name="mode">Merge or Replace.</param>
    /// <param name="dryRun">Whether it writes nothing (true by default).</param>
    /// <param name="mapUsersBy">Id or Name.</param>
    /// <param name="ifPlan">The dry run's PlanToken, for a real run.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The job.</returns>
    Task<UserRatingsJobDto> StartImportAsync(Guid? adminId, Stream body, long? length, string? mode, bool? dryRun, string? mapUsersBy, string? ifPlan, CancellationToken cancellationToken);

    /// <summary>Starts the Jellyfin Enhanced review import's job.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="body">Copies, StoreUnmatched, RecreatePurged and the UserMap; null for the defaults.</param>
    /// <param name="dryRun">Whether it writes nothing (true by default).</param>
    /// <param name="ifPlan">The dry run's PlanToken, for a real run.</param>
    /// <returns>The job.</returns>
    UserRatingsJobDto StartJellyfinEnhancedImport(Guid? adminId, UserRatingsJeImportRequest? body, bool? dryRun, string? ifPlan);

    /// <summary>Lists the data jobs; for admins with every library and API keys.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <returns>The running one and the last 10.</returns>
    IReadOnlyList<UserRatingsJobDto> GetJobs(Guid? adminId);

    /// <summary>Gets a data job; for admins with every library and API keys.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="jobId">The job.</param>
    /// <returns>The job as it is now.</returns>
    UserRatingsJobDto GetJob(Guid? adminId, Guid jobId);

    /// <summary>Cancels a data job before its write phase; for admins with every library and API keys.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="jobId">The job to cancel.</param>
    /// <returns>False when a finished export's file is withdrawn but still open (a download), so its delete waits for the
    /// per-minute expiry; true otherwise.</returns>
    bool CancelJob(Guid? adminId, Guid jobId);

    /// <summary>Lists the snapshots and the integrity; for admins with every library and API keys.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <returns>Both.</returns>
    IDictionary<string, object?> GetSnapshots(Guid? adminId);

    /// <summary>Restores a snapshot.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="name">The snapshot.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>What was restored.</returns>
    Task<IDictionary<string, object?>> RestoreSnapshotAsync(Guid? adminId, string? name, CancellationToken cancellationToken);

    /// <summary>Starts the ratings empty while the database is damaged.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>What was done.</returns>
    Task<IDictionary<string, object?>> StartEmptyAsync(Guid? adminId, CancellationToken cancellationToken);

    /// <summary>Lists the detached ratings.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="reason">Only this reason.</param>
    /// <param name="startIndex">Rows to skip.</param>
    /// <param name="limit">Rows to return.</param>
    /// <returns>The page.</returns>
    QueryResult<UserRatingsDetachedDto> GetDetached(Guid? adminId, string? reason, int startIndex, int? limit);

    /// <summary>Relinks a detached rating.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>A task.</returns>
    Task RelinkDetachedAsync(Guid? adminId, Guid ratingId, Guid? itemId, CancellationToken cancellationToken);

    /// <summary>Purges a detached rating.</summary>
    /// <param name="adminId">The admin; null for an API key.</param>
    /// <param name="ratingId">The rating.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer.</param>
    /// <returns>The safety snapshot's name.</returns>
    Task<string?> PurgeDetachedAsync(Guid? adminId, Guid ratingId, CancellationToken cancellationToken);
}
