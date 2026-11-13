using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Moq;
using Jellyfin.Plugin.Kit.TestSupport;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Duplicates;

/// <summary>
/// A movie library for Duplicates' tests: the kit's <see cref="FakeLibrary"/> with three library folders
/// (two the index reads, one it doesn't), and what Jellyfin 10.11.5 itself does to merged versions: "Group versions",
/// "Split versions", deleting an item, a file renamed on disk, a link whose lookup once failed. For the index builder
/// (<c>MovieIndexBuilderTests</c>) it also answers <c>GetItemIds</c> with 10.11.5's <c>HasOwnerId</c>
/// semantics: <c>false</c> matches only a NULL <c>OwnerId</c> (rows migrated from 10.10, <see cref="MigratedFrom1010"/>),
/// while every item saved on 10.11 stores the zero GUID; and owned items have no top parent unless a test gives one.
/// </summary>
internal sealed class DuplicatesLibrary
{
    /// <summary>The Movies library's folder (its top parent).</summary>
    public static readonly Guid Movies = new("a0000000-0000-0000-0000-000000000001");

    /// <summary>A second movies library the index reads.</summary>
    public static readonly Guid MoreMovies = new("a0000000-0000-0000-0000-000000000002");

    /// <summary>A library the index doesn't read (not included in the settings).</summary>
    public static readonly Guid Kids = new("a0000000-0000-0000-0000-000000000003");

    private readonly Dictionary<Guid, Guid> _folderOf = [];
    private readonly Provider _provider;
    private PluginConfiguration _configuration;
    private int _next = 0x100;

