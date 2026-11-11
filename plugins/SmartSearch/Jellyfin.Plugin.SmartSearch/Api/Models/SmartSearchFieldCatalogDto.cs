using System.Collections.Generic;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.SmartSearch.Api.Models;

/// <summary>
/// The fields, sort keys and item kinds a query may use, for the rule builder,
/// which has no field list of its own. Filtered for the caller: administrator-only fields are left out
/// for everyone else.
/// </summary>
public sealed class SmartSearchFieldCatalogDto
{
    /// <summary>
    /// Gets or sets the registry's version, which changes whenever a field, operator, option or sort key changes.
    /// </summary>
    public int RegistryVersion { get; set; }

    /// <summary>Gets or sets the fields, in the rule builder's order.</summary>
    public IReadOnlyList<SmartSearchFieldDto> Fields { get; set; } = [];

    /// <summary>Gets or sets the sort keys.</summary>
    public IReadOnlyList<string> SortKeys { get; set; } = [];

    /// <summary>Gets or sets the item kinds a scope may name.</summary>
    public IReadOnlyList<BaseItemKind> ItemKinds { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether users may share their saved queries (the setting <c>AllowQuerySharing</c>),
    /// so the page offers sharing only where the server allows it.
    /// </summary>
    public bool AllowQuerySharing { get; set; }

    /// <summary>
    /// Gets or sets what the caller may do with auto-collections: given by <c>GET /SmartSearch/Fields</c> for the signed-in
    /// user, left out of the catalogue itself.
    /// </summary>
    public SmartSearchAutoCollectionAccessDto? AutoCollections { get; set; }
}
