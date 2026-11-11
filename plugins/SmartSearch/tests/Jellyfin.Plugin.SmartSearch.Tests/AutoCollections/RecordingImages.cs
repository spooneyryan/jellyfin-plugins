using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SmartSearch.AutoCollections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.Tests.AutoCollections;

/// <summary>The server's image calls for a collection's poster, recorded: a save sets the collection's Primary image.</summary>
internal sealed class RecordingImages : ICollectionImages
{
    /// <summary>Gets each save: the collection, the bytes and the media type.</summary>
    public List<(Guid CollectionId, byte[] Bytes, string MimeType)> Saved { get; } = [];

    /// <summary>Gets or sets which collections' saves fail (none by default).</summary>
    public Func<BoxSet, bool> Fails { get; set; } = _ => false;

    public bool HasPrimaryImage(BoxSet box) => box.HasImage(ImageType.Primary, 0);

    public async Task SavePrimaryAsync(BoxSet box, Stream source, string mimeType, CancellationToken cancellationToken)
    {
        if (Fails(box))
        {
            throw new IOException("the collection's folder is read-only");
        }

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        Saved.Add((box.Id, buffer.ToArray(), mimeType));
        box.SetImage(new ItemImageInfo { Type = ImageType.Primary, Path = Path.Combine(box.Path ?? "collection", "poster.png") }, 0);
    }
}
