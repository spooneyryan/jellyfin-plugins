using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.Kit.Tests.Library;

/// <summary>
/// <see cref="LibraryAccess"/>: the visible libraries, library
/// and view ids resolved within them, the <c>InternalItemsQuery(user)</c> factory (explicit <c>TopParentIds</c>, no core
/// grouping, never <c>HasOwnerId</c>), and the eligibility query behind <see cref="VersionSets.Collapse"/>.
/// </summary>
public sealed class LibraryAccessTests
{
    private readonly FakeLibrary _library = new();
    private readonly CollectionFolder _movies;
    private readonly CollectionFolder _movies2;
    private readonly CollectionFolder _shows;
    private readonly User _viewer = NewUser("viewer");
    private readonly User _kid = NewUser("kid");
    private readonly LibraryAccess _access;

    public LibraryAccessTests()
    {
        _movies = Library("Movies", CollectionType.movies, 2);
        _movies2 = Library("Movies 2", CollectionType.movies, 1);
        _shows = Library("Shows", CollectionType.tvshows, 1);
        _access = new LibraryAccess(_library.Object, user => user == _kid ? [_movies, _shows] : [_movies, _movies2, _shows], _library.IsVisible, _library.IsVisible);
    }

    [Fact]
    public void VisibleLibraries_AreTheUsers_AndEveryLibraryWithoutAUser()
    {
        Assert.Equal(new[] { _movies, _shows }, _access.VisibleLibraries(_kid));
        Assert.Equal(new[] { _movies, _movies2, _shows }, _access.VisibleLibraries(null));
    }

    [Fact]
    public void ResolveLibraries_NothingAsked_IsEveryVisibleLibrary_AndAHiddenOneIsDropped()
    {
        Assert.Equal(new[] { _movies, _shows }, _access.ResolveLibraries(null, _kid));
        Assert.Equal(new[] { _movies, _shows }, _access.ResolveLibraries([], _kid));
        Assert.Equal(new[] { _movies }, _access.ResolveLibraries([_movies2.Id, _movies.Id], _kid));
        Assert.Empty(_access.ResolveLibraries([_movies2.Id], _kid));
        Assert.Empty(_access.ResolveLibraries([Guid.Empty, Guid.NewGuid()], _kid));
    }

    [Fact]
    public void ResolveLibraries_ANamedView_ResolvesItsDisplayParentFirst_ThenItsParent()
    {
        // A named view's own id matches no library.
        var byDisplayParent = _library.Add(new UserView { Name = "Movies", DisplayParentId = _movies.Id, ViewType = CollectionType.movies });
        var byParent = _library.Add(new UserView { Name = "Shows", ParentId = _shows.Id, ViewType = CollectionType.tvshows });
        var toHidden = _library.Add(new UserView { Name = "Movies 2", DisplayParentId = _movies2.Id, ViewType = CollectionType.movies });

        Assert.Equal(new[] { _movies }, _access.ResolveLibraries([byDisplayParent.Id], _kid));
        Assert.Equal(new[] { _shows }, _access.ResolveLibraries([byParent.Id], _kid));
        Assert.Empty(_access.ResolveLibraries([toHidden.Id], _kid));
    }

    [Fact]
    public void ResolveLibraries_AGroupedView_IsTheUsersGroupedLibrariesOfItsType()
    {
        var grouped = _library.Add(new UserView { Name = "Movies", ViewType = CollectionType.movies });
        _viewer.SetPreference(PreferenceKind.GroupedFolders, [_movies.Id.ToString("N", CultureInfo.InvariantCulture), _movies2.Id.ToString("N", CultureInfo.InvariantCulture)]);

        Assert.Equal(new[] { _movies, _movies2 }, _access.ResolveLibraries([grouped.Id], _viewer));
        Assert.Empty(_access.ResolveLibraries([grouped.Id], null));
    }

    [Fact]
    public void ResolveLibraries_AnOrdinaryItem_StandsForNoLibrary()
    {
        var movie = _library.AddMovie("Heat", 1995, TimeSpan.FromMinutes(170));

        Assert.Empty(_access.ResolveLibraries([movie.Id], _viewer));
    }

