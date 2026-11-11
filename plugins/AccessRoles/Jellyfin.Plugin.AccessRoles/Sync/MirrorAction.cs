namespace Jellyfin.Plugin.AccessRoles.Sync;

/// <summary>
/// What the optional mirror did for a user (<see cref="PreferenceMirrorSync"/>).
/// </summary>
internal enum MirrorAction
{
    /// <summary>Nothing: the flag already was as wanted, and not by the mirror's write.</summary>
    None,

    /// <summary>The flag written off, the mirror's record kept.</summary>
    TurnedOff,

    /// <summary>The flag written back on, the record dropped.</summary>
    TurnedOn,

    /// <summary>The flag already on again: only the record dropped.</summary>
    Forgotten,

    /// <summary>The write didn't happen (the lock busy, the user changed meanwhile, the save failed): the next run tries again.</summary>
    Failed
}
