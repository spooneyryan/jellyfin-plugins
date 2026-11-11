using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Users;
using Jellyfin.Plugin.UserRatings.Lifecycle;

namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// The settings as the services read them: every name-valued setting parsed (ignoring case and
/// surrounding spaces) with a safe fallback for a value this version doesn't know or a blank one, and every number held to
/// its range. One reader, so no service parses a setting string of its own.
/// </summary>
/// <remarks>
/// <para><b>Why the names are strings.</b> Jellyfin 10.11.5's <c>BasePlugin&lt;T&gt;.LoadConfiguration</c> catches any
/// failure to deserialize the settings file and saves a new file of defaults over it
/// (<c>v10.11.5:MediaBrowser.Common/Plugins/BasePluginOfT.cs:184-198</c>), and its XML serializer throws on an enum value it
/// doesn't know. So a value a newer build writes (or a downgrade meets) would reset every setting the admin made; a string
/// loads whatever it holds, and this reader decides what it means.</para>
/// <para><b>The fallbacks</b> are the values that do least when the admin's intent can't be read:
/// <see cref="DeletedUserPolicy"/> <c>KeepAsOrphan</c> (never <c>Delete</c>, which can't be undone, though <c>Delete</c>
/// stays the configured default); <see cref="WhoCanRate"/> <c>SelectedUsers</c> (nobody gains the right to rate; nobody
/// loses the right to read); <see cref="RateableItemTypes"/> the known names only, or the default list when none is
/// known; the display settings their defaults. Each value it couldn't read is listed in <see cref="Problems"/>.</para>
/// </remarks>
internal sealed record RatingsSettings
{
    /// <summary>The shortest and longest review limit an admin may set (2000 is JE's cap).</summary>
    public const int MinReviewLimit = 2000;

    /// <summary>The longest review limit (the database's CHECK).</summary>
    public const int MaxReviewLimit = 20000;

    /// <summary>The most days moderation log rows are kept for; 0 keeps every row.</summary>
    public const int MaxModerationLogDays = 3650;

    // The kit's three page host modes (PageHostMode), as the Settings tab names them.
    private static readonly string[] PageHostModeNames = ["FallbackOnly", "Auto", "PluginPagesOnly"];

    /// <summary>Gets the default rateable kinds (tracks and box sets off).</summary>
    public static IReadOnlySet<BaseItemKind> DefaultRateableItemTypes { get; } = ImmutableHashSet.Create(
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.Episode,
        BaseItemKind.Video,
        BaseItemKind.MusicVideo,
        BaseItemKind.MusicAlbum,
        BaseItemKind.Book,
        BaseItemKind.AudioBook);

    /// <summary>Gets a value indicating whether ratings are on at all (<c>EnableRatings</c>).</summary>
    public bool EnableRatings { get; init; } = true;

    /// <summary>Gets a value indicating whether review text is on (<c>EnableReviews</c>).</summary>
    public bool EnableReviews { get; init; } = true;

    /// <summary>Gets who may rate.</summary>
    public WhoCanRate WhoCanRate { get; init; } = WhoCanRate.AllUsers;

    /// <summary>Gets the users who may rate while <see cref="WhoCanRate"/> is <see cref="WhoCanRate.SelectedUsers"/>.</summary>
    public IReadOnlySet<Guid> RaterUserIds { get; init; } = ImmutableHashSet<Guid>.Empty;

    /// <summary>Gets the kinds members can rate.</summary>
    public IReadOnlySet<BaseItemKind> RateableItemTypes { get; init; } = DefaultRateableItemTypes;

    /// <summary>Gets a value indicating whether a member must have played an item before rating it.</summary>
    public bool RequirePlayedToRate { get; init; }

    /// <summary>Gets the longest review, <see cref="MinReviewLimit"/> to <see cref="MaxReviewLimit"/>.</summary>
    public int MaxReviewLength { get; init; } = 5000;

    /// <summary>Gets a value indicating whether a review can be marked as containing spoilers.</summary>
    public bool AllowSpoilerFlag { get; init; } = true;

    /// <summary>Gets a value indicating whether hidden users' rows are left out for everyone but their author and moderation.</summary>
    public bool HideReviewsFromHiddenUsers { get; init; }

    /// <summary>Gets a value indicating whether disabled users' rows are left out for everyone but their author and moderation.</summary>
    public bool HideReviewsFromDisabledUsers { get; init; } = true;

    /// <summary>Gets a value indicating whether reviewers' avatars show (<c>ShowReviewerAvatars</c>; the
    /// reviews' <c>UserImageTag</c>).</summary>
    public bool ShowReviewerAvatars { get; init; } = true;

    /// <summary>Gets a value indicating whether the details page's media-info row shows the member chip.</summary>
    public bool ShowAverageChip { get; init; } = true;

