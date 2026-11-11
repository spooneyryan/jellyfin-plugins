using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A library in a user's effective access: visible with its sources (<c>Role:Family</c>, <c>UserAdd</c>,
/// <c>AllLibraries</c>, <c>Original</c>, <c>Native</c>), or hidden with the reason (<c>RemovedForUser</c>,
/// <c>NotGranted</c>, <c>Recreated</c>).
/// </summary>
public sealed class AccessRolesEffectiveLibraryDto
{
    /// <summary>Gets or sets the library's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the library's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the library's native "Enable the library" switch is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets why a visible library is visible.</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>Gets or sets why a hidden library is hidden, or <c>null</c> for a visible one.</summary>
    public string? Reason { get; set; }
}
