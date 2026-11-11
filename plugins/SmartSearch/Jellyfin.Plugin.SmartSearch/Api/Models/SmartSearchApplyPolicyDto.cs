using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>The body of <c>POST /SmartSearch/Admin/Users/ApplyPolicy</c>.</summary>
public sealed class SmartSearchApplyPolicyDto
{
    /// <summary>Gets or sets the ids of users that are gone, 1 to 1,000.</summary>
    public IReadOnlyList<Guid> UserIds { get; set; } = [];
}
