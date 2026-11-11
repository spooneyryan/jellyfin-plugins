using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// Who sees which rows: the single choke point, pure logic over one request's
/// view of the users. Every row is server-wide. A row <b>counts</b> (in every summary, for every viewer, so averages and
/// counts are the same for everyone) unless its author is an orphaned id (neither a user nor a former member), or is
/// hidden or disabled while the matching Hide setting is on. A viewer's lists show the counted rows and the viewer's own,
/// which they always see (as "Mine") even when it doesn't count. The admin's Moderation list shows every row.
/// <c>MyStars</c> is only ever the viewer's own.
/// </summary>
/// <remarks>
/// Hidden and disabled are read per request (core raises no event when a user's policy changes), so a change
/// applies to the next request without any cache call; the summary cache keeps raw rows for that reason. Library access is
/// not this policy's: every surface resolves its items for the viewer first (<see cref="ItemAccess"/>).
/// </remarks>
internal sealed class VisibilityPolicy
{
    private readonly IReadOnlyDictionary<Guid, RatingAuthor> _users;
    private readonly IReadOnlyDictionary<Guid, string> _formerUsers;

    /// <summary>
    /// Initializes a new instance of the <see cref="VisibilityPolicy"/> class.
    /// </summary>
    /// <param name="hideHidden">The setting <c>HideReviewsFromHiddenUsers</c>.</param>
    /// <param name="hideDisabled">The setting <c>HideReviewsFromDisabledUsers</c>.</param>
    /// <param name="users">The server's users now, by id.</param>
    /// <param name="formerUsers">The former members (<c>FormerUsers</c>), by id.</param>
    public VisibilityPolicy(bool hideHidden, bool hideDisabled, IReadOnlyDictionary<Guid, RatingAuthor> users, IReadOnlyDictionary<Guid, string> formerUsers)
    {
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(formerUsers);
        HideHidden = hideHidden;
        HideDisabled = hideDisabled;
        _users = users;
        _formerUsers = formerUsers;
    }

    /// <summary>Gets a value indicating whether hidden users' rows are left out of what others see.</summary>
    public bool HideHidden { get; }

    /// <summary>Gets a value indicating whether disabled users' rows are left out of what others see.</summary>
    public bool HideDisabled { get; }

    /// <summary>
    /// Makes the policy of a request from the settings, the server's users (kit <c>KitUsers.GetAll</c>) and the former
    /// members.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="users">The users.</param>
    /// <param name="formerUsers">The former members.</param>
    /// <returns>The policy.</returns>
    public static VisibilityPolicy Create(RatingsSettings settings, IEnumerable<User> users, IReadOnlyDictionary<Guid, string> formerUsers)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(users);
        var authors = new Dictionary<Guid, RatingAuthor>();
        foreach (var user in users)
        {
            if (user is not null)
            {
                authors[user.Id] = new RatingAuthor(user.HasPermission(PermissionKind.IsHidden), user.HasPermission(PermissionKind.IsDisabled));
            }
        }

        return new VisibilityPolicy(settings.HideReviewsFromHiddenUsers, settings.HideReviewsFromDisabledUsers, authors, formerUsers);
    }

    /// <summary>
    /// Tells what an author id is: a user, a former member (<c>Anonymize</c>), or an orphan (a deleted user's id kept by
    /// <c>KeepAsOrphan</c> or found by the reconciliation).
    /// </summary>
    /// <param name="authorId">The row's <c>UserId</c>.</param>
    /// <returns>The kind.</returns>
    public AuthorKind KindOf(Guid authorId)
        => _users.ContainsKey(authorId) ? AuthorKind.Member : _formerUsers.ContainsKey(authorId) ? AuthorKind.FormerMember : AuthorKind.Orphan;

    /// <summary>
    /// Gets the name a former member's rows show ("Former member", "Former member 2", …).
    /// </summary>
    /// <param name="authorId">The row's <c>UserId</c>.</param>
    /// <returns>The name, or <c>null</c> for anyone else.</returns>
    public string? FormerMemberName(Guid authorId) => _formerUsers.TryGetValue(authorId, out var name) ? name : null;

    /// <summary>
    /// Tells whether an author's rows count in every summary (and show in everyone's lists).
    /// </summary>
    /// <param name="authorId">The row's <c>UserId</c>.</param>
    /// <returns>Whether they count.</returns>
    public bool Counts(Guid authorId)
    {
        if (_users.TryGetValue(authorId, out var author))
        {
            return !(HideHidden && author.IsHidden) && !(HideDisabled && author.IsDisabled);
        }

        return _formerUsers.ContainsKey(authorId);
    }

    /// <summary>
    /// Tells whether a viewer's lists show an author's row: a counted row, or the viewer's own.
    /// </summary>
    /// <param name="authorId">The row's <c>UserId</c>.</param>
    /// <param name="viewerId">The viewer; <see cref="Guid.Empty"/> for no viewer (a facade call with aggregates only).</param>
    /// <returns>Whether it shows.</returns>
    public bool IsListedFor(Guid authorId, Guid viewerId) => (viewerId != Guid.Empty && authorId == viewerId) || Counts(authorId);

    /// <summary>
    /// Builds an item's summary from its rows: only the counted ones, so it is the same for every viewer.
    /// </summary>
    /// <param name="rows">The item's attached rows (per-user contributions).</param>
    /// <returns>The summary.</returns>
    public RatingSummary Summarize(IEnumerable<SummaryRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return RatingSummary.Of(rows.Where(r => Counts(r.UserId)).Select(r => (r.Stars2, r.HasText)));
    }

    /// <summary>
    /// Gets the viewer's own stars among an item's rows (<c>MyStars</c>), whether or not their row counts.
    /// </summary>
    /// <param name="rows">The item's attached rows.</param>
    /// <param name="viewerId">The viewer; <see cref="Guid.Empty"/> gives <c>null</c>.</param>
    /// <returns>The stars, or <c>null</c>.</returns>
    public decimal? MyStars(IEnumerable<SummaryRow> rows, Guid viewerId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return viewerId == Guid.Empty || rows.FirstOrDefault(r => r.UserId == viewerId) is not { Stars2: { } stars2 } ? null : stars2 / 2m;
    }

    /// <summary>
    /// Gets the rows a viewer's list shows (<see cref="IsListedFor"/>), in their order.
    /// </summary>
    /// <param name="rows">The rows.</param>
    /// <param name="viewerId">The viewer.</param>
    /// <returns>The rows shown.</returns>
    public IReadOnlyList<RatingRecord> Listed(IEnumerable<RatingRecord> rows, Guid viewerId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(r => IsListedFor(r.UserId, viewerId)).ToList();
    }

    /// <summary>
    /// Gets the other members' rows a viewer's review list shows (<c>Reviews</c>: "excludes own").
    /// </summary>
    /// <param name="rows">The rows.</param>
    /// <param name="viewerId">The viewer.</param>
    /// <returns>The rows shown, without the viewer's own.</returns>
    public IReadOnlyList<RatingRecord> Others(IEnumerable<RatingRecord> rows, Guid viewerId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Where(r => r.UserId != viewerId && Counts(r.UserId)).ToList();
    }
}
