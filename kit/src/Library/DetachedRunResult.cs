using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kit.Library;

/// <summary>
/// What one step of <see cref="DetachedRecords"/> did for one owner.
/// </summary>
internal sealed record DetachedRunResult
{
    /// <summary>Gets the step: <c>Resolve</c>, <c>Archive</c>, <c>Reattach</c> or <c>Purge</c>, or <c>Archive</c>
    /// followed by an owner's reason (<c>ArchiveClipMissing</c>,
    /// <see cref="DetachedRecords.ArchiveForReasonAsync"/>).</summary>
    public required string Step { get; init; }

    /// <summary>Gets the owner's name.</summary>
    public required string Owner { get; init; }

    /// <summary>Gets why nothing was done (<c>ReadOnly</c>, <c>RetentionOff</c>, <c>Failed</c>), or null.</summary>
    public string? Skipped { get; init; }

    /// <summary>Gets how many records were looked at.</summary>
    public int Checked { get; init; }

    /// <summary>Gets how many records were relinked or put back from the archive.</summary>
    public int Relinked { get; init; }

    /// <summary>Gets how many were left detached as ambiguous (several Strong candidates, or one the policy refused).</summary>
    public int Ambiguous { get; init; }

    /// <summary>Gets how many records of users the server doesn't know were left alone (no view to resolve them in).</summary>
    public int OwnerUnknown { get; init; }

    /// <summary>Gets how many records moved to the archive.</summary>
    public int Archived { get; init; }

    /// <summary>Gets how many records were deleted for good (a purge).</summary>
    public int Purged { get; init; }

    /// <summary>Gets the snapshots a purge took first.</summary>
    public IReadOnlyList<string> Snapshots { get; init; } = [];

    /// <summary>Gets the error of a failed step, or null.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Describes the result with plain values.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Step"] = Step,
        ["Owner"] = Owner,
        ["Skipped"] = Skipped,
        ["Checked"] = Checked,
        ["Relinked"] = Relinked,
        ["Ambiguous"] = Ambiguous,
        ["OwnerUnknown"] = OwnerUnknown,
        ["Archived"] = Archived,
        ["Purged"] = Purged,
        ["Snapshots"] = Snapshots,
        ["Error"] = Error
    };
}
