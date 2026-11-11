using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// The immutable in-memory index; never written to disk.
/// </summary>
/// <param name="Version">The build's number, from 1, higher for every build since the server started.</param>
/// <param name="GeneratedUtc">When the build ended.</param>
/// <param name="BuildTime">How long it took.</param>
/// <param name="BuiltDuringScan">Whether a library scan was still reading the libraries when it started (the results may be incomplete; not the post-scan build).</param>
/// <param name="Entries">The entries.</param>
/// <param name="ById">Every member id, indexed or not, to its entry.</param>
/// <param name="Libraries">The libraries read.</param>
/// <param name="Stats">The counts.</param>
/// <param name="Metrics">What the build cost.</param>
internal sealed record MovieIndexSnapshot(
    long Version,
    DateTime GeneratedUtc,
    TimeSpan BuildTime,
    bool BuiltDuringScan,
    IReadOnlyList<VersionEntry> Entries,
    IReadOnlyDictionary<Guid, VersionEntry> ById,
    IReadOnlyList<IndexLibrary> Libraries,
    IndexStats Stats,
    BuildMetrics Metrics);
