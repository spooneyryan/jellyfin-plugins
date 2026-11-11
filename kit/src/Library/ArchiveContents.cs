using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// An archive file as <see cref="DetachedArchive.Read"/> folds it: the records archived and not
/// put back, and where the daily reattach left off.
/// </summary>
internal sealed record ArchiveContents
{
    /// <summary>Gets the contents of an archive that doesn't exist.</summary>
    public static ArchiveContents Empty { get; } = new();

    /// <summary>Gets a value indicating whether the file exists.</summary>
    public bool Exists { get; init; }

    /// <summary>
    /// Gets the archived records in archive order: the last <c>Archived</c> line of each record id not followed by a
    /// <c>Reattached</c> or <c>Superseded</c> line naming it.
    /// </summary>
    public IReadOnlyList<ArchivedRecord> Records { get; init; } = [];

    /// <summary>
    /// Gets the ids of the archived lines that are no longer records: a record put back by the reattach or superseded,
    /// or archived again since. Such a line still holds the record as it was, so an owner that deletes a record it once
    /// archived purges them with <see cref="DetachedArchive.Purge"/>'s <c>includeHistory</c>.
    /// </summary>
    public IReadOnlySet<string> HistoryIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets the sequence number of the last record the daily reattach checked (0: none yet).</summary>
    public long LastCheckedSeq { get; init; }

    /// <summary>Gets the sequence number the next line gets.</summary>
    public long NextSeq { get; init; } = 1;

    /// <summary>Gets how many lines of another format version or an unknown operation were ignored.</summary>
    public int IgnoredLines { get; init; }

    /// <summary>Gets how many lines couldn't be read (not JSON, or missing their fields).</summary>
    public int InvalidLines { get; init; }

    /// <summary>Gets a value indicating whether the last line is incomplete (a write cut short), and was ignored.</summary>
    public bool TruncatedTail { get; init; }

    /// <summary>Gets why the file couldn't be read at all, or null.</summary>
    public string? Problem { get; init; }
}