    [Fact]
    public void CreateQuery_SetsTheUser_TheLibrariesTopParents_AndNoGrouping_AndNeverHasOwnerId()
    {
        var query = _access.CreateQuery(_kid)!;

        Assert.Same(_kid, query.User);
        Assert.Equal(_movies.PhysicalFolderIds.Concat(_shows.PhysicalFolderIds), query.TopParentIds);
        Assert.False(query.GroupByPresentationUniqueKey);
        Assert.Null(query.HasOwnerId);
    }

    [Fact]
    public void CreateQuery_IntersectsTheRequestedLibraries_WithTheUsers()
    {
        Assert.Equal(_movies.PhysicalFolderIds, _access.CreateQuery(_kid, [_movies.Id, _movies2.Id])!.TopParentIds);
        Assert.Equal(_movies2.PhysicalFolderIds, _access.CreateQuery(_viewer, [_movies2.Id])!.TopParentIds);
    }

    [Fact]
    public void CreateQuery_WithNoLibraryLeft_IsNull_NeverAQueryOverEveryLibrary()
    {
        Assert.Null(_access.CreateQuery(_kid, [_movies2.Id]));
        Assert.Null(new LibraryAccess(_library.Object, _ => []).CreateQuery(_viewer));
        Assert.Null(new LibraryAccess(_library.Object, _ => [new CollectionFolder { Id = Guid.NewGuid() }]).CreateQuery(_viewer));
    }

    [Fact]
    public void CreateQuery_WithoutAUser_CoversEveryLibrary_AndSetsNoUser()
    {
        var query = _access.CreateQuery(null)!;

        Assert.Null(query.User);
        Assert.Equal(4, query.TopParentIds.Length);
        Assert.False(query.GroupByPresentationUniqueKey);
    }

    [Fact]
    public void TheFactoryKeepsEveryVersion_WhereCoresGroupingWouldKeepOnlyOne()
    {
        // With a user, core groups by PresentationUniqueKey and keeps one member of a merged set, even for ItemIds.
        var primary = MovieIn(_movies, "Heat");
        var secondary = MovieIn(_movies, "Heat 4K");
        secondary.SetPrimaryVersion(primary.Id);
        primary.PresentationUniqueKey = primary.Id.ToString("N", CultureInfo.InvariantCulture);

        var query = _access.CreateQuery(_viewer)!;
        query.ItemIds = [primary.Id, secondary.Id];
        Assert.Equal(2, _library.Object.GetItemList(query).Count);

        query.GroupByPresentationUniqueKey = true;
        Assert.Single(_library.Object.GetItemList(query));
    }

    [Fact]
    public void GetItem_IsTheUsersView_AndNeverAsksForTheEmptyGuid()
    {
        var movie = MovieIn(_movies2, "Amelie");
        _library.Hide(_kid, movie);

        Assert.Null(_access.GetItem<BaseItem>(movie.Id, _kid));
        Assert.Same(movie, _access.GetItem<BaseItem>(movie.Id, _viewer));
        Assert.Null(_access.GetItem<BaseItem>(Guid.Empty, _viewer));
        Assert.DoesNotContain(Guid.Empty, _library.UserLookups);
    }

    [Fact]
    public void GetItem_OfARowTheServerCantLoad_IsNone()
    {
        // Jellyfin 10.11's placeholder item: GetItemById throws InvalidOperationException for it, which made Library
        // Maintenance's Move Eligibility answer 500 (the 1.0.2 verifier).
        Assert.Throws<InvalidOperationException>(() => _library.Object.GetItemById<BaseItem>(FakeLibrary.PlaceholderId, _viewer));

        Assert.Null(_access.GetItem<BaseItem>(FakeLibrary.PlaceholderId, _viewer));
        Assert.Null(_access.GetItem<BaseItem>(FakeLibrary.PlaceholderId, null));
        Assert.Equal(3, _library.UserLookups.Count(id => id == FakeLibrary.PlaceholderId));
    }

