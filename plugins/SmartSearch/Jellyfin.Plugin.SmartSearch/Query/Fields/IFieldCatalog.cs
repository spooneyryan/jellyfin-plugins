using System.Collections.Generic;

namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// The fields and sort keys a query may name. The validator and the canonicalizer read it;
/// <c>FieldRegistry</c> implements it.
/// </summary>
internal interface IFieldCatalog
{
    /// <summary>Gets every field, in the rule builder's order.</summary>
    IReadOnlyList<FieldDescriptor> Fields { get; }

    /// <summary>Gets every sort key, in its canonical spelling.</summary>
    IReadOnlyList<string> SortKeys { get; }

    /// <summary>Finds a field by its id or one of its aliases, ignoring case.</summary>
    /// <param name="idOrAlias">The id or alias.</param>
    /// <returns>The field, or <c>null</c> when there is none.</returns>
    FieldDescriptor? Find(string idOrAlias);

    /// <summary>Finds a sort key, ignoring case.</summary>
    /// <param name="key">The key.</param>
    /// <returns>Its canonical spelling, or <c>null</c> when there is none.</returns>
    string? FindSortKey(string key);
}
