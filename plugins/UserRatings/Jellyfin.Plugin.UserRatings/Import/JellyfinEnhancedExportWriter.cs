using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// The ratings as Jellyfin Enhanced 12.14's <c>reviews.json</c>: the switch-over's way
/// back. <c>{"Reviews":{"&lt;userIdN&gt;:&lt;movie|tv&gt;:&lt;tmdbKey&gt;":{UserId, TmdbId, MediaType, Content, Rating,
/// CreatedAt, UpdatedAt}}}</c>, where the TMDb key is a film's or series' TMDb id, <c>&lt;id&gt;:s&lt;N&gt;</c> for a season
/// and <c>&lt;id&gt;:s&lt;N&gt;:e&lt;M&gt;</c> for an episode (the series' id), and <c>TmdbId</c> holds the same whole key, as
/// JE 12.14 writes it (seen by posting reviews through JE's own API). <c>Rating</c> is the
/// stars (<c>Stars2 / 2</c>), <c>null</c> for a review without stars; <c>Content</c> is <c>""</c> without a review; the
/// times are UTC with seven fractional digits and <c>Z</c>, as JE writes them.
/// </summary>
/// <remarks>
/// Left out, each counted by its reason: a rating on an item kind JE doesn't review (<c>NotInJe</c>: music, books, home
/// videos...), one whose title has no TMDb id (<c>NoTmdbId</c>), a former member's (<c>FormerMember</c>: no user here for JE
/// to show), a detached rating Jellyfin Enhanced didn't give (<c>Detached</c>: its title isn't in the library, and
/// imported back it would be a second, detached copy), an archived one it didn't give (<c>Archived</c>, since 0.3.24: a
/// review of 0.3.23 found archived ratings dropped without a count), and, while the export leaves them out (the
/// default), one of a title some member can't open (<c>RestrictedLibrary</c>, since 0.3.24, JE 12.14
/// serving every review by TMDb id to any signed-in user). A detached or archived rating JE's import made stays in, with
/// its stored identity (imported back it is <c>AlreadyImported</c>). Copies of one title a member rated in two libraries
/// are one key in JE: the newer is written (<c>MergedCopies</c>). A review over JE's 2,000 characters is written whole and
/// counted (<c>OverJeLimit</c>): JE shows it but refuses to save an edit of it. The writer reads nothing and writes nothing
/// on the server: the caller sends the bytes.
/// </remarks>
internal static class JellyfinEnhancedExportWriter
{
    /// <summary>The file's name: JE's own.</summary>
    public const string FileName = "reviews.json";

    /// <summary>A rating on an item kind JE doesn't review.</summary>
    public const string NotInJe = "NotInJe";

    /// <summary>A rating whose title has no TMDb id JE could use.</summary>
    public const string NoTmdbId = "NoTmdbId";

    /// <summary>A rating of a member who is no longer a user here.</summary>
    public const string FormerMember = "FormerMember";

    /// <summary>A detached rating that didn't come from Jellyfin Enhanced.</summary>
    public const string Detached = "Detached";

    /// <summary>An archived rating that didn't come from Jellyfin Enhanced (since 0.3.24).</summary>
    public const string Archived = "Archived";

    /// <summary>A rating of a title some member can't open, while the export leaves those out (since 0.3.24).</summary>
    public const string RestrictedLibrary = "RestrictedLibrary";

