using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.UserRatings.Import;

/// <summary>
/// A Jellyfin Enhanced export: the file and its counts.
/// </summary>
/// <param name="Bytes">The <c>reviews.json</c> text, UTF-8.</param>
/// <param name="Reviews">The entries written.</param>
/// <param name="Skipped">The ratings left out, by reason.</param>
/// <param name="MergedCopies">Ratings of a title's other copy that went into the same JE key (the newer written).</param>
/// <param name="OverJeLimit">Reviews written whose text is longer than JE saves (2,000 characters): JE shows them but
/// refuses to save an edit of one until it is cut (since 0.3.24).</param>
internal sealed record JellyfinEnhancedExport(byte[] Bytes, int Reviews, IReadOnlyDictionary<string, int> Skipped, int MergedCopies, int OverJeLimit = 0)
{
    /// <summary>Gets how many ratings were left out.</summary>
    public int SkippedTotal => Skipped.Values.Sum();
}
