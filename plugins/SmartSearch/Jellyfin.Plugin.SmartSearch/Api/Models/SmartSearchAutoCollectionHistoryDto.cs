using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>One run of an auto-collection, as its history keeps it (the last 20, newest first).</summary>
public sealed class SmartSearchAutoCollectionHistoryDto
{
    /// <summary>Gets or sets when it ran.</summary>
    public DateTime Utc { get; set; }

    /// <summary>Gets or sets why it ran: <c>PostScan</c>, <c>Events</c>, <c>Daily</c>, <c>Manual</c>, <c>Saved</c>, <c>Startup</c> or <c>WatchState</c>.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Gets or sets how many members it added.</summary>
    public int Added { get; set; }

    /// <summary>Gets or sets how many members it removed.</summary>
    public int Removed { get; set; }

    /// <summary>Gets or sets how many links to deleted files it dropped.</summary>
    public int Dead { get; set; }

    /// <summary>Gets or sets how many edits made outside the plugin it kept as pinned items.</summary>
    public int Pinned { get; set; }

    /// <summary>Gets or sets how many edits made outside the plugin it kept as excluded items.</summary>
    public int Excluded { get; set; }

    /// <summary>Gets or sets how many edits made outside the plugin it put back.</summary>
    public int Reverted { get; set; }

    /// <summary>Gets or sets how it ended: the status it left, such as <c>Ok</c> or <c>Guarded</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Gets or sets how long it took, in milliseconds.</summary>
    public long DurationMs { get; set; }

    /// <summary>Gets or sets the removed members the caller can open (at most 500 are kept).</summary>
    public IReadOnlyList<Guid> RemovedIds { get; set; } = [];

    /// <summary>Gets or sets how many removed members are left out of <see cref="RemovedIds"/> because the caller can't open them.</summary>
    public int HiddenCount { get; set; }
}
