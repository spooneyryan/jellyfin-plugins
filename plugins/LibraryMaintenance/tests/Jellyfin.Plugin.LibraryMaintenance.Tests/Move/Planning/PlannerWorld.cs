using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.Kit.TestSupport;
using Jellyfin.Plugin.Kit.TestSupport.Library;
using Jellyfin.Plugin.LibraryMaintenance.Move;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Moq;

namespace Jellyfin.Plugin.LibraryMaintenance.Tests.Move.Planning;

/// <summary>
/// A temp tree with libraries, a fake library manager (with Jellyfin's id rule: MD5 of the type's full name and the path,
/// case-sensitive as on this server), and volumes by path, for <see cref="UnitPlanner"/>'s tests.
/// </summary>
internal sealed class PlannerWorld : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly List<(string Root, string Volume)> _volumes = [];
    private readonly List<string> _otherLocations = [];

    public PlannerWorld()
    {
        Library = new FakeLibrary();
        Library.Mock.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns((string key, Type type) => IdOf(key, type));
        Movies = Folder("Movies");
        Cult = Folder("Cult");
        Tv = Folder("TV");
        MoviesLibrary = new CatalogLibrary(new Guid("c0000000-0000-0000-0000-0000000000c1"), "Movies", CollectionTypeOptions.movies, [Movies], null);
        CultLibrary = new CatalogLibrary(new Guid("c0000000-0000-0000-0000-0000000000c2"), "Cult", CollectionTypeOptions.movies, [Cult], null);
        TvLibrary = new CatalogLibrary(new Guid("c0000000-0000-0000-0000-0000000000c3"), "TV", CollectionTypeOptions.tvshows, [Tv], new LibraryOptions { EnableAutomaticSeriesGrouping = false });
        Home = Folder("Home");
        Private = Folder("Private");
        HomeLibrary = new CatalogLibrary(new Guid("c0000000-0000-0000-0000-0000000000c4"), "Home", CollectionTypeOptions.homevideos, [Home], null);
        PrivateLibrary = new CatalogLibrary(new Guid("c0000000-0000-0000-0000-0000000000c5"), "Private", CollectionTypeOptions.movies, [Private], null);
        Volumes = new FakeVolumes(_volumes);
        Planner = new UnitPlanner(Library.Object, Volumes, ValidFilename);

        // The catalog's view of them (GetVirtualFolders); Bob can't open Private.
        var all = new[] { MoviesLibrary, CultLibrary, TvLibrary, HomeLibrary, PrivateLibrary };
        Library.Mock.Setup(l => l.GetVirtualFolders()).Returns(() => all.Select(l => new VirtualFolderInfo { Name = l.Name, ItemId = l.Id.ToString("N"), CollectionType = l.CollectionType, Locations = [.. l.Locations], LibraryOptions = l.Options })
            .Append(new VirtualFolderInfo { Name = "Other", ItemId = "c00000000000000000000000000000c9", CollectionType = CollectionTypeOptions.movies, Locations = [.. _otherLocations] })
            .ToList());
        var folders = all.Select(l => new CollectionFolder { Id = l.Id }).ToList();
        IReadOnlyList<CollectionFolder> Visible(User? user) => user == Bob ? folders.Where(f => f.Id != PrivateLibrary.Id).ToList() : folders;
        Access = new LibraryAccess(Library.Object, Visible, Library.IsVisible);
        Catalog = new LibraryLocationCatalog(Library.Object, Access);
    }

    /// <summary>Gets a user who can't open Private.</summary>
    public User Bob { get; } = new("bob", "DefaultAuthenticationProvider", "DefaultPasswordResetProvider") { Id = new Guid("e0000000-0000-0000-0000-00000000000b") };

    public LibraryAccess Access { get; }

    public LibraryLocationCatalog Catalog { get; }

    public string Home { get; }

    public string Private { get; }

    public CatalogLibrary HomeLibrary { get; }

    public CatalogLibrary PrivateLibrary { get; }

    public FakeLibrary Library { get; }

    public FakeVolumes Volumes { get; }

    public UnitPlanner Planner { get; }

    public string Movies { get; }

    public string Cult { get; }

    public string Tv { get; }

    public CatalogLibrary MoviesLibrary { get; }

    public CatalogLibrary CultLibrary { get; }

    public CatalogLibrary TvLibrary { get; set; }

    public string Root => _temp.Path;

    // Jellyfin's ids: MD5 over the UTF-16 bytes of type.FullName + path (BaseItem/LibraryManager.GetNewItemIdInternal).
    public static Guid IdOf(string path, Type type) => new(MD5.HashData(Encoding.Unicode.GetBytes(type.FullName + path)));

    // 10.11.5's IFileSystem.GetValidFilename (ManagedFileSystem.cs:21-28, 305-330): each invalid character becomes a space.
    public static string ValidFilename(string name)
    {
        var invalid = new[] { '"', '<', '>', '|', '\0', ':', '*', '?', '\\', '/' };
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] < 32 || Array.IndexOf(invalid, chars[i]) >= 0)
            {
                chars[i] = ' ';
            }
        }

        return new string(chars);
    }

    public string Folder(params string[] parts)
    {
        var path = _temp.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public string File(string path, int bytes = 10)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void OnVolume(string root, string volume) => _volumes.Add((root, volume));

    /// <summary>Adds a location of another library, one no test user is asked about (GetVirtualFolders only).</summary>
    public void OtherLibraryLocation(string location) => _otherLocations.Add(location);

    public Movie Movie(string path, string name, int? year, bool mixed, params string[] sidecars)
    {
        File(path, 1000);
        foreach (var sidecar in sidecars)
        {
            File(Path.Join(Path.GetDirectoryName(path), sidecar));
        }

        var movie = new Movie { Id = IdOf(path, typeof(Movie)), Name = name, ProductionYear = year, Path = path, IsInMixedFolder = mixed };
        return Library.Add(movie);
    }

    public Trailer Trailer(Movie owner, string path)
    {
        File(path, 100);
        var trailer = Library.Add(new Trailer { Id = IdOf(path, typeof(Trailer)), Name = "Trailer", Path = path, ExtraType = ExtraType.Trailer, OwnerId = owner.Id });
        owner.AddExtraId(trailer.Id);
        return trailer;
    }

    public Video Extra(Movie owner, string path, ExtraType type)
    {
        File(path, 100);
        var extra = Library.Add(new Video { Id = IdOf(path, typeof(Video)), Name = "Extra", Path = path, ExtraType = type, OwnerId = owner.Id });
        owner.AddExtraId(extra.Id);
        return extra;
    }

    public Series Series(string name, string? seriesId = null)
    {
        var path = Folder("TV", name);
        return Library.Add(new Series { Id = seriesId is null ? IdOf(path, typeof(Series)) : new Guid(seriesId), Name = name, Path = path });
    }

    public Season Season(Series series, int number, string? folder = null)
    {
        var path = Folder("TV", series.Name, folder ?? "Season " + number);
        return Library.Add(new Season { Id = IdOf(path, typeof(Season)), Name = "Season " + number, Path = path, IndexNumber = number, SeriesId = series.Id }, series);
    }

    public Episode Episode(Series series, Season? season, string fileName, int? seasonNumber, params string[] sidecars)
    {
        var path = Path.Join(season?.Path ?? series.Path, fileName);
        File(path, 500);
        foreach (var sidecar in sidecars)
        {
            File(Path.Join(Path.GetDirectoryName(path), sidecar));
        }

        return Library.Add(new Episode { Id = IdOf(path, typeof(Episode)), Name = fileName, Path = path, SeriesId = series.Id, SeriesName = series.Name, ParentIndexNumber = seasonNumber, IsInMixedFolder = true }, season ?? (BaseItem)series);
    }

    public MoveUnit Plan(Video item, CatalogLibrary source, CatalogLibrary destination, string? relativePath = null, string? newFolder = null, bool? wrap = null, string? wrapName = null, MoveSettings? settings = null, UnitPlanner? planner = null)
    {
        var resolved = new DestinationResolver(static _ => false).Resolve(destination, 0, relativePath, newFolder);
        return (planner ?? Planner).Plan(new UnitPlanRequest(item, source, 0, resolved, wrap, wrapName, settings ?? new MoveSettings()));
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Volumes by path: everything on "vol-c" unless a root says otherwise.</summary>
    internal sealed class FakeVolumes(List<(string Root, string Volume)> roots) : IVolumeInfoProvider
    {
        public List<string> Asked { get; } = [];

        public VolumeInfo? GetVolume(string path)
        {
            Asked.Add(path);
            foreach (var (root, volume) in roots)
            {
                if (PathRules.IsSameOrUnder(path, root))
                {
                    return new VolumeInfo(root, volume, 1L << 40, null);
                }
            }

            return new VolumeInfo(Path.GetPathRoot(path)!, "vol-c", 1L << 40, null);
        }
    }
}
