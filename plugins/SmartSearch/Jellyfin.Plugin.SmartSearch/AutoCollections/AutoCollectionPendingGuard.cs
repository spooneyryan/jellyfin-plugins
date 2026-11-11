using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Removals held back by the mass-removal guard: exactly what a manager reviews and approves. A run whose removals
/// differ in any way makes a new hold (a later <see cref="DetectedUtc"/>), so an approval never covers more than was read.
/// </summary>
internal sealed record AutoCollectionPendingGuard
{
    /// <summary>The most held members kept by id: as many as can be pinned.</summary>
    public const int MaxHeldIds = AutoCollectionManualEdits.MaxEntries;

    /// <summary>Gets the hold's version: when these removals (this set) were first held. A Keep or an Apply names it.</summary>
    public DateTime DetectedUtc { get; init; }

    /// <summary>Gets how many removals are held: members plus dead links.</summary>
    public int WouldRemoveCount { get; init; }

    /// <summary>Gets the held members, the first <see cref="MaxHeldIds"/> of them.</summary>
    public IReadOnlyList<Guid> WouldRemove { get; init; } = [];

    /// <summary>Gets how many members are held (more than <see cref="WouldRemove"/> lists when over <see cref="MaxHeldIds"/>).</summary>
    public int HeldMembers { get; init; }

    /// <summary>Gets how many dead links are held.</summary>
    public int DeadLinks { get; init; }

    /// <summary>Gets the hash of the held set (<see cref="HashOf"/>); <c>null</c> on a hold an older build stored, which no approval covers.</summary>
    public string? Hash { get; init; }

    /// <summary>Gets why the guard held them.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Gets the missing library locations behind a <c>LocationMissing</c> reason (shown to administrators only).</summary>
    public IReadOnlyList<string>? Locations { get; init; }

    /// <summary>
    /// Gets when a manager approved these removals: the next run applies them, or fewer, once. Any run's end clears it.
    /// </summary>
    public DateTime? ApprovedUtc { get; init; }

    /// <summary>Gets fields this version doesn't know, kept on a round trip.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    /// <summary>Gets a value indicating whether every held member is listed in <see cref="WouldRemove"/>.</summary>
    [JsonIgnore]
    public bool Complete => (WouldRemove?.Count ?? 0) >= HeldMembers;

    /// <summary>Gets a value indicating whether the hold is of a run that would empty the collection.</summary>
    [JsonIgnore]
    public bool Empty => Reasons?.Contains(GuardReasons.Empty) == true;

    /// <summary>The hash of a held set: the members (in any order), the dead links, and whether it empties the collection.</summary>
    /// <param name="members">The members removed.</param>
    /// <param name="deadLinks">The dead links pruned.</param>
    /// <param name="empty">Whether the run empties the collection.</param>
    /// <returns>The hash.</returns>
    public static string HashOf(IEnumerable<Guid> members, int deadLinks, bool empty)
    {
        ArgumentNullException.ThrowIfNull(members);
        var sorted = members.Distinct().Order().ToList();

        // Two more ids carry the count and the flag, so they are hashed too.
        sorted.Add(new Guid(deadLinks, 0, 0, new byte[8]));
        sorted.Add(new Guid(empty ? 1 : 0, 1, 0, new byte[8]));
        return MembershipHash.Of(sorted);
    }

    /// <summary>A hold of a run's removals, keeping the version of the hold before when the set is the same.</summary>
    /// <param name="before">The hold before the run, or <c>null</c>.</param>
    /// <param name="members">The members the run would remove.</param>
    /// <param name="deadLinks">The dead links it would prune.</param>
    /// <param name="verdict">The guard's verdict.</param>
    /// <param name="now">The run's time.</param>
    /// <returns>The hold, not approved.</returns>
    public static AutoCollectionPendingGuard Of(AutoCollectionPendingGuard? before, IReadOnlyCollection<Guid> members, int deadLinks, GuardVerdict verdict, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(verdict);
        var distinct = members.Distinct().ToList();
        var hash = HashOf(distinct, deadLinks, verdict.Empty);
        var detected = before is { Hash: { } previous } && string.Equals(previous, hash, StringComparison.Ordinal) ? before.DetectedUtc
            : before is null || now > before.DetectedUtc ? now
            : before.DetectedUtc.AddTicks(1);
        return new AutoCollectionPendingGuard
        {
            DetectedUtc = detected,
            WouldRemoveCount = distinct.Count + deadLinks,
            WouldRemove = distinct.Take(MaxHeldIds).ToList(),
            HeldMembers = distinct.Count,
            DeadLinks = deadLinks,
            Hash = hash,
            Reasons = verdict.Reasons,
            Locations = verdict.Locations.Count > 0 ? verdict.Locations : null
        };
    }

    /// <summary>
    /// Tells whether this hold's approval covers a run's removals: approved, and the run removes these members or fewer
    /// and no more dead links; a run that would empty the collection only when the approved hold did too.
    /// </summary>
    /// <param name="members">The members the run removes.</param>
    /// <param name="deadLinks">The dead links it prunes.</param>
    /// <param name="empty">Whether it empties the collection.</param>
    /// <returns>Whether the removals may go.</returns>
    public bool Covers(IReadOnlyCollection<Guid> members, int deadLinks, bool empty)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (ApprovedUtc is null || Hash is null || (empty && !Empty) || deadLinks > DeadLinks)
        {
            return false;
        }

        if (Complete)
        {
            var approved = (WouldRemove ?? []).ToHashSet();
            return members.All(approved.Contains);
        }

        // Too many to list: only the very same set.
        return string.Equals(HashOf(members, deadLinks, empty), Hash, StringComparison.Ordinal);
    }
}
