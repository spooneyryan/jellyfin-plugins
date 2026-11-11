using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;

/// <summary>
/// The entries of one title key (<see cref="TitleBuckets"/>).
/// </summary>
/// <param name="Key">The title key.</param>
/// <param name="Entries">The entries.</param>
internal sealed record TitleBucket(string Key, IReadOnlyList<GroupedEntry> Entries);
