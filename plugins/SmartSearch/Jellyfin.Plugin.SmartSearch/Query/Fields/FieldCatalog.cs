using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Jellyfin.Plugin.SmartSearch.Query.Fields;

/// <summary>
/// An <see cref="IFieldCatalog"/> over a fixed list of fields and sort keys, with the lookups by id, alias and key.
/// </summary>
internal class FieldCatalog : IFieldCatalog
{
    private readonly Dictionary<string, FieldDescriptor> _byName;
    private readonly Dictionary<string, string> _sortKeys;

    /// <summary>
    /// Initializes a new instance of the <see cref="FieldCatalog"/> class.
    /// </summary>
    /// <param name="fields">The fields.</param>
    /// <param name="sortKeys">The sort keys.</param>
    /// <exception cref="ArgumentException">Two fields share an id or alias, or two sort keys differ only in case.</exception>
    public FieldCatalog(IEnumerable<FieldDescriptor> fields, IEnumerable<string> sortKeys)
    {
        Fields = new ReadOnlyCollection<FieldDescriptor>(fields.ToList());
        SortKeys = new ReadOnlyCollection<string>(sortKeys.ToList());
        _byName = new Dictionary<string, FieldDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in Fields)
        {
            foreach (var name in field.Aliases.Prepend(field.Id))
            {
                if (!_byName.TryAdd(name, field))
                {
                    throw new ArgumentException("Two fields share the id or alias " + name + ".", nameof(fields));
                }
            }
        }

        _sortKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SortKeys)
        {
            if (!_sortKeys.TryAdd(key, key))
            {
                throw new ArgumentException("The sort key " + key + " repeats.", nameof(sortKeys));
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<FieldDescriptor> Fields { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> SortKeys { get; }

    /// <inheritdoc />
    public FieldDescriptor? Find(string idOrAlias) => _byName.GetValueOrDefault(idOrAlias);

    /// <inheritdoc />
    public string? FindSortKey(string key) => _sortKeys.GetValueOrDefault(key);
}
