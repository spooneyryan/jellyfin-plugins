using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Storage;

/// <summary>
/// What an import did, or would do in a dry run, to one kind of record.
/// </summary>
internal sealed record ImportCounts
{
    /// <summary>Gets how many records the export holds for users found on this server.</summary>
    public int Incoming { get; init; }

    /// <summary>Gets how many were added.</summary>
    public int Added { get; init; }

    /// <summary>Gets how many stored records were replaced by a newer copy (Merge) or a different one (Replace).</summary>
    public int Updated { get; init; }

    /// <summary>Gets how many were the same as the stored copy.</summary>
    public int Unchanged { get; init; }

    /// <summary>Gets how many stored records were kept because they were changed more recently than the export's copy (Merge).</summary>
    public int KeptNewer { get; init; }

    /// <summary>Gets how many stored records the export doesn't have were removed (Replace).</summary>
    public int Removed { get; init; }

    /// <summary>Gets how many of the export's records belong to users not found on this server, and were skipped.</summary>
    public int SkippedUnknownUser { get; init; }

    /// <summary>Gets how many were skipped for another reason the plugin reports in its notes.</summary>
    public int Skipped { get; init; }

    /// <summary>
    /// Describes the counts with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Incoming"] = Incoming,
        ["Added"] = Added,
        ["Updated"] = Updated,
        ["Unchanged"] = Unchanged,
        ["KeptNewer"] = KeptNewer,
        ["Removed"] = Removed,
        ["SkippedUnknownUser"] = SkippedUnknownUser,
        ["Skipped"] = Skipped
    };
}
