using Jellyfin.Plugin.SmartSearch.Api.Models;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// The field catalogue as the rule builder reads it.
/// </summary>
public interface ISmartSearchFields
{
    /// <summary>Gets the catalogue for a caller.</summary>
    /// <param name="isAdmin">
    /// Whether the caller passes Jellyfin's <c>RequiresElevation</c> policy on this request (<c>CurrentUser.IsElevatedAsync</c>);
    /// administrator-only fields are left out otherwise.
    /// </param>
    /// <returns>The catalogue.</returns>
    SmartSearchFieldCatalogDto GetCatalog(bool isAdmin);

    /// <summary>Gets what a caller may do with auto-collections.</summary>
    /// <param name="isAdmin">Whether the caller passes Jellyfin's <c>RequiresElevation</c> policy on this request.</param>
    /// <param name="managesCollections">Whether the caller is an administrator or a user allowed to manage collections.</param>
    /// <returns>The access.</returns>
    SmartSearchAutoCollectionAccessDto GetAutoCollectionAccess(bool isAdmin, bool managesCollections);
}
