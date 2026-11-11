using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>The server's image calls for a collection's poster (see <see cref="ICollectionImages"/>).</summary>
internal sealed class CollectionImages : ICollectionImages
{
    private readonly IProviderManager _providers;
    private readonly ILibraryManager _library;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionImages"/> class.
    /// </summary>
    /// <param name="providers">The provider manager (the image saver).</param>
    /// <param name="library">The library manager (the collection as the server holds it now).</param>
    public CollectionImages(IProviderManager providers, ILibraryManager library)
    {
        _providers = providers;
        _library = library;
    }

    /// <inheritdoc />
    public bool HasPrimaryImage(BoxSet box) => Current(box).HasImage(ImageType.Primary, 0);

    /// <inheritdoc />
    public async Task SavePrimaryAsync(BoxSet box, Stream source, string mimeType, CancellationToken cancellationToken)
    {
        // The server's own copy: a writer through the server's collection calls leaves the one it located stale.
        var current = Current(box);
        await _providers.SaveImage(current, source, mimeType, ImageType.Primary, null, cancellationToken).ConfigureAwait(false);
        await current.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, cancellationToken).ConfigureAwait(false);
    }

    private BoxSet Current(BoxSet box) => _library.GetItemById<BoxSet>(box.Id) ?? box;
}
