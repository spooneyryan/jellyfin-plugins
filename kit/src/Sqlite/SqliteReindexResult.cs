using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// What a rebuild of a damaged database's indexes did at open: only indexes failed <c>integrity_check</c> (their
/// entries no longer matched their tables, which read intact), so they were rebuilt from the tables with <c>REINDEX</c>
/// instead of restoring a snapshot, which would lose every change since it. Shown in Diagnostics as <c>Reindexed</c>
/// until the plugin restarts.
/// </summary>
/// <param name="Utc">When it ran.</param>
/// <param name="Reason">The failed check, and what found it first.</param>
/// <param name="CopyFolder">The <c>quarantine-&lt;ts&gt;-reindex</c> folder's name the database, <c>-wal</c> and <c>-shm</c> were copied into, as found, before the rebuild.</param>
/// <param name="CopiedFiles">The names of the files copied there.</param>
internal sealed record SqliteReindexResult(DateTime Utc, string Reason, string CopyFolder, IReadOnlyList<string> CopiedFiles)
{
    /// <summary>
    /// Gets the result as Diagnostics shows it.
    /// </summary>
    /// <returns>The values by name.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Utc"] = Utc,
        ["Reason"] = Reason,
        ["CopyFolder"] = CopyFolder,
        ["CopiedFiles"] = CopiedFiles
    };
}
