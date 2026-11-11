using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Writes a collection by replacing its links: one save (the database row and <c>collection.xml</c>) and one refresh per
/// changed collection, nothing for an unchanged one.
/// </summary>
/// <remarks>
/// <para><b>Links.</b> The desired members in order; a member already linked keeps its link (its path, with the cached id
/// set to the member, which also clears an empty id the server cached after a failed lookup), a new one gets a link made
/// from the item. Dead and duplicate links aren't carried over, so the write prunes them (dead ones stay while the guard holds removals).</para>
/// <para><b>Presentation.</b> The display order (the query's order, the release date or the name; always the release date
/// on an unlocked collection, which the server resets at every refresh), the rating (the members' least restrictive: the
/// lock stops the server from working it out), the lock, the name, the overview when managed, and the name locked against
/// metadata on an unlocked collection. The mark is already there: an unmarked collection never reaches the writer.</para>
/// <para><b>The refresh</b> (forced save) recomputes the libraries the collection shows in, which the save leaves stale.
/// It makes no image: the server makes none for a locked collection, and an unlocked one gets only remote images.</para>
/// <para><b>A failed save</b> changes nothing: the collection is the server's cached item, so its values are put back
/// when the save throws, and the next run sees the difference again and writes it. A cancelled write stops before it
/// changes anything.</para>
/// </remarks>
internal sealed class DirectReplaceWriter
{
    /// <summary>The members loaded per query.</summary>
    public const int LoadBatch = 500;

    private readonly ILibraryManager _library;
    private readonly ICollectionPersistence _persistence;
    private readonly CollectionLocator _locator;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DirectReplaceWriter"/> class.
    /// </summary>
    /// <param name="library">The library manager (the members, by id).</param>
    /// <param name="persistence">The server's collection calls.</param>
    /// <param name="locator">The locator (a create that failed half-way).</param>
    /// <param name="logger">The logger.</param>
    public DirectReplaceWriter(ILibraryManager library, ICollectionPersistence persistence, CollectionLocator locator, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(logger);
        _library = library;
        _persistence = persistence;
        _locator = locator;
        _logger = logger;
    }

    /// <summary>Replaces a collection's links and presentation, then saves it and queues one refresh.</summary>
    /// <param name="box">The collection (found, marked).</param>
    /// <param name="links">Its resolved live links.</param>
    /// <param name="desired">The desired members, in order.</param>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">Cancels the write before the save.</param>
    /// <param name="keepDeadLinks">Whether the dead links stay (after the members): the guard holds their removal.</param>
    /// <param name="accept">Decides, given the desired members that can't be loaded any more (the write would drop
    /// them), whether to write; without, it always writes.</param>
    /// <returns>The members written (desired members that still exist), or <c>null</c> when <paramref name="accept"/> refused.</returns>
    public async Task<IReadOnlyList<Guid>?> WriteAsync(BoxSet box, ResolvedLinks links, IReadOnlyList<Guid> desired, AutoCollectionDefinition definition, CancellationToken cancellationToken, bool keepDeadLinks = false, Func<IReadOnlyCollection<Guid>, bool>? accept = null)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(definition);
        var items = Load(desired, cancellationToken);
        var members = desired.Distinct().Where(items.ContainsKey).Select(id => items[id]).ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (accept is not null && !accept(Missing(desired, items)))
        {
            return null;
        }

        var before = BoxValues.Of(box);
        box.LinkedChildren = members.Select(item =>
        {
            if (links.Links.TryGetValue(item.Id, out var existing))
            {
                existing.ItemId = item.Id;
                return existing;
            }

            var created = LinkedChild.Create(item);
            created.ItemId = item.Id;
            return created;
        }).Concat(keepDeadLinks ? links.DeadLinks ?? [] : []).ToArray();

