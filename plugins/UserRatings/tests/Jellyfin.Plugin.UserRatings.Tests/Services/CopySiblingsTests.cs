using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.UserRatings.Configuration;
using Jellyfin.Plugin.UserRatings.Services;
using Jellyfin.Plugin.UserRatings.Tests.Storage;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.UserRatings.Tests.Services;

/// <summary>
/// <see cref="CopySiblings"/>, the "Also rated on another copy" hint: other items of the same kind that
/// share a strong provider key and have ratings, each kept only if the viewer can open it, at most three, most rated first,
/// with the copy's library and its summary under the same policy; nothing for an item without a strong key (a book).
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class CopySiblingsTests : IDisposable
{
    private readonly ServicesFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static RatingsSettings Defaults => RatingsSettings.From(new PluginConfiguration());

    [Fact]
    public async Task TheVisibleCopiesWithRatings_MostRatedFirst_AtMostThree()
    {
        var item = _fx.AddMovie(_fx.Movies, "The Matrix", "603");
        var copies = Enumerable.Range(0, 4).Select(i => _fx.AddMovie(i % 2 == 0 ? _fx.Movies2 : _fx.Movies, "Copy " + i, "603")).ToList();
        var unrelated = _fx.AddMovie(_fx.Movies, "Other film", "604");
        for (var i = 0; i < copies.Count; i++)
        {
            for (var n = 0; n <= i; n++)
            {
                var rater = ServicesFixture.NewUser("rater" + i + n);
                _fx.Users.Add(rater);
                await _fx.Service.SetStarsAsync(rater, copies[i].Id, 4m, ServicesFixture.Ct);
            }
        }

        await _fx.Service.SetStarsAsync(_fx.Viewer, unrelated.Id, 5m, ServicesFixture.Ct);
        var identity = ItemIdentity.Capture(item, _fx.Library.Object);

        var forViewer = _fx.Siblings.Find(identity, _fx.Viewer, _fx.Service.Policy(Defaults));
        Assert.Equal([copies[3].Id, copies[2].Id, copies[1].Id], forViewer.Select(s => s.ItemId));
        Assert.Equal([4, 3, 2], forViewer.Select(s => s.Summary.Count));
        Assert.Equal(["Movies", "Movies 2", "Movies"], forViewer.Select(s => s.LibraryName));

        // viewer2 can't open Movies 2: only the copies in Movies.
        var forViewer2 = _fx.Siblings.Find(identity, _fx.Viewer2, _fx.Service.Policy(Defaults));
        Assert.Equal([copies[3].Id, copies[1].Id], forViewer2.Select(s => s.ItemId));
    }

    [Fact]
    public async Task ACopyRatedOnlyByAHiddenAuthor_ShowsOnlyTheViewersOwnStars()
    {
        var item = _fx.AddMovie(_fx.Movies, "The Matrix", "603");
        var copy = _fx.AddMovie(_fx.Movies, "The Matrix (copy)", "603");
        await _fx.Service.SetStarsAsync(_fx.Viewer2, copy.Id, 1m, ServicesFixture.Ct);
        _fx.Configuration = new PluginConfiguration { HideReviewsFromHiddenUsers = true };
        _fx.Viewer2.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsHidden, true);
        var identity = ItemIdentity.Capture(item, _fx.Library.Object);

        // For others the copy has nothing they may see; its author still sees their own stars there.
        Assert.Empty(_fx.Siblings.Find(identity, _fx.Viewer, _fx.Service.Policy(_fx.Settings.Read())));
        var own = Assert.Single(_fx.Siblings.Find(identity, _fx.Viewer2, _fx.Service.Policy(_fx.Settings.Read())));
        Assert.Equal((0, 1m), (own.Summary.Count, own.MyStars));
    }

    [Fact]
    public async Task AnItemWithoutAStrongKey_HasNoSiblings()
    {
        var book = _fx.Add(_fx.Movies, new Book { Name = "A Book" });
        book.ProviderIds["Isbn"] = "9780000000000";
        var other = _fx.Add(_fx.Movies, new Book { Name = "A Book (copy)" });
        other.ProviderIds["Isbn"] = "9780000000000";
        await _fx.Service.SetStarsAsync(_fx.Viewer, other.Id, 3m, ServicesFixture.Ct);

        Assert.Empty(ItemIdentity.StrongKeys(ItemIdentity.Capture(book, _fx.Library.Object)));
        Assert.Empty(_fx.Siblings.Find(ItemIdentity.Capture(book, _fx.Library.Object), _fx.Viewer, _fx.Service.Policy(Defaults)));
    }
}
