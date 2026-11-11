using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Storage;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// One change of the library lifecycle.
/// </summary>
/// <param name="Kind">What happened.</param>
/// <param name="Id">The library's id (the new id for a rename).</param>
/// <param name="Name">The library's name (the new name for a rename).</param>
/// <param name="OldId">For a rename, the id before it.</param>
/// <param name="OldName">For a rename, the name before it.</param>
/// <param name="Exact">Whether the library structure filter saw the call, rather than the backstop's diff.</param>
internal sealed record LifecycleEvent(LifecycleEventKind Kind, Guid Id, string Name, Guid? OldId, string? OldName, bool Exact)
{
    /// <summary>Gets, for a removal the backstop found, the new libraries with the same folders that made it ambiguous.</summary>
    public IReadOnlyList<Guid> RenameCandidates { get; init; } = [];
}
