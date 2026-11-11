using System;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// The rules an import checks each row of an export against before it writes anything: the
/// rules the store and its writer enforce, so a row that breaks one is left out with its reason, never one bad row failing
/// the whole import. What can be repaired is: a review gets the normalisation a member's write gets (<see cref="ReviewText"/>:
/// a NUL and the other control characters removed, line ends made <c>\n</c>), and an item's or a former member's name
/// loses its control characters.
/// </summary>
internal static class ImportRowRules
{
    /// <summary>The longest name an import keeps (an item's, its series', a former member's), in UTF-16 code units.</summary>
    public const int MaxNameLength = 1024;

    private const int MaxShownValue = 40;

    /// <summary>
    /// Checks a rating, live or archived, and normalises its review.
    /// </summary>
    /// <param name="id">Its id.</param>
    /// <param name="userId">Its member.</param>
    /// <param name="identity">Its item's identity, its names cleaned.</param>
    /// <param name="stars2">Its stars ×2.</param>
    /// <param name="review">Its review as in the file; normalised on the way out.</param>
    /// <param name="editCount">Its edit count.</param>
    /// <param name="origin">Its origin (null: an import's).</param>
    /// <param name="createdUtc">When it was created.</param>
    /// <param name="updatedUtc">When it was last written.</param>
    /// <param name="detachReason">Why it is detached (null: attached).</param>
    /// <returns>The reason it is unusable, or null.</returns>
    public static string? Rating(Guid id, Guid userId, ref IdentitySnapshot? identity, int? stars2, ref string? review, int editCount, string? origin, DateTime createdUtc, DateTime updatedUtc, string? detachReason)
    {
        if (id == Guid.Empty)
        {
            return "It has no id.";
        }

        if (userId == Guid.Empty)
        {
            return "It has no UserId.";
        }

        if (Identity(ref identity) is { } bad)
        {
            return bad;
        }

        if (createdUtc == default || updatedUtc == default)
        {
            return "It has no CreatedUtc or no UpdatedUtc.";
        }

        if (stars2 is < 1 or > 10)
        {
            return "Its stars aren't 0.5 to 5.";
        }

        review = ReviewText.Normalize(review);
        if (review is { Length: > SqliteRatingsStore.MaxReviewLength })
        {
            return string.Create(CultureInfo.InvariantCulture, $"Its review is longer than {SqliteRatingsStore.MaxReviewLength} characters.");
        }

        if (stars2 is null && review is null)
        {
            return "It has neither stars nor a review.";
        }

        if (editCount < 0)
        {
            return "Its EditCount is negative.";
        }

        if (!string.IsNullOrWhiteSpace(origin) && !RatingValues.Origin.All.Contains(origin))
        {
            return Unknown("Origin", origin);
        }

        return detachReason is not null && !RatingValues.DetachReason.All.Contains(detachReason) ? Unknown("DetachReason", detachReason) : null;
    }

    /// <summary>
    /// Checks a former member's row and cleans its name.
    /// </summary>
    /// <param name="row">The row; its name cleaned on the way out.</param>
    /// <returns>The reason it is unusable, or null.</returns>
    public static string? FormerUser(ref FormerUserRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Id == Guid.Empty || row.OriginalUserId == Guid.Empty)
        {
            return "It has no Id or no OriginalUserId.";
        }

        if (row.Id == row.OriginalUserId)
        {
            return "Its Id is its deleted user's id.";
        }

        if (row.DeletedUtc == default)
        {
            return "It has no DeletedUtc.";
        }

        var name = CleanName(row.DisplayName);
        if (name is null)
        {
            return "It has no DisplayName.";
        }

        if (name.Length > MaxNameLength)
        {
            return TooLong("DisplayName");
        }

