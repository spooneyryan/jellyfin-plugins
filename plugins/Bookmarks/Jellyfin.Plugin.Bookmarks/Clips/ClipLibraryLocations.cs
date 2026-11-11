using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Bookmarks.Clips;

/// <summary>
/// Every folder of every Jellyfin library (for a clip's <c>InLibrary</c> and <c>LibraryName</c>):
/// <c>ILibraryManager.GetVirtualFolders()</c>, kept for <see cref="Lifetime"/>, one entry per location, so a library
/// with two folders (for example <c>D:\Video\TV</c> and <c>E:\Overflow\TV</c>) has two. A clip file is in a library when
/// its path lies under one of them, compared as <see cref="ClipRootPaths"/> compares (on whole path segments, so
/// <c>E:\TV2</c> isn't under <c>E:\TV</c>, and without regard to case on Windows, so an admin's <c>e:\home clips</c> is under the library's <c>E:\Home Clips</c>); of nested locations the deepest wins.
/// </summary>
/// <remarks>
/// It costs no database query: Jellyfin reads the libraries' shortcut files, which is why the answer is kept. Who may know a
/// library's name depends on the caller's own library access: <see cref="ClipLibraryLocation.LibraryId"/> is for that
/// check.
/// </remarks>
internal sealed class ClipLibraryLocations
{
    private readonly ILibraryManager _library;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private IReadOnlyList<ClipLibraryLocation> _locations = [];
    private DateTimeOffset _until = DateTimeOffset.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipLibraryLocations"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ClipLibraryLocations(ILibraryManager library, ILogger<ClipLibraryLocations> logger)
        : this(library, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClipLibraryLocations"/> class with a given clock (tests).
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="timeProvider">The clock of the answer's lifetime.</param>
    internal ClipLibraryLocations(ILibraryManager library, ILogger logger, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _library = library;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets how long the libraries' folders are kept: 60 s.
    /// </summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Gets every library folder, deepest first.
    /// </summary>
    /// <returns>The folders.</returns>
    public IReadOnlyList<ClipLibraryLocation> GetAll()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (now < _until)
            {
                return _locations;
            }
        }

        var locations = Read();
        lock (_lock)
        {
            _locations = locations;
            _until = now + Lifetime;
            return _locations;
        }
    }

    /// <summary>
    /// Finds the library folder a clip file lies in.
    /// </summary>
    /// <param name="path">The clip file's path.</param>
    /// <returns>The deepest library folder that holds it, or <c>null</c> when none does (or the path isn't one).</returns>
    public ClipLibraryLocation? Find(string? path)
    {
        if (!ClipRootPaths.TryNormalize(path, out var full))
        {
            return null;
        }

        // Deepest first, so a library inside another's folder names the inner one.
        return GetAll().FirstOrDefault(location => full.Length > location.Path.Length && ClipRootPaths.IsUnder(full, location.Path));
    }

    private List<ClipLibraryLocation> Read()
    {
        try
        {
            var found = new List<ClipLibraryLocation>();
            foreach (var folder in _library.GetVirtualFolders() ?? [])
            {
                if (folder is null)
                {
                    continue;
                }

                Guid? id = Guid.TryParse(folder.ItemId, CultureInfo.InvariantCulture, out var parsed) && parsed != Guid.Empty ? parsed : null;
                foreach (var location in folder.Locations ?? [])
                {
                    if (ClipRootPaths.TryNormalize(location, out var normalized))
                    {
                        found.Add(new ClipLibraryLocation(normalized, folder.Name ?? string.Empty, folder.CollectionType, id));
                    }
                }
            }

            return found.OrderByDescending(location => location.Path.Length).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // No library folders known for a while: no clip shows "In library", which only a badge depends on.
            _logger.LogWarning("Could not read the libraries' folders for the clips' In library badge ({Error}); it is tried again in a minute", ex.GetType().Name);
            _logger.LogDebug(ex, "Could not read the libraries' folders");
            return [];
        }
    }
}