    public DuplicatesLibrary()
    {
        Fake.Mock.Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>())).Returns((InternalItemsQuery query) => ItemIds(query));
        Libraries =
        [
            new CollectionFolder { Id = new Guid("c0000000-0000-0000-0000-000000000001"), Name = "Movies", CollectionType = CollectionType.movies, PhysicalFolderIds = [Movies] },
            new CollectionFolder { Id = new Guid("c0000000-0000-0000-0000-000000000002"), Name = "More Movies", CollectionType = null, PhysicalFolderIds = [MoreMovies] },
            new CollectionFolder { Id = new Guid("c0000000-0000-0000-0000-000000000003"), Name = "Kids", CollectionType = CollectionType.movies, PhysicalFolderIds = [Kids] },
            new CollectionFolder { Id = new Guid("c0000000-0000-0000-0000-000000000004"), Name = "Shows", CollectionType = CollectionType.tvshows, PhysicalFolderIds = [new Guid("a0000000-0000-0000-0000-000000000004")] },
        ];
        _configuration = new PluginConfiguration { IncludedLibraryIds = [Libraries[0].Id, Libraries[1].Id] };
        _provider = new Provider(this);
    }

    public FakeLibrary Fake { get; } = new();

    /// <summary>Gets the server's libraries: Movies (movies), More Movies (mixed), Kids (movies) and Shows (tvshows).</summary>
    public List<CollectionFolder> Libraries { get; }

    /// <summary>Gets the items stored with a NULL <c>OwnerId</c> (migrated from 10.10); the rest store the zero GUID.</summary>
    public HashSet<Guid> MigratedFrom1010 { get; } = [];

    /// <summary>Gets every <c>GetItemIds</c> query, in order.</summary>
    public List<InternalItemsQuery> IdQueries { get; } = [];

    /// <summary>Gets or sets what <c>GetItemIds</c> does first (a test's block or fault).</summary>
    public Action<InternalItemsQuery>? BeforeItemIds { get; set; }

    /// <summary>
    /// Gets or sets the settings the builder reads; by default Movies and More Movies are included and Kids is not, as
    /// the version-set fixtures expect. Setting it is a save of the settings: <see cref="IPluginConfigurationProvider.Changed"/>
    /// is raised with the settings before and after (changing a property in place raises nothing).
    /// </summary>
    public PluginConfiguration Configuration
    {
        get => _configuration;
        set
        {
            var previous = _configuration;
            _configuration = value;
            _provider.Raise(previous, value);
        }
    }

    /// <summary>Gets the provider of <see cref="Configuration"/> (one per library, so its <c>Changed</c> reaches every service).</summary>
    public IPluginConfigurationProvider ConfigurationProvider => _provider;

    /// <summary>Gets a builder over this library and <see cref="Configuration"/>.</summary>
    public MovieIndexBuilder Builder() => new(Fake.Object, ConfigurationProvider, () => Libraries, IsEnabled);

    /// <summary>Gets or sets whether a library is enabled (<c>LibraryOptions.Enabled</c>); null is the builder's default.</summary>
    public Func<CollectionFolder, bool>? IsEnabled { get; set; }

    /// <summary>
    /// Adds an owned item (a local alternate version or an extra, <c>OwnerId</c> set) as a movie, so the fake's
    /// <c>IncludeItemTypes</c> keeps it; with <paramref name="topParent"/> the fake's <c>TopParentIds</c> keeps it too,
    /// which the server never does.
    /// </summary>
    public Movie AddOwned(Video owner, string file, Guid? topParent = null)
    {
        var item = new Movie { Name = owner.Name, OwnerId = owner.Id, Path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(owner.Path)!, file) };
        Fake.Add(item);
        if (topParent is { } folder)
        {
            Fake.SetTopParent(item, folder);
        }

        return item;
    }

    /// <summary>
    /// Adds a movie with an id that sorts in the order of creation (native "Group versions" orders by id), in a library
    /// folder, at <c>C:\media\&lt;folder&gt;\&lt;Name&gt; (&lt; Year&gt; )\&lt;file&gt;</c>.
    /// </summary>
    public Movie Add(string name, int? year, Guid? folder = null, string? file = null)
    {
        var library = folder ?? Movies;
        var id = new Guid(string.Create(CultureInfo.InvariantCulture, $"b0000000-0000-0000-0000-{_next++:x12}"));
        var directory = $@"C:\media\{FolderName(library)}\{name} ({year})";
        var movie = new Movie
        {
            Id = id,
            Name = name,
            SortName = name,
            ProductionYear = year,
            RunTimeTicks = TimeSpan.FromMinutes(110).Ticks,
            Path = $@"{directory}\{file ?? $"{name} ({year}).mkv"}"
        };
        Fake.Add(movie);
        Fake.SetTopParent(movie, library);
        _folderOf[id] = library;
        return movie;
    }

    /// <summary>
    /// The videos the index reads from the given library folders (default: <see cref="Movies"/> and
    /// <see cref="MoreMovies"/>), in the library's order.
    /// </summary>
    public IReadOnlyList<Video> Indexed(params Guid[] folders)
    {
        var read = folders.Length == 0 ? [Movies, MoreMovies] : folders;
        return Fake.Items.OfType<Video>().Where(v => _folderOf.TryGetValue(v.Id, out var f) && read.Contains(f)).ToList();
    }

    /// <summary>
    /// The version sets of the indexed videos as the index builds them: the index is the lookup, with the library's
    /// <c>GetItemById</c> for items outside it, and the library's <c>FindByPath</c> for links.
    /// </summary>
    public IReadOnlyList<VersionSet> Group(params Guid[] folders)
    {
        var index = Indexed(folders).Select(VersionMember.From).ToList();
        var byId = index.ToDictionary(m => m.Id);
        var library = VersionSets.LibraryLookup(Fake.Object);
        return VersionSets.Group(index, id => byId.TryGetValue(id, out var member) ? member : library(id), VersionSets.LibraryPaths(Fake.Object));
    }

    /// <summary>
    /// Jellyfin 10.11.5's POST /Videos/MergeVersions on in-memory videos, as the kit's tests model it
    /// (<c>VideosController.cs:185-253</c>): the items ordered by id; the primary is the first with more than one source and
    /// no pointer, else the first; every other item points at it, is added to its list unless a listed path is the same
    /// (ignoring case), and brings its own list along, which is then cleared.
    /// </summary>
    public static Video MergeVersions(params Video[] videos)
    {
        var items = videos.OrderBy(i => i.Id).ToList();
        var primary = items.FirstOrDefault(i => i.LinkedAlternateVersions.Length + 1 > 1 && i.PrimaryVersionText() is null) ?? items[0];
        var list = primary.LinkedAlternateVersions.ToList();
        foreach (var item in items.Where(i => i.Id != primary.Id))
        {
            item.SetPrimaryVersion(primary.Id);
            if (!list.Any(l => l.ItemId == item.Id || string.Equals(l.LinkPath(), item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(JellyfinLine.Link(item.Id, item.Path));
            }

            foreach (var linked in item.LinkedAlternateVersions)
            {
                if (!list.Any(l => (l.ItemId is { } id && id == linked.ItemId) || (l.LinkPath() is { } path && string.Equals(path, linked.LinkPath(), StringComparison.OrdinalIgnoreCase))))
                {
                    list.Add(linked);
                }
            }

            item.LinkedAlternateVersions = [];
        }

        primary.LinkedAlternateVersions = list.ToArray();
        return primary;
    }

    /// <summary>
    /// Jellyfin 10.11.5's DELETE /Videos/{id}/AlternateSources ("Split versions", <c>VideosController.cs:143-174</c>):
    /// every listed version's pointer and list cleared and saved, then the primary's. With <paramref name="savedVersions"/>
    /// the call stops after that many versions were saved (a crash, a database error), leaving the primary's list.
    /// </summary>
    public void SplitVersions(Video primary, int? savedVersions = null)
    {
        var versions = primary.LinkedAlternateVersions
            .Select(l => l.ItemId is { } id && id != Guid.Empty ? Fake.Object.GetItemById(id) as Video : null)
            .OfType<Video>()
            .ToList();
        foreach (var version in versions.Take(savedVersions ?? versions.Count))
        {
            version.SetPrimaryVersion(null);
            version.LinkedAlternateVersions = [];
        }

        if (savedVersions is null)
        {
            primary.LinkedAlternateVersions = [];
            primary.SetPrimaryVersion(null);
        }
    }

    /// <summary>Deletes an item as <c>LibraryManager.DeleteItem</c> does: nothing clears the pointers at it.</summary>
    public void Delete(BaseItem item) => Fake.Remove(item, raiseEvent: false);

    /// <summary>
    /// A file renamed on disk: the next scan removes the old item and adds a new one with a new id and path, and no
    /// pointer (a new item has none); the primary's link still names the old id and path.
    /// </summary>
    public Movie Rename(Movie movie, string newFile)
    {
        Delete(movie);
        var renamed = Add(movie.Name, movie.ProductionYear, _folderOf[movie.Id], newFile);
        _folderOf.Remove(movie.Id);
        return renamed;
    }

    /// <summary>
    /// A link whose lookup failed once (the drive was offline during a refresh): Jellyfin caches the empty GUID and never
    /// looks the entry up again, so the link stays dead after the file is back (<c>BaseItem.GetLinkedChild</c>).
    /// </summary>
    public static void LookupFailedOnce(Video primary, Video version)
    {
        foreach (var link in primary.LinkedAlternateVersions.Where(l => l.ItemId == version.Id))
        {
            link.ItemId = Guid.Empty;
        }
    }

    private List<Guid> ItemIds(InternalItemsQuery query)
    {
        IdQueries.Add(query);
        BeforeItemIds?.Invoke(query);
        return Fake.Object.GetItemList(query)
            .Where(i => query.HasOwnerId switch
            {
                false => i.OwnerId == Guid.Empty && MigratedFrom1010.Contains(i.Id),
                true => i.OwnerId != Guid.Empty,
                null => true
            })
            .Select(i => i.Id)
            .ToList();
    }

    private static string FolderName(Guid folder) => folder == Movies ? "Movies" : folder == MoreMovies ? "More Movies" : "Kids";

    private sealed class Provider(DuplicatesLibrary library) : IPluginConfigurationProvider
    {
        public event EventHandler<PluginConfigurationChangedEventArgs>? Changed;

        public PluginConfiguration GetConfiguration() => library.Configuration;

        public void Save(PluginConfiguration configuration) => library.Configuration = configuration;

        public void Raise(PluginConfiguration previous, PluginConfiguration current) => Changed?.Invoke(this, new PluginConfigurationChangedEventArgs(previous, current));
    }
}
