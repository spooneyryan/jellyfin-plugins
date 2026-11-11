using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Users;

namespace Jellyfin.Plugin.Bookmarks.Services;

/// <summary>
/// Implements <see cref="IBookmarkRecordsAdmin"/> over the kit's <see cref="UserReconciler"/> (with the bookmark store as
/// one of its owners, <see cref="BookmarkUserDataOwner"/>) and <see cref="DetachedRecords"/> (with
/// <see cref="BookmarkIdentityService"/> as the owner of the detached bookmarks).
/// </summary>
internal sealed class BookmarkRecordsAdmin : IBookmarkRecordsAdmin
{
    private readonly UserReconciler _reconciler;
    private readonly DetachedRecords _detached;
    private readonly BookmarkIdentityService _owner;

    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkRecordsAdmin"/> class.
    /// </summary>
    /// <param name="reconciler">The kit's reconciler.</param>
    /// <param name="detached">The kit's detached records.</param>
    /// <param name="owner">The bookmarks' record owner.</param>
    public BookmarkRecordsAdmin(UserReconciler reconciler, DetachedRecords detached, BookmarkIdentityService owner)
    {
        ArgumentNullException.ThrowIfNull(reconciler);
        ArgumentNullException.ThrowIfNull(detached);
        ArgumentNullException.ThrowIfNull(owner);
        _reconciler = reconciler;
        _detached = detached;
        _owner = owner;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> RemapUsersAsync(string? by, bool? dryRun, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(by) && !string.Equals(by.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
        {
            throw PluginException.Invalid("by must be Name.");
        }

        return (await _reconciler.RemapByNameAsync(dryRun ?? true, cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ApplyDeletedUserPolicyAsync(BookmarkApplyPolicyRequest request, CancellationToken cancellationToken)
    {
        if (request?.UserIds is not { } ids)
        {
            throw PluginException.Invalid("The body must be {UserIds: [...]}.");
        }

        return (await _reconciler.ApplyPolicyAsync([.. ids], cancellationToken).ConfigureAwait(false)).ToDictionary();
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> PurgeDetachedAsync(int? olderThanDays, bool includeArchive, CancellationToken cancellationToken)
        => (await _detached.PurgeAsync(_owner, olderThanDays, includeArchive, cancellationToken).ConfigureAwait(false)).ToDictionary();
}