    [Fact]
    public void EligibleIds_AreInsideTheLibraries_VisibleToTheUser_AndNotOwned()
    {
        var inMovies = MovieIn(_movies, "Heat");
        var inMovies2 = MovieIn(_movies2, "Heat (copy)");
        var hidden = MovieIn(_movies, "Hidden");
        _library.Hide(_viewer, hidden);
        var extra = _library.Add(new Movie { Name = "Heat trailer", OwnerId = inMovies.Id });
        _library.SetTopParent(extra, _movies.PhysicalFolderIds[0]);
        var noTopParent = _library.Add(new Movie { Name = "Local alternate", OwnerId = inMovies.Id });

        var eligible = _access.EligibleIds(_viewer, [_movies], [inMovies.Id, inMovies2.Id, hidden.Id, extra.Id, noTopParent.Id, Guid.Empty]);

        Assert.Equal(new[] { inMovies.Id }, eligible);
        var query = Assert.Single(_library.Queries);
        Assert.Same(_viewer, query.User);
        Assert.False(query.GroupByPresentationUniqueKey);
        Assert.Equal(_movies.PhysicalFolderIds, query.TopParentIds);
        Assert.Null(query.HasOwnerId);
        Assert.DoesNotContain(Guid.Empty, query.ItemIds);
        Assert.False(query.DtoOptions.EnableUserData);
    }

    [Fact]
    public void EligibleIds_AskInChunks_AndNothingForNoIdsOrNoLibraries()
    {
        var ids = Enumerable.Range(0, LibraryAccess.IdsPerQuery + 1).Select(_ => Guid.NewGuid()).ToList();

        Assert.Empty(_access.EligibleIds(_viewer, [_movies], ids));
        Assert.Equal(2, _library.Queries.Count);
        Assert.Equal(LibraryAccess.IdsPerQuery, _library.Queries[0].ItemIds.Length);

        _library.Queries.Clear();
        Assert.Empty(_access.EligibleIds(_viewer, [_movies], []));
        Assert.Empty(_access.EligibleIds(_viewer, [], ids));
        Assert.Empty(_library.Queries);
    }

    [Fact]
    public void EligibleIds_WorkWithCollapse_ForASetSplitBetweenTwoLibraries()
    {
        // A version set across libraries: the primary in Movies 2, which kid can't open; the 4K secondary in Movies matched.
        var primary = MovieIn(_movies2, "Heat");
        var secondary = MovieIn(_movies, "Heat 4K");
        secondary.SetPrimaryVersion(primary.Id);
        primary.LinkedAlternateVersions = [new LinkedChild { ItemId = secondary.Id }];
        var lookup = VersionSets.LibraryLookup(_library.Object);

        var forKid = VersionSets.Collapse([VersionMember.From(secondary)], ids => _access.EligibleIds(_kid, _access.ResolveLibraries(null, _kid), ids), lookup);
        var forViewer = VersionSets.Collapse([VersionMember.From(secondary)], ids => _access.EligibleIds(_viewer, _access.ResolveLibraries(null, _viewer), ids), lookup);

        Assert.Equal(secondary.Id, Assert.Single(forKid).Id);
        Assert.Equal(primary.Id, Assert.Single(forViewer).Id);
    }

    [Fact]
    public void EligibleIds_LeaveOutAnItemAParentHides_AsGetItemDoes()
    {
        // GetItemList checks only the item itself, GetItemById(id, user) every parent too. A TV-PG episode of a TV-MA
        // series (or of an unrated series BlockUnratedItems blocks), a PG movie in a folder rated R.
        var (series, season, pilot) = EpisodeIn(_shows, "Rated Series");
        var (_, _, open) = EpisodeIn(_shows, "Open Series");
        var folder = _library.Add(new Folder { Name = "Rated Folder", Path = @"C:\media\Movies\Rated Folder" });
        var inFolder = MovieIn(_movies, "Heat");
        inFolder.ParentId = folder.Id;
        _library.Hide(_kid, series, folder);

        var forKid = _access.EligibleIds(_kid, [_movies, _shows], [pilot.Id, open.Id, inFolder.Id]);
        var forViewer = _access.EligibleIds(_viewer, [_movies, _shows], [pilot.Id, open.Id, inFolder.Id]);

        Assert.Equal(new[] { open.Id }, forKid);
        Assert.Null(_access.GetItem<BaseItem>(pilot.Id, _kid));
        Assert.Null(_access.GetItem<BaseItem>(inFolder.Id, _kid));
        Assert.Equal(new[] { pilot.Id, open.Id, inFolder.Id }.Order(), forViewer.Order());
        Assert.NotNull(_access.GetItem<BaseItem>(season.Id, _viewer));
    }

