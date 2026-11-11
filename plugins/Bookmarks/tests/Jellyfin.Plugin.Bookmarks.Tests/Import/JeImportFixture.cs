using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Import;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Bookmarks.Tests.TestSupport;
using Jellyfin.Plugin.Kit.Interop;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.Kit.TestSupport.Logging;
using Jellyfin.Plugin.Kit.TestSupport.PluginIdentity;
using Jellyfin.Plugin.Kit.TestSupport.Time;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Jellyfin.Plugin.Bookmarks.Tests.Import;

/// <summary>
/// The Jellyfin Enhanced import's test bed: a temporary plugin configurations folder with JE's data folder, the
/// kit's reader over it, a real bookmark store, the kit's <see cref="FakeLibrary"/> built from the 81-style fixture's
/// <c>library.json</c> (<c>kit/testdata/je-data/import-81</c>), its two users (<c>…81</c> admin, <c>…82</c> viewer; <c>…83</c>
/// is no user of the server) and the importer.
/// </summary>
internal sealed class JeImportFixture : IDisposable
{
    public static readonly Guid AdminId = Guid.ParseExact("00000000000000000000000000000081", "N");
    public static readonly Guid ViewerId = Guid.ParseExact("00000000000000000000000000000082", "N");
    public static readonly Guid UnknownId = Guid.ParseExact("00000000000000000000000000000083", "N");
    public static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public static readonly string FixtureFolder = Path.Combine(PluginIdentityChecks.FindRepositoryRoot(), "kit", "testdata", "je-data", "import-81");

    private readonly TempDirectory _directory = new();
    private readonly List<IDisposable> _disposables = [];
    private readonly Dictionary<string, BaseItem> _items = new(StringComparer.Ordinal);

    public JeImportFixture()
    {
        Admin = TestData.CreateUser("admin");
        Admin.Id = AdminId;
        Viewer = TestData.CreateUser("viewer");
        Viewer.Id = ViewerId;
        Users.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns((Guid id) => id == AdminId ? Admin : id == ViewerId ? Viewer : null);
        Directory.CreateDirectory(DataFolder);
        Recreate();
    }

    public User Admin { get; }

    public User Viewer { get; }

    public Mock<IUserManager> Users { get; } = new();

    public FakeLibrary Library { get; } = new();

    public ManualTimeProvider Clock { get; } = new(Now);

    public ListLogger Logger { get; } = new();

    public string ConfigurationsFolder => _directory.Combine("config");

    public string DataFolder => Path.Combine(ConfigurationsFolder, JellyfinEnhancedFiles.ConfigurationName);

    public string StoreFile => _directory.Combine("bookmarks", "bookmarks.json");

    public string SnapshotsFolder => _directory.Combine("bookmarks", "snapshots");

    public string JournalFile => _directory.Combine("moves.jsonl");

    public BookmarkStore Store { get; private set; } = null!;

    public JellyfinEnhancedReader Reader { get; private set; } = null!;

    public JellyfinEnhancedBookmarkImporter Importer { get; private set; } = null!;

    public BaseItem Item(string key) => _items[key];

