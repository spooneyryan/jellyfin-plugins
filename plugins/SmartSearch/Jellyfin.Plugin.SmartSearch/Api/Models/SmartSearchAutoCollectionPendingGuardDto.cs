using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>Removals the mass-removal guard holds back for a manager's review.</summary>
public sealed class SmartSearchAutoCollectionPendingGuardDto
{
    /// <summary>Gets or sets the hold's version, when these removals were first held: what <c>Guard/Apply</c> and <c>Guard/Keep</c> send back. Any change to the removals makes a new one.</summary>
    public DateTime DetectedUtc { get; set; }

    /// <summary>Gets or sets how many members would be removed (dead links included).</summary>
    public int WouldRemoveCount { get; set; }

    /// <summary>Gets or sets the members that would be removed (at most 1,000), only those the caller may open.</summary>
    public IReadOnlyList<Guid> WouldRemove { get; set; } = [];

    /// <summary>Gets or sets how many of the listed members the caller may not open.</summary>
    public int HiddenCount { get; set; }

    /// <summary>
    /// Gets or sets why they are held: <c>Empty</c> (the collection would be emptied), <c>ScanRunning</c> (the run started
    /// during a library scan), <c>LocationUnreachable</c> (a library folder can't be reached), <c>CandidateDrop</c> (the
    /// query found less than half as many candidates as last time) or <c>StartupPass</c> (the pass after the server
    /// started).
    /// </summary>
    public IReadOnlyList<string> Reasons { get; set; } = [];

    /// <summary>Gets or sets the library folders that couldn't be reached (administrators only).</summary>
    public IReadOnlyList<string>? Locations { get; set; }

    /// <summary>Gets or sets a value indicating whether a manager asked to apply them once on the next run.</summary>
    public bool Approved { get; set; }
}
