namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// One lock as it applies to a user, with its source (<c>Locks</c>).
/// </summary>
/// <param name="Locked">Whether the section or surface is locked.</param>
/// <param name="Source">Why: one of <see cref="AccessSources"/>, such as <c>Role:Kid</c> or <c>UserOverride</c>.</param>
internal sealed record LockDecision(bool Locked, string Source);
