using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Paths;

/// <summary>
/// Library access: the libraries' locations come from Jellyfin's virtual folders, limited to the
/// libraries the caller can open (the kit's <see cref="LibraryAccess"/>, like Duplicates; an API key sees all), and an
/// item's library is the one whose location holds it, separator-aware.
/// </summary>
public sealed class LibraryLocationCatalogTests
{
    private static readonly Guid _movies = new("c0000000-0000-0000-0000-0000000000b1");
    private static readonly Guid _movies2 = new("c0000000-0000-0000-0000-0000000000b2");
    private static readonly Guid _shows = new("c0000000-0000-0000-0000-0000000000b3");
    private static readonly Guid _hidden = new("c0000000-0000-0000-0000-0000000000b4");

    private readonly Mock<ILibraryManager> _library = new();
    private readonly User _bob = new("bob", "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");

    public LibraryLocationCatalogTests()
    {
        _library.Setup(l => l.GetVirtualFolders()).Returns(
        [
            Folder(_movies, "Movies", CollectionTypeOptions.movies, @"Q:\Video\Movies", @"Q:\Video\Movies\Kids"),
            Folder(_movies2, "Movies 2", CollectionTypeOptions.movies, @"Q:\Video\Movies2"),
            Folder(_shows, "Shows", CollectionTypeOptions.tvshows, @"Q:\Video\Shows", string.Empty),
            Folder(_hidden, "Private", CollectionTypeOptions.movies, @"Q:\Video\Private"),
            new VirtualFolderInfo { Name = "Broken", ItemId = "not-a-guid", Locations = [@"Q:\Broken"] },
        ]);
    }

    [Fact]
    public void GetLibraries_ForAnApiKey_IsEveryLibrary_InJellyfinsOrder_WithItsLocations()
    {
        var libraries = Catalog().GetLibraries(null);
        Assert.Equal([_movies, _movies2, _shows, _hidden], libraries.Select(l => l.Id));
        Assert.Equal([@"Q:\Video\Movies", @"Q:\Video\Movies\Kids"], libraries[0].Locations);
        Assert.Equal([@"Q:\Video\Shows"], libraries[2].Locations);
        Assert.Equal("tvshows", libraries[2].CollectionTypeName);
    }

    [Fact]
    public void GetLibraries_ForAUser_IsOnlyTheLibrariesTheyCanOpen()
    {
        var catalog = Catalog();
        Assert.Equal([_movies, _shows], catalog.GetLibraries(_bob).Select(l => l.Id));
        Assert.Null(catalog.FindLibrary(_hidden, _bob));
        Assert.Equal("Private", catalog.FindLibrary(_hidden, null)?.Name);
        Assert.Null(catalog.FindLibrary(Guid.Empty, null));
        Assert.Null(catalog.FindLibrary(Guid.NewGuid(), null));
    }

    [Theory]
    [InlineData(@"Q:\Video\Movies\Heat (1995)\Heat (1995).mkv", "b1", 0)]
    [InlineData(@"Q:\Video\Movies\Heat.1995.mkv", "b1", 0)]
    [InlineData(@"q:\video\movies\heat.1995.mkv", "b1", 0)]
    [InlineData(@"Q:\Video\Movies\Kids\Up (2009).mkv", "b1", 1)]
    [InlineData(@"Q:\Video\Movies2\Heat.1995.mkv", "b2", 0)]
    [InlineData(@"Q:\Video\Shows\Lost\Season 01\Lost S01E01.mkv", "b3", 0)]
    public void FindSource_IsTheLibraryWhoseLocationHoldsTheItem_TheLongestLocation(string path, string library, int index)
    {
        var source = Catalog().FindSource(new Movie { Id = Guid.NewGuid(), Path = path }, null);
        Assert.NotNull(source);
        Assert.Equal(new Guid("c0000000-0000-0000-0000-0000000000" + library), source.Value.Library.Id);
        Assert.Equal(index, source.Value.LocationIndex);
    }

    [Fact]
    public void FindSource_PrefersTheItemsOwnCollectionFolder_WhenTwoLibrariesShareALocation()
    {
        _library.Setup(l => l.GetVirtualFolders()).Returns(
        [
            Folder(_movies, "Movies", CollectionTypeOptions.movies, @"Q:\Video\Movies"),
            Folder(_movies2, "Movies too", CollectionTypeOptions.movies, @"Q:\Video\Movies"),
        ]);
        var item = new Movie { Id = Guid.NewGuid(), Path = @"Q:\Video\Movies\Heat.1995.mkv" };
        _library.Setup(l => l.GetCollectionFolders(item)).Returns([new CollectionFolder { Id = _movies2 }]);

        Assert.Equal(_movies2, Catalog().FindSource(item, null)?.Library.Id);
    }

    [Theory]
    [InlineData(@"Q:\Video\Private\Secret.mkv")]
    [InlineData(@"Q:\Video\Movies2\Heat.1995.mkv")]
    [InlineData(@"Q:\Elsewhere\Heat.1995.mkv")]
    [InlineData(@"Q:\Video\MoviesX\Heat.1995.mkv")]
    [InlineData("")]
    public void FindSource_IsNull_OutsideTheLibrariesTheCallerCanOpen(string path)
        => Assert.Null(Catalog().FindSource(new Movie { Id = Guid.NewGuid(), Path = path }, _bob));

    private static VirtualFolderInfo Folder(Guid id, string name, CollectionTypeOptions type, params string[] locations)
        => new() { Name = name, ItemId = id.ToString("N"), CollectionType = type, Locations = locations };

    private LibraryLocationCatalog Catalog()
    {
        var all = new[] { _movies, _movies2, _shows, _hidden }.Select(id => new CollectionFolder { Id = id }).ToList();
        IReadOnlyList<CollectionFolder> Visible(User? user) => user is null ? all : all.Where(f => f.Id == _movies || f.Id == _shows).ToList();
        return new LibraryLocationCatalog(_library.Object, new LibraryAccess(_library.Object, Visible));
    }
}
