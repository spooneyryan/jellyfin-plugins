using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// One run of <see cref="UserReconciler.ReconcileAsync"/>.
/// </summary>
internal sealed record UserReconcileResult
{
    /// <summary>Gets when it ran.</summary>
    public required DateTime RanUtc { get; init; }

    /// <summary>Gets how many users the server has.</summary>
    public required int ServerUsers { get; init; }

    /// <summary>Gets how many user ids own records.</summary>
    public required int StoredUsers { get; init; }

    /// <summary>Gets how many records there are, over every owner that isn't read-only.</summary>
    public required int Records { get; init; }

    /// <summary>Gets the stored ids the server doesn't know.</summary>
    public required IReadOnlyList<OrphanUser> Orphans { get; init; }

    /// <summary>Gets the ids that became orphans in this run, or would have without the guard.</summary>
    public required IReadOnlyList<Guid> NewlyOrphaned { get; init; }

    /// <summary>
    /// Gets a value indicating whether the guard tripped: more than one id, or more than 10 % of the records, would have
    /// become orphans at once, so nothing was changed.
    /// </summary>
    public required bool GuardTripped { get; init; }

    /// <summary>Gets the owners left out because their store is read-only.</summary>
    public IReadOnlyList<string> SkippedOwners { get; init; } = [];

    /// <summary>
    /// Describes the run with plain values (for admins only).
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["RanUtc"] = RanUtc,
        ["ServerUsers"] = ServerUsers,
        ["StoredUsers"] = StoredUsers,
        ["Records"] = Records,
        ["OrphanUsers"] = Orphans.Select(o => o.ToDictionary()).ToList(),
        ["NewlyOrphaned"] = NewlyOrphaned.Select(KnownUsers.FormatId).ToList(),
        ["GuardTripped"] = GuardTripped,
        ["SkippedOwners"] = SkippedOwners
    };
}