    /// <summary>Gets a value indicating whether the section shows the 10-bucket histogram.</summary>
    public bool ShowHistogram { get; init; } = true;

    /// <summary>Gets a value indicating whether the section starts expanded.</summary>
    public bool SectionExpandedByDefault { get; init; } = true;

    /// <summary>Gets a value indicating whether posters show badges (1.1).</summary>
    public bool ShowOnPosters { get; init; }

    /// <summary>Gets a value indicating whether the Top Rated page is wanted (1.1; registering it needs a restart).</summary>
    public bool EnableTopRatedPage { get; init; } = true;

    /// <summary>Gets how many ratings an item needs before its average shows, 1 to 10.</summary>
    public int MinRatingsForAverage { get; init; } = 1;

    /// <summary>Gets how many reviews a page of the list holds, 5 to 50.</summary>
    public int ReviewsPageSize { get; init; } = 20;

    /// <summary>Gets where the Rate button shows.</summary>
    public DetailButtonMode ShowDetailButton { get; init; } = DetailButtonMode.Auto;

    /// <summary>Gets what a poster badge shows (1.1).</summary>
    public PosterBadgeContent PosterBadgeContent { get; init; } = PosterBadgeContent.Average;

    /// <summary>Gets a poster badge's corner (1.1).</summary>
    public PosterBadgePosition PosterBadgePosition { get; init; } = PosterBadgePosition.TopStart;

    /// <summary>Gets how many days the moderation log's rows are kept (the daily maintenance deletes
    /// older ones), 0 to <see cref="MaxModerationLogDays"/>; 0 keeps every row.</summary>
    public int ModerationLogDays { get; init; } = 365;

    /// <summary>Gets how many daily snapshots the maintenance keeps (<c>SnapshotsToKeep</c>, the kit's daily class), 0 to
    /// <see cref="RatingsSettingsFields.MaxSnapshotsToKeep"/>; 0 takes none (since 0.3.18:
    /// it was read straight from the configuration, held only to 0 at the low end).</summary>
    public int SnapshotsToKeep { get; init; } = 7;

    /// <summary>Gets after how many days a detached rating moves into the archive (<c>DetachedRetentionDays</c>),
    /// 0 to <see cref="RatingsLibraryConsumer.MaxRetentionDays"/>; 0 keeps them live (since 0.3.18, as
    /// <see cref="SnapshotsToKeep"/>: a value out of range was held to it with no problem named).</summary>
    public int DetachedRetentionDays { get; init; } = 90;

    /// <summary>Gets what happens to a deleted user's ratings.</summary>
    public DeletedUserPolicy DeletedUserPolicy { get; init; } = DeletedUserPolicy.Delete;

    /// <summary>Gets the settings this reader couldn't read as they are, each with what it used instead (for the log and
    /// Diagnostics); empty when every value was known.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>
    /// Reads the settings from the plugin's configuration.
    /// </summary>
    /// <param name="configuration">The configuration as Jellyfin loaded it.</param>
    /// <returns>The settings.</returns>
    public static RatingsSettings From(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var problems = new List<string>();

        return new RatingsSettings
        {
            EnableRatings = configuration.EnableRatings,
            EnableReviews = configuration.EnableReviews,
            WhoCanRate = ParseName(configuration.WhoCanRate, WhoCanRate.SelectedUsers, nameof(PluginConfiguration.WhoCanRate), problems),
            RaterUserIds = (configuration.RaterUserIds ?? []).Where(id => id != Guid.Empty).ToImmutableHashSet(),
            RateableItemTypes = ParseKinds(configuration.RateableItemTypes, problems),
            RequirePlayedToRate = configuration.RequirePlayedToRate,
            MaxReviewLength = Clamp(configuration.MaxReviewLength, MinReviewLimit, MaxReviewLimit, nameof(PluginConfiguration.MaxReviewLength), problems),
            AllowSpoilerFlag = configuration.AllowSpoilerFlag,
            HideReviewsFromHiddenUsers = configuration.HideReviewsFromHiddenUsers,
            HideReviewsFromDisabledUsers = configuration.HideReviewsFromDisabledUsers,
            ShowReviewerAvatars = configuration.ShowReviewerAvatars,
            ShowAverageChip = configuration.ShowAverageChip,
            ShowHistogram = configuration.ShowHistogram,
            SectionExpandedByDefault = configuration.SectionExpandedByDefault,
            ShowOnPosters = configuration.ShowOnPosters,
            EnableTopRatedPage = configuration.EnableTopRatedPage,
            MinRatingsForAverage = Clamp(configuration.MinRatingsForAverage, 1, 10, nameof(PluginConfiguration.MinRatingsForAverage), problems),
            ReviewsPageSize = Clamp(configuration.ReviewsPageSize, 5, 50, nameof(PluginConfiguration.ReviewsPageSize), problems),
            ShowDetailButton = ParseName(configuration.ShowDetailButton, DetailButtonMode.Auto, nameof(PluginConfiguration.ShowDetailButton), problems),
            PosterBadgeContent = ParseName(configuration.PosterBadgeContent, PosterBadgeContent.Average, nameof(PluginConfiguration.PosterBadgeContent), problems),
            PosterBadgePosition = ParseName(configuration.PosterBadgePosition?.Replace("-", string.Empty, StringComparison.Ordinal), PosterBadgePosition.TopStart, nameof(PluginConfiguration.PosterBadgePosition), problems),
            ModerationLogDays = Clamp(configuration.ModerationLogDays, 0, MaxModerationLogDays, nameof(PluginConfiguration.ModerationLogDays), problems),
            SnapshotsToKeep = Clamp(configuration.SnapshotsToKeep, 0, RatingsSettingsFields.MaxSnapshotsToKeep, nameof(PluginConfiguration.SnapshotsToKeep), problems),
            DetachedRetentionDays = Clamp(configuration.DetachedRetentionDays, 0, RatingsLibraryConsumer.MaxRetentionDays, nameof(PluginConfiguration.DetachedRetentionDays), problems),
            DeletedUserPolicy = ParsePolicy(configuration.DeletedUserPolicy, problems),
            Problems = CheckPageHostMode(configuration.PageHostMode, problems)
        };
    }

