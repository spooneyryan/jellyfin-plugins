using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A user's departure from library management whose write is still to come: the admin's
/// <c>Restore</c> or <c>GrantAll</c>, the access it gives, and the native access it was computed against. The library sync
/// writes it only while the native access still matches that baseline, and drops it otherwise.
/// </summary>
public sealed class AccessRolesPendingReleaseDto
{
    /// <summary>Gets or sets the admin's choice, or <c>null</c> for one a newer build wrote.</summary>
    public AccessRolesUnmanageChoice? Choice { get; set; }

    /// <summary>Gets or sets when the user left management, UTC.</summary>
    public DateTime RequestedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether the access to write is every library, future ones included.</summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>Gets or sets the libraries to write otherwise.</summary>
    public IReadOnlyList<Guid> EnabledFolders { get; set; } = [];

    /// <summary>
    /// Gets or sets the user's native library access when the choice was made, or <c>null</c> when the release doesn't record
    /// it (a 0.9.2.0 build's).
    /// </summary>
    public AccessRolesNativeLibrariesDto? Baseline { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the native access still is the baseline: only then does the sync write the
    /// release; otherwise it drops it without a write, so a native edit made since is kept.
    /// </summary>
    public bool BaselineMatches { get; set; }
}
