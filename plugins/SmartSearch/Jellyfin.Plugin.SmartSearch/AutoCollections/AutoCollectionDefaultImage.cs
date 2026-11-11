using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// The default poster of an auto-collection's collection (<c>AutoCollection.png</c>, embedded): the server makes no
/// image for a locked collection and only remote ones for an unlocked one, so a new collection would otherwise show a
/// blank card. Given once per collection, when a run or a create reaches it and it has no poster, and recorded in the
/// definition's state (<see cref="AutoCollectionDefaultImageRecord"/>), so a poster removed or replaced later is left as
/// it is, and a collection that had one never gets this one. The <c>AutoCollectionDefaultImage</c> setting turns it off,
/// which removes nothing.
/// </summary>
internal sealed class AutoCollectionDefaultImage
{
    /// <summary>The embedded poster's resource name.</summary>
    public const string ResourceName = "AutoCollections.AutoCollection.png";

    private readonly ICollectionImages _images;
    private readonly ILogger _logger;
    private readonly Func<Stream?> _open;

    /// <summary>
    /// Initializes a new instance of the <see cref="AutoCollectionDefaultImage"/> class.
    /// </summary>
    /// <param name="images">The server's image calls.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="open">Opens the poster; the embedded one by default.</param>
    public AutoCollectionDefaultImage(ICollectionImages images, ILogger logger, Func<Stream?>? open = null)
    {
        _images = images;
        _logger = logger;
        _open = open ?? OpenEmbedded;
    }

    /// <summary>Opens the embedded poster.</summary>
    /// <returns>The poster, or <c>null</c> when the assembly lacks it.</returns>
    public static Stream? OpenEmbedded() => typeof(AutoCollectionDefaultImage).Assembly.GetManifestResourceStream(ResourceName);

    /// <summary>
    /// Gives the collection the poster when the setting is on, nothing was decided for this collection yet, and it has
    /// no poster. Never throws: a failed save is logged and tried again at the next run.
    /// </summary>
    /// <param name="box">The collection the run reached.</param>
    /// <param name="recorded">What the state remembers, or <c>null</c>.</param>
    /// <param name="enabled">The setting.</param>
    /// <param name="now">The run's time.</param>
    /// <returns>What the state remembers now.</returns>
    public async Task<AutoCollectionDefaultImageRecord?> ApplyAsync(BoxSet box, AutoCollectionDefaultImageRecord? recorded, bool enabled, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (!enabled || (recorded is not null && recorded.CollectionId.Equals(box.Id)))
        {
            return recorded;
        }

        try
        {
            if (_images.HasPrimaryImage(box))
            {
                return new AutoCollectionDefaultImageRecord { CollectionId = box.Id, Outcome = AutoCollectionDefaultImageRecord.HadImage, Utc = now };
            }

            var source = _open();
            if (source is null)
            {
                _logger.LogWarning("The default auto-collection poster isn't in the plugin's assembly; collections get none.");
                return recorded;
            }

            // Never cancelled half way: the run's write is done by now, and a save cut short could leave a file the item doesn't name.
            using (source)
            {
                await _images.SavePrimaryAsync(box, source, "image/png", CancellationToken.None).ConfigureAwait(false);
            }

            _logger.LogInformation("The collection {Id} of an auto-collection got the default poster.", box.Id);
            return new AutoCollectionDefaultImageRecord { CollectionId = box.Id, Outcome = AutoCollectionDefaultImageRecord.Applied, Utc = now };
        }
#pragma warning disable CA1031 // The poster is a nicety: its failure never fails the run, and the next run tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "The collection {Id} of an auto-collection couldn't get the default poster; the next run tries again.", box.Id);
            return recorded;
        }
    }
}
