using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.Storage;
using Jellyfin.Plugin.UserRatings.Domain;
using Jellyfin.Plugin.UserRatings.Storage;

namespace Jellyfin.Plugin.UserRatings.Lifecycle;

/// <summary>
/// A detached rating in the kit's archive (the detached ratings' retention): the row in the archive's
/// <c>Record</c> (the kit's JSON shape, PascalCase, nulls left out), with its item's identity in the archive line's own
/// <c>Identity</c>, so the export carries it whole and the reattach puts back the row as it was.
/// </summary>
internal sealed class ArchivedRating
{
    /// <summary>Gets or sets the rating's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets its member, also a former one's (the archive line's <c>UserId</c> is null for a former member).</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the stars ×2 (1-10), or null for a review without stars.</summary>
    public int? Stars2 { get; set; }

    /// <summary>Gets or sets the review text.</summary>
    public string? ReviewText { get; set; }

    /// <summary>Gets or sets a value indicating whether the review is a spoiler.</summary>
    public bool IsSpoiler { get; set; }

    /// <summary>Gets or sets when it was created.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets when it was last written.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets when the review was last written.</summary>
    public DateTime? ReviewUpdatedUtc { get; set; }

    /// <summary>Gets or sets how often it was edited.</summary>
    public int EditCount { get; set; }

    /// <summary>Gets or sets where it came from.</summary>
    public string? Origin { get; set; }

    /// <summary>Gets or sets when it was detached.</summary>
    public DateTime DetachedUtc { get; set; }

    /// <summary>Gets or sets why.</summary>
    public string? DetachReason { get; set; }

    /// <summary>Gets or sets the value mirrored to Jellyfin's own rating, if any.</summary>
    public double? MirroredValue { get; set; }

    /// <summary>Gets or sets when it was last attached (null: since it was created).</summary>
    public DateTime? AttachedUtc { get; set; }

    /// <summary>
    /// The archive's record of a detached rating.
    /// </summary>
    /// <param name="row">The rating; detached.</param>
    /// <param name="userKnown">Whether its member is still a user (the line's <c>UserId</c>, which the kit resolves in).</param>
    /// <returns>The record.</returns>
    public static ArchivedRecord ToArchive(RatingRecord row, bool userKnown)
    {
        ArgumentNullException.ThrowIfNull(row);
        var detachedUtc = row.DetachedUtc ?? throw new ArgumentException("Only a detached rating is archived.", nameof(row));
        var record = new ArchivedRating
        {
            Id = row.Id,
            UserId = row.UserId,
            Stars2 = row.Stars2,
            ReviewText = row.ReviewText,
            IsSpoiler = row.IsSpoiler,
            CreatedUtc = row.CreatedUtc,
            UpdatedUtc = row.UpdatedUtc,
            ReviewUpdatedUtc = row.ReviewUpdatedUtc,
            EditCount = row.EditCount,
            Origin = row.Origin,
            DetachedUtc = detachedUtc,
            DetachReason = row.DetachReason,
            MirroredValue = row.MirroredValue,
            AttachedUtc = row.AttachedUtc
        };

        return new ArchivedRecord
        {
            Id = row.Id.ToString("D", CultureInfo.InvariantCulture),
            UserId = userKnown ? row.UserId : null,
            Identity = row.Identity,
            AttachedUtc = row.AttachedSinceUtc,
            DetachedUtc = detachedUtc,
            Record = JsonSerializer.SerializeToNode(record, JsonStoreFormat.Options)!.AsObject()
        };
    }

    /// <summary>
    /// The rating an archived record holds, detached as it was archived, or null when the record isn't one of ours (another
    /// id than the line's, no member, or no time it was written).
    /// </summary>
    /// <param name="archived">The archived record.</param>
    /// <returns>The rating, or null.</returns>
    public static RatingRecord? FromArchive(ArchivedRecord archived)
    {
        ArgumentNullException.ThrowIfNull(archived);
        ArchivedRating? record;
        try
        {
            record = archived.Record.Deserialize<ArchivedRating>(JsonStoreFormat.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (record is null || record.UserId == Guid.Empty || record.CreatedUtc == default || record.UpdatedUtc == default
            || !string.Equals(record.Id.ToString("D", CultureInfo.InvariantCulture), archived.Id, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new RatingRecord
        {
            Id = record.Id,
            UserId = record.UserId,
            Identity = archived.Identity,
            Stars2 = record.Stars2,
            ReviewText = record.ReviewText,
            IsSpoiler = record.IsSpoiler,
            CreatedUtc = Utc(record.CreatedUtc),
            UpdatedUtc = Utc(record.UpdatedUtc),
            ReviewUpdatedUtc = record.ReviewUpdatedUtc is { } review ? Utc(review) : null,
            EditCount = record.EditCount,
            Origin = record.Origin ?? RatingValues.Origin.User,
            DetachedUtc = Utc(record.DetachedUtc == default ? archived.DetachedUtc : record.DetachedUtc),
            DetachReason = record.DetachReason ?? RatingValues.DetachReason.Missing,
            MirroredValue = record.MirroredValue,
            AttachedUtc = record.AttachedUtc is { } attached ? Utc(attached) : null
        };
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
}
