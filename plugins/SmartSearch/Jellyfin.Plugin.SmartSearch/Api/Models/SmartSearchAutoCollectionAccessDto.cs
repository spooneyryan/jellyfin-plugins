namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>What the caller may do with auto-collections, so the page shows their tab only to those who may use it.</summary>
public sealed class SmartSearchAutoCollectionAccessDto
{
    /// <summary>Gets or sets a value indicating whether auto-collections are on (the setting <c>EnableAutoCollections</c>).</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the caller manages collections: an administrator, or a user allowed to manage collections.</summary>
    public bool CanManage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the caller passes the server's administrator policy on this request: they
    /// may choose another user's watch state and see the full reason of a failed update.
    /// </summary>
    public bool IsAdmin { get; set; }
}
