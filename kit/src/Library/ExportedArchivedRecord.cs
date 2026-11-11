using System;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// An export's archived record (an entry of its <c>Archive</c>) split by <see cref="DetachedRecords.ReadExported"/>
/// into the record as its store has it and what the kit's export carries next to it: the owner's reason
/// (<see cref="ArchivedRecord.Reason"/>) and when that reason began (the archive line's detach time). An entry of a
/// detached record, or of an export made before, has neither.
/// </summary>
/// <param name="Record">The record in its store's shape, without the kit's properties.</param>
/// <param name="Reason">The owner's reason, or <c>null</c>.</param>
/// <param name="ReasonSinceUtc">When the reason began, or <c>null</c>.</param>
internal sealed record ExportedArchivedRecord(JsonObject Record, string? Reason, DateTime? ReasonSinceUtc)
{
    /// <summary>
    /// Gives the archived record an owner's import adapter built from <see cref="Record"/> what the export carried: its
    /// reason and, for a record with one, the time it began as its detach time. A record without a reason is returned as it is.
    /// </summary>
    /// <param name="built">The record the adapter built.</param>
    /// <returns>The record to import.</returns>
    public ArchivedRecord WithExportedFacts(ArchivedRecord built)
    {
        ArgumentNullException.ThrowIfNull(built);
        return Reason is null ? built : built with { Reason = Reason, DetachedUtc = ReasonSinceUtc ?? built.DetachedUtc };
    }
}
