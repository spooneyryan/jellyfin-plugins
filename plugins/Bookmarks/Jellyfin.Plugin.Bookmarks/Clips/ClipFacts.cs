using System;
using Jellyfin.Plugin.Bookmarks.Api.Models;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What a clip is right now, worked out on a request and never stored.
/// </summary>
/// <param name="State">What the page shows: ready, being made, failed, the file missing, the folder offline.</param>
/// <param name="FileExists">Whether the clip file is there.</param>
/// <param name="RootAvailable">Whether the clip file's clip folder is there and the path is one Bookmarks may use;
/// <c>null</c> without a file.</param>
/// <param name="Container"><c>mp4</c> or <c>mkv</c>, from the file's name; <c>null</c> without a file.</param>
/// <param name="DurationTicks">The clip's length: the file's range when known, else the most recent job's.</param>
/// <param name="IsOutdated">Whether the bookmark's range moved since the file was made ("Range changed"); never for a
/// tombstone.</param>
/// <param name="IsNameOutdated">Whether the file's name no longer says the bookmark's name, else its note ("Name
/// changed").</param>
/// <param name="LastJobFailed">Whether the most recent job failed or was cancelled while an earlier file is there.</param>
/// <param name="MadeAt">When the file was made: its job's completion time, else the file's last write time.</param>
/// <param name="Check">The guard's verdict on the clip path, through the cache; <c>null</c> without a file.</param>
internal sealed record ClipFacts(
    BookmarkClipState State,
    bool FileExists,
    bool? RootAvailable,
    string? Container,
    long DurationTicks,
    bool IsOutdated,
    bool IsNameOutdated,
    bool LastJobFailed,
    DateTime? MadeAt,
    ClipPathCheck? Check);
