using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Xunit;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// The index builder: the queries the build makes (never <c>HasOwnerId</c>, never empty
/// <c>TopParentIds</c>, minimal loading), the movies it keeps (those saved on 10.11 and those migrated from 10.10, never an
/// owned item), the libraries it reads, its chunks, the excluded folders, and the version sets it makes.
/// </summary>
public sealed class MovieIndexBuilderTests
{
    [Fact]
    public void TheQueries_NeverSetHasOwnerId_NorAnEmptyTopParentIds_AndLoadLittle()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        library.Add("Heat", 1995, DuplicatesLibrary.MoreMovies);

        library.Builder().Build(CancellationToken.None);

        Assert.Equal(2, library.IdQueries.Count);
        Assert.All(library.IdQueries, query =>
        {
            Assert.Null(query.HasOwnerId);
            Assert.NotEmpty(query.TopParentIds);
            Assert.Equal(new[] { BaseItemKind.Movie }, query.IncludeItemTypes);
            Assert.True(query.Recursive);
            Assert.False(query.IsVirtualItem);
            Assert.False(query.GroupByPresentationUniqueKey);
            Assert.Null(query.User);
        });
        Assert.Equal(new[] { new[] { DuplicatesLibrary.Movies }, new[] { DuplicatesLibrary.MoreMovies } }, library.IdQueries.Select(q => q.TopParentIds));