    [Fact]
    public void EligibleIds_CheckEachParentOncePerCall()
    {
        // Cheap at scale: twenty episodes of one season ask for the season and the series once each.
        var (series, season, first) = EpisodeIn(_shows, "Long Series");
        var episodes = new List<Guid> { first.Id };
        for (var i = 2; i <= 20; i++)
        {
            var more = _library.AddEpisode((Series)series, (Season)season, i);
            _library.SetTopParent(more, _shows.PhysicalFolderIds[0]);
            episodes.Add(more.Id);
        }

        var asked = new List<Guid>();
        var access = new LibraryAccess(_library.Object, _ => [_shows], (parent, user) =>
        {
            asked.Add(parent.Id);
            return _library.IsVisible(parent, user);
        });

        Assert.Equal(20, access.EligibleIds(_kid, [_shows], episodes).Count);
        Assert.Equal(new[] { season.Id, series.Id }, asked);
        Assert.Equal(1, _library.IdLookups.Count(id => id == season.Id));
        Assert.Equal(1, _library.IdLookups.Count(id => id == series.Id));

        _library.Hide(_kid, season);
        Assert.Empty(access.EligibleIds(_kid, [_shows], episodes));
    }

    [Fact]
    public void EligibleIds_WithoutAUser_CheckNoParent_AndAParentCycleIsNotVisible()
    {
        var (series, _, pilot) = EpisodeIn(_shows, "Rated Series");
        _library.Hide(_kid, series);
        _library.IdLookups.Clear();

        Assert.Equal(new[] { pilot.Id }, _access.EligibleIds(null, [_shows], [pilot.Id]));
        Assert.Empty(_library.IdLookups);

        // Two folders that name each other as parent: the walk stops and the item counts as not visible.
        var a = _library.Add(new Folder { Name = "A" });
        var b = _library.Add(new Folder { Name = "B", ParentId = a.Id });
        a.ParentId = b.Id;
        var looped = MovieIn(_movies, "Looped");
        looped.ParentId = a.Id;
        Assert.Empty(_access.EligibleIds(_viewer, [_movies], [looped.Id]));
    }

    [Fact]
    public void EligibleIds_FollowOwner_KeepsALocalTrailerAndAnAlternateVersion_OfAVisibleMovie()
    {
        // Access Roles: owned items have no top parent, so the batch never returns them; for ids a client sent, their
        // visibility follows their owner. By default they stay out.
        var movie = MovieIn(_movies, "Heat");
        var trailer = _library.Add(new Trailer { Name = "Heat trailer", OwnerId = movie.Id });
        var alternate = _library.Add(new Movie { Name = "Heat (director's cut)", OwnerId = movie.Id });

        Assert.Equal(new[] { movie.Id }, _access.EligibleIds(_viewer, [_movies], [movie.Id, trailer.Id, alternate.Id]));
        _library.Queries.Clear();
        var followed = _access.EligibleIds(_viewer, [_movies], [movie.Id, trailer.Id, alternate.Id], OwnedItems.FollowOwner);

        Assert.Equal(new[] { movie.Id, trailer.Id, alternate.Id }.Order(), followed.Order());
        // The owner was among the ids, so no second query.
        Assert.Single(_library.Queries);
    }

