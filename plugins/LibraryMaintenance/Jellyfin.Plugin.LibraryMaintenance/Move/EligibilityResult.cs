using System;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// Whether an item can move, and when it can, the item that moves (its owner, for a version or part),
/// its library and location.
/// </summary>
/// <param name="ItemId">The id asked about.</param>
/// <param name="Code">Why not, as a <c>move.*</c> code; null when eligible.</param>
/// <param name="Reason">Why not, as a sentence; null when eligible.</param>
/// <param name="Item">The item that moves, or null.</param>
/// <param name="OwnerItemId">The owner's id when the item asked about is a version or part of another item.</param>
/// <param name="UnitKind">The unit's kind, when eligible.</param>
/// <param name="Library">The source library, when eligible.</param>
/// <param name="LocationIndex">The source location's index, when eligible.</param>
internal sealed record EligibilityResult(
    Guid ItemId,
    string? Code,
    string? Reason,
    Video? Item = null,
    Guid? OwnerItemId = null,
    MoveUnitKind? UnitKind = null,
    CatalogLibrary? Library = null,
    int LocationIndex = -1)
{
    /// <summary>Gets a value indicating whether the item can move.</summary>
    public bool Eligible => Code is null;

    /// <summary>Gets a value indicating whether the item exists for the caller (a 404 otherwise, never a reason that names
    /// a library the caller can't open).</summary>
    public bool Found => Code != MoveEligibility.NotFoundCode;
}