        row = row with { DisplayName = name };
        return null;
    }

    /// <summary>Checks a tombstone.</summary>
    /// <param name="row">The row.</param>
    /// <returns>The reason it is unusable, or null.</returns>
    public static string? Tombstone(TombstoneRecord row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.RatingId == Guid.Empty)
        {
            return "It has no RatingId.";
        }

        if (row.DeletedUtc == default)
        {
            return "It has no DeletedUtc.";
        }

        return RatingValues.DeletedBy.All.Contains(row.DeletedBy ?? string.Empty) ? null : Unknown("DeletedBy", row.DeletedBy);
    }

    /// <summary>Checks a moderation log row.</summary>
    /// <param name="action">Its action.</param>
    /// <param name="atUtc">When.</param>
    /// <returns>The reason it is unusable, or null.</returns>
    public static string? LogRow(string? action, DateTime atUtc)
    {
        if (atUtc == default)
        {
            return "It has no AtUtc.";
        }

        return RatingValues.ModerationAction.All.Contains(action ?? string.Empty) ? null : Unknown("Action", action);
    }

    /// <summary>Checks a ledger row (the import ledger's entry, or one of its rating ids).</summary>
    /// <param name="source">Its source.</param>
    /// <param name="sourceKey">Its key.</param>
    /// <param name="ratingId">Its rating, for a rating id row.</param>
    /// <returns>The reason it is unusable, or null.</returns>
    public static string? Ledger(string? source, string? sourceKey, Guid? ratingId = null)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sourceKey))
        {
            return "It has no Source or no SourceKey.";
        }

        return ratingId == Guid.Empty ? "It has no RatingId." : null;
    }

    /// <summary>The reason for a row this version can't read at all.</summary>
    /// <param name="ex">Why, when the serializer said.</param>
    /// <returns>The reason.</returns>
    public static string Unreadable(Exception? ex)
        => ex is JsonException { Path: { Length: > 0 } path }
            ? string.Create(CultureInfo.InvariantCulture, $"A value can't be read ({Shown(path)}).")
            : "This version can't read it.";

    /// <summary>
    /// Cleans a name (a former member's, a value in a reason): control characters removed (a tab or a line end becomes a
    /// space), and since the 0.3.19 verification the bidi controls and U+FEFF too
    /// (<see cref="ReviewText.IsBidiControlOrBom"/>; the joiners U+200D and U+200C stay), trimmed; null when nothing is
    /// left. A review keeps its bidi controls (<see cref="ReviewText"/>).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The cleaned name, or null.</returns>
    public static string? CleanName(string? name) => Clean(name, keepFormat: false);

    /// <summary>
    /// Cleans an item's name or series name in a row's identity: control characters removed, as <see cref="CleanName"/>,
    /// but every format character kept, since the name is matched against the library's own names, which keep theirs (a
    /// title's right-to-left mark stripped here would never match).
    /// </summary>
    /// <param name="name">The name.</param>
    /// <returns>The cleaned name, or null.</returns>
    public static string? CleanTitle(string? name) => Clean(name, keepFormat: true);

    /// <summary>A value in a reason: control characters removed and cut short, so a crafted file can't fill the page.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The value as shown.</returns>
    public static string Shown(string? value)
    {
        var cleaned = CleanName(value) ?? string.Empty;
        return cleaned.Length <= MaxShownValue ? cleaned : string.Concat(cleaned.AsSpan(0, MaxShownValue), "…");
    }

    private static string? Identity(ref IdentitySnapshot? identity)
    {
        if (identity is null)
        {
            return "It has no Identity.";
        }

        if (identity.ItemId == Guid.Empty)
        {
            return "Its Identity has no ItemId.";
        }

        if (string.IsNullOrWhiteSpace(identity.Kind))
        {
            return "Its Identity has no Kind.";
        }

        var name = CleanTitle(identity.Name) ?? string.Empty;
        var series = CleanTitle(identity.SeriesName);
        if (name.Length > MaxNameLength || series is { Length: > MaxNameLength })
        {
            return TooLong("Identity's name");
        }

        if (!string.Equals(name, identity.Name, StringComparison.Ordinal) || !string.Equals(series, identity.SeriesName, StringComparison.Ordinal))
        {
            identity = identity with { Name = name, SeriesName = series };
        }

        return null;
    }

    private static string? Clean(string? name, bool keepFormat)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '\t' or '\n' or '\r')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(c) && (keepFormat || !ReviewText.IsBidiControlOrBom(c)))
            {
                builder.Append(c);
            }
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string Unknown(string column, string? value)
        => string.Create(CultureInfo.InvariantCulture, $"Its {column} '{Shown(value)}' isn't one this version knows.");

    private static string TooLong(string what)
        => string.Create(CultureInfo.InvariantCulture, $"Its {what} is longer than {MaxNameLength} characters.");
}
