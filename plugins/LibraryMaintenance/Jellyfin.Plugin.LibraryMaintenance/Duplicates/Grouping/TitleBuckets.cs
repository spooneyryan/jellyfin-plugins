using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

/// <summary>
/// The caller's entries by title key (<see cref="DuplicateGrouper.Bucket"/>): the buckets with two or more entries, which
/// both modes group, and the counts of what was bucketed.
/// </summary>
/// <param name="Candidates">The buckets with two or more entries.</param>
/// <param name="Entries">The entries in scope with a title.</param>
/// <param name="Movies">Their members in scope.</param>
/// <param name="NoTitle">The entries in scope whose name gives an empty key.</param>
internal sealed record TitleBuckets(IReadOnlyList<TitleBucket> Candidates, int Entries, int Movies, int NoTitle);
