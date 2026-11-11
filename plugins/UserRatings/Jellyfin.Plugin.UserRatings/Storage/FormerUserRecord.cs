using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.UserRatings.Storage;

/// <summary>
/// A former member's row (the <c>Anonymize</c> policy): the id the rows carry now, the deleted user's id, the name shown.
/// </summary>
/// <param name="Id">The id the rows carry.</param>
/// <param name="OriginalUserId">The deleted user's id.</param>
/// <param name="DisplayName">"Former member", "Former member 2", and so on.</param>
/// <param name="DeletedUtc">When the user was deleted.</param>
internal sealed record FormerUserRecord(Guid Id, Guid OriginalUserId, string DisplayName, DateTime DeletedUtc)
{
    /// <summary>
    /// Gets where the former member was made (<see cref="RatingValues.DeletionOrigin"/>; schema 4): the deleted-user policy
    /// here, or an import that brought them. Not in an export.
    /// </summary>
    [JsonIgnore]
    public string Origin { get; init; } = RatingValues.DeletionOrigin.Local;
}
