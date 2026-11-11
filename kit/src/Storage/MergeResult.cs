using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// The records after <see cref="ImportMerge.Merge"/> or <see cref="ImportMerge.Replace"/>, and what changed.
/// </summary>
/// <typeparam name="T">The record type.</typeparam>
/// <param name="Records">The records to store.</param>
/// <param name="Counts">What changed.</param>
internal sealed record MergeResult<T>(IReadOnlyList<T> Records, ImportCounts Counts);
