using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SmartSearch.Api.Models;
using Jellyfin.Plugin.SmartSearch.Query;
using Jellyfin.Plugin.SmartSearch.Query.Fields;

namespace Jellyfin.Plugin.SmartSearch.Services;

/// <summary>
/// Builds <see cref="SmartSearchFieldCatalogDto"/> from the field registry: every field, in the registry's
/// order, with administrator-only fields only for administrators, and without the operators that wait for a later
/// version (<see cref="QueryOperators.Dormant"/>: <c>Matches</c>).
/// </summary>
/// <remarks>
/// The item kinds are the ones the <c>AllowedItemKinds</c> setting allows, and sharing the <c>AllowQuerySharing</c>
/// setting, read on each call.
/// </remarks>
internal sealed class SmartSearchFields : ISmartSearchFields
{
    private readonly IFieldCatalog _catalog;
    private readonly Func<IReadOnlyList<BaseItemKind>> _allowedKinds;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _allowSharing;
    private readonly Func<bool> _autoCollectionsEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartSearchFields"/> class.
    /// </summary>
    /// <param name="catalog">The field registry.</param>
    /// <param name="allowedKinds">The kinds a query may name (the setting); every kind when <c>null</c>.</param>
    /// <param name="enabled">The setting <c>EnableSearchPage</c>; on when <c>null</c>.</param>
    /// <param name="allowSharing">The setting <c>AllowQuerySharing</c>; on when <c>null</c>.</param>
    /// <param name="autoCollectionsEnabled">The setting <c>EnableAutoCollections</c>; on when <c>null</c>.</param>
    public SmartSearchFields(IFieldCatalog catalog, Func<IReadOnlyList<BaseItemKind>>? allowedKinds = null, Func<bool>? enabled = null, Func<bool>? allowSharing = null, Func<bool>? autoCollectionsEnabled = null)
    {
        _autoCollectionsEnabled = autoCollectionsEnabled ?? (() => true);
        _catalog = catalog;
        _allowedKinds = allowedKinds ?? (() => QueryLimits.ItemKindOrder);
        _enabled = enabled ?? (() => true);
        _allowSharing = allowSharing ?? (() => true);
    }

    /// <inheritdoc />
    public SmartSearchFieldCatalogDto GetCatalog(bool isAdmin)
    {
        SearchPageGate.Ensure(_enabled());
        return Catalog(isAdmin);
    }

    /// <inheritdoc />
    public SmartSearchAutoCollectionAccessDto GetAutoCollectionAccess(bool isAdmin, bool managesCollections) => new()
    {
        Enabled = _autoCollectionsEnabled(),
        CanManage = isAdmin || managesCollections,
        IsAdmin = isAdmin
    };

    private SmartSearchFieldCatalogDto Catalog(bool isAdmin) => new()
    {
        RegistryVersion = FieldRegistry.Version,
        Fields = [.. _catalog.Fields.Where(f => isAdmin || !f.AdminOnly).Select(ToDto)],
        SortKeys = [.. _catalog.SortKeys],
        ItemKinds = [.. _allowedKinds()],
        AllowQuerySharing = _allowSharing()
    };

    private static SmartSearchFieldDto ToDto(FieldDescriptor field) => new()
    {
        Id = field.Id,
        Label = field.Label,
        Group = field.Group,
        Type = field.Type.ToString(),
        Kinds = [.. field.Kinds],
        Operators = [.. field.Operators.Where(o => !QueryOperators.Dormant.Contains(o)).Select(o => o.ToString())],
        Options = [.. field.Options.Select(o => new SmartSearchFieldOptionDto { Name = o.Option.ToString(), Required = o.Required, Choices = o.Choices?.ToArray() })],
        ElementKind = field.ElementKind.ToString(),
        Choices = field.Choices?.ToArray(),
        Min = field.Min,
        Max = field.Max,
        Integer = field.Integer,
        MaxSpan = field.MaxSpan,
        ValueSource = field.ValueSource,
        Cost = field.Cost.ToString(),
        PerUser = field.PerUser,
        AdminOnly = field.AdminOnly,
        Available = field.Available,
        Note = field.Note
    };
}
