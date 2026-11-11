using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// A record that a rating was deliberately deleted, so imports never bring it back (<c>Tombstones</c>): ids and
/// times only, no stars and no text.
/// </summary>
/// <param name="RatingId">The deleted row's id.</param>
/// <param name="UserId">Its author.</param>
/// <param name="ItemId">Its item.</param>
/// <param name="DeletedUtc">When it was deleted (UTC).</param>
/// <param name="DeletedBy">Who deleted it (<see cref="RatingValues.DeletedBy"/>).</param>
internal sealed record TombstoneRecord(Guid RatingId, Guid UserId, Guid ItemId, DateTime DeletedUtc, string DeletedBy)
{
    /// <summary>
    /// Gets where the deletion was made (<see cref="RatingValues.DeletionOrigin"/>; schema 4). Not in an export: it says
    /// which server's job made it, so it means nothing on another one.
    /// </summary>
    [JsonIgnore]
    public string Origin { get; init; } = RatingValues.DeletionOrigin.Local;
}
