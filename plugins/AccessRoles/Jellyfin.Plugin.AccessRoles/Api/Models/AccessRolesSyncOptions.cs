using System;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// The query options of <c>POST /AccessRoles/Admin/Sync</c>.
/// </summary>
public sealed class AccessRolesSyncOptions
{
    /// <summary>Gets or sets a value indicating whether to only report what a run would do: nothing written or saved.</summary>
    public bool DryRun { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the admin confirms writes that leave no enabled administrator with every
    /// library (<c>LockoutGuard</c>); without it such writes are skipped (<c>NoFullAdministrator</c>).
    /// </summary>
    public bool ConfirmNoFullAdmin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether drifted users get the rules' result written even with
    /// <c>DriftPolicy=ReportOnly</c> (the drift's <i>Revert</i>).
    /// </summary>
    public bool ReapplyDrift { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the admin confirms the library sync after Access Roles' data file was restored
    /// from a snapshot, when it loaded or by <c>POST Snapshots/Restore</c> (the sync is paused, <c>StoreRestored</c>, until then).
    /// A dry run shows what
    /// the confirmed run would write and never lifts the pause; the confirmation needs one made since the pause began (409
    /// <c>DryRunRequired</c> otherwise).
    /// </summary>
    public bool ConfirmRestore { get; set; }

    /// <summary>
    /// Gets or sets the <c>PlanToken</c> of the dry run the admin confirmed (the admin page's <i>Revert</i>):
    /// a real run that would write anything else (a user the dry run didn't list, or other access for one it did) writes
    /// nothing and answers 409 <c>Changed</c> with the users it would write now.
    /// </summary>
    public string? IfPlan { get; set; }

    /// <summary>
    /// Gets or sets the one user the run is for (the drift's per-user <i>Revert</i>): it plans,
    /// writes and audits only them, its <c>PlanToken</c> covers only them (a token of a run for everyone, or for another
    /// user, is another), and a 409 <c>Changed</c> names only them. The library lifecycle is reconciled as in any run. 404
    /// for a user the server doesn't have; 400 with <c>confirmRestore</c> (the hold after a restore is lifted by a run for
    /// everyone), and a dry run for one user doesn't count as the dry run the hold waits for.
    /// </summary>
    public Guid? UserId { get; set; }
}
