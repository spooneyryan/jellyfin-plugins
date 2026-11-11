using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What one verify sweep did for one <see cref="IAttachedRecordOwner"/>.
/// </summary>
internal sealed record VerifySweepOwnerResult
{
    /// <summary>Gets the owner's name.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets why the owner was left alone (<c>ReadOnly</c>, <c>Failed</c>), or null when it was swept.</summary>
    public string? Skipped { get; init; }

    /// <summary>Gets how many distinct item ids were looked up.</summary>
    public int Checked { get; init; }

    /// <summary>Gets how many of them the library no longer has.</summary>
    public int Missing { get; init; }

    /// <summary>
    /// Gets how many missing ids were left attached as pending relinks: the <c>NewId</c> of a Committed move-journal line
    /// less than 7 days old that no scan has picked up yet.
    /// </summary>
    public int Pending { get; init; }

    /// <summary>Gets how many lookups threw; their records are never detached.</summary>
    public int LookupErrors { get; init; }

    /// <summary>Gets how many records were detached.</summary>
    public int Detached { get; init; }

    /// <summary>Gets the error of a failed owner, or null.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Describes the result with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Owner"] = Owner,
        ["Skipped"] = Skipped,
        ["Checked"] = Checked,
        ["Missing"] = Missing,
        ["Pending"] = Pending,
        ["LookupErrors"] = LookupErrors,
        ["Detached"] = Detached,
        ["Error"] = Error
    };
}
