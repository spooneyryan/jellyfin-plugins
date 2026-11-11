using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Audit;
using Jellyfin.Plugin.AccessRoles.Policy;
using Jellyfin.Plugin.AccessRoles.Services;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// The audit lines of the library sync and the library lifecycle: plain values, ids in Jellyfin's <c>N</c>
/// form, library names next to their ids, as the roles API's lines (<see cref="AccessRolesAuditDetails"/>).
/// </summary>
internal static class LibrarySyncAudit
{
    /// <summary>
    /// The line of a user's write, release, drop, drift or skip.
    /// </summary>
    /// <param name="actor">Who started the run.</param>
    /// <param name="trigger">What started it.</param>
    /// <param name="result">What the sync did for the user.</param>
    /// <param name="catalog">The libraries, for names.</param>
    /// <returns>The line.</returns>
    public static AuditRecord User(AuditActor actor, SyncTrigger trigger, SyncUserResult result, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(catalog);
        var action = result.Kind switch
        {
            SyncActionKind.Write when result.Wrote => AuditActions.PolicySynced,
            SyncActionKind.Release when result.Wrote || result.Outcome == PolicyWriteOutcome.Unchanged => AuditActions.LibraryAccessRestored,
            SyncActionKind.DropRelease when result.Reason == SyncReasons.AlreadyApplied => AuditActions.LibraryAccessRestored,
            SyncActionKind.DropRelease => AuditActions.ReleaseDropped,
            SyncActionKind.Drift => AuditActions.DriftDetected,
            SyncActionKind.Skip when result.Reason == SyncReasons.NativeChanged => AuditActions.DriftDetected,
            _ when result.Outcome is PolicyWriteOutcome.Aborted or PolicyWriteOutcome.Resynced => AuditActions.WriteAborted,
            _ => AuditActions.SyncSkipped
        };

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reason"] = result.Reason,
            ["Trigger"] = trigger.ToString(),
            ["Native"] = Fields(result.Native, catalog)
        };
        if (result.Kind == SyncActionKind.Drift && result.DriftDecision is { } drift)
        {
            // ReportOnly: the rules' result, what the admin's edit keeps, what was applied, what wasn't.
            details["Intended"] = Fields(result.Target!, catalog);
            if (drift.Write is { } safe)
            {
                details[result.Wrote ? "Written" : "ToWrite"] = Fields(safe, catalog);
                details["EnabledFolders"] = Change(result.Native, safe, catalog);
            }

            details["Kept"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["EveryLibrary"] = drift.KeptEveryLibrary,
                ["Added"] = Named(drift.KeptAdded, catalog),
                ["Removed"] = Named(drift.KeptRemoved, catalog)
            };
            details["Applied"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Deleted"] = AccessRolesAuditDetails.Ids(drift.Deleted),
                ["Recreated"] = Named(drift.Recreated, catalog),
                ["NoLongerGranted"] = Named(drift.NoLongerGranted, catalog)
            };
            details["NotApplied"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Added"] = Named(drift.NotApplied, catalog),
                ["EveryLibrary"] = drift.NotAppliedEveryLibrary,
                ["HeldBack"] = Named(drift.HeldBack, catalog)
            };
        }
        else if (result.Target is { } target)
        {
            details[result.Wrote || result.Kind is SyncActionKind.Write or SyncActionKind.Release ? "Written" : "Intended"] = Fields(target, catalog);
            details["EnabledFolders"] = Change(result.Native, target, catalog);
        }

        if (result.Write is { } write)
        {
            Writer(details, write, catalog);
        }

        if (result.Outcome is { } outcome)
        {
            details["Outcome"] = outcome.ToString();
        }

        if (result.Differences.Count > 0)
        {
            details["ChangedFields"] = result.Differences;
        }

