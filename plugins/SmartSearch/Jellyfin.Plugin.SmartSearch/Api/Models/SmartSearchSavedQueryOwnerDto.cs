using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>The body of <c>POST /SmartSearch/Admin/Queries/{id}/Owner</c>.</summary>
public sealed class SmartSearchSavedQueryOwnerDto
{
    /// <summary>Gets or sets the id of the user who gets the query.</summary>
    public Guid UserId { get; set; }
}
