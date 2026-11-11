using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// A saved query with its definition.
/// </summary>
public sealed class SmartSearchSavedQueryDto
{
    /// <summary>Gets or sets the query's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description, empty for none.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the owner's id: administrators only; <c>null</c> for everyone else (<c>IsOwner</c> tells their own queries).</summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>Gets or sets the owner's user name, or "Deleted user".</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the owner was deleted.</summary>
    public bool OwnerDeleted { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller owns it.</summary>
    public bool IsOwner { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller may change or delete it (the owner, or an administrator).</summary>
    public bool CanEdit { get; set; }

    /// <summary>Gets or sets a value indicating whether every user may open and run it, each as themselves.</summary>
    public bool Shared { get; set; }

    /// <summary>
    /// Gets or sets the definition as stored (a <see cref="SmartSearchQueryDefinition"/> object): as this version wrote it,
    /// or, for one it can't read, as a newer version did.
    /// </summary>
    public JsonElement Query { get; set; }

    /// <summary>Gets or sets the query's canonical hash (<c>sha256:…</c>), or <c>null</c> when it isn't valid.</summary>
    public string? QueryHash { get; set; }

    /// <summary>Gets or sets a value indicating whether this version of Smart Search can read it.</summary>
    public bool Supported { get; set; }

    /// <summary>Gets or sets a value indicating whether it has no errors.</summary>
    public bool Valid { get; set; }

    /// <summary>Gets or sets its errors, each with its path inside the query; empty for a valid query.</summary>
    public IReadOnlyList<SmartSearchValidationErrorDto> Errors { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a rule reads the user's own data (watch state): each user who runs it gets
    /// their own.
    /// </summary>
    public bool UsesPerUserFields { get; set; }

    /// <summary>Gets or sets a value indicating whether a rule uses an administrator-only field (such a query can't be shared).</summary>
    public bool UsesAdminOnlyFields { get; set; }

    /// <summary>Gets or sets when it was created.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets when it was last changed: the value a change sends back as <c>IfUpdatedAt</c>.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets when its owner last ran it (written at most once an hour); <c>null</c> when never, and for callers who
    /// can't change it.
    /// </summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>Gets or sets how many auto-collections use it: for its owner and administrators; 0 for others.</summary>
    public int AutoCollectionCount { get; set; }

    /// <summary>Gets or sets, after a change, the auto-collections it affects (each with its messages); empty otherwise.</summary>
    public IReadOnlyList<SmartSearchAutoCollectionRefDto> AffectedAutoCollections { get; set; } = [];
}
