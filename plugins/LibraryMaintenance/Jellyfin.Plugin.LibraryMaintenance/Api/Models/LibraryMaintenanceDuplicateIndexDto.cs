using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// The movie index's status (<c>GET /LibraryMaintenance/Duplicates/Index</c>): the last build's counts and the
/// state of the next one.
/// </summary>
public sealed class LibraryMaintenanceDuplicateIndexDto
{
    /// <summary>
    /// Gets or sets the build's number: 0 before the first build since the server started, then higher for every build.
    /// </summary>
    public long Version { get; set; }

    /// <summary>
    /// Gets or sets when the last build ended (UTC), or null before the first.
    /// </summary>
    public DateTime? GeneratedUtc { get; set; }

    /// <summary>
    /// Gets or sets how long the last build took, in milliseconds.
    /// </summary>
    public long? BuildMs { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a library scan was still reading the libraries when the last build
    /// started, so its results may be incomplete (the first build of a server that has none, during a scan). The
    /// build after a scan, which starts once the scan has read the libraries, is not.
    /// </summary>
    public bool BuiltDuringScan { get; set; }

    /// <summary>
    /// Gets or sets the movies indexed.
    /// </summary>
    public int Movies { get; set; }

    /// <summary>
    /// Gets or sets the entries: version sets, so a merged movie is one entry.
    /// </summary>
    public int Entries { get; set; }

    /// <summary>
    /// Gets or sets the entries with more than one version.
    /// </summary>
    public int MergedEntries { get; set; }

    /// <summary>
    /// Gets or sets the entries with any broken state (<see cref="States"/>).
    /// </summary>
    public int BrokenEntries { get; set; }

    /// <summary>
    /// Gets or sets the libraries the last build read.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceDuplicateIndexLibraryDto> Libraries { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the index is out of date: the library changed, or a rebuild was
    /// asked for, since the last build started. A build follows at once, after the running one, or, during a library scan,
    /// when the scan ends.
    /// </summary>
    public bool Dirty { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a build is running.
    /// </summary>
    public bool Rebuilding { get; set; }

    /// <summary>
    /// Gets or sets the running build's progress, from 0 to 1, or null when none runs.
    /// </summary>
    public double? Progress { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a library scan is running now.
    /// </summary>
    public bool ScanInProgress { get; set; }

    /// <summary>
    /// Gets or sets the number of entries in each broken state; an entry can be in several.
    /// </summary>
    public LibraryMaintenanceDuplicateIndexStatesDto States { get; set; } = new();

    /// <summary>
    /// Gets or sets the movies left out because they have no name.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets or sets the movies left out by the settings' excluded folders.
    /// </summary>
    public int Excluded { get; set; }

    /// <summary>
    /// Gets or sets why the last build failed, or null when it didn't; the index before it stays in use.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Gets or sets when the last build failed (UTC), or null.
    /// </summary>
    public DateTime? LastErrorUtc { get; set; }
}
