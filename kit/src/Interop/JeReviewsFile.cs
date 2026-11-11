using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Interop;

/// <summary>
/// Jellyfin Enhanced's <c>reviews.json</c>, read-only.
/// </summary>
/// <param name="Format">What the file is: <see cref="JeDataFormat.Reviews1"/>, or why it can't be imported.</param>
/// <param name="Entries">The reviews, in file order.</param>
/// <param name="Invalid">The entries left out, and why.</param>
/// <param name="Problem">Why the file couldn't be used, or null.</param>
internal sealed record JeReviewsFile(JeDataFormat Format, IReadOnlyList<JeReview> Entries, IReadOnlyList<JeDataProblem> Invalid, string? Problem)
{
    /// <summary>The format name import reports give <see cref="JeDataFormat.Reviews1"/>.</summary>
    public const string FormatName = "je-reviews/1";

    /// <summary>The most field names <see cref="Fields"/> lists.</summary>
    public const int MaxFields = 50;

    /// <summary>
    /// Gets the field names the entries have, in the order first seen (names only, never values; at most
    /// <see cref="MaxFields"/>, each cut to 64 characters): User Ratings' <c>SourceFields</c>.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; } = [];
}
