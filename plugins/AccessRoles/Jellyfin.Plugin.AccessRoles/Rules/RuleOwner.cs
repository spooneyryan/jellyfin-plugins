namespace Jellyfin.Plugin.AccessRoles.Rules;

/// <summary>
/// Which part of the plugin enforces a rule, so every rule of the <see cref="ActionManifest"/> (and the runtime Jellyfin
/// Enhanced row) is classified and the filter knows which it applies.
/// </summary>
internal enum RuleOwner
{
    /// <summary>
    /// The settings locks (since 0.9.5): the Profile, Quick Connect and Jellyfin Enhanced rejects and the configuration and
    /// display-preference merges, applied by <c>AccessRulesActionFilter</c> to signed-in non-admins.
    /// </summary>
    Settings,

    /// <summary>
    /// The library sync's hooks on admin and sign-in actions (the per-user lock, the <c>UpdateUserPolicy</c> pre-merge, the sign-in hook,
    /// the library structure calls): applied by <c>AccessRulesActionFilter</c> through the library sync's handlers, unchanged.
    /// </summary>
    LibraryHooks,

    /// <summary>
    /// The admin surfaces and the server-wide hardening (the <c>AdminSurfaces</c> and <c>UserDirectory</c> locks,
    /// configuration reads, the restart block, the public user list, library leak hardening): applied by
    /// <c>AccessRulesActionFilter</c> through <c>ServerHardening</c> to non-admins (anonymous callers too for the restart
    /// block and the public user list) since 0.9.7.
    /// </summary>
    ServerHardening,

    /// <summary><c>LibraryHardening=Strict</c> (not done yet): classified, not applied.</summary>
    Strict
}
