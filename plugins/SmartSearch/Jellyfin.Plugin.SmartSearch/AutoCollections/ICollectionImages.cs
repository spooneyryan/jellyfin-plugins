using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>A collection's poster, through the server's own image saving (behind a seam, so a test can record it).</summary>
internal interface ICollectionImages
{
    /// <summary>Whether the collection, as the server holds it now, has a poster (a Primary image).</summary>
    /// <param name="box">The collection.</param>
    /// <returns><c>true</c> when it has one.</returns>
    bool HasPrimaryImage(BoxSet box);

    /// <summary>
    /// Saves a poster for the collection as the server's image upload does: the image saver writes the file (into the
    /// collection's folder) and sets it, then the item is saved as an image update. No collection event is raised, so
    /// the edit listener sees nothing, and the members aren't touched.
    /// </summary>
    /// <param name="box">The collection.</param>
    /// <param name="source">The image.</param>
    /// <param name="mimeType">Its media type.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task.</returns>
    Task SavePrimaryAsync(BoxSet box, Stream source, string mimeType, CancellationToken cancellationToken);
}
