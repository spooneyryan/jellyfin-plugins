using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.UserRatings.Lifecycle;

namespace Jellyfin.Plugin.UserRatings.Configuration;

/// <summary>
/// The Settings tab's fields, pure: every setting of <see cref="PluginConfiguration"/> in the tab's order,
/// with its label, its range or names, whether it needs a restart, its value in a fixed form (what the settings' version
/// hashes and a change compares) and as the report's text.
/// </summary>
/// <remarks>
/// <para>Every public setting of <see cref="PluginConfiguration"/> is here (a test holds the two lists together), so a
/// setting can't be added without the tab's version covering it.</para>
/// <para>The version is 16 hex digits of a SHA-256 of <c>Name=value</c> lines, so it changes with any setting, whoever saved
/// it (the tab, another admin, Jellyfin's own <c>POST /Plugins/{id}/Configuration</c>).</para>
/// </remarks>
internal static class RatingsSettingsFields
{
    /// <summary>The most daily snapshots a save keeps (the kit's daily class; Bookmarks' limit).</summary>
    public const int MaxSnapshotsToKeep = 365;

    private static readonly Dictionary<string, PropertyInfo> Properties = typeof(PluginConfiguration)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite)
        .ToDictionary(p => p.Name, StringComparer.Ordinal);

    /// <summary>Gets the kinds the Settings tab offers for <c>RateableItemTypes</c>: the default list and the ones it
    /// leaves off (tracks, box sets) with trailers, the kinds the ratings lifecycle follows.</summary>
    public static IReadOnlyList<string> OfferedItemTypes { get; } =
    [
        nameof(BaseItemKind.Movie), nameof(BaseItemKind.Series), nameof(BaseItemKind.Season), nameof(BaseItemKind.Episode),
        nameof(BaseItemKind.Video), nameof(BaseItemKind.MusicVideo), nameof(BaseItemKind.Trailer), nameof(BaseItemKind.MusicAlbum),
        nameof(BaseItemKind.Audio), nameof(BaseItemKind.Book), nameof(BaseItemKind.AudioBook), nameof(BaseItemKind.BoxSet)
    ];

    /// <summary>Gets the offered kinds' names as the Settings tab words them ('set.kind.*'), for the dry run's sentences.</summary>
    public static IReadOnlyDictionary<string, string> KindNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(BaseItemKind.Movie)] = "Movies",
        [nameof(BaseItemKind.Series)] = "Series",
        [nameof(BaseItemKind.Season)] = "Seasons",
        [nameof(BaseItemKind.Episode)] = "Episodes",
        [nameof(BaseItemKind.Video)] = "Videos",
        [nameof(BaseItemKind.MusicVideo)] = "Music videos",
        [nameof(BaseItemKind.Trailer)] = "Trailers",
        [nameof(BaseItemKind.MusicAlbum)] = "Albums",
        [nameof(BaseItemKind.Audio)] = "Tracks",
        [nameof(BaseItemKind.Book)] = "Books",
        [nameof(BaseItemKind.AudioBook)] = "Audiobooks",
        [nameof(BaseItemKind.BoxSet)] = "Collections"
    };

    /// <summary>Gets the fields, in the Settings tab's order.</summary>
    public static IReadOnlyList<SettingField> Fields { get; } =
    [
        // Ratings and reviews ("Who and what")
        new(nameof(PluginConfiguration.EnableRatings), "Ratings"),
        new(nameof(PluginConfiguration.EnableReviews), "Reviews"),
        new(nameof(PluginConfiguration.WhoCanRate), "Who can rate and review", Names: [nameof(WhoCanRate.AllUsers), nameof(WhoCanRate.SelectedUsers)]),
        new(nameof(PluginConfiguration.RaterUserIds), "Users who can rate"),
        new(nameof(PluginConfiguration.RateableItemTypes), "What can be rated"),
        new(nameof(PluginConfiguration.RequirePlayedToRate), "Only after playing"),
        new(nameof(PluginConfiguration.MaxReviewLength), "Longest review", Min: RatingsSettings.MinReviewLimit, Max: RatingsSettings.MaxReviewLimit),
        new(nameof(PluginConfiguration.AllowSpoilerFlag), "Spoiler flag"),

        // Reviewers
        new(nameof(PluginConfiguration.HideReviewsFromHiddenUsers), "Leave out hidden users' ratings"),
        new(nameof(PluginConfiguration.HideReviewsFromDisabledUsers), "Leave out disabled users' ratings"),
        new(nameof(PluginConfiguration.ShowReviewerAvatars), "Reviewers' pictures"),

        // Details page
        new(nameof(PluginConfiguration.ShowAverageChip), "Member average chip"),
        new(nameof(PluginConfiguration.ShowHistogram), "Ratings histogram"),
        new(nameof(PluginConfiguration.MinRatingsForAverage), "Ratings before the average shows", Min: 1, Max: 10),
        new(nameof(PluginConfiguration.SectionExpandedByDefault), "Section starts open"),
        new(nameof(PluginConfiguration.ShowDetailButton), "Rate button", Names: [nameof(DetailButtonMode.Auto), nameof(DetailButtonMode.Always), nameof(DetailButtonMode.Never)]),
        new(nameof(PluginConfiguration.ReviewsPageSize), "Reviews per page", Min: 5, Max: 50),

        // Web client (the kit's)
        new(nameof(PluginConfiguration.EnableWebUi), "Add User Ratings to the web client"),
        new(nameof(PluginConfiguration.ReportWebAnchorProblems), "Record web client problems"),

        // Data and retention
        new(nameof(PluginConfiguration.SnapshotsToKeep), "Daily snapshots to keep", Min: 0, Max: MaxSnapshotsToKeep),
        new(nameof(PluginConfiguration.DetachedRetentionDays), "Archive detached ratings after", Min: 0, Max: RatingsLibraryConsumer.MaxRetentionDays),
        new(nameof(PluginConfiguration.ModerationLogDays), "Keep the moderation log for", Min: 0, Max: RatingsSettings.MaxModerationLogDays),
        new(nameof(PluginConfiguration.DeletedUserPolicy), "When a user is deleted", Names: ["Delete", "Anonymize", "KeepAsOrphan"]),

        // Jellyfin Enhanced
        new(nameof(PluginConfiguration.ShowJellyfinEnhancedBanner), "Jellyfin Enhanced banner"),

        // 1.1: posters, the Top Rated page, the mirror
        new(nameof(PluginConfiguration.ShowOnPosters), "Rating badges on posters"),
        new(nameof(PluginConfiguration.PosterBadgeContent), "A badge shows", Names: [nameof(PosterBadgeContent.Average), nameof(PosterBadgeContent.Mine), nameof(PosterBadgeContent.Both)]),
        new(nameof(PluginConfiguration.PosterBadgePosition), "Badge corner", Names: ["top-start", "top-end", "bottom-start", "bottom-end"]),
        new(nameof(PluginConfiguration.EnableTopRatedPage), "Top Rated page", RestartNeeded: true),
        new(nameof(PluginConfiguration.RegisterPluginPages), "Register with Plugin Pages", RestartNeeded: true),
        new(nameof(PluginConfiguration.PageHostMode), "Where the pages live", RestartNeeded: true, Names: ["FallbackOnly", "Auto", "PluginPagesOnly"]),
        new(nameof(PluginConfiguration.ShowFallbackNav), "Library tools links"),
        new(nameof(PluginConfiguration.MirrorToUserData), "Copy ratings into Jellyfin's own rating"),
    ];

    /// <summary>Gets a kind's name as the Settings tab words it, or the kind itself for one the tab doesn't offer.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The name.</returns>
    public static string KindName(string kind) => KindNames.TryGetValue(kind, out var name) ? name : kind;

    /// <summary>
    /// Gets a field by its name, in any case.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The field, or null.</returns>
    public static SettingField? Find(string name) => Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets a copy of the settings, every array its own.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The copy.</returns>
    public static PluginConfiguration Copy(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var copy = new PluginConfiguration();
        foreach (var property in Properties.Values)
        {
            var value = property.GetValue(configuration);
            property.SetValue(copy, value switch
            {
                Guid[] ids => (Guid[])ids.Clone(),
                string[] names => (string[])names.Clone(),
                _ => value
            });
        }

        return copy;
    }

    /// <summary>
    /// Gets a field's value.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="field">The field's name.</param>
    /// <returns>The value as the configuration holds it.</returns>
    public static object? Get(PluginConfiguration configuration, string field) => Property(field).GetValue(configuration);

    /// <summary>
    /// Sets a field's value.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="field">The field's name.</param>
    /// <param name="value">The value, of the field's type.</param>
    public static void Set(PluginConfiguration configuration, string field, object? value) => Property(field).SetValue(configuration, value);

    /// <summary>
    /// Gets the type of a field's value.
    /// </summary>
    /// <param name="field">The field's name.</param>
    /// <returns>The type.</returns>
    public static Type TypeOf(string field) => Property(field).PropertyType;

    /// <summary>
    /// Gets every field's value in its fixed form, by name: what the version hashes, and what tells a 409 the fields saved
    /// since.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyDictionary<string, string> Lines(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Fields.ToDictionary(f => f.Name, f => Canonical(configuration, f.Name), StringComparer.Ordinal);
    }

    /// <summary>
    /// Gets the settings' version: 16 hex digits of a SHA-256 of every field's <c>Name=value</c> line.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The version.</returns>
    public static string Version(PluginConfiguration configuration) => Version(Lines(configuration));

    /// <summary>
    /// Gets the version of a set of lines (<see cref="Lines"/>).
    /// </summary>
    /// <param name="lines">The lines.</param>
    /// <returns>The version.</returns>
    public static string Version(IReadOnlyDictionary<string, string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var text = new StringBuilder();
        foreach (var field in Fields)
        {
            text.Append(field.Name).Append('=').Append(lines[field.Name]).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())), 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Gets a field's value in its fixed form: <c>1</c> or <c>0</c>, a number, a name as stored, ids as <c>N</c> text joined
    /// by commas and names joined by the unit separator, both sorted (a list's order isn't a change).
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="field">The field's name.</param>
    /// <returns>The text.</returns>
    public static string Canonical(PluginConfiguration configuration, string field) => Get(configuration, field) switch
    {
        bool on => on ? "1" : "0",
        int number => number.ToString(CultureInfo.InvariantCulture),
        string name => name,
        Guid[] ids => string.Join(',', ids.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)),
        string[] names => string.Join('\u001f', names.Order(StringComparer.Ordinal)),
        null => string.Empty,
        var other => throw new InvalidOperationException("The setting " + field + " has a type the Settings tab doesn't know: " + other.GetType().Name)
    };

    /// <summary>
    /// Gets a field's value as the report's text.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="field">The field's name.</param>
    /// <param name="userName">A user's name, or null for an id that isn't a user of the server.</param>
    /// <returns>The text.</returns>
    public static string Text(PluginConfiguration configuration, string field, Func<Guid, string?> userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        return Get(configuration, field) switch
        {
            bool on => on ? "On" : "Off",
            int number => number.ToString(CultureInfo.InvariantCulture),
            string name => name.Length == 0 ? "(none)" : name,
            Guid[] { Length: 0 } => "Nobody",
            Guid[] ids => string.Join(", ", ids.Select(id => userName(id) ?? "a user who isn't on the server (" + id.ToString("N", CultureInfo.InvariantCulture) + ")")),
            string[] { Length: 0 } => "None",
            string[] names => string.Join(", ", names),
            _ => "(none)"
        };
    }

    private static PropertyInfo Property(string field)
        => Properties.TryGetValue(field, out var property)
            ? property
            : throw new ArgumentOutOfRangeException(nameof(field), field, "Not a setting of User Ratings.");
}
