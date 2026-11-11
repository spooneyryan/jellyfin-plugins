using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AccessRoles.Access;
using Jellyfin.Plugin.AccessRoles.Model;
using Jellyfin.Plugin.AccessRoles.Storage;
using Jellyfin.Plugin.Kit.Storage;

namespace Jellyfin.Plugin.AccessRoles.Enforcement;

/// <summary>
/// The lock part of the <c>EffectiveRulesCache</c>: an immutable snapshot of every assigned user's locks as a
/// non-administrator, built from one store document (<see cref="EffectiveRulesResolver.LockedForNonAdministrator"/>), so a
/// request's lookup is one dictionary read.
/// </summary>
/// <remarks>
/// <para><b>Degraded mode</b> (<see cref="DegradedMode"/>): when a start can't trust the store
/// (recovered empty, or read-only without a usable document), the users listed as managed in the newest readable copy of
/// the data, or every non-admin when none is readable, get every server-enforced settings section
/// (<see cref="SettingsSections.ServerEnforced"/>) and the <c>AdminSurfaces</c> and <c>UserDirectory</c> locks
/// (<see cref="SettingsSections.ServerEnforcedSurfaces"/>) on top of the document's own: locks never weaken for them. The
/// other users get the document's. Before 0.9.13 (the interim rule of 0.9.5) a store recovered empty or read-only with no assignment
/// locked every non-admin, until the next restart.</para>
/// </remarks>
internal sealed class LockSnapshot
{
    private static readonly IReadOnlySet<AccessRolesLock> _none = new HashSet<AccessRolesLock>();
    private static readonly IReadOnlySet<AccessRolesLock> _everyServerEnforced = SettingsSections.ServerEnforced.Concat(SettingsSections.ServerEnforcedSurfaces).ToHashSet();
    private static readonly IReadOnlySet<AccessRolesLock> _everySettingsSection = AccessPresets.SettingsSections.Concat(SettingsSections.ServerEnforcedSurfaces).ToHashSet();

    private readonly IReadOnlyDictionary<Guid, IReadOnlySet<AccessRolesLock>> _locks;
    private readonly IReadOnlySet<Guid> _managed;

    private LockSnapshot(IReadOnlyDictionary<Guid, IReadOnlySet<AccessRolesLock>> locks, IReadOnlySet<Guid> managed, DegradedState? degraded, StoreStatus status)
    {
        _locks = locks;
        _managed = managed;
        Degraded = degraded;
        Status = status;
    }

    /// <summary>
    /// Gets why some users' (or every non-admin's) server-enforced settings are locked whatever the document says (the
    /// degraded start's store status), or <c>null</c> when the snapshot is the store's document alone.
    /// </summary>
    public string? DegradedBecause => Degraded?.Because;

    /// <summary>Gets the Degraded mode the snapshot was built with, or <c>null</c>.</summary>
    public DegradedState? Degraded { get; }

    /// <summary>Gets the store status the snapshot was built for.</summary>
    public StoreStatus Status { get; }

    /// <summary>Gets how many users are managed: an assignment that isn't <c>Excluded</c>.</summary>
    public int Managed => _managed.Count;

    /// <summary>Gets how many users have at least one lock.</summary>
    public int Locked => _locks.Count;

    /// <summary>
    /// Builds the snapshot of a store document.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="status">The store's status.</param>
    /// <param name="degraded">The store's Degraded mode (<see cref="DegradedMode.Current"/>), or <c>null</c>.</param>
    /// <returns>The snapshot.</returns>
    public static LockSnapshot Build(AccessRolesDocument document, StoreStatus status, DegradedState? degraded = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var locks = new Dictionary<Guid, IReadOnlySet<AccessRolesLock>>();
        var managed = new HashSet<Guid>();
        foreach (var assignment in document.Assignments)
        {
            if (!assignment.State.IsKnown || !assignment.State.Is(AccessRolesAssignmentState.Excluded))
            {
                managed.Add(assignment.UserId);
            }

            var locked = EffectiveRulesResolver.LockedForNonAdministrator(document, assignment);
            if (locked.Count > 0)
            {
                locks[assignment.UserId] = locked;
            }
        }

        return new LockSnapshot(locks, managed, degraded, status);
    }

    /// <summary>
    /// Gets a non-administrator's locks (the caller's administrator claim is the filter's to check first).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The locked sections and surfaces; for a user Degraded mode locks, every server-enforced settings section and
    /// both surfaces too.</returns>
    public IReadOnlySet<AccessRolesLock> For(Guid userId)
    {
        var locked = _locks.TryGetValue(userId, out var own) ? own : _none;
        return Degraded?.Applies(userId) == true ? Union(_everyServerEnforced, locked) : locked;
    }

    /// <summary>
    /// Gets the locks <c>GET /AccessRoles/Me</c> reports: for a user Degraded mode locks, every settings section (the web
    /// hides them all) and both surfaces with the user's own; else the user's.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The locks.</returns>
    public IReadOnlySet<AccessRolesLock> Reported(Guid userId)
        => Degraded?.Applies(userId) == true ? Union(_everySettingsSection, For(userId)) : For(userId);

    /// <summary>
    /// Gets whether a user is managed: an assignment that isn't <c>Excluded</c>, or a user Degraded mode locks (who may have
    /// one).
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns><c>true</c> when the user is, or may be, managed.</returns>
    public bool IsManaged(Guid userId) => Degraded?.Applies(userId) == true || _managed.Contains(userId);

    private static IReadOnlySet<AccessRolesLock> Union(IReadOnlySet<AccessRolesLock> all, IReadOnlySet<AccessRolesLock> own)
        => own.IsSubsetOf(all) ? all : all.Concat(own).ToHashSet();
}