        var chunks = library.Fake.Queries.Where(q => q.ItemIds.Length > 0).ToList();
        var chunk = Assert.Single(chunks);
        Assert.Null(chunk.HasOwnerId);
        Assert.Null(chunk.User);
        Assert.All(library.IdQueries.Concat(chunks), query =>
        {
            Assert.False(query.DtoOptions.EnableImages);
            Assert.False(query.DtoOptions.EnableUserData);
            Assert.Contains(ItemFields.ProviderIds, query.DtoOptions.Fields);
        });
    }

    [Fact]
    public void MoviesSavedOn1011_AndMigratedFrom1010_AreBothIndexed()
    {
        // 10.11 stores the zero GUID as OwnerId; HasOwnerId = false would match only the NULL rows migrated from 10.10.
        var library = new DuplicatesLibrary();
        var saved = library.Add("Heat", 1995);
        var migrated = library.Add("Ronin", 1998);
        library.MigratedFrom1010.Add(migrated.Id);

        var content = library.Builder().Build(CancellationToken.None);

        Assert.Equal(new[] { saved.Id, migrated.Id }, content.Entries.Select(e => e.Representative.Id));
        Assert.Equal(2, content.Stats.Movies);

        // What a HasOwnerId = false query would have returned on 10.11.5: the migrated row only.
        var withFilter = MovieIndexBuilder.LibraryQuery([DuplicatesLibrary.Movies]);
        withFilter.HasOwnerId = false;
        Assert.Equal(new[] { migrated.Id }, library.Fake.Object.GetItemIds(withFilter));
    }

    [Fact]
    public void OwnedItems_AreDropped_EvenWhenTheQueryReturnsThem()
    {
        var library = new DuplicatesLibrary();
        var movie = library.Add("Heat", 1995);
        var localAlternate = library.AddOwned(movie, "Heat (1995) - 4K.mkv", DuplicatesLibrary.Movies);
        var extra = library.AddOwned(movie, @"extras\Heat - Making of.mkv", DuplicatesLibrary.Movies);
        var ownedWithoutTopParent = library.AddOwned(movie, "Heat (1995) - Remux.mkv");
        movie.LocalAlternateVersions = [localAlternate.Path, ownedWithoutTopParent.Path];

        var content = library.Builder().Build(CancellationToken.None);

        var entry = Assert.Single(content.Entries);
        Assert.Equal(movie.Id, entry.Representative.Id);
        Assert.Equal(2, content.Stats.Owned);
        Assert.DoesNotContain(content.Entries.SelectMany(e => e.AllIds), id => id == localAlternate.Id || id == extra.Id || id == ownedWithoutTopParent.Id);

        // The local alternates count as sources of the movie, never as movies of their own.
        Assert.Equal(2, entry.Representative.LocalAlternateCount);
        Assert.Equal(3, entry.SourceCount);
    }

    [Fact]
    public void OnlyMoviesAndMixedLibraries_AreRead_AndTheSettingsNarrowThem()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        library.Add("Ronin", 1998, DuplicatesLibrary.MoreMovies);
        library.Add("Paddington", 2014, DuplicatesLibrary.Kids);

        // Every movies and mixed library when the settings name none; never the tvshows library.
        library.Configuration = new PluginConfiguration();
        var all = library.Builder().Build(CancellationToken.None);
        Assert.Equal(new[] { "Movies", "More Movies", "Kids" }, all.Libraries.Select(l => l.Name));
        Assert.Equal(new[] { "movies", "mixed", "movies" }, all.Libraries.Select(l => l.Kind));
        Assert.Equal(new[] { 1, 1, 1 }, all.Libraries.Select(l => l.Movies));
        Assert.Equal(3, all.Stats.Movies);

        // The included libraries only; a tvshows library named there is still not read.
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = [library.Libraries[2].Id, library.Libraries[3].Id] };
        var kids = library.Builder().Build(CancellationToken.None);
        Assert.Equal(new[] { "Kids" }, kids.Libraries.Select(l => l.Name));
        Assert.Equal(new[] { "Paddington" }, kids.Entries.Select(e => e.Representative.Name));
    }

    [Fact]
    public void ADisabledLibrary_IsNotRead_AndItsMoviesAreNotIndexed()
    {
        // 10.11.5's CollectionFolder.IsVisible(user) is false for every user when LibraryOptions.Enabled is off.
        var library = new DuplicatesLibrary { Configuration = new PluginConfiguration() };
        library.Add("Heat", 1995);
        library.Add("Ronin", 1998, DuplicatesLibrary.MoreMovies);
        library.Add("Paddington", 2014, DuplicatesLibrary.Kids);
        var kids = library.Libraries[2].Id;
        library.IsEnabled = l => l.Id != kids;

        var content = library.Builder().Build(CancellationToken.None);

        Assert.Equal(new[] { "Movies", "More Movies" }, content.Libraries.Select(l => l.Name));
        Assert.Equal(new[] { "Heat", "Ronin" }, content.Entries.Select(e => e.Representative.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, library.IdQueries.Count);
        Assert.DoesNotContain(library.IdQueries, q => q.TopParentIds.Contains(DuplicatesLibrary.Kids));

        // A movie whose folder is also in an enabled library is indexed there.
        library.Libraries[1].PhysicalFolderIds = [DuplicatesLibrary.MoreMovies, DuplicatesLibrary.Kids];
        var shared = library.Builder().Build(CancellationToken.None);
        Assert.Contains("Paddington", shared.Entries.Select(e => e.Representative.Name));
        Assert.Equal(new[] { "More Movies" }, shared.Entries.Single(e => e.Representative.Name == "Paddington").Representative.LibraryIds.Select(id => shared.Libraries.Single(l => l.Id == id).Name));
    }

    [Fact]
    public void IsEnabled_ReadsTheLibrarysOptions()
    {
        // The default the server uses: the library's options.xml as CollectionFolder caches it.
        CollectionFolder.XmlSerializer ??= new Moq.Mock<MediaBrowser.Model.Serialization.IXmlSerializer>().Object;
        var enabledPath = $@"C:\lm-tests\{Guid.NewGuid():N}\Enabled";
        var disabledPath = $@"C:\lm-tests\{Guid.NewGuid():N}\Disabled";
        CollectionFolder.SaveLibraryOptions(enabledPath, new MediaBrowser.Model.Configuration.LibraryOptions { Enabled = true });
        CollectionFolder.SaveLibraryOptions(disabledPath, new MediaBrowser.Model.Configuration.LibraryOptions { Enabled = false });

        Assert.True(MovieIndexBuilder.IsEnabled(new CollectionFolder { Name = "Enabled", Path = enabledPath }));
        Assert.False(MovieIndexBuilder.IsEnabled(new CollectionFolder { Name = "Disabled", Path = disabledPath }));
        Assert.True(MovieIndexBuilder.IsEnabled(new CollectionFolder { Name = "No path" }));
    }

    [Fact]
    public void ALibraryWithoutFolders_IsNeverQueried()
    {
        // An empty TopParentIds would mean every library.
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        library.Libraries.Add(new CollectionFolder { Id = Guid.NewGuid(), Name = "Empty", CollectionType = CollectionType.movies, PhysicalFolderIds = [] });
        library.Configuration = new PluginConfiguration();

        var content = library.Builder().Build(CancellationToken.None);

        Assert.Equal(3, library.IdQueries.Count);
        Assert.All(library.IdQueries, q => Assert.NotEmpty(q.TopParentIds));
        Assert.Contains("Empty", content.Libraries.Select(l => l.Name));
        Assert.Throws<ArgumentException>(() => MovieIndexBuilder.LibraryQuery([]));
    }

    [Fact]
    public void AMovieInTwoLibraries_IsOneRecord_InBoth()
    {
        // One folder can be in two libraries.
        var library = new DuplicatesLibrary();
        var heat = library.Add("Heat", 1995);
        library.Libraries[1].PhysicalFolderIds = [DuplicatesLibrary.MoreMovies, DuplicatesLibrary.Movies];

        var content = library.Builder().Build(CancellationToken.None);

        var entry = Assert.Single(content.Entries);
        Assert.Equal(heat.Id, entry.Representative.Id);
        Assert.Equal(new[] { library.Libraries[0].Id, library.Libraries[1].Id }, entry.Representative.LibraryIds);
        Assert.Equal(new[] { 1, 1 }, content.Libraries.Select(l => l.Movies));
        Assert.Equal(1, content.Stats.Movies);
    }

    [Fact]
    public void TheItems_AreReadInChunksOf500_WithTheProgress()
    {
        var library = new DuplicatesLibrary();
        for (var i = 0; i < 1201; i++)
        {
            library.Add("Movie " + i, 2000 + (i % 20));
        }

        var progress = new List<double>();
        var content = library.Builder().Build(CancellationToken.None, progress.Add);

        var chunks = library.Fake.Queries.Where(q => q.ItemIds.Length > 0).Select(q => q.ItemIds.Length).ToList();
        Assert.Equal(new[] { 500, 500, 201 }, chunks);
        Assert.Equal(3, content.ItemQueries);
        Assert.Equal(2, content.LibraryQueries);
        Assert.Equal(1201, content.Stats.Movies);
        Assert.Equal(new[] { 0.333333, 0.666667, 1, 1 }, progress.Select(p => Math.Round(p, 6)));
    }

    [Fact]
    public void ExcludedFoldersAndNamelessMovies_AreLeftOut_ButStayInTheirSets()
    {
        // The clip is made first, so "Group versions" makes it the primary.
        var library = new DuplicatesLibrary();
        var clip = library.Add("Heat", 1995, file: "Heat (1995) - clip.mkv");
        clip.Path = @"C:\media\Movies\Clips\Heat - clip.mkv";
        var heat = library.Add("Heat", 1995);
        var nameless = library.Add(" ", 2001);
        var ronin = library.Add("Ronin", 1998);
        Assert.Equal(clip.Id, DuplicatesLibrary.MergeVersions(clip, heat).Id);
        library.Configuration = new PluginConfiguration { IncludedLibraryIds = [library.Libraries[0].Id], ExcludedPathPrefixes = [@"c:/media/movies/clips/"] };

        var content = library.Builder().Build(CancellationToken.None);

        Assert.Equal(1, content.Stats.Excluded);
        Assert.Equal(1, content.Stats.Skipped);
        Assert.Equal(2, content.Stats.Movies);
        Assert.Equal(new[] { heat.Id, ronin.Id }, content.Entries.Select(e => e.Representative.Id));

        // The excluded primary still holds the set together: one entry, the indexed member its representative.
        var entry = content.Entries[0];
        Assert.Equal(new[] { heat.Id }, entry.Members.Select(m => m.Id));
        Assert.Equal(new[] { clip.Id }, entry.ExternalIds);
        Assert.Equal(clip.Id, entry.RootId);
        Assert.Equal(2, entry.SourceCount);
        Assert.DoesNotContain(content.Entries, e => e.AllIds.Contains(nameless.Id));
    }

    [Theory]
    [InlineData(@"C:\media\Clips\a.mkv", @"C:\media\Clips", true)]
    [InlineData(@"C:\media\Clips\a.mkv", @"C:\media\Clips\", true)]
    [InlineData(@"C:\media\Clips\a.mkv", @"c:/MEDIA/clips", true)]
    [InlineData(@"C:\media\Clips\sub\a.mkv", @"C:\media\Clips", true)]
    [InlineData(@"C:\media\Clips", @"C:\media\Clips", true)]
    [InlineData(@"C:\media\Clips2\a.mkv", @"C:\media\Clips", false)]
    [InlineData(@"C:\media\Clipsa.mkv", @"C:\media\Clips", false)]
    [InlineData(@"C:\media\Clips\a.mkv", "", false)]
    [InlineData(@"C:\media\Clips\a.mkv", "   ", false)]
    [InlineData(@"C:\media\Clips\a.mkv", @"media\Clips", false)]
    [InlineData("", @"C:\media", false)]
    public void IsExcluded_ComparesAtASeparator_IgnoringCase(string path, string prefix, bool excluded)
        => Assert.Equal(excluded, MovieIndexBuilder.IsExcluded(path, [prefix]));

    [Fact]
    public void TheVersionSets_AreEntries_WithTheirStates()
    {
        var library = new DuplicatesLibrary();
        var a = library.Add("Blade Runner", 1982);
        var b = library.Add("Blade Runner", 1982, file: "Blade Runner (1982) - Final Cut.mkv");
        var other = library.Add("Blade Runner", 1982, DuplicatesLibrary.MoreMovies);
        var alien = new[] { library.Add("Alien", 1979), library.Add("Alien", 1979, file: "b.mkv"), library.Add("Alien", 1979, file: "c.mkv") };
        library.Delete(DuplicatesLibrary.MergeVersions(alien));
        var kids = library.Add("Paddington", 2014, DuplicatesLibrary.Kids);
        var paddington = library.Add("Paddington", 2014);
        DuplicatesLibrary.MergeVersions(a, b);
        DuplicatesLibrary.MergeVersions(kids, paddington);
        // Jellyfin keeps an existing primary when it merges a newer list into it: the primary comes after its alternate.
        var alternate = library.Add("Heat", 1995, file: "Heat (1995) - 4K.mkv");
        var primary = library.Add("Heat", 1995);
        var extended = library.Add("Heat", 1995, file: "Heat (1995) - Extended.mkv");
        DuplicatesLibrary.MergeVersions(primary, extended);
        Assert.Same(primary, DuplicatesLibrary.MergeVersions(alternate, primary));

        var content = library.Builder().Build(CancellationToken.None);

        Assert.Equal(9, content.Stats.Movies);
        Assert.Equal(6, content.Stats.Entries);
        Assert.Equal(3, content.Stats.MergedEntries);
        Assert.Equal(2, content.Stats.BrokenEntries);
        Assert.Equal(2, content.Stats.States[VersionSetState.Orphaned]);
        Assert.Equal(0, content.Stats.States[VersionSetState.Cycle]);

        var merged = content.Entries.Single(e => e.RootId == a.Id);
        Assert.Equal(new[] { a.Id, b.Id }, merged.Members.Select(m => m.Id));
        Assert.Equal(2, merged.SourceCount);
        Assert.Equal(VersionSetState.None, merged.State);
        Assert.Equal(new[] { library.Libraries[0].Id }, merged.LibraryIds);
        Assert.Contains(content.Entries, e => e.Representative.Id == other.Id);

        // The primary represents its set, even when the library lists its alternate first.
        var heat = content.Entries.Single(e => e.RootId == primary.Id);
        Assert.Equal(primary.Id, heat.Representative.Id);
        Assert.Equal(new[] { primary.Id, alternate.Id, extended.Id }, heat.Members.Select(m => m.Id));

        // The primary in Kids, which the settings leave out, is found through the library and named external.
        var external = content.Entries.Single(e => e.RootId == kids.Id);
        Assert.Equal(paddington.Id, external.Representative.Id);
        Assert.Equal(new[] { kids.Id }, external.ExternalIds);
        Assert.Contains(kids.Id, library.Fake.IdLookups);
        Assert.Equal(2, external.SourceCount);
    }

    [Fact]
    public void TheRecord_KeepsWhatGroupingAndTheCardsNeed()
    {
        var library = new DuplicatesLibrary();
        var movie = library.Add("Heat", 1995);
        movie.OriginalTitle = "Heat";
        movie.PremiereDate = new DateTime(1995, 12, 15, 0, 0, 0, DateTimeKind.Utc);
        movie.ProviderIds[MetadataProvider.Tmdb.ToString()] = "949";
        movie.ProviderIds[MetadataProvider.Imdb.ToString()] = "tt0113277";
        movie.ProviderIds[MetadataProvider.Tvdb.ToString()] = "123";
        movie.Width = 1920;
        movie.Height = 800;
        movie.Size = 31_400_000_000;
        movie.TotalBitrate = 21_000_000;
        movie.Container = "mkv";
        movie.AdditionalParts = [@"C:\media\Movies\Heat (1995)\Heat (1995) - part2.mkv"];
        var unknownSize = library.Add("Ronin", 1998);

        var content = library.Builder().Build(CancellationToken.None);

        var record = content.Entries[0].Representative;
        Assert.Equal("Heat", record.Name);
        Assert.Equal(1995, record.ProductionYear);
        Assert.Equal(1995, record.PremiereYear);
        Assert.Equal("949", record.Tmdb);
        Assert.Equal("tt0113277", record.Imdb);
        Assert.Equal((1920, 800), (record.Width!.Value, record.Height!.Value));
        Assert.Equal(31_400_000_000, record.Size);
        Assert.Equal(21_000_000, record.TotalBitrate);
        Assert.Equal("mkv", record.Container);
        Assert.Equal(2, record.PartCount);
        Assert.Equal(@"C:\media\Movies\Heat (1995)", record.ContainingFolder);
        Assert.Equal(VideoType.VideoFile, record.VideoType);
        Assert.Null(record.PrimaryVersionId);

        var ronin = content.Entries.Single(e => e.Representative.Id == unknownSize.Id).Representative;
        Assert.Null(ronin.Width);
        Assert.Null(ronin.Tmdb);
    }

    [Fact]
    public void ACancelledBuild_Stops()
    {
        var library = new DuplicatesLibrary();
        library.Add("Heat", 1995);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => library.Builder().Build(cancelled.Token));
    }

    [Fact]
    public void AnEmptyLibrary_IsAnEmptyIndex()
    {
        var library = new DuplicatesLibrary();

        var content = library.Builder().Build(CancellationToken.None);

        Assert.Empty(content.Entries);
        Assert.Equal(0, content.ItemQueries);
        Assert.Equal(2, content.Libraries.Count);
    }
}
