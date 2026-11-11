using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move.Planning;

/// <summary>
/// One planned move (<c>UnitPlanner</c>, <c>Unit</c>): what moves, where to, how, with which new paths and
/// predicted ids, and what warns or blocks. Nothing here has been written to disk.
/// </summary>
internal sealed class MoveUnit
{
    /// <summary>Gets the item moved (the owner, when the menu was opened on a version or part).</summary>
    public required Video Primary { get; init; }

    /// <summary>Gets the unit's kind.</summary>
    public required MoveUnitKind Kind { get; init; }

    /// <summary>Gets the source library and location.</summary>
    public required CatalogLibrary SourceLibrary { get; init; }

    /// <summary>Gets the source location's index.</summary>
    public required int SourceLocationIndex { get; init; }

    /// <summary>Gets the folder the manifest is relative to: the unit folder, or a file set's directory.</summary>
    public required string SourceRoot { get; init; }

    /// <summary>Gets the destination the client chose, resolved.</summary>
    public required ResolvedDestination Destination { get; init; }

    /// <summary>Gets the folder the unit lands in: the destination, or the wrap folder inside it.</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>Gets where the unit's root ends up: <c>TargetDirectory\&lt;unit folder&gt;</c> for a folder unit, else
    /// <see cref="TargetDirectory"/>.</summary>
    public required string TargetRoot { get; init; }

    /// <summary>Gets the wrap folder's name, when a loose file is put into its own folder.</summary>
    public string? WrapFolderName { get; init; }

    /// <summary>Gets the folders the commit creates, top-most first (a new folder, a wrap folder, an episode's metadata
    /// folder for its images).</summary>
    public IReadOnlyList<string> CreatedDirectories { get; init; } = [];

    /// <summary>Gets the commit's renames, in order.</summary>
    public IReadOnlyList<UnitRename> Renames { get; init; } = [];

    /// <summary>Gets the manifest: every file and folder that moves, relative to <see cref="SourceRoot"/>.</summary>
    public IReadOnlyList<UnitEntry> Entries { get; init; } = [];

    /// <summary>Gets the items that move, the primary first.</summary>
    public IReadOnlyList<UnitItem> Items { get; init; } = [];

    /// <summary>Gets or sets the method.</summary>
    public MoveMethod Method { get; set; }

    /// <summary>Gets or sets the source's volume GUID path, or null when it couldn't be told.</summary>
    public string? SourceVolumeId { get; set; }

    /// <summary>Gets or sets the destination's volume GUID path, or null when it couldn't be told.</summary>
    public string? DestinationVolumeId { get; set; }

    /// <summary>Gets the plan's warnings.</summary>
    public List<PlanIssue> Warnings { get; } = [];

    /// <summary>Gets the plan's blockers; a plan with any can't run.</summary>
    public List<PlanIssue> Blockers { get; } = [];

    /// <summary>
    /// Gets the junctions and symbolic links the plan found where it would move from or to (each under a
    /// <c>move.reparse-point</c> blocker): from the source location down to the unit (<see cref="LinkGuard"/>), inside
    /// the unit, and at the target. A link is never followed: nothing at or below one is opened, probed, enumerated or
    /// asked for its volume (<see cref="IsLinked"/>), as a link may point at another drive or a network share.
    /// </summary>
    public List<string> Links { get; } = [];

    /// <summary>Gets the manifest's total size in bytes.</summary>
    public long TotalBytes => Entries.Sum(e => e.Length);

    /// <summary>Gets the number of files in the manifest.</summary>
    public int FileCount => Entries.Count(e => !e.IsDirectory);

    /// <summary>Gets the primary's entry in <see cref="Items"/>.</summary>
    public UnitItem PrimaryItem => Items.First(i => i.Role == UnitItemRole.Primary);

    /// <summary>Gets the items' old and predicted ids.</summary>
    public IEnumerable<Guid> AllIds => Items.SelectMany(i => new[] { i.OldId, i.PredictedId }).Distinct();

    /// <summary>
    /// Gets whether a path is one of <see cref="Links"/> or below one, so must not be touched.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>True at or below a link.</returns>
    public bool IsLinked(string path) => Links.Any(link => PathRules.IsSameOrUnder(path, link));
}
