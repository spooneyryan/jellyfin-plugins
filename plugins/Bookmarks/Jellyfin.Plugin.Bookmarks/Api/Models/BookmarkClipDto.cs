using System;
using Jellyfin.Plugin.Bookmarks.Model;

namespace Jellyfin.Plugin.Bookmarks.Api.Models;

/// <summary>
/// The state of a bookmark's clip.
/// </summary>
/// <remarks>
/// <para><c>ClipDto</c> in Bookmarks 1.0, renamed in 1.1.0 for the schema prefix. Its JSON is unchanged;
/// only its OpenAPI schema id changed.</para>
/// <para>Since 1.2 it also carries what the My Clips page works out about the clip and its file
/// (<see cref="State"/> to <see cref="MadeAt"/>). Only the clip list fills them (<c>GET /Bookmarks/Clips</c>, the clip of each
/// <see cref="BookmarkClipItemDto"/>); every bookmark route leaves them out (<c>null</c>), so a bookmark's JSON is 1.1's.</para>
/// </remarks>
public sealed class BookmarkClipDto
{
    /// <summary>
    /// Gets or sets the status of the most recent job.
    /// </summary>
    public BookmarkClipStatus Status { get; set; }

    /// <summary>
    /// Gets or sets the extraction mode.
    /// </summary>
    public BookmarkClipMode Mode { get; set; }

    /// <summary>
    /// Gets or sets the start of the extracted range, in ticks.
    /// </summary>
    public long StartTicks { get; set; }

    /// <summary>
    /// Gets or sets the end of the extracted range, in ticks.
    /// </summary>
    public long EndTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bookmark's range has changed since the clip file was made ("Range
    /// changed"); always <c>false</c> for the clip of a deleted bookmark.
    /// </summary>
    public bool IsOutdated { get; set; }

    /// <summary>
    /// Gets or sets the progress of a running job, from 0 to 100.
    /// </summary>
    public double? Progress { get; set; }

    /// <summary>
    /// Gets or sets the name of the current clip file, which may come from an earlier job.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the size of the current clip file in bytes.
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// Gets or sets when the clip was requested (UTC).
    /// </summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// Gets or sets when the job finished (UTC).
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the reason a job failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets what the clip is right now: ready, being made, failed, its file missing, or its folder offline.
    /// Only the clip list fills it (1.2).
    /// </summary>
    public BookmarkClipState? State { get; set; }

    /// <summary>
    /// Gets or sets the clip file's container, <c>mp4</c> (Accurate) or <c>mkv</c> (Fast), from its name; <c>null</c>
    /// without a file. Only the clip list fills it (1.2).
    /// </summary>
    public string? Container { get; set; }

    /// <summary>
    /// Gets or sets the clip's length in ticks: the clip file's range when it is known, else the range of the most recent job.
    /// Only the clip list fills it (1.2).
    /// </summary>
    public long? DurationTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the clip file is there. Only the clip list fills it (1.2).
    /// </summary>
    public bool? FileExists { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the clip file's name no longer says the bookmark's name, else its note, as a
    /// clip made now would ("Name changed"; the bookmark was renamed after the clip was made). Only the clip list fills it
    /// (1.2).
    /// </summary>
    public bool? IsNameOutdated { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the most recent job failed or was cancelled while an earlier clip file is
    /// still there ("Last remake failed"). Only the clip list fills it (1.2).
    /// </summary>
    public bool? LastJobFailed { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the clip file lies in a folder of a Jellyfin library, where everyone who can
    /// open that library sees it. Only the clip list fills it (1.2).
    /// </summary>
    public bool? InLibrary { get; set; }

    /// <summary>
    /// Gets or sets the name of that library, only when the caller can open it; <c>null</c> otherwise.
    /// Only the clip list fills it (1.2).
    /// </summary>
    public string? LibraryName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the clip file's folder can be reached (<c>false</c> for a clip folder offline);
    /// <c>null</c> without a file. Only the clip list fills it (1.2).
    /// </summary>
    public bool? RootAvailable { get; set; }

    /// <summary>
    /// Gets or sets when the clip file was made (UTC): its job's completion time, else the file's last write time (a clip
    /// kept while a remake was asked for, an earlier file next to a remake that failed); <c>null</c> without a
    /// file, or when its folder can't be reached. Only the clip list fills it (1.2).
    /// </summary>
    public DateTime? MadeAt { get; set; }
}
