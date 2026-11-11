using System;
using System.Linq;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// <see cref="ItemAccess"/>: one id through <c>GetItemById&lt;BaseItem&gt;(id, user)</c>,
/// with one 404 for an item the user can't open and one that isn't rateable; a list through the kit's
/// <c>LibraryAccess.EligibleIds</c> (one <c>GetItemList</c> with the user's libraries), with a per-item check for the ids it
/// drops, so an owned item the user can open isn't lost and nothing the user can't open is let in.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ItemAccessTests : IDisposable
{
    private readonly ServicesFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void GetRateable_IsTheItem_OrOne404ForInvisibleUnrateableAndMissing()
    {
        var movie = _fx.AddMovie(_fx.Movies, "Visible");
        var hidden = _fx.AddMovie(_fx.Movies2, "Hidden from viewer2");
        var boxSet = _fx.Add(_fx.Movies, new BoxSet { Name = "Box" });
        var settings = RatingsSettings.From(new PluginConfiguration());

        Assert.Same(movie, _fx.ItemAccess.GetRateable(movie.Id, _fx.Viewer2, settings));
        Assert.Same(hidden, _fx.ItemAccess.GetRateable(hidden.Id, _fx.Viewer, settings));
        var messages = new[] { hidden.Id, boxSet.Id, Guid.NewGuid(), Guid.Empty }
            .Select(id => Assert.Throws<PluginException>(() => _fx.ItemAccess.GetRateable(id, _fx.Viewer2, settings)))
            .Select(e => (e.Kind, e.Message))
            .Distinct()
            .ToList();
        Assert.Equal([(PluginErrorKind.NotFound, ItemAccess.NotFoundMessage)], messages);

        // RateableItemTypes decides: with box sets on, the same box set is rateable.
        Assert.Same(boxSet, _fx.ItemAccess.GetRateable(boxSet.Id, _fx.Viewer, RatingsSettings.From(new PluginConfiguration { RateableItemTypes = ["Movie", "BoxSet"] })));
        Assert.Throws<PluginException>(() => _fx.ItemAccess.GetRateable(movie.Id, _fx.Viewer, RatingsSettings.From(new PluginConfiguration { RateableItemTypes = ["Episode"] })));
    }

    [Fact]
    public void GetWritable_IsGetRateable_AndAlsoThe404ForACacheGhost_WhichGetRateableStillAnswers()
    {
        // An album of a deleted artist folder, which 10.11.5's item cache keeps with its parent link.
        var artist = _fx.Add(_fx.Movies, new MusicArtist { Name = "Artist A" });
        var album = _fx.Add(_fx.Movies, new MusicAlbum { Name = "Album A", ParentId = artist.Id });
        var settings = RatingsSettings.From(new PluginConfiguration());
        Assert.Same(album, _fx.ItemAccess.GetWritable(album.Id, _fx.Viewer, settings));
        Assert.True(_fx.ItemAccess.IsInLibrary(album));

        _fx.Library.Remove(artist, raiseEvent: false, cacheKeepsDescendants: true);

        Assert.Same(album, _fx.ItemAccess.GetRateable(album.Id, _fx.Viewer, settings));
        Assert.False(_fx.ItemAccess.IsInLibrary(album));
        var ghost = Assert.Throws<PluginException>(() => _fx.ItemAccess.GetWritable(album.Id, _fx.Viewer, settings));
        Assert.Equal((PluginErrorKind.NotFound, ItemAccess.NotFoundMessage), (ghost.Kind, ghost.Message));

        // The other answers are GetRateable's: the same 404 for what the member can't open or rate.
        var hidden = _fx.AddMovie(_fx.Movies2, "Hidden from viewer2");
        Assert.Equal(ItemAccess.NotFoundMessage, Assert.Throws<PluginException>(() => _fx.ItemAccess.GetWritable(hidden.Id, _fx.Viewer2, settings)).Message);
        Assert.Equal(ItemAccess.NotFoundMessage, Assert.Throws<PluginException>(() => _fx.ItemAccess.GetWritable(Guid.NewGuid(), _fx.Viewer, settings)).Message);
    }

    [Fact]
    public void VisibleIds_IsTheKitsBatch_WithAPerItemCheckForWhatItDrops()
    {
        var a = _fx.AddMovie(_fx.Movies, "A");
        var b = _fx.AddMovie(_fx.Movies2, "B");

        // An owned item (a local alternate version): no top parent, so the batch leaves it out; core's own check lets it in.
        var owned = _fx.Library.Add(new Movie { Name = "A (alternate)", OwnerId = a.Id });
        var unknown = Guid.NewGuid();

        Assert.Equal(new[] { a.Id, b.Id, owned.Id }.Order(), _fx.ItemAccess.VisibleIds(_fx.Viewer, [a.Id, b.Id, owned.Id, unknown, Guid.Empty, a.Id]).Order());
        Assert.Equal(new[] { a.Id, owned.Id }.Order(), _fx.ItemAccess.VisibleIds(_fx.Viewer2, [a.Id, b.Id, owned.Id]).Order());
        Assert.Empty(_fx.ItemAccess.VisibleIds(_fx.Viewer, []));

        // One batch query with the viewer's libraries, then per item only for what it dropped.
        var batch = _fx.Library.Queries.Last(q => q.ItemIds.Length > 0);
        Assert.Same(_fx.Viewer2, batch.User);
        Assert.Equal(_fx.Movies.PhysicalFolderIds, batch.TopParentIds);
    }

    [Fact]
    public void KindOf_IsNullForATypeTheEnumDoesntName()
    {
        Assert.Equal(Jellyfin.Data.Enums.BaseItemKind.Movie, ItemAccess.KindOf(new Movie()));
        Assert.False(ItemAccess.IsRateable(new UnnamedItem(), RatingsSettings.From(new PluginConfiguration())));
    }

    private sealed class UnnamedItem : BaseItem
    {
    }
}