    /// <summary>The most characters Jellyfin Enhanced 12.14 saves in a review ("Review content must not exceed 2000 characters.").</summary>
    public const int JeMaxReviewLength = 2000;

    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Writes the file.
    /// </summary>
    /// <param name="rows">Every rating in the database, attached and detached.</param>
    /// <param name="archived">Every rating in the archive, each with the identity it was archived with.</param>
    /// <param name="identityOf">A row's identity: the item's as it is now when it is in the library, else the row's.</param>
    /// <param name="isUser">Whether a user id is a user of this server.</param>
    /// <param name="isRestricted">Whether a rating that would be written is of a title some member can't open (null: none
    /// is left out for it).</param>
    /// <returns>The file and its counts.</returns>
    public static JellyfinEnhancedExport Write(
        IEnumerable<RatingRecord> rows,
        IEnumerable<RatingRecord> archived,
        Func<RatingRecord, IdentitySnapshot> identityOf,
        Func<Guid, bool> isUser,
        Func<RatingRecord, bool>? isRestricted = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(archived);
        ArgumentNullException.ThrowIfNull(identityOf);
        ArgumentNullException.ThrowIfNull(isUser);

        var entries = new Dictionary<string, (RatingRecord Row, string MediaType, string TmdbKey)>(StringComparer.Ordinal);
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var merged = 0;
        var live = new List<RatingRecord>(rows);
        var liveIds = live.Select(r => r.Id).ToHashSet();

        // An archived rating is detached; one the database holds too (archived while the rows were read) counts once.
        foreach (var (row, isArchived) in live.Select(r => (r, false)).Concat(archived.Where(r => !liveIds.Contains(r.Id)).Select(r => (r, true))))
        {
            if (row.Stars2 is null && string.IsNullOrEmpty(row.ReviewText))
            {
                continue;
            }

            string? reason = null;
            (string MediaType, string TmdbKey)? target = null;
            var fromJe = string.Equals(row.Origin, RatingValues.Origin.JellyfinEnhanced, StringComparison.Ordinal);
            if (!isUser(row.UserId))
            {
                reason = FormerMember;
            }
            else if (isArchived && !fromJe)
            {
                reason = Archived;
            }
            else if (row.DetachedUtc is not null && !fromJe)
            {
                reason = Detached;
            }
            else
            {
                // An archived rating's title isn't in the library: its identity is the one it was archived with.
                target = TmdbKeyOf(isArchived ? row.Identity : identityOf(row), out reason);
                if (target is not null && isRestricted is not null && isRestricted(row))
                {
                    target = null;
                    reason = RestrictedLibrary;
                }
            }

            if (target is not { } t)
            {
                skipped[reason!] = skipped.GetValueOrDefault(reason!) + 1;
                continue;
            }

            var key = row.UserId.ToString("N", CultureInfo.InvariantCulture) + ":" + t.MediaType + ":" + t.TmdbKey;
            if (entries.TryGetValue(key, out var have))
            {
                merged++;
                if (row.UpdatedUtc <= have.Row.UpdatedUtc)
                {
                    continue;
                }
            }

            entries[key] = (row, t.MediaType, t.TmdbKey);
        }

        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, Options))
        {
            json.WriteStartObject();
            json.WriteStartObject("Reviews");
            foreach (var (key, e) in entries.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                json.WriteStartObject(key);
                json.WriteString("UserId", e.Row.UserId.ToString("N", CultureInfo.InvariantCulture));
                json.WriteString("TmdbId", e.TmdbKey);
                json.WriteString("MediaType", e.MediaType);
                json.WriteString("Content", e.Row.ReviewText ?? string.Empty);
                if (e.Row.Stars2 is { } stars2)
                {
                    json.WriteNumber("Rating", stars2 / 2.0);
                }
                else
                {
                    json.WriteNull("Rating");
                }

                json.WriteString("CreatedAt", Time(e.Row.CreatedUtc));
                json.WriteString("UpdatedAt", Time(e.Row.UpdatedUtc));
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
        }

        var overLimit = entries.Values.Count(e => (e.Row.ReviewText?.Length ?? 0) > JeMaxReviewLength);
        return new JellyfinEnhancedExport(stream.ToArray(), entries.Count, skipped, merged, overLimit);
    }

    /// <summary>
    /// The JE media type and TMDb key of an identity: a movie's or a series' own TMDb id; a season's or an episode's series'
    /// id with its numbers.
    /// </summary>
    /// <param name="identity">The identity.</param>
    /// <param name="reason">Why there is none: <see cref="NotInJe"/> or <see cref="NoTmdbId"/>.</param>
    /// <returns>The media type and key, or null.</returns>
    internal static (string MediaType, string TmdbKey)? TmdbKeyOf(IdentitySnapshot identity, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(identity);
        reason = null;
        switch (identity.Kind)
        {
            case "Movie" when Tmdb(identity.ProviderIds) is { } movie:
                return ("movie", movie);
            case "Series" when Tmdb(identity.ProviderIds) is { } series:
                return ("tv", series);
            case "Season" when Tmdb(identity.SeriesProviderIds) is { } series && identity.Season is >= 0 and var season:
                return ("tv", string.Create(CultureInfo.InvariantCulture, $"{series}:s{season}"));
            case "Episode" when Tmdb(identity.SeriesProviderIds) is { } series && identity.Season is >= 0 and var season && identity.Episode is >= 0 and var episode:
                return ("tv", string.Create(CultureInfo.InvariantCulture, $"{series}:s{season}:e{episode}"));
            case "Movie" or "Series" or "Season" or "Episode":
                reason = NoTmdbId;
                return null;
            default:
                reason = NotInJe;
                return null;
        }
    }

    // JE's TMDb ids are 1 to 9 digits (its own routes' pattern); the provider id's name in any case.
    private static string? Tmdb(IReadOnlyDictionary<string, string>? ids)
    {
        var value = ids?.FirstOrDefault(p => string.Equals(p.Key, "Tmdb", StringComparison.OrdinalIgnoreCase)).Value?.Trim();
        return value is { Length: >= 1 and <= 9 } && value.All(char.IsAsciiDigit) ? value : null;
    }

    private static string Time(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
