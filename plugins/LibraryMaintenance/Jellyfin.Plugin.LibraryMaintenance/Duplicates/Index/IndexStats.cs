using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Kit.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// What a build found (<c>GET /Index</c>), over every library it read: Diagnostics' server-wide numbers. A
/// caller's <c>GET /Index</c> counts only the libraries they can open (<see cref="MovieIndexService.GetStatus(Jellyfin.Database.Implementations.Entities.User)"/>).
/// </summary>
internal sealed record IndexStats
{
    /// <summary>Gets the movies indexed.</summary>
    public int Movies { get; init; }

    /// <summary>Gets the entries (version sets).</summary>
    public int Entries { get; init; }

    /// <summary>Gets the entries with more than one member.</summary>
    public int MergedEntries { get; init; }

    /// <summary>Gets the entries with any broken state.</summary>
    public int BrokenEntries { get; init; }

    /// <summary>Gets the movies left out because they have no name.</summary>
    public int Skipped { get; init; }

    /// <summary>Gets the movies left out by the settings' excluded folders.</summary>
    public int Excluded { get; init; }

    /// <summary>Gets the owned items dropped (local alternates and extras the query returned anyway).</summary>
    public int Owned { get; init; }

    /// <summary>Gets the number of entries in each broken state; an entry can be in several.</summary>
    public IReadOnlyDictionary<VersionSetState, int> States { get; init; } = new Dictionary<VersionSetState, int>();

    /// <summary>
    /// Gets each movie left out for its name with its libraries, so a caller's <c>GET /Index</c> counts only the ones they
    /// can see (their libraries, and not hidden from them by a parental rating or tags).
    /// </summary>
    public IReadOnlyList<IndexLeftOut> SkippedMovies { get; init; } = [];

    /// <summary>Gets each movie the excluded folders left out, with its libraries.</summary>
    public IReadOnlyList<IndexLeftOut> ExcludedMovies { get; init; } = [];
}
