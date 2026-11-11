using System;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// Whether one item can move (<c>GET /Move/Eligibility</c>), for the details page button and the menu entry.
/// </summary>
public sealed class LibraryMaintenanceMoveEligibilityDto
{
    /// <summary>
    /// Gets or sets the item asked about.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it can move.
    /// </summary>
    public bool Eligible { get; set; }

    /// <summary>
    /// Gets or sets why not, as a code: <c>move.not-eligible</c>, <c>move.item-busy</c>, or <c>move.not-found</c> for an
    /// item that isn't there or is in a library the caller can't open (the same answer for both).
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    /// Gets or sets why not, as a sentence.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Gets or sets what would move: <c>Folder</c> (the item's own folder) or <c>FileSet</c> (its files and sidecars).
    /// </summary>
    public string? UnitKind { get; set; }

    /// <summary>
    /// Gets or sets the item that moves instead, when the one asked about is a version or part of it.
    /// </summary>
    public Guid? OwnerItemId { get; set; }
}
