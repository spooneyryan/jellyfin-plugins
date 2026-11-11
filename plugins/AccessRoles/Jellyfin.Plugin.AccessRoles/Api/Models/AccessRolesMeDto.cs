using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AccessRoles.Model;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// What <c>GET /AccessRoles/Me</c> tells the signed-in user's web client: only the caller's own lock names and
/// whether they have no library, for the web guard to hide and explain.
/// </summary>
public sealed class AccessRolesMeDto
{
    /// <summary>Gets or sets the caller's id.</summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Access Roles manages the caller (an assignment that isn't <c>Excluded</c>;
    /// while the data file can't be trusted, everyone). The web caches every answer, <c>false</c> too.
    /// </summary>
    public bool Managed { get; set; }

    /// <summary>
    /// Gets or sets a short version of this answer, which changes whenever the answer does, so the web can tell a stale
    /// cached copy.
    /// </summary>
    public string RulesVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the caller's locked sections and surfaces, in the enum's order; always empty for an administrator, to
    /// whom no lock applies.
    /// </summary>
    public IReadOnlyList<AccessRolesLock> Locked { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether every Settings section is locked (Jellyfin Enhanced's only when it is
    /// installed): the web then hides the Settings entry points and shows a note above Sign out.
    /// </summary>
    public bool AllSettingsLocked { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller is managed and can open no library the server lists
    /// (the web replaces the stock "no libraries" text).
    /// </summary>
    public bool NoLibraries { get; set; }

    /// <summary>Gets or sets the message the web shows, or <c>null</c>.</summary>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the locks the server doesn't enforce yet in this build (before 0.9.7
    /// <c>AdminSurfaces</c> and <c>UserDirectory</c>, since then none). The same for every caller: a fact about the
    /// build, nothing about a user. <see cref="Locked"/> still names them, so the web may hide what they cover; the admin
    /// page says "not enforced yet" for them.
    /// </summary>
    public IReadOnlyList<AccessRolesLock> NotEnforced { get; set; } = [];
}
