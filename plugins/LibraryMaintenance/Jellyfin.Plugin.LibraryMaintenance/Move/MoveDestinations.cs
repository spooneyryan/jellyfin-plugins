using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Move.Paths;
using Jellyfin.Plugin.LibraryMaintenance.Move.Planning;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.LibraryMaintenance.Move;

/// <summary>
/// Where an item can go (<c>GET /Move/Destinations</c> and <c>GET /Move/Folders</c>): the libraries of the
/// source's type the caller can open (only the source's own with <c>AllowCrossLibraryMoves</c> off), their folders with
/// drive, label, free space and whether they can be chosen, and a folder's subfolders with their badges. A network,
/// mapped or device location is listed but never opened.
/// </summary>
internal sealed class MoveDestinations
{
    /// <summary>The most subfolders on one page (200 per page).</summary>
    public const int MaxFolders = 200;

    private readonly LibraryLocationCatalog _catalog;
    private readonly DestinationResolver _resolver;
    private readonly IVolumeInfoProvider _volumes;
    private readonly ILibraryManager _library;
    private readonly UnitPlanner _planner;
    private readonly MoveFeature _feature;

    /// <summary>
    /// Initializes a new instance of the <see cref="MoveDestinations"/> class.
    /// </summary>
    /// <param name="catalog">The libraries' locations.</param>
    /// <param name="resolver">The destination resolver.</param>
    /// <param name="volumes">The volumes.</param>
    /// <param name="library">The library manager (a folder's Series or Season item).</param>
    /// <param name="planner">The unit planner (a folder's own-folder movie).</param>
    /// <param name="feature">The settings.</param>
    public MoveDestinations(LibraryLocationCatalog catalog, DestinationResolver resolver, IVolumeInfoProvider volumes, ILibraryManager library, UnitPlanner planner, MoveFeature feature)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(feature);
        _catalog = catalog;
        _resolver = resolver;
        _volumes = volumes;
        _library = library;
        _planner = planner;
        _feature = feature;
    }

    /// <summary>
    /// Gets whether an item in one library may move to another: its own, or one of the same type when cross-library moves
    /// are allowed.
    /// </summary>
    /// <param name="source">The source library.</param>
    /// <param name="destination">The destination library.</param>
    /// <returns>Whether it may.</returns>
    public bool IsCompatible(CatalogLibrary source, CatalogLibrary destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return source.Id == destination.Id
            || (_feature.Settings.AllowCrossLibraryMoves && source.CollectionType is not null && source.CollectionType == destination.CollectionType);
    }

    /// <summary>
    /// Gets the libraries an eligible item can move to, the source's first.
    /// </summary>
    /// <param name="source">The item, eligible.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<LibraryMaintenanceMoveDestinationLibraryDto> GetDestinations(EligibilityResult source, User? user)
    {
        ArgumentNullException.ThrowIfNull(source);
        var library = source.Library ?? throw new ArgumentException("An eligible item is needed.", nameof(source));

        // Eligibility walked it; walked again before its volume is asked (GetVolumePathNameW follows a link), as the disk
        // may have changed since. The unit's folder is asked, never the file.
        var root = MoveEligibility.UnitRootOf(source.Item!);
        if (LinkGuard.Find(library.Locations[source.LocationIndex], root) is { } link)
        {
            throw new PluginException(PluginErrorKind.Conflict, link.Message) { Code = MoveErrorCodes.ReparsePoint };
        }

        var sourceVolume = _volumes.GetVolume(root)?.VolumeId;
        return _catalog.GetLibraries(user)
            .Where(l => IsCompatible(library, l))
            .OrderBy(l => l.Id == library.Id ? 0 : 1)
            .Select(l => new LibraryMaintenanceMoveDestinationLibraryDto
            {
                LibraryId = l.Id,
                Name = l.Name,
                CollectionType = l.CollectionTypeName,
                IsSource = l.Id == library.Id,
                Locations = l.Locations.Select((path, index) => Location(path, index, sourceVolume)).ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// Gets a page of a destination folder's subfolders.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="locationIndex">Its folder's index.</param>
    /// <param name="relativePath">The folder under it, or empty for the library folder.</param>
    /// <param name="filter">Only names containing this, ignoring case.</param>
    /// <param name="startIndex">The first folder of the page.</param>
    /// <param name="limit">The page size, 1 to 200.</param>
    /// <param name="item">The item being moved, eligible, or null.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The page.</returns>
    /// <exception cref="PluginException">404 for a library the caller can't open or a folder index it doesn't have; 400 for a
    /// bad page, a bad path or a library the item can't move to; 503 for a folder that isn't reachable.</exception>
    public LibraryMaintenanceMoveFoldersDto GetFolders(Guid libraryId, int locationIndex, string? relativePath, string? filter, int startIndex, int limit, EligibilityResult? item, User? user)
    {
        if (startIndex < 0 || limit < 1 || limit > MaxFolders)
        {
            throw PluginException.Invalid("Ask for 1 to " + MaxFolders + " folders from an index of 0 or more.");
        }

        var library = _catalog.FindLibrary(libraryId, user) ?? throw PluginException.NotFound("There is no such library, or you can't open it.");
        if (locationIndex < 0 || locationIndex >= library.Locations.Count)
        {
            throw PluginException.NotFound("The library " + library.Name + " has no folder number " + locationIndex + ".");
        }

        if (item?.Library is { } source && !IsCompatible(source, library))
        {
            throw new PluginException(PluginErrorKind.Invalid, "This item can't move to " + library.Name + ".") { Code = MoveErrorCodes.DestinationInvalid };
        }

        var resolved = _resolver.Resolve(library, locationIndex, relativePath, null);
        var directory = resolved.ExistingDirectory;
        List<DirectoryInfo> folders;
        try
        {
            folders = new DirectoryInfo(directory).EnumerateDirectories("*", ForeignMediaGuard.Listing).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PluginException(PluginErrorKind.Unavailable, directory + " can't be listed.") { Code = MoveErrorCodes.NotWritable };
        }

        var text = filter?.Trim();
        var matching = folders
            .Where(f => string.IsNullOrEmpty(text) || f.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new LibraryMaintenanceMoveFoldersDto
        {
            Items = matching.Skip(startIndex).Take(limit).Select(f => Folder(f, resolved, item?.Item)).ToList(),
            TotalRecordCount = matching.Count,
            StartIndex = startIndex,
            Path = directory,
        };
    }

    private static LibraryMaintenanceMoveFolderDto Row(DirectoryInfo folder, string relative, string kind, string? reason)
        => new() { Name = folder.Name, RelativePath = relative, Kind = kind, Selectable = reason is null, Reason = reason };

    private LibraryMaintenanceMoveDestinationLocationDto Location(string path, int index, string? sourceVolume)
    {
        var location = new LibraryMaintenanceMoveDestinationLocationDto { Index = index, Path = path };
        if (_resolver.LocationProblem(path) is { } problem)
        {
            location.Reason = problem;
            return location;
        }

        // A location that is a link is never opened or asked for its volume (v1 fails closed).
        if (LinkGuard.Find(path, path) is { } link)
        {
            location.Reason = link.Unreadable ? "unreachable" : "reparse-point";
            return location;
        }

        if (!Directory.Exists(path))
        {
            location.Reason = "unreachable";
            return location;
        }

        var volume = _volumes.GetVolume(path);
        location.Reachable = true;
        location.Label = volume?.Label;
        location.FreeBytes = volume?.FreeBytes;
        location.SameVolumeAsSource = volume is not null && sourceVolume is not null && string.Equals(volume.VolumeId, sourceVolume, StringComparison.OrdinalIgnoreCase);
        if (DestinationResolver.FindIgnoreFile(path) is not null)
        {
            location.Reason = "ignored";
        }
        else if (!location.SameVolumeAsSource)
        {
            // v1 renames on one volume only; moving between drives comes with series and season moves.
            location.Reason = "cross-volume-later";
        }

        location.Selectable = location.Reason is null;
        return location;
    }

    private LibraryMaintenanceMoveFolderDto Folder(DirectoryInfo folder, ResolvedDestination parent, MediaBrowser.Controller.Entities.Video? item)
    {
        var relative = parent.Segments.Count == 0 ? folder.Name : parent.RelativePath + "\\" + folder.Name;
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return Row(folder, relative, "ReparsePoint", "This is a junction or symbolic link; Move doesn't follow links.");
        }

        if (PathRules.IsIgnoredName(folder.Name) || PathRules.CheckName(folder.Name) is not null || File.Exists(Path.Join(folder.FullName, ".ignore")))
        {
            return Row(folder, relative, "Ignored", "Jellyfin ignores this folder.");
        }

        if (PathRules.IsIgnoredExtrasFolder(folder.Name, parent.IsTopLevelLocation(parent.Directory)))
        {
            return Row(folder, relative, "Ignored", "Jellyfin ignores a folder of this name here: it reads it as the extras of the folder above.");
        }

        switch (KitItemQueries.FindAnyByPath(_library, folder.FullName, true))
        {
            case Season:
                return Row(folder, relative, "Season", null);
            case Series:
                return Row(folder, relative, "Series", null);
        }

        if (item is not null && MoveEligibility.KindOf(item) == MoveUnitKind.Folder && PathRules.SamePath(folder.FullName, item.ContainingFolderPath))
        {
            return Row(folder, relative, "MovieFolder", "This is the item's own folder.");
        }

        if (_planner.FindOwnFolderMovie(folder.FullName) is { } movie)
        {
            return Row(folder, relative, "MovieFolder", "This is the folder of " + movie.Name + "; one movie's folder can't hold another.");
        }

        return Row(folder, relative, "Plain", null);
    }
}
