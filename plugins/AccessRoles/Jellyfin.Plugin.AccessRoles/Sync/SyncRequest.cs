using System;
using Jellyfin.Plugin.AccessRoles.Audit;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What a library sync run is asked to do.
/// </summary>
/// <param name="Trigger">What started it.</param>
/// <param name="Actor">Who started it, for the audit lines: <c>System</c> for the plugin's own runs.</param>
internal sealed record SyncRequest(SyncTrigger Trigger, AuditActor Actor)
{
    /// <summary>Gets a value indicating whether to only report what the run would do (<c>Sync?dryRun=true</c>).</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// Gets a value indicating whether an admin confirmed writes that leave no enabled administrator with every library
    /// (<c>confirmNoFullAdmin=true</c>); recorded on those administrators' assignments.
    /// </summary>
    public bool ConfirmNoFullAdmin { get; init; }

    /// <summary>
    /// Gets a value indicating whether drifted users get the rules' result written even with <c>DriftPolicy=ReportOnly</c>
    /// (the admin's <i>Revert</i>).
    /// </summary>
    public bool ReapplyDrift { get; init; }

    /// <summary>
    /// Gets a value indicating whether an admin confirms the sync after the store was restored from a snapshot at load
    /// (<c>confirmRestore=true</c>, <see cref="RestoreHold"/>); a dry run never lifts the hold.
    /// </summary>
    public bool ConfirmRestore { get; init; }

    /// <summary>
    /// Gets the <see cref="SyncPlanToken"/> of the dry run the admin confirmed, if any (<c>ifPlan</c>): a
    /// run whose own plan is another writes nothing and answers 409 <c>Changed</c>.
    /// </summary>
    public string? IfPlan { get; init; }

    /// <summary>
    /// Gets the one user the run is for, or <c>null</c> for everyone (the per-user <i>Revert</i>): only they are
    /// planned, written, audited and covered by the plan's token; the library lifecycle is reconciled as in any run.
    /// </summary>
    public Guid? UserId { get; init; }
}
