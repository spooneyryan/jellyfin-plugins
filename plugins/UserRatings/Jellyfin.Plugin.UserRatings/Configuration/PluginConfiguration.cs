using System;
using System.Diagnostics.CodeAnalysis;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// The plugin's settings (with the kit's common fields), in Jellyfin's XML file
/// <c>plugins/configurations/Jellyfin.Plugin.UserRatings.xml</c>. Ratings and reviews are not settings: they live in
/// <c>data/userratings/userratings.db</c>.
/// </summary>
/// <remarks>
/// The development builds 0.3.x store every setting and reads only the web client's two; the Settings tab renders
/// all of them, and each takes effect with the build that brings its feature. Settings whose values are names
/// (<see cref="WhoCanRate"/>, <see cref="ShowDetailButton"/> and the like) are strings, so a value a newer build wrote never
/// stops the file from loading; each task reads them with its default for anything it doesn't know.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---- The kit's common fields

    /// <summary>
    /// Gets or sets a value indicating whether the plugin's web script is added to the web client. Off: no stars,
    /// sections or chips in the web client; the REST API keeps working. Read on every page load.
    /// </summary>
    public bool EnableWebUi { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the server records the reports browsers send when a jellyfin-web element
    /// the web script relies on is missing.
    /// </summary>
    public bool ReportWebAnchorProblems { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the Top Rated page (1.1) is registered with the Plugin Pages plugin at
    /// startup, in the <c>Auto</c> and <c>PluginPagesOnly</c> page host modes. Changes apply after a restart.
    /// </summary>
    public bool RegisterPluginPages { get; set; } = true;

    /// <summary>
    /// Gets or sets where the Top Rated page (1.1) lives: <c>FallbackOnly</c> (the default),
    /// <c>Auto</c> or <c>PluginPagesOnly</c>.
    /// </summary>
    public string PageHostMode { get; set; } = "FallbackOnly";

    /// <summary>
    /// Gets or sets a value indicating whether the plugin's pages are linked from the "Library tools" section of the
    /// drawer.
    /// </summary>
    public bool ShowFallbackNav { get; set; } = true;

    /// <summary>
    /// Gets or sets how many daily snapshots of <c>userratings.db</c> the maintenance task keeps (the kit's
    /// daily class); 0 takes none.
    /// </summary>
    public int SnapshotsToKeep { get; set; } = 7;

    /// <summary>
    /// Gets or sets the days a detached rating stays in the live table before it moves to the kit's archive; never a delete.
    /// 0 keeps detached ratings live.
    /// </summary>
    public int DetachedRetentionDays { get; set; } = 90;

    /// <summary>
    /// Gets or sets what happens to a deleted user's ratings and reviews: <c>Delete</c> (the
    /// default), <c>Anonymize</c> or <c>KeepAsOrphan</c>.
    /// </summary>
    public string DeletedUserPolicy { get; set; } = "Delete";

    /// <summary>
    /// Gets or sets a value indicating whether the settings page shows the banner while Jellyfin Enhanced's user reviews are
    /// still on.
    /// </summary>
    public bool ShowJellyfinEnhancedBanner { get; set; } = true;

    // ---- Reviewers

    /// <summary>
    /// Gets or sets a value indicating whether hidden users' ratings and reviews are left out for everyone but their author
    /// and moderation (off by default; the JE import offers to adopt JE's value).
    /// </summary>
    public bool HideReviewsFromHiddenUsers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether disabled users' ratings and reviews are left out for everyone but their
    /// author and moderation.
    /// </summary>
    public bool HideReviewsFromDisabledUsers { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether reviews show their author's avatar.
    /// </summary>
    public bool ShowReviewerAvatars { get; set; } = true;

    // ---- Who and what

    /// <summary>
    /// Gets or sets a value indicating whether ratings are on. Off hides the whole web UI.
    /// </summary>
    public bool EnableRatings { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether reviews are on. Off hides only the text UI.
    /// </summary>
    public bool EnableReviews { get; set; } = true;

    /// <summary>
    /// Gets or sets who may rate and review (the requirement's "limited to single-user in settings"):
    /// <c>AllUsers</c> (the default) or <c>SelectedUsers</c>, the users in <see cref="RaterUserIds"/>. Everyone still reads
    /// every rating and review.
    /// </summary>
    public string WhoCanRate { get; set; } = "AllUsers";

    /// <summary>
    /// Gets or sets the users who may rate and review while <see cref="WhoCanRate"/> is <c>SelectedUsers</c>, possibly one.
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML serializer writes the settings file; an array round-trips there.")]
    public Guid[] RaterUserIds { get; set; } = [];

    /// <summary>
    /// Gets or sets the item kinds members can rate, as <c>BaseItemKind</c> names (tracks and box sets off by default).
    /// </summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Jellyfin's XML serializer writes the settings file; an array round-trips there.")]
    public string[] RateableItemTypes { get; set; } = ["Movie", "Series", "Season", "Episode", "Video", "MusicVideo", "MusicAlbum", "Book", "AudioBook"];

    /// <summary>
    /// Gets or sets a value indicating whether a member must have played an item (for a series, season or album: any of its
    /// episodes or tracks) before rating it (off by default).
    /// </summary>
    public bool RequirePlayedToRate { get; set; }

    /// <summary>
    /// Gets or sets the longest review, 2000 to 20000 characters. Lowering it never truncates stored text.
    /// </summary>
    public int MaxReviewLength { get; set; } = 5000;

    /// <summary>
    /// Gets or sets a value indicating whether a review can be marked as containing spoilers.
    /// </summary>
    public bool AllowSpoilerFlag { get; set; } = true;

    // ---- Details page

    /// <summary>
    /// Gets or sets a value indicating whether the details page shows the member-average chip.
    /// </summary>
    public bool ShowAverageChip { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the section shows the ratings histogram.
    /// </summary>
    public bool ShowHistogram { get; set; } = true;

    /// <summary>
    /// Gets or sets how many ratings an item needs before its average shows, 1 to 10.
    /// </summary>
    public int MinRatingsForAverage { get; set; } = 1;

    /// <summary>
    /// Gets or sets a value indicating whether the "Member ratings &amp; reviews" section starts expanded.
    /// </summary>
    public bool SectionExpandedByDefault { get; set; } = true;

    /// <summary>
    /// Gets or sets where the details page's Rate button shows: <c>Auto</c> (the default:
    /// series, season and album pages, whose child list sits above the section), <c>Always</c> or <c>Never</c>.
    /// </summary>
    public string ShowDetailButton { get; set; } = "Auto";

    /// <summary>
    /// Gets or sets how many reviews a page of the list holds, 5 to 50.
    /// </summary>
    public int ReviewsPageSize { get; set; } = 20;

    // ---- Posters (1.1)

    /// <summary>
    /// Gets or sets a value indicating whether poster cards show a rating badge (off by default).
    /// </summary>
    public bool ShowOnPosters { get; set; }

    /// <summary>
    /// Gets or sets what a poster badge shows: <c>Average</c> (the default), <c>Mine</c> or <c>Both</c>.
    /// </summary>
    public string PosterBadgeContent { get; set; } = "Average";

    /// <summary>
    /// Gets or sets the poster badge's corner, in logical terms: <c>top-start</c> (the default),
    /// <c>top-end</c>, <c>bottom-start</c> or <c>bottom-end</c>.
    /// </summary>
    public string PosterBadgePosition { get; set; } = "top-start";

    // ---- Pages and integration (1.1)

    /// <summary>
    /// Gets or sets a value indicating whether the Top Rated page is on. Its registration changes need a restart.
    /// </summary>
    public bool EnableTopRatedPage { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a member's own rating is written into Jellyfin's native user rating too
    /// (off; it can't be turned on yet).
    /// </summary>
    public bool MirrorToUserData { get; set; }

    // ---- Moderation. ModerationSnapshotDays (30) went in 0.3.16: the log
    // never keeps a removed review's text (TextSnapshot stays empty), so there was nothing for it to expire. A file that
    // still has it loads, the value ignored (XmlSerializer skips an element it doesn't know), and the next save drops it.

    /// <summary>
    /// Gets or sets the days moderation log rows are kept, 0 to 3650: the daily maintenance deletes the
    /// rows older than that, then asks for a checkpoint; 0 keeps every row. The log holds ids, reason codes and counts, never
    /// a review's text.
    /// </summary>
    public int ModerationLogDays { get; set; } = 365;
}
