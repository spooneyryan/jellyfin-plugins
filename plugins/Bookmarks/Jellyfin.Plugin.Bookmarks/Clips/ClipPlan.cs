using System;
using System.IO;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// A resolved clip job: the ffmpeg command and where its output ends up.
/// </summary>
internal sealed record ClipPlan
{
    /// <summary>
    /// Gets the ffmpeg executable to run.
    /// </summary>
    public required string EncoderPath { get; init; }

    /// <summary>
    /// Gets the command, which writes to <see cref="TempPath"/>.
    /// </summary>
    public required ClipCommand Command { get; init; }

    /// <summary>
    /// Gets the final clip path; the temporary file is moved here on success.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// Gets the path ffmpeg writes to while the clip is being produced.
    /// </summary>
    public required string TempPath { get; init; }

    /// <summary>
    /// Gets the clip length.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the clip folder the output lies under: the configured or default one, normalised
    /// (<see cref="ClipRootPaths.TryNormalize"/>), before the per-user folder. The worker records it in
    /// <c>ClipRoots</c> before it writes anything there, so every clip file it makes passes <see cref="ClipFileGuard"/>.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>
    /// Gets the free-space guard's estimate of the clip's size in bytes (<see cref="ClipSizeEstimator"/>), or
    /// <c>null</c> when it can't be estimated. The request keeps it as <c>ClipInfo.EstimatedBytes</c> when it queues the job.
    /// </summary>
    public long? EstimatedBytes { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="OutputPath"/> is the clip's own file, which the finished clip replaces (since
    /// Bookmarks 1.2.1: a remake keeps its file's name, <see cref="ClipFileOps.ReusableOwnPath"/>); the worker puts it under a
    /// fresh name when the file can't be replaced.
    /// </summary>
    public bool ReplacesOwnFile { get; init; }

    /// <summary>
    /// Gets the folder the clip is written to: <see cref="Root"/>, or the user's folder in it. It need not exist yet; the
    /// worker creates it when the job starts.
    /// </summary>
    public string OutputFolder => Path.GetDirectoryName(OutputPath) ?? Root;
}