    /// <summary>
    /// Tells whether a user may create or edit ratings and reviews ("Who can rate"); everyone may read, and delete
    /// their own rows.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>Whether they may rate.</returns>
    public bool MayRate(Guid userId) => WhoCanRate == WhoCanRate.AllUsers || RaterUserIds.Contains(userId);

    /// <summary>
    /// Tells whether items of a kind can be rated (<see cref="RateableItemTypes"/>).
    /// </summary>
    /// <param name="kind">The item's kind.</param>
    /// <returns>Whether it is rateable.</returns>
    public bool IsRateable(BaseItemKind kind) => RateableItemTypes.Contains(kind);

    private static TEnum ParseName<TEnum>(string? value, TEnum fallback, string setting, List<string> problems)
        where TEnum : struct, Enum
    {
        var text = value?.Trim();
        if (!string.IsNullOrEmpty(text) && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{setting} '{value}' isn't a value this version knows; {fallback} is used"));
        return fallback;
    }

    private static DeletedUserPolicy ParsePolicy(string? value, List<string> problems)
    {
        if (DeletedUserPolicies.TryParse(value, out var policy, DeletedUserPolicy.KeepAsOrphan))
        {
            return policy;
        }

        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{nameof(PluginConfiguration.DeletedUserPolicy)} '{value}' isn't a value this version knows; {DeletedUserPolicy.KeepAsOrphan} is used, which deletes nothing"));
        return DeletedUserPolicy.KeepAsOrphan;
    }

    private static IReadOnlySet<BaseItemKind> ParseKinds(string[]? names, List<string> problems)
    {
        var kinds = ImmutableHashSet.CreateBuilder<BaseItemKind>();
        foreach (var name in names ?? [])
        {
            var text = name?.Trim();
            if (!string.IsNullOrEmpty(text) && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) && Enum.TryParse<BaseItemKind>(text, ignoreCase: true, out var kind) && Enum.IsDefined(kind))
            {
                kinds.Add(kind);
            }
            else
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{nameof(PluginConfiguration.RateableItemTypes)} '{name}' isn't an item kind this version knows; it is left out"));
            }
        }

        if (kinds.Count == 0)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{nameof(PluginConfiguration.RateableItemTypes)} names no item kind this version knows; the default list is used"));
            return DefaultRateableItemTypes;
        }

        return kinds.ToImmutable();
    }

    // PageHostMode is read by the Top Rated page's host (1.1), which isn't built: a name it won't know is named now, as the
    // kit's PageHostModes reads it (blank is the default, FallbackOnly; anything else not one of the three is FallbackOnly).
    private static List<string> CheckPageHostMode(string? value, List<string> problems)
    {
        var text = value?.Trim();
        if (!string.IsNullOrEmpty(text) && !PageHostModeNames.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"{nameof(PluginConfiguration.PageHostMode)} '{value}' isn't a value this version knows; FallbackOnly is used"));
        }

        return problems;
    }

    private static int Clamp(int value, int min, int max, string setting, List<string> problems)
    {
        if (value >= min && value <= max)
        {
            return value;
        }

        var used = Math.Clamp(value, min, max);
        problems.Add(string.Create(CultureInfo.InvariantCulture, $"{setting} {value} is outside {min} to {max}; {used} is used"));
        return used;
    }
}
