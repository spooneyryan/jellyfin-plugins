using System;
using Jellyfin.Plugin.Kit.Api;

namespace Jellyfin.Plugin.UserRatings.Services;

/// <summary>
/// What a review write expects to find (<c>PUT Mine/Review</c>), at most one of: the whole row as
/// last read (<see cref="IfUpdatedUtc"/>), or the review itself: its own
/// time (<see cref="IfReviewUpdatedUtc"/>) or none yet (<see cref="IfNoReview"/>). The review preconditions ignore the
/// stars, so a rating saved elsewhere (or by the same page) never makes a review write a 409, and a first review written in
/// two windows is a 409 in the second instead of a silent overwrite. All three are optional: none overwrites whatever is
/// there, as before.
/// </summary>
/// <param name="IfUpdatedUtc">The row's <c>UpdatedUtc</c> as the client last read it.</param>
/// <param name="IfReviewUpdatedUtc">The review's <c>ReviewUpdatedUtc</c> as the client last read it.</param>
/// <param name="IfNoReview">Whether the client expects the member to have no review of the item yet.</param>
internal readonly record struct ReviewPrecondition(DateTime? IfUpdatedUtc, DateTime? IfReviewUpdatedUtc, bool IfNoReview)
{
    /// <summary>The 400 of a write that sends the row's precondition and a review precondition.</summary>
    public const string RowAndReviewMessage = "Send IfUpdatedUtc or a review precondition (IfReviewUpdatedUtc, IfNoReview), not both.";

    /// <summary>The 400 of a write that sends both review preconditions.</summary>
    public const string BothReviewMessage = "Send IfReviewUpdatedUtc or IfNoReview, not both.";

    /// <summary>Gets no precondition: the review replaces whatever is there.</summary>
    public static ReviewPrecondition None => default;

    /// <summary>
    /// Throws the 400 of preconditions that can't both hold.
    /// </summary>
    /// <exception cref="PluginException">400: the row's precondition with a review one, or both review ones.</exception>
    public void Validate()
    {
        var review = IfReviewUpdatedUtc is not null || IfNoReview;
        if (IfUpdatedUtc is not null && review)
        {
            throw PluginException.Invalid(RowAndReviewMessage);
        }

        if (IfReviewUpdatedUtc is not null && IfNoReview)
        {
            throw PluginException.Invalid(BothReviewMessage);
        }
    }
}
