using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// A caller's view of an index entry (<see cref="EntryScope.View"/>).
/// </summary>
/// <param name="Representative">An in-scope member: the root when it is in scope, else the in-scope member with the lowest id.</param>
/// <param name="Members">The in-scope members, the representative first, then by id.</param>
/// <param name="LibraryIds">Their libraries within the scope.</param>
/// <param name="SourceCount">The in-scope members, plus the root's local alternates when the root is in scope (the whole
/// set's <see cref="VersionEntry.SourceCount"/> when every member is).</param>
/// <param name="State">The broken states the in-scope members show.</param>
/// <param name="IsWhole">Whether every member of the set is in scope.</param>
internal sealed record EntryView(
    MovieRecord Representative,
    IReadOnlyList<MovieRecord> Members,
    IReadOnlyList<Guid> LibraryIds,
    int SourceCount,
    VersionSetState State,
    bool IsWhole);
