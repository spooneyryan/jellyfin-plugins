using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// What a read of the movie index gets: the snapshot to answer from, null when none is built yet
/// (the route answers 202 with the progress then, or 503 when the last build failed and none runs), and whether it is out
/// of date.
/// </summary>
/// <param name="Snapshot">The snapshot, or null when the server has none yet.</param>
/// <param name="Stale">Whether the snapshot is out of date: the library changed since its build, or a build is running.</param>
/// <param name="Rebuilding">Whether a build is running.</param>
internal sealed record IndexRead(MovieIndexSnapshot? Snapshot, bool Stale, bool Rebuilding)
{
    /// <summary>Gets why the last build failed (a fixed sentence, no path), or null when it didn't.</summary>
    public string? LastError { get; init; }

    /// <summary>Gets when the last build failed, or null.</summary>
    public DateTime? LastErrorUtc { get; init; }

    /// <summary>
    /// Gets a value indicating whether there is no index because the last build failed and none runs ("Index
    /// error"): the route answers 503 rather than a 202 that would never end.
    /// </summary>
    public bool Failed => Snapshot is null && !Rebuilding && LastError is not null;
}
