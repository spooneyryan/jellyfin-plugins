using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A playlist export worked out (since 1.5.0 / 2.3.0, <see cref="PlaylistExportPlanner"/>): the videos joined and those left
/// out, the joined file's format, the estimates, and where it goes.
/// </summary>
internal sealed record PlaylistExportPlan
{
    /// <summary>Gets the playlist's id.</summary>
    public required Guid PlaylistId { get; init; }

    /// <summary>Gets the playlist's name.</summary>
    public required string PlaylistName { get; init; }

    /// <summary>Gets the user the export is for.</summary>
    public required Guid UserId { get; init; }

    /// <summary>Gets the videos joined, in playlist order.</summary>
    public required IReadOnlyList<PlaylistExportItem> Items { get; init; }

    /// <summary>Gets the entries left out, in playlist order.</summary>
    public required IReadOnlyList<PlaylistExportSkip> Skipped { get; init; }

    /// <summary>Gets the joined file's frame and frame rate.</summary>
    public required PlaylistExportFormat Format { get; init; }

    /// <summary>Gets the clip encoding settings it is made with.</summary>
    public required ClipEncodingOptions Encoding { get; init; }

    /// <summary>Gets the joined file's length: the sum of the videos'.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets the joined file's estimated size (<see cref="PlaylistExportEstimator"/>).</summary>
    public required long EstimatedBytes { get; init; }

    /// <summary>Gets the estimated time to make it.</summary>
    public required TimeSpan EstimatedTime { get; init; }

    /// <summary>Gets Jellyfin's ffmpeg.</summary>
    public required string EncoderPath { get; init; }

    /// <summary>Gets the clip folder, as <c>ClipRoots</c> records it.</summary>
    public required string Root { get; init; }

    /// <summary>Gets the folder the export goes to: the user's folder in the clip folder, as their clips' (or the clip folder).</summary>
    public required string OutputFolder { get; init; }

    /// <summary>Gets the export's path as it would be now: made unique again when it is written.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Gets the poster image to copy beside it (the playlist's own, else its first video's), or <c>null</c>.</summary>
    public string? PosterPath { get; init; }

    /// <summary>Gets why it can't be made (no videos, too many), or <c>null</c> when it can.</summary>
    public string? Problem { get; init; }

    /// <summary>Gets a value indicating whether it is very large (<see cref="PlaylistExportEstimator.IsLarge"/>).</summary>
    public bool IsLarge => PlaylistExportEstimator.IsLarge(Duration, EstimatedBytes);
}
