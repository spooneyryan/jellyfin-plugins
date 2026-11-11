using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LibraryMaintenance.Api.Models;

/// <summary>
/// A move plan (<c>POST /Move/Plan</c>), answered with 200 even when it has blockers.
/// </summary>
public sealed class LibraryMaintenanceMovePlanDto
{
    /// <summary>
    /// Gets or sets the plan's id, for <c>POST /Move/Jobs</c>.
    /// </summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SHA-256 of what the plan moves and where, so a job can tell the plan is stale.
    /// </summary>
    public string PlanHash { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the plan expires (15 minutes).
    /// </summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary>
    /// Gets or sets <c>Rename</c> (one volume) or <c>CopyVerifyDelete</c> (a blocker in this version).
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the units.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceMovePlanUnitDto> Units { get; set; } = [];

    /// <summary>
    /// Gets or sets the volumes written to.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceMovePlanVolumeDto> Volumes { get; set; } = [];

    /// <summary>
    /// Gets or sets how long a copy would take; null for a rename.
    /// </summary>
    public int? EstimatedSeconds { get; set; }

    /// <summary>
    /// Gets or sets what is kept.
    /// </summary>
    public LibraryMaintenanceMovePreservedDto Preserved { get; set; } = new();

    /// <summary>
    /// Gets or sets what is regenerated or lost.
    /// </summary>
    public IReadOnlyList<string> Regenerated { get; set; } = [];

    /// <summary>
    /// Gets or sets the warnings.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceMoveIssueDto> Warnings { get; set; } = [];

    /// <summary>
    /// Gets or sets the blockers; any blocker means the move can't run.
    /// </summary>
    public IReadOnlyList<LibraryMaintenanceMoveIssueDto> Blockers { get; set; } = [];
}
