using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.TagCloud.Configuration;
using Jellyfin.Plugin.TagCloud.Index;
using Jellyfin.Plugin.TagCloud.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.TagCloud.Tests.TestSupport;

/// <summary>
/// A small library for the index and the counts: Movies (Alien, Aliens, Heat, and a hidden-from-the-kid Scarface), Shows
/// (The Wire, and an episode the index never counts), an owned extra and a virtual movie that it never counts either, and
/// two users: the admin, who sees both libraries, and the kid, who sees Movies only and not Scarface.
/// </summary>
internal sealed class TagCloudFixture
{
    public TagCloudFixture()
    {
        Library = new FakeLibrary();
        Movies = AddLibrary("Movies", CollectionType.movies);
        Shows = AddLibrary("Shows", CollectionType.tvshows);
        Admin = NewUser("admin");
        Kid = NewUser("kid");

        Alien = Movie("Alien", 1979, ["Space", "Horror", "creature"], ["Horror", "Science Fiction"], ["20th Century Fox"]);
        Aliens = Movie("Aliens", 1986, ["space", "Sequel", "creature"], ["Action", "Science Fiction"], ["20th Century Fox"]);
        Heat = Movie("Heat", 1995, ["heist", "Los Angeles, California"], ["Crime"], ["Warner Bros."]);
        Scarface = Movie("Scarface", 1983, ["gangster", "heist"], ["Crime"], ["Universal"]);
        Wire = Library.AddSeries("The Wire");
        Wire.SortName = "Wire";
        Wire.Tags = ["police", "Baltimore"];
        Wire.Genres = ["Crime", "Drama"];
        Library.SetTopParent(Wire, Shows.PhysicalFolderIds[0]);
        var season = Library.AddSeason(Wire, 1);
        var episode = Library.AddEpisode(Wire, season, 1);
        episode.Tags = ["episode tag"];
        Library.SetTopParent(episode, Shows.PhysicalFolderIds[0]);
        var extra = Movie("Alien trailer", 1979, ["trailer tag"], [], []);
        extra.OwnerId = Alien.Id;
        var missing = Movie("Missing", 2000, ["virtual tag"], [], []);
        missing.IsVirtualItem = true;
        Library.Hide(Kid, Scarface);

        People = new Dictionary<Guid, List<PersonInfo>>
        {
            [Alien.Id] = [Person("Sigourney Weaver", PersonKind.Actor), Person("Ridley Scott", PersonKind.Director), Person("Dan O'Bannon", PersonKind.Writer)],
            [Aliens.Id] = [Person("Sigourney Weaver", PersonKind.Actor), Person("Michael Biehn", PersonKind.Actor), Person("James Cameron", PersonKind.Director), Person("James Cameron", PersonKind.Writer)],
            [Heat.Id] = [Person("Al Pacino", PersonKind.Actor), Person("Michael Mann", PersonKind.Director), Person("Someone", PersonKind.Producer)],
            [Scarface.Id] = [Person("Al Pacino", PersonKind.Actor)]
        };
        Library.Mock.Setup(l => l.GetPeople(It.IsAny<BaseItem>()))
            .Returns((BaseItem item) => People.TryGetValue(item.Id, out var people) ? people : new List<PersonInfo>());

        Access = new LibraryAccess(Library.Object, user => (user == Kid ? new[] { Movies } : [Movies, Shows]).Where(l => !SwitchedOff.Contains(l)).ToList(), Library.IsVisible);
        Titles = new TitleReader(Library.Object);
        PeopleReader = new PeopleReader(Library.Object, () => null, NullLogger.Instance);
        Index = new TagCloudIndex(Titles, PeopleReader, ConfigurationProvider, NullLogger.Instance);
        Query = new TagCloudQueryService(Index, Access, Library.Object, ConfigurationProvider);
    }

    public FakeLibrary Library { get; }

    public CollectionFolder Movies { get; }

    public CollectionFolder Shows { get; }

    /// <summary>Gets the libraries switched off server-wide (core's <c>LibraryOptions.Enabled = false</c>): no user sees them.</summary>
    public HashSet<CollectionFolder> SwitchedOff { get; } = [];

    public User Admin { get; }

    public User Kid { get; }

    public Movie Alien { get; }

    public Movie Aliens { get; }

    public Movie Heat { get; }

    public Movie Scarface { get; }

    public Series Wire { get; }

    public Dictionary<Guid, List<PersonInfo>> People { get; }

    public FakeConfiguration ConfigurationProvider { get; } = new();

    public LibraryAccess Access { get; }

    public TitleReader Titles { get; }

    public PeopleReader PeopleReader { get; }

    public TagCloudIndex Index { get; }

    public TagCloudQueryService Query { get; }

    public static User NewUser(string name)
        => new(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider") { Id = Guid.NewGuid() };

    public Dictionary<string, int> Counts(Jellyfin.Plugin.TagCloud.Api.Models.TagCloudWordsDto words) => words.Words.ToDictionary(w => w.Text, w => w.Count, StringComparer.Ordinal);

    public Movie Movie(string name, int year, string[] tags, string[] genres, string[] studios)
    {
        var movie = Library.AddMovie(name, year, TimeSpan.FromMinutes(100));

        // Items read from the database carry their sort name; computing one needs the server's configuration.
        movie.SortName = name;
        movie.Tags = tags;
        movie.Genres = genres;
        movie.Studios = studios;
        Library.SetTopParent(movie, Movies.PhysicalFolderIds[0]);
        return movie;
    }

    private static PersonInfo Person(string name, PersonKind kind) => new() { Name = name, Type = kind };

    private CollectionFolder AddLibrary(string name, CollectionType type)
        => Library.Add(new CollectionFolder { Name = name, CollectionType = type, PhysicalFolderIds = [Guid.NewGuid()] });
}

/// <summary>Settings a test can change.</summary>
internal sealed class FakeConfiguration : IPluginConfigurationProvider
{
    public PluginConfiguration Configuration { get; set; } = new();

    public PluginConfiguration GetConfiguration() => Configuration;
}
