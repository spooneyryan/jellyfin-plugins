using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// The admin actions on Bookmarks' records of users and videos that are gone:
/// <i>Remap by name</i> and <i>Apply deleted-user policy</i> for the unknown users the reconciler lists (it never changes
/// a record itself), and <i>Purge</i>, the only hard delete of detached bookmarks. Each takes a pre-operation snapshot
/// first. Public only so the admin controller can take it: DTOs and BCL types only.
/// </summary>
public interface IBookmarkRecordsAdmin
{
    /// <summary>
    /// <i>Remap by name</i>: each unknown user id whose last seen name is one server user's name gets that
    /// user; a dry run only reports.
    /// </summary>
    /// <param name="by">How to match: <c>Name</c>, the only way (the default).</param>
    /// <param name="dryRun">Whether to only report; true by default.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The report: <c>DryRun</c>, <c>Mapped</c>, <c>Unmatched</c> with reasons, <c>Moved</c>, <c>Snapshots</c>.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for another <paramref name="by"/>; 409 while the data is read-only (not
    /// in a dry run).</exception>
    Task<IDictionary<string, object?>> RemapUsersAsync(string? by, bool? dryRun, CancellationToken cancellationToken);

    /// <summary>
    /// <i>Apply deleted-user policy</i> to unknown user ids: Bookmarks' <c>DeletedUserPolicy</c>, after a
    /// snapshot, as if Jellyfin had just deleted the users.
    /// </summary>
    /// <param name="request">The ids.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The report: <c>Policy</c>, <c>Affected</c>, per user, <c>Snapshots</c>.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for no ids, too many, an empty id or a current user's id; 409 while the
    /// data is read-only.</exception>
    Task<IDictionary<string, object?>> ApplyDeletedUserPolicyAsync(BookmarkApplyPolicyRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// <i>Purge</i>: deletes detached bookmarks for good, after a snapshot, and with
    /// <paramref name="includeArchive"/> the archived ones too. Bookmarks with a clip file are kept: their file needs its
    /// record.
    /// </summary>
    /// <param name="olderThanDays">Only bookmarks detached at least this many days ago; every detached one when null.</param>
    /// <param name="includeArchive">Whether to purge the archive's bookmarks too.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The report: <c>Purged</c>, <c>ArchivePurged</c>, <c>Snapshot</c>.</returns>
    /// <exception cref="Kit.Api.PluginException">400 for a negative age; 409 while the data is read-only; 503 when the
    /// archive can't be read.</exception>
    Task<IDictionary<string, object?>> PurgeDetachedAsync(int? olderThanDays, bool includeArchive, CancellationToken cancellationToken);
}
