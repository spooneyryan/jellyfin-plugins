using System;

namespace Jellyfin.Plugin.UserRatings.Api.Models;

/// <summary>
/// A data job: an export or an envelope import, dry run or not, run by the <c>AdminJobRunner</c> one
/// at a time.
/// </summary>
public sealed class UserRatingsJobDto
{
    /// <summary>Gets the job's id (32 hex digits).</summary>
    public required string Id { get; init; }

    /// <summary>Gets the kind: <c>Export</c> or <c>Import</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets a value indicating whether the job writes nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets the state: <c>Queued</c>, <c>Running</c>, <c>Succeeded</c>, <c>Failed</c> or <c>Cancelled</c>.</summary>
    public required string State { get; init; }

    /// <summary>Gets the phase: <c>Read</c>, <c>Resolve</c>, <c>Write</c>, <c>Archive</c> or <c>Done</c>.</summary>
    public string? Phase { get; init; }

    /// <summary>Gets a value indicating whether the job is in its write phase, where Cancel is refused until it commits.</summary>
    public bool Writing { get; init; }

    /// <summary>Gets the rows done in this phase.</summary>
    public int Processed { get; init; }

    /// <summary>Gets the rows of this phase.</summary>
    public int Total { get; init; }

    /// <summary>Gets when the job started.</summary>
    public DateTime StartedUtc { get; init; }

    /// <summary>Gets when it finished, or null.</summary>
    public DateTime? FinishedUtc { get; init; }

    /// <summary>Gets the kind's report once it succeeded.</summary>
    public object? Report { get; init; }

    /// <summary>Gets what went wrong, or null.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the error's stable code (<c>Changed</c>, <c>ReadOnly</c>, …), or null.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Gets the HTTP status the error would have had as a request's answer, or null.</summary>
    public int? ErrorStatus { get; init; }

    /// <summary>Gets a value indicating whether a finished export's file can be downloaded (<c>GET Admin/Jobs/{id}/File</c>).</summary>
    public bool HasFile { get; init; }

    /// <summary>Gets when the server deletes a finished export's file (an hour after it finished), or null without one.</summary>
    public DateTime? FileExpiresUtc { get; init; }
}
