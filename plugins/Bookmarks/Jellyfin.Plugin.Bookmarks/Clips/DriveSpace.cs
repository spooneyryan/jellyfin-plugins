using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// What <see cref="IDriveSpaceProbe"/> found out about the free space for a folder.
/// </summary>
internal sealed record DriveSpace
{
    /// <summary>
    /// Gets the outcome: <see cref="DriveSpaceStatus.Ok"/> with the numbers, <see cref="DriveSpaceStatus.Unavailable"/> (the
    /// folder's drive or share isn't there: clips are refused) or <see cref="DriveSpaceStatus.Unknown"/> (free space can't be
    /// read here: the guard is off for the folder).
    /// </summary>
    public required DriveSpaceStatus Status { get; init; }

    /// <summary>
    /// Gets the folder asked about, as a full path when it is one.
    /// </summary>
    public required string Folder { get; init; }

    /// <summary>
    /// Gets the folder that was probed: the nearest existing one at or above <see cref="Folder"/> (a per-user folder or a
    /// newly configured clip folder isn't there before its first clip), or <c>null</c> when there is none.
    /// </summary>
    public string? ProbedPath { get; init; }

    /// <summary>
    /// Gets the name of the volume <see cref="ProbedPath"/> is on, for messages: its mount point (<c>C:\</c>, a share's
    /// root, or the folder a volume is mounted into), or <c>null</c>.
    /// </summary>
    public string? Volume { get; init; }

    /// <summary>
    /// Gets the bytes the server may still write there (quotas applied), when <see cref="Status"/> is
    /// <see cref="DriveSpaceStatus.Ok"/>.
    /// </summary>
    public long? AvailableBytes { get; init; }

    /// <summary>
    /// Gets the volume's size as the server sees it, when <see cref="Status"/> is <see cref="DriveSpaceStatus.Ok"/>.
    /// </summary>
    public long? TotalBytes { get; init; }

    /// <summary>
    /// Gets the error code of the failed call (a Win32 error on Windows), or <c>null</c>.
    /// </summary>
    public int? Error { get; init; }

    /// <summary>
    /// Gets a value indicating whether the probe took too long and was given up (a share that doesn't answer): then
    /// <see cref="Status"/> is <see cref="DriveSpaceStatus.Unknown"/>.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Gets the volume's name for messages and the log: <see cref="Volume"/>, else the root of the probed or asked-about folder
    /// (<c>C:\</c>, <c>\\server\share\</c>). Never a folder below a root, so no per-user folder's name.
    /// </summary>
    public string VolumeName => Volume ?? Path.GetPathRoot(ProbedPath ?? Folder) ?? "the clip folder's drive";
}
