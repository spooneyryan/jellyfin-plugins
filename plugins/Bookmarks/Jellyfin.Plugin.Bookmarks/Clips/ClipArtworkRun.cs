using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What one pass of <see cref="ClipArtwork"/> did, for the log and Diagnostics <c>Maintenance</c>.
/// </summary>
internal sealed class ClipArtworkRun
{
    /// <summary>Gets or sets how many artwork files were written.</summary>
    public int Written { get; set; }

    /// <summary>Gets or sets how many clips had artwork written.</summary>
    public int ClipsWritten { get; set; }

    /// <summary>Gets or sets how many artwork files were up to date already.</summary>
    public int UpToDate { get; set; }

    /// <summary>Gets or sets how many clips couldn't be given their artwork (a Warning each).</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets how many clips' first frames were taken (since 1.7.0 / 2.5.0).</summary>
    public int Frames { get; set; }

    /// <summary>Gets or sets how many clips' first frames couldn't be taken (a Warning each; their video's images are
    /// used).</summary>
    public int FramesFailed { get; set; }

    /// <summary>Gets or sets how many clips that are library items had an image refresh queued.</summary>
    public int Refreshed { get; set; }

    /// <summary>Gets or sets how many artwork files were moved to a renamed clip's name.</summary>
    public int Moved { get; set; }

    /// <summary>Gets or sets how many orphaned artwork files (or stale temporary ones) were deleted.</summary>
    public int Deleted { get; set; }

    /// <summary>Gets or sets how many orphaned artwork files couldn't be deleted.</summary>
    public int NotDeleted { get; set; }

    /// <summary>Gets or sets how many clip folders were left out of the clean-up: not there, not recognised, or not
    /// listed.</summary>
    public int FoldersLeftOut { get; set; }

    /// <summary>Gets or sets why the writing part was skipped, if it was.</summary>
    public string? Skipped { get; set; }

    /// <summary>
    /// Gets what was done, for Diagnostics <c>Maintenance</c>.
    /// </summary>
    /// <returns>The counts.</returns>
    public IDictionary<string, object?> ToDictionary() => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        ["Written"] = Written,
        ["Clips"] = ClipsWritten,
        ["UpToDate"] = UpToDate,
        ["Failed"] = Failed,
        ["Frames"] = Frames,
        ["FramesFailed"] = FramesFailed,
        ["Refreshed"] = Refreshed,
        ["Deleted"] = Deleted,
        ["NotDeleted"] = NotDeleted,
        ["FoldersLeftOut"] = FoldersLeftOut,
        ["Skipped"] = Skipped
    };
}
