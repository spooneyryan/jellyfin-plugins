using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AccessRoles.Access;

/// <summary>
/// The libraries from <c>ILibraryManager.GetVirtualFolders()</c> (<c>v10.11.5:MediaBrowser.Controller/Library/ILibraryManager.cs:167</c>),
/// the only list without ghosts (as measured on 10.11.5), each with the id the core gives its folder:
/// <c>GetNewItemId(&lt;DefaultUserViewsPath&gt;\&lt;Name&gt;, typeof(CollectionFolder))</c>, the folder's creation time, and
/// whether the core has the library's item yet (<c>ItemId</c> set; never used as the id, which after a case-only rename on
/// Windows is the old one).
/// </summary>
internal sealed class VirtualFolderCatalogSource : ILibraryCatalogSource
{
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationPaths _paths;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualFolderCatalogSource"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="paths">The server's paths, for <c>DefaultUserViewsPath</c>.</param>
    /// <param name="logger">The logger.</param>
    public VirtualFolderCatalogSource(ILibraryManager libraryManager, IServerApplicationPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _libraryManager = libraryManager;
        _paths = paths;
        _logger = logger;
    }

    /// <inheritdoc />
    public LibraryCatalog Read()
    {
        var root = _paths.DefaultUserViewsPath;
        var libraries = new List<CatalogLibrary>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            if (string.IsNullOrEmpty(folder?.Name))
            {
                continue;
            }

            var path = Path.Combine(root, folder.Name);
            libraries.Add(new CatalogLibrary(
                _libraryManager.GetNewItemId(path, typeof(CollectionFolder)),
                folder.Name,
                folder.Locations?.Where(l => !string.IsNullOrEmpty(l)).ToArray() ?? [],
                folder.LibraryOptions?.Enabled ?? true,
                folder.CollectionType?.ToString(),
                CreatedUtc(path),
                HasItem: !string.IsNullOrEmpty(folder.ItemId)));
        }

        return new LibraryCatalog(libraries);
    }

    /// <inheritdoc />
    public Guid IdOf(string name)
        => string.IsNullOrEmpty(name) ? Guid.Empty : _libraryManager.GetNewItemId(Path.Combine(_paths.DefaultUserViewsPath, name), typeof(CollectionFolder));

    private DateTime? CreatedUtc(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "The creation time of a library folder couldn't be read");
            return null;
        }
    }
}