    [Fact]
    public void EligibleIds_FollowOwner_AsksForOwnersNotAmongTheIds_InOneMoreQuery()
    {
        var heat = MovieIn(_movies, "Heat");
        var alien = MovieIn(_movies, "Alien");
        var trailers = new[] { heat, alien, heat }.Select((m, i) => _library.Add(new Trailer { Name = "Trailer " + i, OwnerId = m.Id }).Id).ToList();

        var eligible = _access.EligibleIds(_viewer, [_movies], trailers, OwnedItems.FollowOwner);

        Assert.Equal(trailers.Order(), eligible.Order());
        Assert.Equal(2, _library.Queries.Count);
        Assert.Equal(new[] { heat.Id, alien.Id }.Order(), _library.Queries[1].ItemIds.Order());
        Assert.Equal(_movies.PhysicalFolderIds, _library.Queries[1].TopParentIds);
        // Only the ids asked for come back, never their owners.
        Assert.DoesNotContain(heat.Id, eligible);
    }

    [Fact]
    public void EligibleIds_FollowOwner_DropsATrailerWhoseOwnerIsHidden_OutsideTheLibraries_OrGone_AndOneHiddenItself()
    {
        var hiddenOwner = MovieIn(_movies, "Hidden");
        _library.Hide(_viewer, hiddenOwner);
        var otherLibrary = MovieIn(_movies2, "Elsewhere");
        var visible = MovieIn(_movies, "Visible");
        var ofHidden = _library.Add(new Trailer { Name = "of hidden", OwnerId = hiddenOwner.Id });
        var ofOther = _library.Add(new Trailer { Name = "of other", OwnerId = otherLibrary.Id });
        var ofGone = _library.Add(new Trailer { Name = "of gone", OwnerId = Guid.NewGuid() });
        var hiddenItself = _library.Add(new Trailer { Name = "rated", OwnerId = visible.Id });
        _library.Hide(_viewer, hiddenItself);
        var kept = _library.Add(new Trailer { Name = "kept", OwnerId = visible.Id });

        var eligible = _access.EligibleIds(_viewer, [_movies], [ofHidden.Id, ofOther.Id, ofGone.Id, hiddenItself.Id, kept.Id], OwnedItems.FollowOwner);

        Assert.Equal(new[] { kept.Id }, eligible);
    }

    [Fact]
    public void EligibleIds_FollowOwner_WalksAChainOfOwners_StopsAtACycle_AndWithoutAUserChecksOnlyTheLibraries()
    {
        var movie = MovieIn(_movies, "Heat");
        var version = _library.Add(new Movie { Name = "Heat 4K", OwnerId = movie.Id });
        var extra = _library.Add(new Trailer { Name = "4K trailer", OwnerId = version.Id });
        var a = _library.Add(new Trailer { Name = "A" });
        var b = _library.Add(new Trailer { Name = "B", OwnerId = a.Id });
        a.OwnerId = b.Id;
        _library.Hide(_viewer, movie);

        Assert.Empty(_access.EligibleIds(_viewer, [_movies], [extra.Id, a.Id], OwnedItems.FollowOwner));
        Assert.Equal(new[] { extra.Id }, _access.EligibleIds(_kid, [_movies], [extra.Id, a.Id], OwnedItems.FollowOwner));
        Assert.Equal(new[] { extra.Id }, _access.EligibleIds(null, [_movies], [extra.Id, a.Id], OwnedItems.FollowOwner));
        Assert.Empty(_access.EligibleIds(null, [_movies2], [extra.Id], OwnedItems.FollowOwner));
    }

    [Fact]
    public void Visibility_IsUnknownForNothing_HiddenForWhatGetItemRefuses_AndVisibleOtherwise()
    {
        var movie = MovieIn(_movies, "Heat");
        var hidden = MovieIn(_movies, "Hidden");
        _library.Hide(_viewer, hidden);

        Assert.Equal(ItemVisibility.Unknown, _access.Visibility(Guid.Empty, _viewer));
        Assert.Equal(ItemVisibility.Unknown, _access.Visibility(Guid.NewGuid(), _viewer));
        Assert.Equal(ItemVisibility.Hidden, _access.Visibility(hidden.Id, _viewer));
        Assert.Equal(ItemVisibility.Visible, _access.Visibility(movie.Id, _viewer));
        Assert.Equal(ItemVisibility.Visible, _access.Visibility(hidden.Id, _kid));
        Assert.DoesNotContain(Guid.Empty, _library.IdLookups);
    }

