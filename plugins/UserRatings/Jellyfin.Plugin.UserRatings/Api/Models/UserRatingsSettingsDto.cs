using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// The plugin's settings on the Settings tab: every value in the answer of
/// <c>GET /UserRatings/Admin/Settings</c>, and the body of <c>POST /UserRatings/Admin/Settings</c>, where a setting left out
/// (or null) keeps its saved value. Names are the settings' own, in the tab's order.
/// </summary>
public sealed class UserRatingsSettingsDto
{
    /// <summary>Gets or sets a value indicating whether ratings are on: off hides the whole web UI (default on).</summary>
    public bool? EnableRatings { get; set; }

    /// <summary>Gets or sets a value indicating whether reviews are on: off hides only the text UI (default on).</summary>
    public bool? EnableReviews { get; set; }

    /// <summary>Gets or sets who may rate and review: <c>AllUsers</c> (the default) or <c>SelectedUsers</c>, the users of <see cref="RaterUserIds"/>.</summary>
    public string? WhoCanRate { get; set; }

    /// <summary>Gets or sets the users who may rate while <see cref="WhoCanRate"/> is <c>SelectedUsers</c>: users of this server (an id already saved may stay).</summary>
    public IReadOnlyList<Guid>? RaterUserIds { get; set; }

    /// <summary>Gets or sets the item kinds members can rate, as <c>BaseItemKind</c> names (at least one).</summary>
    public IReadOnlyList<string>? RateableItemTypes { get; set; }

    /// <summary>Gets or sets a value indicating whether a member must have played an item before rating it (default off).</summary>
    public bool? RequirePlayedToRate { get; set; }

    /// <summary>Gets or sets the longest review, 2000 to 20000 characters (default 5000).</summary>
    public int? MaxReviewLength { get; set; }

    /// <summary>Gets or sets a value indicating whether a review can be marked as containing spoilers (default on).</summary>
    public bool? AllowSpoilerFlag { get; set; }

    /// <summary>Gets or sets a value indicating whether hidden users' ratings and reviews are left out for everyone but their author and moderation (default off).</summary>
    public bool? HideReviewsFromHiddenUsers { get; set; }

    /// <summary>Gets or sets a value indicating whether disabled users' ratings and reviews are left out for everyone but their author and moderation (default on).</summary>
    public bool? HideReviewsFromDisabledUsers { get; set; }

    /// <summary>Gets or sets a value indicating whether reviews show their author's picture (default on).</summary>
    public bool? ShowReviewerAvatars { get; set; }

    /// <summary>Gets or sets a value indicating whether the details page shows the member-average chip (default on).</summary>
    public bool? ShowAverageChip { get; set; }

    /// <summary>Gets or sets a value indicating whether the section shows the ratings histogram (default on).</summary>
    public bool? ShowHistogram { get; set; }

    /// <summary>Gets or sets how many ratings an item needs before its average shows, 1 to 10 (default 1).</summary>
    public int? MinRatingsForAverage { get; set; }

    /// <summary>Gets or sets a value indicating whether the section starts expanded (default on).</summary>
    public bool? SectionExpandedByDefault { get; set; }

    /// <summary>Gets or sets where the Rate button shows: <c>Auto</c> (the default), <c>Always</c> or <c>Never</c>.</summary>
    public string? ShowDetailButton { get; set; }

    /// <summary>Gets or sets how many reviews a page of the list holds, 5 to 50 (default 20).</summary>
    public int? ReviewsPageSize { get; set; }

    /// <summary>Gets or sets a value indicating whether the web client's stars, sections and chips are on (default on; the API works either way).</summary>
    public bool? EnableWebUi { get; set; }

    /// <summary>Gets or sets a value indicating whether the server records the web client's reports of missing page elements (default on).</summary>
    public bool? ReportWebAnchorProblems { get; set; }

    /// <summary>Gets or sets how many daily snapshots of the database the maintenance keeps, 0 to 365 (default 7; 0 takes none).</summary>
    public int? SnapshotsToKeep { get; set; }

    /// <summary>Gets or sets the days a detached rating stays live before it moves to the archive, 0 to 36500 (default 90; 0 keeps it live; never a delete).</summary>
    public int? DetachedRetentionDays { get; set; }

    /// <summary>Gets or sets the days the moderation log keeps its rows, 0 to 3650 (default 365; 0 keeps every row).</summary>
    public int? ModerationLogDays { get; set; }

    /// <summary>Gets or sets what happens to a deleted user's ratings: <c>Delete</c> (the default), <c>Anonymize</c> or <c>KeepAsOrphan</c>.</summary>
    public string? DeletedUserPolicy { get; set; }

    /// <summary>Gets or sets a value indicating whether the settings page shows the banner while Jellyfin Enhanced's reviews are on (default on).</summary>
    public bool? ShowJellyfinEnhancedBanner { get; set; }

    /// <summary>Gets or sets a value indicating whether poster cards show a rating badge (1.1; default off).</summary>
    public bool? ShowOnPosters { get; set; }

    /// <summary>Gets or sets what a badge shows (1.1): <c>Average</c> (the default), <c>Mine</c> or <c>Both</c>.</summary>
    public string? PosterBadgeContent { get; set; }

    /// <summary>Gets or sets a badge's corner (1.1): <c>top-start</c> (the default), <c>top-end</c>, <c>bottom-start</c> or <c>bottom-end</c>.</summary>
    public string? PosterBadgePosition { get; set; }

    /// <summary>Gets or sets a value indicating whether the Top Rated page is on (1.1; default on; a change needs a restart).</summary>
    public bool? EnableTopRatedPage { get; set; }

    /// <summary>Gets or sets a value indicating whether the pages are registered with the Plugin Pages plugin (1.1; default on; a change needs a restart).</summary>
    public bool? RegisterPluginPages { get; set; }

    /// <summary>Gets or sets where the pages live (1.1): <c>FallbackOnly</c> (the default), <c>Auto</c> or <c>PluginPagesOnly</c>; a change needs a restart.</summary>
    public string? PageHostMode { get; set; }

    /// <summary>Gets or sets a value indicating whether the pages are linked from the drawer's Library tools (1.1; default on).</summary>
    public bool? ShowFallbackNav { get; set; }

    /// <summary>Gets or sets a value indicating whether a member's rating is written into Jellyfin's own user rating too (1.1; off, and it can't be turned on yet).</summary>
    public bool? MirrorToUserData { get; set; }

    /// <summary>
    /// Gets the keys of a body that name no setting (misspelt, or a newer build's), which a save or a dry run refuses (400
    /// <c>UnknownSetting</c>, naming them); never in an answer.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; init; }
}
