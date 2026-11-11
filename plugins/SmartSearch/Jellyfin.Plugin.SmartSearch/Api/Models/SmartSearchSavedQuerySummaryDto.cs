using System;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// A saved query in a list: who owns it, whether it is shared, and whether it can be run.
/// </summary>
public sealed class SmartSearchSavedQuerySummaryDto
{
    /// <summary>Gets or sets the query's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the description, empty for none.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the owner's id: administrators only; <c>null</c> for everyone else (<c>IsOwner</c> tells their own
    /// queries).
    /// </summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>Gets or sets the owner's user name, or "Deleted user" for a query whose owner was deleted.</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the owner was deleted (the query is kept until an administrator gives it to
    /// another user or deletes it).
    /// </summary>
    public bool OwnerDeleted { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller owns it.</summary>
    public bool IsOwner { get; set; }

    /// <summary>Gets or sets a value indicating whether every user may open and run it, each as themselves.</summary>
    public bool Shared { get; set; }

    /// <summary>Gets or sets when it was last changed.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this version of Smart Search can read it. One written by a newer version
    /// can't be run or changed; it can be deleted, exported or duplicated.
    /// </summary>
    public bool Supported { get; set; }

    /// <summary>Gets or sets a value indicating whether it has no errors (an imported query may have some).</summary>
    public bool Valid { get; set; }

    /// <summary>Gets or sets how many auto-collections use it: for its owner and administrators; 0 for others.</summary>
    public int AutoCollectionCount { get; set; }
}
