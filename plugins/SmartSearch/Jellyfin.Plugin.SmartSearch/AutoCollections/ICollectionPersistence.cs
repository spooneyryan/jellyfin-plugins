using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The server calls that write a collection, behind one seam so the writer's calls can be counted: one save and one
/// refresh per changed collection, none for an unchanged one.
/// </summary>
internal interface ICollectionPersistence
{
    /// <summary>Creates a collection (<c>ICollectionManager.CreateCollectionAsync</c>).</summary>
    /// <param name="name">Its name.</param>
    /// <param name="isLocked">Whether it is locked.</param>
    /// <param name="providerIds">Its provider ids (the mark).</param>
    /// <param name="itemIds">Its first members, in order.</param>
    /// <returns>The new collection.</returns>
    Task<BoxSet> CreateAsync(string name, bool isLocked, IReadOnlyDictionary<string, string> providerIds, IReadOnlyList<Guid> itemIds);

    /// <summary>Sets the collection's rating to its members' least restrictive one (<c>UpdateRatingToItems</c>).</summary>
    /// <param name="box">The collection.</param>
    /// <param name="members">Its members.</param>
    void UpdateRating(BoxSet box, IReadOnlyList<BaseItem> members);

    /// <summary>Saves the collection: its database row and its <c>collection.xml</c> (<c>UpdateToRepositoryAsync(MetadataEdit)</c>).</summary>
    /// <param name="box">The collection.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task SaveAsync(BoxSet box, CancellationToken cancellationToken);

    /// <summary>Queues a forced-save refresh, which recomputes the libraries the collection shows in.</summary>
    /// <param name="box">The collection.</param>
    void QueueRefresh(BoxSet box);

    /// <summary>
    /// Finds a collection the user may delete, with the server's delete endpoint's checks: the user can see it, and may
    /// delete it (<c>CanDelete</c>: an administrator or a user allowed to manage collections).
    /// </summary>
    /// <param name="id">The collection's id.</param>
    /// <param name="user">The user.</param>
    /// <returns>The collection, or <c>null</c>.</returns>
    BoxSet? FindDeletable(Guid id, User user);

    /// <summary>Deletes a collection as the server's delete endpoint does (its folder too).</summary>
    /// <param name="box">The collection.</param>
    /// <returns>A task.</returns>
    Task DeleteAsync(BoxSet box);

    /// <summary>Adds items to a collection (<c>ICollectionManager.AddToCollectionAsync</c>: it saves, refreshes and fires the event).</summary>
    /// <param name="collectionId">The collection's id.</param>
    /// <param name="itemIds">The items, each one that exists.</param>
    /// <returns>A task.</returns>
    Task AddAsync(Guid collectionId, IReadOnlyList<Guid> itemIds);

    /// <summary>Removes items from a collection (<c>ICollectionManager.RemoveFromCollectionAsync</c>: it saves, refreshes and fires the event, even for none).</summary>
    /// <param name="collectionId">The collection's id.</param>
    /// <param name="itemIds">The items.</param>
    /// <returns>A task.</returns>
    Task RemoveAsync(Guid collectionId, IReadOnlyList<Guid> itemIds);

    /// <summary>Reads a collection again as the server holds it now (its cached item, else the database's row).</summary>
    /// <param name="collectionId">The collection's id.</param>
    /// <returns>The collection, or <c>null</c> when there is none.</returns>
    BoxSet? Reload(Guid collectionId);
}