        _persistence.UpdateRating(box, members);
        ApplyPresentation(box, definition);
        await SaveOrRestoreAsync(box, before).ConfigureAwait(false);
        _persistence.QueueRefresh(box);
        return members.Select(m => m.Id).ToList();
    }

    /// <summary>
    /// Creates a definition's collection with its first members, then sets its presentation and saves it once (the create
    /// already queued a refresh). A create that fails may still have made the collection (the row is saved before the
    /// members are added): the mark is looked up before giving up, and a collection found is used.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="desired">The members, in order.</param>
    /// <param name="cancellationToken">Cancels the create, or the presentation after it.</param>
    /// <param name="beforeCreate">Runs just before the server creates the collection (the name checked again); it may
    /// throw to refuse.</param>
    /// <returns>The collection.</returns>
    public async Task<BoxSet> CreateAsync(AutoCollectionDefinition definition, IReadOnlyList<Guid> desired, CancellationToken cancellationToken, Func<Task>? beforeCreate = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(desired);

        // The server's add throws on any id it can't find: the ids are checked again just before.
        var items = Load(desired, cancellationToken);
        var ids = desired.Distinct().Where(items.ContainsKey).ToList();
        var providerIds = new Dictionary<string, string> { [AutoCollectionDefinition.TetherKey] = definition.Tether };
        cancellationToken.ThrowIfCancellationRequested();
        if (beforeCreate is not null)
        {
            await beforeCreate().ConfigureAwait(false);
        }

        BoxSet box;
        try
        {
            box = await _persistence.CreateAsync(definition.Name, definition.Presentation.Lock, providerIds, ids).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var made = _locator.FindByTether(definition.Id).OrderBy(b => b.DateCreated).FirstOrDefault();
            if (made is null)
            {
                throw;
            }

            _logger.LogWarning(ex, "Creating the collection of auto-collection {Id} failed, but the collection was made; the next update fixes its members.", definition.Id);
            box = made;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var before = BoxValues.Of(box);
        ApplyPresentation(box, definition);
        await SaveOrRestoreAsync(box, before).ConfigureAwait(false);
        return box;
    }

    /// <summary>Removes the definition's mark from its collection, which stays as an ordinary collection.</summary>
    /// <param name="box">The collection.</param>
    /// <param name="definitionId">The definition's id.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>Whether the mark was there (and the collection saved).</returns>
    public async Task<bool> DetachAsync(BoxSet box, Guid definitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (!CollectionLocator.HasTether(box, definitionId))
        {
            return false;
        }

        // A new dictionary, so the cached item's copy is replaced rather than changed under a reader.
        cancellationToken.ThrowIfCancellationRequested();
        var before = BoxValues.Of(box);
        box.ProviderIds = new Dictionary<string, string>(box.ProviderIds.Where(p => !string.Equals(p.Key, AutoCollectionDefinition.TetherKey, StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase);
        await SaveOrRestoreAsync(box, before).ConfigureAwait(false);
        return true;
    }

    /// <summary>Finds a collection the user may delete, as the server's delete endpoint does.</summary>
    /// <param name="id">The collection's id.</param>
    /// <param name="user">The user.</param>
    /// <returns>The collection, or <c>null</c>.</returns>
    public BoxSet? FindDeletable(Guid id, User user) => _persistence.FindDeletable(id, user);

    /// <summary>Deletes a collection (its folder too).</summary>
    /// <param name="box">The collection.</param>
    /// <returns>A task.</returns>
    public Task DeleteAsync(BoxSet box) => _persistence.DeleteAsync(box);

    /// <summary>Sets the rating and presentation and saves the collection, its links as they are (the other writer's).</summary>
    /// <param name="box">The collection.</param>
    /// <param name="members">Its members (the rating's).</param>
    /// <param name="definition">The definition.</param>
    /// <returns>A task.</returns>
    internal async Task SavePresentationAsync(BoxSet box, IReadOnlyList<BaseItem> members, AutoCollectionDefinition definition)
    {
        var before = BoxValues.Of(box);
        _persistence.UpdateRating(box, members);
        ApplyPresentation(box, definition);
        await SaveOrRestoreAsync(box, before).ConfigureAwait(false);
    }

    private async Task SaveOrRestoreAsync(BoxSet box, BoxValues before)
    {
        try
        {
            await _persistence.SaveAsync(box, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            before.Restore(box);
            throw;
        }
    }

    private static void ApplyPresentation(BoxSet box, AutoCollectionDefinition definition)
    {
        var presentation = definition.Presentation;
        box.DisplayOrder = CollectionDiff.DisplayOrderOf(presentation);
        box.IsLocked = presentation.Lock;
        box.Name = definition.Name;
        if (presentation.OverviewMode.Value == SmartSearchOverviewMode.Managed)
        {
            box.Overview = presentation.Overview;
        }

        var locked = box.LockedFields ?? [];
        if (!presentation.Lock && !locked.Contains(MetadataField.Name))
        {
            box.LockedFields = [.. locked, MetadataField.Name];
        }
    }

    /// <summary>The ids that weren't loaded.</summary>
    /// <param name="ids">The ids asked for.</param>
    /// <param name="items">The items loaded.</param>
    /// <returns>The ids missing.</returns>
    internal static IReadOnlyCollection<Guid> Missing(IReadOnlyList<Guid> ids, Dictionary<Guid, BaseItem> items)
        => ids.Where(id => !id.Equals(Guid.Empty) && !items.ContainsKey(id)).Distinct().ToList();

    /// <summary>Loads items by id, in batches of <see cref="LoadBatch"/> (the ones that exist).</summary>
    /// <param name="ids">The ids.</param>
    /// <param name="cancellationToken">Cancels between batches.</param>
    /// <returns>The items, by id.</returns>
    internal Dictionary<Guid, BaseItem> Load(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var items = new Dictionary<Guid, BaseItem>();
        foreach (var chunk in ids.Where(id => !id.Equals(Guid.Empty)).Distinct().Chunk(LoadBatch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new InternalItemsQuery
            {
                ItemIds = chunk,
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false }
            };
            foreach (var item in _library.GetItemList(query))
            {
                items.TryAdd(item.Id, item);
            }
        }

        return items;
    }

    // What a write changes on the cached collection, to put back when its save fails: the links (and the ids cached on
    // the links kept), the rating, the presentation and the provider ids.
    private sealed record BoxValues(
        LinkedChild[] Links,
        Guid?[] LinkIds,
        string? OfficialRating,
        string? DisplayOrder,
        bool IsLocked,
        string? Name,
        string? Overview,
        MetadataField[] LockedFields,
        Dictionary<string, string> ProviderIds)
    {
        public static BoxValues Of(BoxSet box)
        {
            var links = box.LinkedChildren ?? [];
            return new BoxValues(links, links.Select(l => l.ItemId).ToArray(), box.OfficialRating, box.DisplayOrder, box.IsLocked, box.Name, box.Overview, box.LockedFields, box.ProviderIds);
        }

        public void Restore(BoxSet box)
        {
            for (var i = 0; i < Links.Length; i++)
            {
                Links[i].ItemId = LinkIds[i];
            }

            box.LinkedChildren = Links;
            box.OfficialRating = OfficialRating;
            box.DisplayOrder = DisplayOrder;
            box.IsLocked = IsLocked;
            box.Name = Name;
            box.Overview = Overview;
            box.LockedFields = LockedFields;
            box.ProviderIds = ProviderIds;
        }
    }
}