    /// <summary>Builds the fixture's library: its films, and the series with its episodes.</summary>
    public void BuildLibrary()
    {
        var library = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureFolder, "library.json")))!.AsObject();
        var seriesNode = library["Series"]!.AsObject();
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = (string)seriesNode["Name"]!,
            ProductionYear = (int)seriesNode["Year"]!,
            Path = @"C:\media\Shows\Test Series (2020) [tvdbid-900401]"
        };
        foreach (var (provider, value) in seriesNode["ProviderIds"]!.AsObject())
        {
            series.ProviderIds[provider] = (string)value!;
        }

        Library.Add(series);
        var seasons = new Dictionary<int, Season>();
        foreach (var node in library["Items"]!.AsArray())
        {
            var item = node!.AsObject();
            var id = Guid.ParseExact((string)item["Id"]!, "N");
            var runtime = TimeSpan.FromSeconds((int)item["RuntimeSeconds"]!).Ticks;
            BaseItem added;
            if ((string)item["Kind"]! == "Episode")
            {
                var seasonNumber = (int)item["Season"]!;
                if (!seasons.TryGetValue(seasonNumber, out var season))
                {
                    season = seasons[seasonNumber] = Library.AddSeason(series, seasonNumber);
                }

                var number = (int)item["Episode"]!;
                added = Library.Add(
                    new Episode
                    {
                        Id = id,
                        Name = "Episode " + number.ToString(CultureInfo.InvariantCulture),
                        IndexNumber = number,
                        ParentIndexNumber = seasonNumber,
                        SeriesId = series.Id,
                        SeasonId = season.Id,
                        SeriesName = series.Name,
                        RunTimeTicks = runtime,
                        Path = string.Create(CultureInfo.InvariantCulture, $@"{season.Path}\Test Series (2020) S{seasonNumber:00}E{number:00}.mkv")
                    },
                    season);
            }
            else
            {
                var name = (string)item["Name"]!;
                var year = (int)item["Year"]!;
                var movie = new Movie
                {
                    Id = id,
                    Name = name,
                    SortName = name,
                    ProductionYear = year,
                    RunTimeTicks = runtime,
                    Path = string.Create(CultureInfo.InvariantCulture, $@"C:\media\Movies\{name} ({year})\{name} ({year}).mkv")
                };
                foreach (var (provider, value) in item["ProviderIds"]!.AsObject())
                {
                    movie.ProviderIds[provider] = (string)value!;
                }

                added = Library.Add(movie);
            }

            _items[(string)item["Key"]!] = added;
        }
    }

    /// <summary>Copies the fixture's user folders into JE's data folder.</summary>
    public void CopyFixtureFiles()
    {
        foreach (var folder in Directory.EnumerateDirectories(FixtureFolder))
        {
            var target = Path.Combine(DataFolder, Path.GetFileName(folder));
            Directory.CreateDirectory(target);
            File.Copy(Path.Combine(folder, JellyfinEnhancedFiles.BookmarksFile), Path.Combine(target, JellyfinEnhancedFiles.BookmarksFile), overwrite: true);
        }
    }

    /// <summary>Writes one user's <c>bookmark.json</c>.</summary>
    public void WriteBookmarks(Guid userId, string json)
    {
        var folder = Path.Combine(DataFolder, userId.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, JellyfinEnhancedFiles.BookmarksFile), json);
    }

    /// <summary>Writes one user's <c>bookmark.json</c> as bytes (text that isn't UTF-8).</summary>
    public void WriteBookmarks(Guid userId, byte[] bytes)
    {
        var folder = Path.Combine(DataFolder, userId.ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, JellyfinEnhancedFiles.BookmarksFile), bytes);
    }

    /// <summary>Writes one user's <c>bookmark.json</c> from entries.</summary>
    public void WriteBookmarks(Guid userId, params (string Key, object Entry)[] entries)
        => WriteBookmarks(userId, JsonSerializer.Serialize(new { Bookmarks = entries.ToDictionary(e => e.Key, e => e.Entry) }));

    /// <summary>A JE entry as JE 12.11 writes it.</summary>
    public static Dictionary<string, object?> Entry(Guid itemId, double seconds, string? label, string updatedAt = "2026-05-22T21:23:40.598Z", string tmdb = "", string mediaType = "movie", string name = "A film (2000)")
        => new()
        {
            ["ItemId"] = itemId.ToString("N", CultureInfo.InvariantCulture),
            ["TmdbId"] = tmdb,
            ["TvdbId"] = string.Empty,
            ["MediaType"] = mediaType,
            ["Name"] = name,
            ["Timestamp"] = seconds,
            ["Label"] = label,
            ["CreatedAt"] = "2026-05-01T10:00:00.000Z",
            ["UpdatedAt"] = updatedAt,
            ["SyncedFrom"] = string.Empty,
            ["SeasonNumber"] = null,
            ["EpisodeNumber"] = null
        };

    /// <summary>Creates the store, the reader and the importer again over the same folders (a restart).</summary>
    public void Recreate()
    {
        Store?.Dispose();
        Importer?.Dispose();
        Store = BookmarkStore.Create(StoreFile, Logger, Clock);
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(ConfigurationsFolder);
        var services = new ServiceCollection().BuildServiceProvider();
        _disposables.Add(services);
        Reader = new JellyfinEnhancedReader(paths.Object, null, services, Logger, Clock) { PluginPagesLister = static _ => PluginPagesState.NotInstalled };
        var journal = new MoveJournalReader(JournalFile, Logger);
        Importer = new JellyfinEnhancedBookmarkImporter(Store, Reader, Library.Object, Users.Object, new IdentityResolver(Library.Object, journal, Clock), journal, SnapshotsFolder, Logger, Clock);
    }

    public void Dispose()
    {
        Importer?.Dispose();
        Store?.Dispose();
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }

        _directory.Dispose();
    }
}
