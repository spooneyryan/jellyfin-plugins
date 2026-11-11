using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>One definition's run state.</summary>
internal sealed record AutoCollectionState
{
    /// <summary>The most history entries kept.</summary>
    public const int MaxHistory = 20;

    /// <summary>The most removed ids kept, for the most recent written change only.</summary>
    public const int MaxRemovedIds = 500;

    /// <summary>The most edits kept for the next run: more stop the runs (<see cref="PendingEditsOverflow"/>) until a manager decides.</summary>
    public const int MaxPendingEdits = 5000;

    /// <summary>Gets the status.</summary>
    public StoredEnum<SmartSearchAutoCollectionStatus> Status { get; init; } = SmartSearchAutoCollectionStatus.NotCreated;

    /// <summary>Gets the machine-readable reason of <see cref="SmartSearchAutoCollectionStatus.QueryInvalid"/> and <see cref="SmartSearchAutoCollectionStatus.Error"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets when it last ran.</summary>
    public DateTime? LastRunUtc { get; init; }

    /// <summary>Gets when it last ended <see cref="SmartSearchAutoCollectionStatus.Ok"/>.</summary>
    public DateTime? LastSuccessUtc { get; init; }

    /// <summary>Gets what started the last run.</summary>
    public StoredEnum<SmartSearchAutoCollectionRunReason>? LastReason { get; init; }

    /// <summary>Gets the canonical hash of the query of the last run.</summary>
    public string? LastQueryHash { get; init; }

    /// <summary>Gets the SHA-256 of the ordered members the last run wanted (history only; never skips a run).</summary>
    public string? LastResultHash { get; init; }

    /// <summary>Gets the SHA-256 of the links the last run found (history only).</summary>
    public string? LastLinkHash { get; init; }

    /// <summary>Gets the number of members after the last run.</summary>
    public int LastMemberCount { get; init; }

    /// <summary>Gets the size of the last run's scope (the items of its query's kinds in its libraries); a held run keeps the last unheld run's.</summary>
    public int LastCandidateCount { get; init; }

    /// <summary>Gets the members the last change added.</summary>
    public int LastAdded { get; init; }

    /// <summary>Gets the members the last change removed.</summary>
    public int LastRemoved { get; init; }

    /// <summary>Gets the dead links the last change pruned.</summary>
    public int LastDeadLinks { get; init; }

    /// <summary>Gets how long the last run took.</summary>
    public long LastDurationMs { get; init; }

    /// <summary>Gets what stopped the last run, as every manager may read it (no paths, no query details), or <c>null</c>.</summary>
    public string? LastError { get; init; }

    /// <summary>Gets the full message of what stopped the last run (shown to administrators only), or <c>null</c>.</summary>
    public string? LastErrorDetail { get; init; }

    /// <summary>Gets the removals held back for review, or <c>null</c>.</summary>
    public AutoCollectionPendingGuard? PendingGuard { get; init; }

    /// <summary>Gets a value indicating whether some people who can see the collection can't open all of its members.</summary>
    public bool MixedAudience { get; init; }

    /// <summary>
    /// Gets a value indicating whether a create stopped before it made the collection (the engine was busy, timed out or
    /// found the query too broad): the next run of any kind makes it. A definition that never asked for one (an import)
    /// waits for a create.
    /// </summary>
    public bool CreatePending { get; init; }

    /// <summary>
    /// Gets the edits made to the collection outside the plugin since the last run that sorted them (oldest first): the
    /// next run makes them pins and excludes, or reverts them (Overwrite mode, the server's automatic adds).
    /// </summary>
    public IReadOnlyList<AutoCollectionManualEdit> PendingEdits { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether more edits were made outside the plugin than <see cref="MaxPendingEdits"/>: those
    /// beyond weren't kept, so the runs stop (nothing is reverted) until a manager saves the definition or a run in
    /// Overwrite mode writes the query's members.
    /// </summary>
    public bool PendingEditsOverflow { get; init; }

    /// <summary>Gets the dead links a writer that can't remove them left in place.</summary>
    public int UnprunableDeadLinks { get; init; }

    /// <summary>Gets the last runs, newest first, at most <see cref="MaxHistory"/>.</summary>
    public IReadOnlyList<AutoCollectionHistoryEntry> History { get; init; } = [];

    /// <summary>
    /// Gets the removals of the preview a manager accepted when saving the definition, until the next run: that run is
    /// not held for a large removal of about that size.
    /// </summary>
    public int? AcceptedRemovals { get; init; }

    /// <summary>
    /// Gets the hash of the members of the preview a manager accepted (<see cref="MembershipHash"/>): the relaxation of
    /// <see cref="AcceptedRemovals"/> applies only to a run that finds the same members.
    /// </summary>
    public string? AcceptedResultHash { get; init; }

    /// <summary>
    /// Gets what was decided about the default poster, and for which collection (<see cref="AutoCollectionDefaultImage"/>),
    /// or <c>null</c> before a run reached a collection with the setting on.
    /// </summary>
    public AutoCollectionDefaultImageRecord? DefaultImage { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