    [Fact]
    public void Visibility_JudgesANamedViewByItsDisplayParent_ThenItsParent()
    {
        // A library's sub-views (moviemovies, movielatest...) pass GetItemById(view, user); their library decides.
        var subView = _library.Add(new UserView { Name = "Movies", ViewType = CollectionType.movies, DisplayParentId = _movies2.Id });
        var shadow = _library.Add(new UserView { Name = "Movies 2", ParentId = _movies2.Id });
        var nested = _library.Add(new UserView { Name = "Latest", ParentId = shadow.Id });
        var open = _library.Add(new UserView { Name = "Shows", DisplayParentId = _shows.Id });
        _library.Hide(_kid, _movies2);

        Assert.Equal(ItemVisibility.Hidden, _access.Visibility(subView.Id, _kid));
        Assert.Equal(ItemVisibility.Hidden, _access.Visibility(nested.Id, _kid));
        Assert.Equal(ItemVisibility.Visible, _access.Visibility(subView.Id, _viewer));
        Assert.Equal(ItemVisibility.Visible, _access.Visibility(open.Id, _kid));
        // A view whose parent names nothing is the view's own answer.
        Assert.Equal(ItemVisibility.Visible, _access.Visibility(_library.Add(new UserView { Name = "Grouped", DisplayParentId = Guid.NewGuid() }).Id, _kid));
    }

    [Fact]
    public void Visibility_AndEligibleIdsFollowingOwners_Agree_OnAMovieItsTrailerAndAHiddenOne()
    {
        var movie = MovieIn(_movies, "Heat");
        var trailer = _library.Add(new Trailer { Name = "Heat trailer", OwnerId = movie.Id });
        var hidden = MovieIn(_movies, "Hidden");
        _library.Hide(_viewer, hidden);
        var ids = new[] { movie.Id, trailer.Id, hidden.Id };

        var perItem = ids.Where(id => _access.Visibility(id, _viewer) == ItemVisibility.Visible).Order();
        var batch = _access.EligibleIds(_viewer, [_movies], ids, OwnedItems.FollowOwner).Order();

        Assert.Equal(perItem, batch);
    }

    [Fact]
    public void IsOwned_ReadsOwnerId()
    {
        Assert.False(LibraryAccess.IsOwned(new Movie()));
        Assert.True(LibraryAccess.IsOwned(new Movie { OwnerId = Guid.NewGuid() }));
    }

    [Fact]
    public void TopParentIds_AreEveryPhysicalFolderOnce()
    {
        var shared = Guid.NewGuid();
        var a = new CollectionFolder { PhysicalFolderIds = [shared, Guid.Empty] };
        var b = new CollectionFolder { PhysicalFolderIds = [shared] };

        Assert.Equal(new[] { shared }, LibraryAccess.TopParentIds([a, b]));
    }

    private static User NewUser(string name)
        => new(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Id = Guid.NewGuid() };

    private CollectionFolder Library(string name, CollectionType type, int folders)
        => _library.Add(new CollectionFolder { Name = name, CollectionType = type, PhysicalFolderIds = Enumerable.Range(0, folders).Select(_ => Guid.NewGuid()).ToArray() });

    private Movie MovieIn(CollectionFolder library, string name)
    {
        var movie = _library.AddMovie(name, 1995, TimeSpan.FromMinutes(100));
        _library.SetTopParent(movie, library.PhysicalFolderIds[0]);
        return movie;
    }

    // A series with season 1 and its episode 1, each with the library's top parent, as a scan stores them.
    private (BaseItem Series, BaseItem Season, BaseItem Episode) EpisodeIn(CollectionFolder library, string name)
    {
        var series = _library.AddSeries(name);
        var season = _library.AddSeason(series, 1);
        var episode = _library.AddEpisode(series, season, 1);
        foreach (var item in new BaseItem[] { series, season, episode })
        {
            _library.SetTopParent(item, library.PhysicalFolderIds[0]);
        }

        return (series, season, episode);
    }
}
