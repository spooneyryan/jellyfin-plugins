namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// What one build cost: its queries and the memory it allocated.
/// </summary>
/// <param name="LibraryQueries">The <c>GetItemIds</c> calls, one per library folder set.</param>
/// <param name="ItemQueries">The <c>GetItemList</c> calls, one per chunk of 500 ids.</param>
/// <param name="AllocatedBytes">The bytes the build's thread allocated (the queries' deserialisation included).</param>
/// <param name="WorkingSetBytes">The server's working set when the build ended.</param>
/// <param name="PeakWorkingSetBytes">The server's peak working set so far.</param>
internal sealed record BuildMetrics(int LibraryQueries, int ItemQueries, long AllocatedBytes, long WorkingSetBytes, long PeakWorkingSetBytes);
