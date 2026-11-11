namespace Jellyfin.Plugin.Kit.Users;

/// <summary>
/// Why a user id is marked as an orphan (<see cref="OrphanMark"/>).
/// </summary>
internal enum OrphanMarkSource
{
    /// <summary>The reconciler found the id unknown (a user deleted while the plugin was off, say) and its guard didn't trip.</summary>
    Reconciler,

    /// <summary>Jellyfin reported the user deleted and the policy kept some records (<see cref="DeletedUserPolicy.KeepAsOrphan"/>, <see cref="DeletedUserPolicy.Anonymize"/>).</summary>
    Deleted,

    /// <summary>An admin applied the deleted-user policy to the id and some records were kept.</summary>
    PolicyApplied
}
