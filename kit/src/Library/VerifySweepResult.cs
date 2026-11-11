using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// One verify sweep of <see cref="AttachedRecordVerifier"/>.
/// </summary>
internal sealed record VerifySweepResult
{
    /// <summary>Gets why it ran; several requests joined into one sweep list every reason.</summary>
    public required IReadOnlyList<VerifySweepReason> Reasons { get; init; }

    /// <summary>Gets when it started.</summary>
    public required DateTime StartedUtc { get; init; }

    /// <summary>Gets how long it took.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets the per-owner results.</summary>
    public required IReadOnlyList<VerifySweepOwnerResult> Owners { get; init; }

    /// <summary>Gets the move journal's state during the sweep (pending relinks need it to be <c>Ok</c>).</summary>
    public MoveJournalStatus JournalStatus { get; init; }

    /// <summary>Gets how many records were detached in total.</summary>
    public int Detached => Owners.Sum(o => o.Detached);

    /// <summary>Gets how many distinct ids were checked in total.</summary>
    public int Checked => Owners.Sum(o => o.Checked);

    /// <summary>
    /// Describes the result with plain values, for Diagnostics.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Reasons"] = Reasons.Select(r => r.ToString()).ToList(),
        ["StartedUtc"] = StartedUtc,
        ["Milliseconds"] = (long)Duration.TotalMilliseconds,
        ["Checked"] = Checked,
        ["Detached"] = Detached,
        ["JournalStatus"] = JournalStatus.ToString(),
        ["Owners"] = Owners.Select(o => o.ToDictionary()).ToList()
    };
}
