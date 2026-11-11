using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;

namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What one library sync run found and did (<c>POST /AccessRoles/Admin/Sync</c>, Diagnostics' <c>LibrarySync</c>).
/// </summary>
/// <param name="Trigger">What started it.</param>
/// <param name="StartedUtc">When it started, UTC.</param>
internal sealed record SyncReport(SyncTrigger Trigger, DateTime StartedUtc)
{
    /// <summary>Gets a value indicating whether it only reported.</summary>
    public bool DryRun { get; init; }

    /// <summary>Gets why it wrote nothing (<see cref="SyncReasons"/>' pause reasons), or <c>null</c> when it may write.</summary>
    public string? Paused { get; init; }

    /// <summary>Gets how long it took.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Gets the users it did, or would do, something for: every user not already in sync.</summary>
    public IReadOnlyList<SyncUserResult> Users { get; init; } = [];

    /// <summary>Gets what the library lifecycle found.</summary>
    public IReadOnlyList<LifecycleEvent> Libraries { get; init; } = [];

    /// <summary>Gets the libraries that wait for a rename to resolve.</summary>
    public IReadOnlyCollection<Guid> WaitingLibraries { get; init; } = [];

    /// <summary>Gets the listed libraries a waiting rename waits for, which no rule grants until it is re-pointed.</summary>
    public IReadOnlyCollection<Guid> WithheldLibraries { get; init; } = [];

    /// <summary>
    /// Gets the users the run left alone because they are being created, or have just been (see
    /// <see cref="LibrarySync.BeginCreation"/>).
    /// </summary>
    public IReadOnlyCollection<Guid> BeingCreated { get; init; } = [];

    /// <summary>Gets how many users' libraries the rules manage.</summary>
    public int ManagedUsers { get; init; }

    /// <summary>Gets the one user the run was for (<see cref="SyncRequest.UserId"/>), or <c>null</c> for everyone.</summary>
    public Guid? Scope { get; init; }

    /// <summary>Gets how many users drifted (whether reapplied or reported).</summary>
    public int Drifted => Users.Count(u => u.Kind == SyncActionKind.Drift || u.Reason == SyncReasons.Drift);

    /// <summary>Gets how many writes changed a user's access.</summary>
    public int Written => Users.Count(u => u.Wrote);

    /// <summary>Gets how many users were skipped (any reason, failed writes included).</summary>
    public int Skipped => Users.Count(u => u.Kind == SyncActionKind.Skip);
}
