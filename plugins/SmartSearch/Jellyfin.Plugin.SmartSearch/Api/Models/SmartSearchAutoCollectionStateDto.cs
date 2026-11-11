using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>An auto-collection's state: queued, running, or how its last run went.</summary>
public sealed class SmartSearchAutoCollectionStateDto
{
    /// <summary>
    /// Gets or sets the status: <c>NotCreated</c>, <c>Queued</c> (a run was asked for; with the reason
    /// <c>WaitingForScan</c> while it waits for a library scan to end), <c>Running</c>, <c>Ok</c>, <c>Guarded</c>
    /// (removals held for a review), <c>TetherMissing</c> (the collection at its id isn't marked as this
    /// auto-collection's any more), <c>Orphaned</c> (the collection was deleted), <c>QueryMissing</c>,
    /// <c>QueryInvalid</c>, <c>EvaluationUserMissing</c>, <c>CreatorMissing</c>, <c>NeedsNewerVersion</c>, <c>Error</c>
    /// or <c>Disabled</c>.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the machine-readable reason of a status other than <c>Ok</c>.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets when it last ran.</summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>Gets or sets when it last ran successfully.</summary>
    public DateTime? LastSuccessUtc { get; set; }

    /// <summary>Gets or sets what started the last run: <c>PostScan</c>, <c>Events</c>, <c>Daily</c>, <c>Manual</c>, <c>Saved</c> or <c>Startup</c>.</summary>
    public string? LastReason { get; set; }

    /// <summary>Gets or sets its members after the last successful run.</summary>
    public int LastMemberCount { get; set; }

    /// <summary>Gets or sets the size of the last run's scope: the items of its query's kinds in its libraries.</summary>
    public int LastCandidateCount { get; set; }

    /// <summary>Gets or sets the members the last run added.</summary>
    public int LastAdded { get; set; }

    /// <summary>Gets or sets the members the last run removed.</summary>
    public int LastRemoved { get; set; }

    /// <summary>Gets or sets the dead or duplicate links the last run pruned.</summary>
    public int LastDeadLinks { get; set; }

    /// <summary>Gets or sets the dead links left in place because the writer can't remove them.</summary>
    public int UnprunableDeadLinks { get; set; }

    /// <summary>Gets or sets how long the last run took, in milliseconds.</summary>
    public long LastDurationMs { get; set; }

    /// <summary>Gets or sets the removals held back for a review, or <c>null</c>.</summary>
    public SmartSearchAutoCollectionPendingGuardDto? PendingGuard { get; set; }

    /// <summary>Gets or sets a value indicating whether some people who can see the collection can't open all of its items.</summary>
    public bool MixedAudience { get; set; }

    /// <summary>Gets or sets what went wrong, in plain English: the full message for an administrator, a short text without paths or query details for other managers.</summary>
    public string? LastError { get; set; }
}
