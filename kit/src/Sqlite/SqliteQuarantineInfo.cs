using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Sqlite;

/// <summary>
/// A <c>quarantine-&lt;yyyyMMdd-HHmmss&gt;</c> folder next to a database: a damaged database moved aside together with
/// its <c>-wal</c> and <c>-shm</c>, kept until an admin deletes it and listed on the Diagnostics tab.
/// </summary>
/// <param name="Name">The folder's name.</param>
/// <param name="Utc">When it was made, from its name (UTC).</param>
/// <param name="Files">The names of the files in it.</param>
/// <param name="Size">Their size in bytes.</param>
internal sealed record SqliteQuarantineInfo(string Name, DateTime Utc, IReadOnlyList<string> Files, long Size)
{
    /// <summary>
    /// Describes the folder with plain values, for Diagnostics.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Name"] = Name,
        ["Utc"] = Utc,
        ["Files"] = Files,
        ["Size"] = Size
    };
}
