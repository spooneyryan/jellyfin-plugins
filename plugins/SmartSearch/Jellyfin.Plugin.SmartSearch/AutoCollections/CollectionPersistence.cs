using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The server's collection calls (see <see cref="ICollectionPersistence"/>).</summary>
internal sealed class CollectionPersistence : ICollectionPersistence
{
    private readonly ICollectionManager _collections;
    private readonly IProviderManager _providers;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryManager? _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionPersistence"/> class.
    /// </summary>
    /// <param name="collections">The collection manager.</param>
    /// <param name="providers">The provider manager (the refresh).</param>
    /// <param name="fileSystem">The file system (the refresh's directory service).</param>
    /// <param name="library">The library manager (a delete).</param>
    public CollectionPersistence(ICollectionManager collections, IProviderManager providers, IFileSystem fileSystem, ILibraryManager? library = null)
    {
        _library = library;
        _collections = collections;
        _providers = providers;
        _fileSystem = fileSystem;
    }

    /// <inheritdoc />
    public Task<BoxSet> CreateAsync(string name, bool isLocked, IReadOnlyDictionary<string, string> providerIds, IReadOnlyList<Guid> itemIds)
    {
        var options = new CollectionCreationOptions
        {
            Name = name,
            IsLocked = isLocked,
            ItemIdList = itemIds.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).ToArray()
        };
        foreach (var (key, value) in providerIds)
        {
            options.ProviderIds[key] = value;
        }

        return _collections.CreateCollectionAsync(options);
    }

    /// <inheritdoc />
    public Task AddAsync(Guid collectionId, IReadOnlyList<Guid> itemIds) => _collections.AddToCollectionAsync(collectionId, itemIds);

    /// <inheritdoc />
    public Task RemoveAsync(Guid collectionId, IReadOnlyList<Guid> itemIds) => _collections.RemoveFromCollectionAsync(collectionId, itemIds);

    /// <inheritdoc />
    public void UpdateRating(BoxSet box, IReadOnlyList<BaseItem> members) => box.UpdateRatingToItems(members);

    /// <inheritdoc />
    public Task SaveAsync(BoxSet box, CancellationToken cancellationToken) => box.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken);

    /// <inheritdoc />
    public void QueueRefresh(BoxSet box) => _providers.QueueRefresh(box.Id, RefreshOptions(_fileSystem), RefreshPriority.High);

    /// <summary>
    /// The options of the refresh after a write: a forced save, which recomputes the libraries the collection shows in,
    /// and no metadata or image providers. A default refresh of an unlocked collection could run the online providers
    /// (on its first refresh, when the library's refresh interval is due, or when a provider reports a change) and fetch
    /// metadata and images for it; with both modes off the server only validates the local images, saves, and checks the
    /// collection folder's own children.
    /// </summary>
    /// <param name="fileSystem">The file system (the refresh's directory service).</param>
    /// <returns>The options.</returns>
    internal static MetadataRefreshOptions RefreshOptions(IFileSystem fileSystem) => new(new DirectoryService(fileSystem))
    {
        ForceSave = true,
        MetadataRefreshMode = MetadataRefreshMode.None,
        ImageRefreshMode = MetadataRefreshMode.None
    };

    /// <inheritdoc />
    public BoxSet? FindDeletable(Guid id, User user)
        => RequireLibrary().GetItemById<BoxSet>(id, user) is { } box && box.CanDelete(user) ? box : null;

    /// <inheritdoc />
    public Task DeleteAsync(BoxSet box)
    {
        RequireLibrary().DeleteItem(box, new DeleteOptions { DeleteFileLocation = true }, true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public BoxSet? Reload(Guid collectionId) => RequireLibrary().GetItemById<BoxSet>(collectionId);

    private ILibraryManager RequireLibrary() => _library ?? throw new InvalidOperationException("No library manager was given.");
}