        if (result.Kind is SyncActionKind.Release or SyncActionKind.DropRelease && result.Effective?.PendingRelease is { } release)
        {
            details["Choice"] = release.Choice.Name;
            details["RequestedUtc"] = release.RequestedUtc;
            details["Baseline"] = release.Baseline is { } baseline ? Fields(LibraryFields.Of(baseline.EnableAllFolders, baseline.EnabledFolders), catalog) : null;
            details["PolicyWritten"] = result.Wrote;
        }

        return new AuditRecord(actor, action, result.User.Id, null, result.User.Name, details);
    }

    /// <summary>
    /// The line of a write again after a post-write check found the old access enforced ("verify after writing").
    /// </summary>
    /// <param name="check">The check.</param>
    /// <param name="write">The write again.</param>
    /// <param name="userName">The user's name, if known.</param>
    /// <param name="catalog">The libraries, for names.</param>
    /// <returns>The line.</returns>
    public static AuditRecord Reverted(VerificationResult check, PolicyWriteResult write, string? userName, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(write);
        var found = check.Found ?? check.Check.Before;
        var written = write.Outcome == PolicyWriteOutcome.Resynced ? write.Target : check.Check.Target;
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reason"] = SyncReasons.RevertedAfterWrite,
            ["Native"] = Fields(found, catalog),
            ["Written"] = Fields(written, catalog),
            ["EnabledFolders"] = Change(found, written, catalog),
            ["Outcome"] = write.Outcome.ToString(),

            // The race heals from the committed row: the database held the write, the cache the old object.
            ["FromDatabase"] = write.Outcome == PolicyWriteOutcome.Resynced
        };
        Writer(details, write, catalog);
        details.Remove("Resynced");
        return new AuditRecord(
            AuditActor.System,
            write.Outcome is PolicyWriteOutcome.Written or PolicyWriteOutcome.Resynced ? AuditActions.PolicySynced : AuditActions.SyncSkipped,
            check.Check.UserId,
            null,
            userName,
            details);
    }

    /// <summary>
    /// The line of a library lifecycle change.
    /// </summary>
    /// <param name="actor">Who started the run.</param>
    /// <param name="change">The change.</param>
    /// <returns>The line.</returns>
    public static AuditRecord Library(AuditActor actor, LifecycleEvent change)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(change);
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["LibraryId"] = AccessRolesAuditDetails.Id(change.Id),
            ["Name"] = change.Name,
            ["SeenCall"] = change.Exact
        };
        if (change.OldId is { } oldId)
        {
            details["OldId"] = AccessRolesAuditDetails.Id(oldId);
            details["OldName"] = change.OldName;
        }

        if (change.RenameCandidates.Count > 0)
        {
            details["RenameCandidates"] = AccessRolesAuditDetails.Ids(change.RenameCandidates);
        }

        var action = change.Kind switch
        {
            LifecycleEventKind.Added => AuditActions.LibraryAdded,
            LifecycleEventKind.Renamed => AuditActions.LibraryRenamed,
            LifecycleEventKind.Removed => AuditActions.LibraryRemoved,
            _ => AuditActions.LibraryRecreated
        };
        return new AuditRecord(actor, action, null, null, change.Name, details);
    }

    /// <summary>
    /// The line of managed users that library deletions left with no library.
    /// </summary>
    /// <param name="actor">Who started the run.</param>
    /// <param name="users">The users.</param>
    /// <returns>The line.</returns>
    public static AuditRecord WithoutLibraries(AuditActor actor, IReadOnlyList<SyncUserResult> users)
    {
        ArgumentNullException.ThrowIfNull(users);
        return new AuditRecord(actor, AuditActions.UsersWithoutLibraries, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Users"] = users.Select(u => new Dictionary<string, object?>(StringComparer.Ordinal) { ["UserId"] = AccessRolesAuditDetails.Id(u.User.Id), ["Name"] = u.User.Name }).ToList()
        });
    }

    /// <summary>
    /// The line of a pause or a resume of the sync.
    /// </summary>
    /// <param name="actor">Who started the run.</param>
    /// <param name="paused">Why it pauses, or <c>null</c> when it resumes.</param>
    /// <param name="previous">Why it was paused before, if it was.</param>
    /// <returns>The line.</returns>
    public static AuditRecord Pause(AuditActor actor, string? paused, string? previous)
        => new(actor, paused is null ? AuditActions.LibrarySyncResumed : AuditActions.LibrarySyncPaused, null, null, null, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reason"] = paused ?? previous
        });

    /// <summary>
    /// The line of an admin's policy save whose library edit the rules' result replaced (the user policy pre-merge:
    /// "Manual edit to Library access replaced by roles (added: Movies B)"): what the save posted, what was written, and
    /// what the save added and removed that the write undid.
    /// </summary>
    /// <param name="actor">The admin or API key.</param>
    /// <param name="result">The pre-merge's plan (its <c>Native</c> the posted access, its <c>Target</c> what was written).</param>
    /// <param name="before">The access the server enforced before the save.</param>
    /// <param name="catalog">The libraries, for names.</param>
    /// <returns>The line.</returns>
    public static AuditRecord Overridden(AuditActor actor, SyncUserResult result, LibraryFields before, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(catalog);
        var posted = result.Native;
        var written = result.Target ?? posted;
        return new AuditRecord(actor, AuditActions.ManualEditOverridden, result.User.Id, null, result.User.Name, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reason"] = result.Reason,
            ["Trigger"] = SyncTrigger.PolicySave.ToString(),
            ["Native"] = Fields(before, catalog),
            ["Posted"] = Fields(posted, catalog),
            ["Written"] = Fields(written, catalog),

            // What the save tried that the rules undid: its additions taken out again, its removals put back.
            ["Overridden"] = Change(written, posted, catalog),
            ["EnabledFolders"] = Change(before, written, catalog),
            ["Outcome"] = result.Outcome?.ToString()
        });
    }

    /// <summary>
    /// The line of a user policy hook that threw ("fail open": the post went through unchanged): <c>DriftDetected</c>
    /// with the reason <c>HookFailed</c>, the access posted and the error.
    /// </summary>
    /// <param name="actor">The admin or API key.</param>
    /// <param name="userId">The user.</param>
    /// <param name="userName">The user's name, if known.</param>
    /// <param name="posted">The posted library fields.</param>
    /// <param name="error">The error.</param>
    /// <param name="catalog">The libraries, for names.</param>
    /// <returns>The line.</returns>
    public static AuditRecord HookFailed(AuditActor actor, Guid userId, string? userName, LibraryFields posted, string error, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(posted);
        ArgumentNullException.ThrowIfNull(catalog);
        return new AuditRecord(actor, AuditActions.DriftDetected, userId, null, userName, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reason"] = SyncReasons.HookFailed,
            ["Trigger"] = SyncTrigger.PolicySave.ToString(),
            ["Posted"] = Fields(posted, catalog),
            ["Error"] = error
        });
    }

    /// <summary>
    /// The line of a sign-in the hook refused (fail closed): what the server enforced, what the rules give, why the
    /// correction didn't happen.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <param name="userName">The user's name.</param>
    /// <param name="correction">What the hook found, or <c>null</c> when it threw.</param>
    /// <param name="native">The access the server enforces, if read.</param>
    /// <param name="error">The error, when the hook threw.</param>
    /// <param name="tokenRevoked">Whether the token the core issued was revoked (<c>false</c> when the logout threw, or
    /// without a session manager).</param>
    /// <param name="catalog">The libraries, for names.</param>
    /// <returns>The line.</returns>
    public static AuditRecord SignInRefused(Guid userId, string? userName, UserCorrection? correction, LibraryFields? native, string? error, bool tokenRevoked, LibraryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Reason"] = correction is null ? SyncReasons.HookFailed : correction.BeingCreated ? SyncReasons.BeingCreated : correction.HeldBecause ?? correction.Result?.Reason,
            ["Trigger"] = SyncTrigger.SignIn.ToString(),
            ["TokenRevoked"] = tokenRevoked
        };
        if (correction?.HeldBecause is not null)
        {
            // The store holds the sync: Intended is what Access Roles last wrote.
            details["Held"] = true;
        }

        if (native is not null)
        {
            details["Native"] = Fields(native, catalog);
        }

        if (correction?.Planned is { } planned)
        {
            details["Intended"] = Fields(planned, catalog);
        }

        if (correction?.Result?.Write is { } write)
        {
            details["Outcome"] = write.Outcome.ToString();
            if (write.Error is { } writeError)
            {
                details["Error"] = writeError;
            }
        }

        if (error is not null)
        {
            details["Error"] = error;
        }

        return new AuditRecord(AuditActor.System, AuditActions.SignInRefused, userId, null, userName, details);
    }

    // What the policy writer reported beyond the outcome: a failed save and the write again, the fields taken from the
    // committed row, the disable kept, the committed row written back, the access found under the lock.
    private static void Writer(Dictionary<string, object?> details, PolicyWriteResult write, LibraryCatalog catalog)
    {
        if (write.SaveFailed || write.RetriedAfterFailedSave)
        {
            details["SaveFailed"] = true;
            details["RetriedAfterFailedSave"] = write.RetriedAfterFailedSave;
        }

        if (write.CommittedFields.Count > 0)
        {
            details["CommittedFields"] = write.CommittedFields;
        }

        if (write.KeptDisabled)
        {
            details["KeptDisabled"] = true;
        }

        if (write.Outcome == PolicyWriteOutcome.Resynced)
        {
            details["Resynced"] = Fields(write.Target, catalog);
        }

        if (write.CommittedUnavailable)
        {
            details["CommittedUnavailable"] = true;
        }

        if (write.Cancelled)
        {
            details["Cancelled"] = true;
        }

        if (write.NativeChanged && write.Before is { } found)
        {
            details["Found"] = Fields(found, catalog);
        }
    }

    private static Dictionary<string, object?> Named(IReadOnlyList<Guid> ids, LibraryCatalog catalog) => new(StringComparer.Ordinal)
    {
        ["EnabledFolders"] = AccessRolesAuditDetails.Ids(ids),
        ["LibraryNames"] = ids.Distinct().ToDictionary(AccessRolesAuditDetails.Id, id => (object?)catalog.Find(id)?.Name, StringComparer.Ordinal)
    };

    private static Dictionary<string, object?> Fields(LibraryFields fields, LibraryCatalog catalog) => new(StringComparer.Ordinal)
    {
        ["EnableAllFolders"] = fields.EnableAllFolders,
        ["EnabledFolders"] = AccessRolesAuditDetails.Ids(fields.EnabledFolders),
        ["LibraryNames"] = fields.EnabledFolders.Distinct().ToDictionary(AccessRolesAuditDetails.Id, id => (object?)catalog.Find(id)?.Name, StringComparer.Ordinal)
    };

    private static Dictionary<string, object?> Change(LibraryFields from, LibraryFields to, LibraryCatalog catalog)
    {
        var before = catalog.Expand(from.EnableAllFolders, from.EnabledFolders);
        var after = catalog.Expand(to.EnableAllFolders, to.EnabledFolders);
        var added = catalog.Order(after.Where(id => !before.Contains(id)));
        var removed = catalog.Order(before.Where(id => !after.Contains(id)).Concat(from.EnableAllFolders ? [] : from.EnabledFolders.Where(id => !catalog.Contains(id) && !to.EnabledFolders.Contains(id))));
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Added"] = AccessRolesAuditDetails.Ids(added),
            ["Removed"] = AccessRolesAuditDetails.Ids(removed),
            ["LibraryNames"] = added.Concat(removed).Distinct().ToDictionary(AccessRolesAuditDetails.Id, id => (object?)catalog.Find(id)?.Name, StringComparer.Ordinal)
        };
    }
}
