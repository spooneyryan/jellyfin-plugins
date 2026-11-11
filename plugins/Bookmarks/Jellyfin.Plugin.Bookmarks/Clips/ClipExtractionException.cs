using System;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A clip couldn't be extracted. The message is written for end users.
/// </summary>
/// <remarks>
/// Internal since 1.1.0: it never leaves the plugin. The planner throws it, the manager turns it into a
/// <see cref="Services.BookmarkException"/> and the clip service records its message on the job.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1064:Exceptions should be public",
    Justification = "Thrown and caught inside the plugin only; public types are the plugin's public surface.")]
internal sealed class ClipExtractionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ClipExtractionException"/> class.
    /// </summary>
    public ClipExtractionException()
        : base("The clip couldn't be extracted.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipExtractionException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public ClipExtractionException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipExtractionException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public ClipExtractionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets ffmpeg's exit code, when ffmpeg ran and failed (<see cref="FfmpegRunner"/>): <c>-28</c> is <c>AVERROR(ENOSPC)</c>,
    /// a full disk (measured on Windows), which <see cref="ClipStorageGuard.DescribeFailureAsync"/> maps.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Gets a value indicating whether the free-space guard refused the job when it was about to start: its
    /// message is the job's error as it is, never mapped.
    /// </summary>
    public bool FromStorageGuard { get; init; }

    /// <summary>
    /// Gets a value indicating whether the clip folder couldn't be reached while the clip file's name was chosen (since
    /// 1.2.0: a drive that isn't ready, a share that doesn't answer): a request answers 503, not 400, and a job fails with
    /// the message as it is.
    /// </summary>
    public bool FolderUnreachable { get; init; }
}
