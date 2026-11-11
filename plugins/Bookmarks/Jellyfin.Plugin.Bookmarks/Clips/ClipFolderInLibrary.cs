using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Bookmarks.Storage;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// The warning that a clip folder lies in a Movies or TV library (since Bookmarks 1.2.1; clips should never go in one):
/// Jellyfin takes the clips there for the films or episodes they come from and shows them as duplicates. The settings page
/// asks as the admin types (<see cref="Check"/>, <c>POST /Bookmarks/Admin/ClipFolder/Check</c>), and Diagnostics'
/// <c>ClipStorage</c> warns of the saved folder (<see cref="AddTo"/>, code <see cref="WarningCode"/>).
/// </summary>
/// <remarks>The library is found by its folders (<see cref="ClipLibraryLocations"/>, kept a minute): the clip folder is one
/// of them or lies inside one. Its name is given only to an admin who may open it; Diagnostics, read without a caller,
/// says its type only. Other library types (Home Videos and Photos, the right one for clips; Music; mixed content) are
/// left alone.</remarks>
internal sealed class ClipFolderInLibrary
{
    /// <summary>The warning's code, in Diagnostics' <c>ClipStorage.Warnings</c> and the check's <c>Warnings</c>.</summary>
    public const string WarningCode = "InVideoLibrary";

    private readonly ClipLibraryLocations _locations;
    private readonly LibraryAccess _libraries;
    private readonly IUserManager _users;
    private readonly BookmarkPaths _paths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipFolderInLibrary"/> class.
    /// </summary>
    /// <param name="locations">The libraries' folders.</param>
    /// <param name="libraries">The libraries an admin may open, for a library's name.</param>
    /// <param name="users">The users: the admin who asks.</param>
    /// <param name="paths">The plugin's paths: the default clip folder.</param>
    /// <param name="logger">The logger.</param>
    public ClipFolderInLibrary(ClipLibraryLocations locations, LibraryAccess libraries, IUserManager users, BookmarkPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(locations);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _locations = locations;
        _libraries = libraries;
        _users = users;
        _paths = paths;
        _logger = logger;
    }

    /// <summary>
    /// The warning's sentence.
    /// </summary>
    /// <param name="type">The library's type: movies or TV.</param>
    /// <param name="libraryName">The library's name, for an admin who may open it; else <c>null</c>.</param>
    /// <returns>The sentence.</returns>
    public static string Message(CollectionTypeOptions type, string? libraryName)
    {
        var kind = type == CollectionTypeOptions.movies ? "Movies" : "TV";
        var library = string.IsNullOrWhiteSpace(libraryName) ? "a " + kind + " library" : "the " + kind + " library \"" + libraryName.Trim() + "\"";
        var what = type == CollectionTypeOptions.movies ? "films" : "episodes";
        return "This folder is in " + library + ": Jellyfin takes clips there for the " + what + " they come from and shows them as duplicates. "
            + "Choose a folder of its own outside every Movies and TV library, or leave the field empty for the default folder.";
    }

    /// <summary>
    /// Finds the Movies or TV library a folder is in: one of its folders, or inside one (the deepest).
    /// </summary>
    /// <param name="folder">The folder, as typed.</param>
    /// <returns>The library's folder, or <c>null</c>.</returns>
    public ClipLibraryLocation? Find(string? folder)
    {
        if (!ClipRootPaths.TryNormalize(folder, out var full))
        {
            return null;
        }

        return _locations.GetAll().FirstOrDefault(location
            => location.CollectionType is CollectionTypeOptions.movies or CollectionTypeOptions.tvshows && ClipRootPaths.IsUnder(full, location.Path));
    }

    /// <summary>
    /// Checks a clip folder as the admin types it: <c>{Path, Valid, Warnings}</c>, the folder normalised (the default one for
    /// an empty field), whether it is a folder path, and the warning when it lies in a Movies or TV library.
    /// </summary>
    /// <param name="path">The field's value.</param>
    /// <param name="adminId">The admin who asks, for the library's name.</param>
    /// <returns>The check.</returns>
    public IDictionary<string, object?> Check(string? path, Guid adminId)
    {
        var typed = string.IsNullOrWhiteSpace(path) ? _paths.DefaultClipsDirectory : path.Trim();
        var warnings = new List<IDictionary<string, object?>>();
        var result = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Warnings"] = warnings };
        if (!ClipRootPaths.TryNormalize(typed, out var folder))
        {
            result["Path"] = null;
            result["Valid"] = false;
            return result;
        }

        result["Path"] = folder;
        result["Valid"] = true;
        if (Find(folder) is { CollectionType: { } type } location)
        {
            var admin = adminId == Guid.Empty ? null : _users.GetUserById(adminId);
            warnings.Add(Warning(type, MayOpen(admin, location) ? location.LibraryName : null));
        }

        return result;
    }

    /// <summary>
    /// Adds the warning to Diagnostics' <c>ClipStorage</c> entry when its <c>Root</c> lies in a Movies or TV library.
    /// </summary>
    /// <param name="clipStorage">The entry (<see cref="ClipStorageGuard.DescribeCurrentRoot"/>).</param>
    /// <returns>The entry.</returns>
    public IDictionary<string, object?> AddTo(IDictionary<string, object?> clipStorage)
    {
        ArgumentNullException.ThrowIfNull(clipStorage);
        try
        {
            if (clipStorage.TryGetValue("Root", out var root) && root is string folder
                && clipStorage.TryGetValue("Warnings", out var list) && list is IList<IDictionary<string, object?>> warnings
                && Find(folder) is { CollectionType: { } type })
            {
                warnings.Add(Warning(type, null));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not tell whether the clip folder is in a Movies or TV library");
        }

        return clipStorage;
    }

    private static Dictionary<string, object?> Warning(CollectionTypeOptions type, string? libraryName)
        => new(StringComparer.Ordinal) { ["Code"] = WarningCode, ["Message"] = Message(type, libraryName) };

    private bool MayOpen(User? admin, ClipLibraryLocation location)
    {
        if (admin is null || location.LibraryId is not { } id)
        {
            return false;
        }

        try
        {
            return _libraries.VisibleLibraries(admin).Any(library => library.Id == id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the libraries admin {UserId} may open", admin.Id);
            return false;
        }
    }
}
