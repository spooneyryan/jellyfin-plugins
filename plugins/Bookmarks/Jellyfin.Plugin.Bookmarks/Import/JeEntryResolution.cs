using System;
using Jellyfin.Plugin.Bookmarks.Api.Models;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.Bookmarks.Import;

/// <summary>
/// Where one Jellyfin Enhanced bookmark goes, found before the import's write: the video of its
/// owner's libraries it matches, or what it is kept detached with.
/// </summary>
/// <param name="Outcome"><see cref="BookmarkJeImportOutcome.MatchedById"/>, <see cref="BookmarkJeImportOutcome.MatchedByProviderId"/>,
/// <see cref="BookmarkJeImportOutcome.Ambiguous"/> (attached to the first of several), <see cref="BookmarkJeImportOutcome.Detached"/>,
/// or <see cref="BookmarkJeImportOutcome.Skipped"/> for an entry that can't be imported (its item id isn't one).</param>
/// <param name="ItemId">The bookmark's item id: the matched video's, or JE's own for a detached one.</param>
/// <param name="Identity">The bookmark's identity snapshot: the matched video's, or what JE knew of it.</param>
/// <param name="ItemName">The display name: the matched video's, or the title JE stored.</param>
/// <param name="RunTimeTicks">The matched video's runtime, for the clamp; <c>null</c> when detached or unknown.</param>
/// <param name="Reason">Why, for the report.</param>
internal sealed record JeEntryResolution(
    BookmarkJeImportOutcome Outcome,
    Guid ItemId,
    IdentitySnapshot Identity,
    string ItemName,
    long? RunTimeTicks,
    string Reason)
{
    /// <summary>Gets a value indicating whether the bookmark is on a video of the library.</summary>
    public bool Attached => Outcome is BookmarkJeImportOutcome.MatchedById or BookmarkJeImportOutcome.MatchedByProviderId or BookmarkJeImportOutcome.Ambiguous;

    /// <summary>
    /// Gets an entry that can't be imported.
    /// </summary>
    /// <param name="reason">Why.</param>
    /// <returns>The resolution.</returns>
    public static JeEntryResolution Skip(string reason) => new(BookmarkJeImportOutcome.Skipped, Guid.Empty, new IdentitySnapshot(), string.Empty, null, reason);
}
