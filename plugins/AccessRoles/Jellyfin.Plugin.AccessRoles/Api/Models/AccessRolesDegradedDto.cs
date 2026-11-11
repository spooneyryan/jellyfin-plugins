using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AccessRoles.Api.Models;

/// <summary>
/// Degraded mode as <c>POST /AccessRoles/Admin/Degraded/Resolve</c> answers it: whom it locks and
/// whether the call ended it.
/// </summary>
public sealed class AccessRolesDegradedDto
{
    /// <summary>Gets or sets the store status that made a start degraded.</summary>
    public string Because { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether every non-admin is locked.</summary>
    public bool EveryNonAdmin { get; set; }

    /// <summary>Gets or sets the users locked (a name the server no longer has is empty).</summary>
    public IReadOnlyList<AccessRolesUserRefDto> Users { get; set; } = [];

    /// <summary>Gets or sets the file the users were read from.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets when it began.</summary>
    public DateTime SinceUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether an earlier start's marker keeps the users locked.</summary>
    public bool FromEarlierStart { get; set; }

    /// <summary>Gets or sets a value indicating whether this was a dry run.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets the dry run's token, which the confirmation sends as <c>ifPreview</c>.</summary>
    public string? PreviewToken { get; set; }

    /// <summary>Gets or sets a value indicating whether the call ended it.</summary>
    public bool Resolved { get; set; }
}
