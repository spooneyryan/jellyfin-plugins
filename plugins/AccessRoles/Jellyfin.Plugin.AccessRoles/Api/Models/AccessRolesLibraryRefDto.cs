using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// A library a role refers to (<c>Refs[]</c>): its id, what it is called now, and what it was called and where it
/// lived when the admin picked it.
/// </summary>
public sealed class AccessRolesLibraryRefDto
{
    /// <summary>Gets or sets the library's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the library's name now, or its name when picked for a library the server no longer lists.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the library's name when the admin picked it.</summary>
    public string? NameAtSelection { get; set; }

    /// <summary>Gets or sets the library's folders when the admin picked it.</summary>
    public IReadOnlyList<string> Locations { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the library is gone (the server doesn't list it, or the catalog marked it
    /// deleted): the reference is kept until the admin purges it.
    /// </summary>
    public bool Orphaned { get; set; }

    /// <summary>Gets or sets whether the library's native "Enable the library" switch is on, or <c>null</c> for a gone one.</summary>
    public bool? Enabled { get; set; }
}
