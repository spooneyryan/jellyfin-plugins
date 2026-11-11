namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// What a plugin does with a deleted user's records. Each plugin sets its default
/// (<see cref="Hosting.KitOptions.DeletedUserPolicy"/>): Bookmarks <see cref="KeepAsOrphan"/>, User Ratings
/// <see cref="Delete"/>, Access Roles always <see cref="Delete"/>.
/// </summary>
/// <remarks>
/// Only an explicit deletion applies it: Jellyfin's <c>UserDeletedEventArgs</c> (<see cref="KitUserDeletedConsumer"/>), or
/// the admin's <i>Apply deleted-user policy</i> on users the reconciler lists (<see cref="UserReconciler.ApplyPolicyAsync"/>).
/// An unknown id found later is never enough (<see cref="UserReconciler"/>).
/// </remarks>
internal enum DeletedUserPolicy
{
    /// <summary>The user's records are removed (plugins may keep what must outlive them, such as User Ratings'
    /// tombstones).</summary>
    Delete,

    /// <summary>The records stay under the old id: invisible to users, excluded from aggregates, shown to admins as "Deleted user (id)".</summary>
    KeepAsOrphan,

    /// <summary>The records stay but no longer say who wrote them ("Former member"); plugins without authorship treat it as <see cref="KeepAsOrphan"/>.</summary>
    Anonymize
}
