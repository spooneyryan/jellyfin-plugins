using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Finds a definition's collection: by the stored id, where it must carry the definition's mark (the <c>SmartSearch</c>
/// provider id, the definition's id in <c>N</c> format), else by the mark alone.
/// </summary>
/// <remarks>
/// <para>A collection at the stored id without the mark isn't ours any more: the server creates a new collection on the
/// folder its name maps to and takes over the row there, with the user's name and members and without the mark. Writing
/// it would take over the user's collection, so it is reported and left alone.</para>
/// <para>The mark lookup is the server's provider-id query, which is exact and case-sensitive in key and value; its
/// dictionary must be assigned, because the query leaves it null (a collection initializer on it throws). Several hits:
/// the oldest is used, the others listed.</para>
/// </remarks>
internal sealed class CollectionLocator
{
    private readonly ILibraryManager _library;
    private readonly LibraryAccess _access;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionLocator"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="access">The kit's library access.</param>
    public CollectionLocator(ILibraryManager library, LibraryAccess access)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(access);
        _library = library;
        _access = access;
    }

    /// <summary>Tells whether a collection carries a definition's mark.</summary>
    /// <param name="box">The collection.</param>
    /// <param name="definitionId">The definition's id.</param>
    /// <returns>Whether it does.</returns>
    public static bool HasTether(BaseItem box, Guid definitionId)
        => box.ProviderIds is { } ids
            && ids.TryGetValue(AutoCollectionDefinition.TetherKey, out var value)
            && string.Equals(value, AutoCollectionDefinition.FormatTether(definitionId), StringComparison.Ordinal);

    /// <summary>Builds the query that finds collections by a definition's mark.</summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <returns>The query.</returns>
    public static InternalItemsQuery TetherQuery(Guid definitionId) => new()
    {
        IncludeItemTypes = [BaseItemKind.BoxSet],
        HasAnyProviderId = new Dictionary<string, string> { [AutoCollectionDefinition.TetherKey] = AutoCollectionDefinition.FormatTether(definitionId) },
        Recursive = true,
        DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds] }
    };

    /// <summary>Finds a definition's collection.</summary>
    /// <param name="definition">The definition.</param>
    /// <returns>Where it is.</returns>
    public CollectionLocation Locate(AutoCollectionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.CollectionId is { } stored && _access.GetItem<BoxSet>(stored, null) is { } box)
        {
            return HasTether(box, definition.Id)
                ? new CollectionLocation(CollectionLocationKind.Found, box, [])
                : new CollectionLocation(CollectionLocationKind.TetherMissing, box, []);
        }

        var hits = FindByTether(definition.Id);
        if (hits.Count == 0)
        {
            return new CollectionLocation(definition.CollectionId is null ? CollectionLocationKind.NotCreated : CollectionLocationKind.Orphaned, null, []);
        }

        var oldest = hits.OrderBy(b => b.DateCreated).ThenBy(b => b.Id).First();
        return new CollectionLocation(CollectionLocationKind.Rebound, oldest, hits.Where(b => !b.Id.Equals(oldest.Id)).Select(b => b.Id).ToList());
    }

    /// <summary>Lists every collection carrying a mark, by its mark (one library query; for Diagnostics).</summary>
    /// <returns>Mark to collections.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<BoxSet>> MarkedCollections()
        => _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.BoxSet],
                Recursive = true,
                DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds] }
            })
            .OfType<BoxSet>()
            .Select(b => (Box: b, Mark: b.ProviderIds is { } ids && ids.TryGetValue(AutoCollectionDefinition.TetherKey, out var value) ? value : null))
            .Where(x => !string.IsNullOrEmpty(x.Mark))
            .GroupBy(x => x.Mark!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BoxSet>)g.Select(x => x.Box).ToList(), StringComparer.Ordinal);

    /// <summary>Finds the collections carrying a definition's mark.</summary>
    /// <param name="definitionId">The definition's id.</param>
    /// <returns>The collections.</returns>
    public IReadOnlyList<BoxSet> FindByTether(Guid definitionId)
        => _library.GetItemList(TetherQuery(definitionId)).OfType<BoxSet>().Where(b => HasTether(b, definitionId)).ToList();
}
