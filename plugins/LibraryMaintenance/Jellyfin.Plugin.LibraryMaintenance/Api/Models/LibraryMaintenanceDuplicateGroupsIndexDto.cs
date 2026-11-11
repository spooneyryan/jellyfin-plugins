using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The index a groups result was made from (<c>Index</c>).
/// </summary>
public sealed class LibraryMaintenanceDuplicateGroupsIndexDto
{
    /// <summary>
    /// Gets or sets the snapshot's build number.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// Gets or sets when that build ended (UTC).
    /// </summary>
    public DateTime GeneratedUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the library changed since the build, or a build is running: the groups
    /// may be out of date.
    /// </summary>
    public bool Stale { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a build is running.
    /// </summary>
    public bool Rebuilding { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a library scan is running.
    /// </summary>
    public bool ScanInProgress { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the build started while a library scan was reading the libraries, so the
    /// results may be incomplete.
    /// </summary>
    public bool BuiltDuringScan { get; set; }

    /// <summary>
    /// Gets or sets why the last build failed, a fixed sentence with no path in it (the server's log has the details), or
    /// null when it didn't: the groups are then those of the build before it.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets when the last build failed (UTC), or null.
    /// </summary>
    public DateTime? LastErrorUtc { get; set; }
}
