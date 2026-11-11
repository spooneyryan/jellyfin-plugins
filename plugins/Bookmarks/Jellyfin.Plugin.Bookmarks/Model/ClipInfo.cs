using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Bookmarks.Model;

/// <summary>
/// A bookmark's clip: the most recent extraction job, and the clip file currently on disk. The file can come
/// from an earlier job, since it is only replaced once a new clip has been written successfully.
/// </summary>
/// <remarks>
/// Internal since schema 2, with <see cref="Bookmark"/>. Schema 2 adds <see cref="MissingSinceUtc"/>,
/// <see cref="EstimatedBytes"/> and <see cref="Extra"/>; they are defined in 1.1.0 although only later releases
/// write the first, so neither 1.2.0 nor 1.3.0 needs a schema change.
/// </remarks>
internal sealed record ClipInfo
{
    /// <summary>
    /// Gets the status of the most recent job.
    /// </summary>
    public BookmarkClipStatus Status { get; init; }

    /// <summary>
    /// Gets the extraction mode of the most recent job.
    /// </summary>
    public BookmarkClipMode Mode { get; init; }

    /// <summary>
    /// Gets the start of the range the most recent job extracts, in ticks. Captured when the job was queued,
    /// so later edits to the bookmark don't change a job in progress.
    /// </summary>
    public long StartTicks { get; init; }

    /// <summary>
    /// Gets the end of the range the most recent job extracts, in ticks.
    /// </summary>
    public long EndTicks { get; init; }

    /// <summary>
    /// Gets when the most recent job was requested (UTC).
    /// </summary>
    public DateTime RequestedAt { get; init; }

    /// <summary>
    /// Gets when the most recent job finished (UTC), whatever the outcome.
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Gets the failure reason of the most recent job, if it failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the absolute path of the current clip file, if one has been written.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets the size of the current clip file in bytes.
    /// </summary>
    public long? FileSize { get; init; }

    /// <summary>
    /// Gets the start of the range in the current clip file, in ticks.
    /// </summary>
    public long? FileStartTicks { get; init; }

    /// <summary>
    /// Gets the end of the range in the current clip file, in ticks.
    /// </summary>
    public long? FileEndTicks { get; init; }

    /// <summary>
    /// Gets since when <see cref="FilePath"/> has been missing while its clip folder exists (UTC), set by the clip
    /// maintenance sweep and cleared when the file is back (from 1.2.0). Since schema 2.
    /// </summary>
    public DateTime? MissingSinceUtc { get; init; }

    /// <summary>
    /// Gets the free-space guard's estimate of the clip's size in bytes, written when the job is queued and recomputed when
    /// an interrupted job is queued again at startup; the guard subtracts it for every Queued or Running job.
    /// Since schema 2.
    /// </summary>
    public long? EstimatedBytes { get; init; }

    /// <summary>
    /// Gets the stored fields this version doesn't know, kept on a round trip. Since schema 2.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}
