using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Api.Models;
using Jellyfin.Plugin.UserRatings.Import;
using Jellyfin.Plugin.UserRatings.Lifecycle;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// <see cref="IUserRatingsAdminApi"/> over the ratings lifecycle and the kit's
/// <see cref="UserReconciler"/>, with the ratings store as its owner (<see cref="RatingsUserDataOwner"/>), and
/// the moderation (<see cref="RatingsModeration"/>), and the Settings tab (<see cref="RatingsSettingsService"/>).
/// </summary>
internal sealed class UserRatingsAdminApi : IUserRatingsAdminApi
{
    /// <summary>The JE export's header: how many reviews the file holds.</summary>
    public const string ExportedHeader = "X-UserRatings-Exported";

    /// <summary>The JE export's header: how many ratings were left out (<c>X-UserRatings-Skipped</c>).</summary>
    public const string SkippedHeader = "X-UserRatings-Skipped";

    /// <summary>The JE export's header: the ratings left out by reason, <c>Reason=count</c> joined by commas.</summary>
    public const string SkippedDetailHeader = "X-UserRatings-Skipped-Detail";

    /// <summary>The JE export's header: ratings of another copy of a title merged into one JE key (the newer written).</summary>
    public const string MergedCopiesHeader = "X-UserRatings-Merged-Copies";

    /// <summary>The JE export's header: reviews in the file longer than JE's 2,000 characters (0.3.24; JE shows them, but
    /// refuses to save an edit of one until it is cut).</summary>
    public const string OverJeLimitHeader = "X-UserRatings-Over-Je-Limit";

    /// <summary>The 400 for a Remap with another way to match than by name.</summary>
    public const string ByMessage = "by must be Name.";

    /// <summary>The 400 for an Apply deleted-user policy without the list.</summary>
    public const string UserIdsMessage = "The body must be {UserIds: [...]}.";

    /// <summary>The 400 for a Detach now without ids or with too many.</summary>
    public static readonly string ItemIdsMessage = string.Format(
        CultureInfo.InvariantCulture,
        "ItemIds must list 1 to {0} item ids.",
        UserRatingsHeldDetachRequest.MaxItemIds);

    private readonly RatingsLibraryConsumer _lifecycle;
    private readonly Func<UserReconciler> _users;
    private readonly Func<RatingsUserDataOwner> _owner;
    private readonly Func<RatingsModeration>? _moderation;
    private readonly Func<RatingsSettingsService>? _settings;
    private readonly Func<RatingsData>? _data;
    private readonly Func<Guid, bool>? _hasAllLibraries;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserRatingsAdminApi"/> class.
    /// </summary>
    /// <param name="lifecycle">The ratings lifecycle.</param>
    /// <param name="users">The kit's reconciler, asked at the first use of the user routes.</param>
    /// <param name="owner">The ratings' user-data owner, for a remap's would-be Ambiguous rows.</param>
    /// <param name="moderation">The moderation, asked at the first use of its routes; none in the tests of the other
    /// routes.</param>
    /// <param name="settings">The Settings tab, asked at the first use of its routes; none in the tests of the other
    /// routes.</param>
    /// <param name="data">The Data tab, asked at the first use of its routes; none in the tests of the other routes.</param>
    /// <param name="hasAllLibraries">Whether an admin (by user id) has access to every library, for the unknown users' tools;
    /// without it only an API key may use them.</param>
    public UserRatingsAdminApi(RatingsLibraryConsumer lifecycle, Func<UserReconciler> users, Func<RatingsUserDataOwner> owner, Func<RatingsModeration>? moderation = null, Func<RatingsSettingsService>? settings = null, Func<RatingsData>? data = null, Func<Guid, bool>? hasAllLibraries = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(owner);
        _lifecycle = lifecycle;
        _users = users;
        _owner = owner;
        _moderation = moderation;
        _settings = settings;
        _data = data;
        _hasAllLibraries = hasAllLibraries;
    }

    /// <inheritdoc />
    public Task MarkDetachedSeenAsync(CancellationToken cancellationToken) => _lifecycle.MarkAmbiguousSeenAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<UserRatingsHeldDetachDto> DetachHeldAsync(IReadOnlyCollection<Guid>? itemIds, Guid? adminId, CancellationToken cancellationToken)
    {
        if (itemIds is null || itemIds.Count == 0 || itemIds.Count > UserRatingsHeldDetachRequest.MaxItemIds)
        {
            throw PluginException.Invalid(ItemIdsMessage);
        }

        var (items, ratings, notHeld) = await _lifecycle.DetachHeldAsync(itemIds, adminId, cancellationToken).ConfigureAwait(false);
        return new UserRatingsHeldDetachDto { Items = items, Ratings = ratings, NotHeld = notHeld };
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> RemapUsersAsync(Guid? adminId, string? by, bool? dryRun, string? ifPlan, CancellationToken cancellationToken)
    {
        RequireAllLibraries(adminId, "A remap");
        if (!string.IsNullOrWhiteSpace(by) && !string.Equals(by.Trim(), "Name", StringComparison.OrdinalIgnoreCase))
        {
            throw PluginException.Invalid(ByMessage);
        }

        var dry = dryRun ?? true;
        var tally = RatingsUserDataOwner.BeginTally();
        var report = await _users().RemapByNameAsync(dry, ifPlan, cancellationToken).ConfigureAwait(false);
        var result = report.ToDictionary();
        result["Ambiguous"] = dry ? _owner().CountRemapConflicts(report.Mapped.ToDictionary(m => m.OldId, m => m.NewId)) : tally.Ambiguous;
        result["ArchivedRecords"] = dry ? _owner().CountRemapArchived(report.Mapped.ToDictionary(m => m.OldId, m => m.NewId)) : tally.Archived;
        return result;
    }

    /// <inheritdoc />
    public async Task<IDictionary<string, object?>> ApplyDeletedUserPolicyAsync(Guid? adminId, IReadOnlyCollection<Guid>? userIds, CancellationToken cancellationToken)
    {
        RequireAllLibraries(adminId, "The deleted-user policy");
        if (userIds is null)
        {
            throw PluginException.Invalid(UserIdsMessage);
        }

        var tally = RatingsUserDataOwner.BeginTally();
        var result = (await _users().ApplyPolicyAsync(userIds, cancellationToken).ConfigureAwait(false)).ToDictionary();
        result["Archived"] = tally.Archived;
        return result;
    }

    /// <inheritdoc />
    public QueryResult<UserRatingsAdminEntryDto> GetReviews(Guid? adminId, Guid? userId, Guid? itemId, DateTime? since, bool? hasText, UserRatingsModerationState? state, string? searchTerm, int startIndex, int? limit)
        => Moderation().List(adminId, new RatingsModeration.ModerationQuery
        {
            UserId = userId,
            ItemId = itemId,
            SinceUtc = since,
            HasText = hasText,
            State = state,
            SearchTerm = searchTerm,
            StartIndex = startIndex,
            Limit = limit
        });

    /// <inheritdoc />
    public Task<UserRatingsModerationDto> RemoveReviewAsync(Guid? adminId, Guid ratingId, bool? textOnly, UserRatingsModerationReason? reason, bool? dryRun, string? ifPreview, CancellationToken cancellationToken)
        => Moderation().RemoveAsync(adminId, ratingId, textOnly, reason, dryRun, ifPreview, cancellationToken);

    /// <inheritdoc />
    public QueryResult<UserRatingsModerationLogDto> GetModerationLog(Guid? adminId, int startIndex, int? limit, string? action)
        => Moderation().Log(adminId, startIndex, limit, action);

    /// <inheritdoc />
    public UserRatingsSettingsViewDto GetSettings() => Settings().Read();

    /// <inheritdoc />
    public Task<UserRatingsSettingsReportDto> ChangeSettingsAsync(UserRatingsSettingsDto? change, string? version, bool? dryRun, CancellationToken cancellationToken)
        => Settings().ChangeAsync(change, version, dryRun, cancellationToken);

    public IActionResult Export(Guid? adminId, HttpContext context) => Data().Export(adminId).ToFileResult(context, RatingsData.ExportFileStem);

    public IActionResult ExportJellyfinEnhanced(Guid? adminId, HttpContext context, bool? leaveOutRestricted = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var export = Data().ExportJellyfinEnhanced(adminId, leaveOutRestricted ?? true);
        var headers = context.Response.Headers;
        headers[ExportedHeader] = export.Reviews.ToString(CultureInfo.InvariantCulture);
        headers[SkippedHeader] = export.SkippedTotal.ToString(CultureInfo.InvariantCulture);
        headers[SkippedDetailHeader] = string.Join(",", export.Skipped.Select(p => p.Key + "=" + p.Value.ToString(CultureInfo.InvariantCulture)));
        headers[MergedCopiesHeader] = export.MergedCopies.ToString(CultureInfo.InvariantCulture);
        headers[OverJeLimitHeader] = export.OverJeLimit.ToString(CultureInfo.InvariantCulture);
        return new FileContentResult(export.Bytes, "application/json") { FileDownloadName = JellyfinEnhancedExportWriter.FileName };
    }

    public UserRatingsJobDto StartExport(Guid? adminId) => Data().StartExport(adminId);

    public (string Path, string Name) GetExportFile(Guid? adminId, Guid jobId) => Data().ExportFile(adminId, jobId);

    public Task<UserRatingsJobDto> StartImportAsync(Guid? adminId, Stream body, long? length, string? mode, bool? dryRun, string? mapUsersBy, string? ifPlan, CancellationToken cancellationToken)
        => Data().StartImportAsync(adminId, body, length, mode, dryRun, mapUsersBy, ifPlan, cancellationToken);

    public UserRatingsJobDto StartJellyfinEnhancedImport(Guid? adminId, UserRatingsJeImportRequest? body, bool? dryRun, string? ifPlan)
        => Data().StartJellyfinEnhancedImport(adminId, body, dryRun, ifPlan);

    public IReadOnlyList<UserRatingsJobDto> GetJobs(Guid? adminId) => Data().Jobs(adminId);

    public UserRatingsJobDto GetJob(Guid? adminId, Guid jobId) => Data().Job(adminId, jobId);

    public bool CancelJob(Guid? adminId, Guid jobId) => Data().CancelJob(adminId, jobId);

    public IDictionary<string, object?> GetSnapshots(Guid? adminId) => Data().Snapshots(adminId);

    public Task<IDictionary<string, object?>> StartEmptyAsync(Guid? adminId, CancellationToken cancellationToken)
        => Data().StartEmptyAsync(adminId, cancellationToken);

    /// <inheritdoc />
    public Task<IDictionary<string, object?>> RestoreSnapshotAsync(Guid? adminId, string? name, CancellationToken cancellationToken)
        => Data().RestoreAsync(adminId, name, cancellationToken);

    public QueryResult<UserRatingsDetachedDto> GetDetached(Guid? adminId, string? reason, int startIndex, int? limit)
        => Data().Detached(adminId, reason, startIndex, limit);

    public Task RelinkDetachedAsync(Guid? adminId, Guid ratingId, Guid? itemId, CancellationToken cancellationToken)
        => Data().RelinkAsync(adminId, ratingId, itemId, cancellationToken);

    public Task<string?> PurgeDetachedAsync(Guid? adminId, Guid ratingId, CancellationToken cancellationToken)
        => Data().PurgeAsync(adminId, ratingId, cancellationToken);

    /// <inheritdoc />
    public IDictionary<string, object?> ScopeDiagnostics(Guid? adminId, IDictionary<string, object?> described)
    {
        if (described is not null && !HasAllLibraries(adminId) && described.TryGetValue(KitUsers.UsersProperty, out var section) && section is IDictionary<string, object?> users)
        {
            described[KitUsers.UsersProperty] = new Dictionary<string, object?>(users, StringComparer.Ordinal)
            {
                ["OrphanUsers"] = Array.Empty<object>(),
                ["Message"] = null,
                ["OrphanUsersHidden"] = RatingsData.AllLibrariesCode
            };
        }

        return described!;
    }

    private bool HasAllLibraries(Guid? adminId) => adminId is not { } id || (_hasAllLibraries?.Invoke(id) ?? false);

    // The unknown users' tools change every library's ratings, so they are for admins with every library, as the Data tab's
    // routes: checked first, so a restricted admin learns nothing more.
    private void RequireAllLibraries(Guid? adminId, string what)
    {
        if (!HasAllLibraries(adminId))
        {
            throw new PluginException(PluginErrorKind.Forbidden, RatingsData.AllLibrariesMessage(what)) { Code = RatingsData.AllLibrariesCode };
        }
    }

    private RatingsData Data()
        => _data?.Invoke() ?? throw new InvalidOperationException("The Data tab isn't part of this admin API.");

    private RatingsSettingsService Settings()
        => _settings?.Invoke() ?? throw new InvalidOperationException("The Settings tab isn't part of this admin API.");

    private RatingsModeration Moderation()
        => _moderation?.Invoke() ?? throw new InvalidOperationException("The moderation isn't part of this admin API.");
}
