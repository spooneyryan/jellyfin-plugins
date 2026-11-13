using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.SmartSearch.AutoCollections;

/// <summary>
/// Whether a new collection can take a name, without writing anything. The server makes a collection's folder, and from
/// it the collection's id, out of its name, and doesn't check whether either exists: a second collection of the same
/// name would take over the first one's row. So a name is refused when a collection of that name (ignoring case, as the
/// file system does) or on that folder exists, when the folder exists, or when the id is taken; the caller gets free
/// names close to it instead, never a silent suffix.
/// </summary>
internal sealed class CollectionNameChecker
{
    /// <summary>The longest name, in characters.</summary>
    public const int MaxNameLength = 100;

    private const int MoviePage = 1000;
    private static readonly TimeSpan _collectionNamesLifetime = TimeSpan.FromMinutes(5);

    private readonly ILibraryManager _library;
    private readonly ICollectionManager _collections;
    private readonly IFileSystem _fileSystem;
    private readonly IServerApplicationPaths _paths;
    private readonly AutoCollectionStore _store;
    private readonly TimeProvider _time;
    private readonly Lock _cacheLock = new();
    private (DateTimeOffset Taken, IReadOnlySet<string> Names)? _collectionNames;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionNameChecker"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="collections">The collection manager.</param>
    /// <param name="fileSystem">The file system, for the server's folder names.</param>
    /// <param name="paths">The server's paths (the collections folder before the first collection exists).</param>
    /// <param name="store">The auto-collection definitions.</param>
    /// <param name="time">The clock; the system clock by default.</param>
    public CollectionNameChecker(ILibraryManager library, ICollectionManager collections, IFileSystem fileSystem, IServerApplicationPaths paths, AutoCollectionStore store, TimeProvider? time = null)
    {
        _library = library;
        _collections = collections;
        _fileSystem = fileSystem;
        _paths = paths;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Checks a name for a new collection, or for a definition's own (which may keep its collection's).</summary>
    /// <param name="name">The name.</param>
    /// <param name="definitionId">The definition the name is for, when it exists.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The outcome.</returns>
    public async Task<CollectionNameCheck> CheckAsync(string? name, Guid? definitionId, CancellationToken cancellationToken)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var error = NameError(trimmed);
        if (error is not null)
        {
            return new CollectionNameCheck(trimmed, error, [], [], [], null, null);
        }

        if (string.IsNullOrWhiteSpace(_fileSystem.GetValidFilename(trimmed)))
        {
            return new CollectionNameCheck(trimmed, "The name needs at least one character that can be used in a folder name.", [], [], [], null, null);
        }

        var root = await CollectionsRootAsync().ConfigureAwait(false);
        var boxSets = BoxSets();
        cancellationToken.ThrowIfCancellationRequested();
        var tether = definitionId is { } id ? AutoCollectionDefinition.FormatTether(id) : null;
        var (conflicts, rebind, folder) = Conflicts(trimmed, root, boxSets, tether, definitionId);
        var warnings = new List<string>();
        if (CollectionNamesOfAutoAdd().Contains(trimmed))
        {
            warnings.Add("Movies in a library that adds movies to collections automatically name a collection \"" + trimmed + "\". The server would add them to this collection after every scan, and the next update would remove them again. Choose another name, or turn that library option off.");
        }

        var suggestions = conflicts.Count == 0 ? [] : Suggest(trimmed, root, boxSets, tether, definitionId);
        return new CollectionNameCheck(trimmed, null, conflicts, warnings, suggestions, folder, rebind);
    }

    private static string? NameError(string name)
        => name.Length == 0 ? "The name can't be empty."
            : name.Length > MaxNameLength ? "The name can't be longer than " + MaxNameLength.ToString(CultureInfo.InvariantCulture) + " characters."
            : null;

    private static bool HasTether(BaseItem item, string? tether)
        => tether is not null
            && item.ProviderIds is { } ids
            && ids.TryGetValue(AutoCollectionDefinition.TetherKey, out var value)
            && string.Equals(value, tether, StringComparison.Ordinal);

    private (List<CollectionNameConflict> Conflicts, Guid? Rebind, string? Folder) Conflicts(string name, string root, IReadOnlyList<BoxSet> boxSets, string? tether, Guid? definitionId)
    {
        var conflicts = new List<CollectionNameConflict>();
        var folder = Path.Combine(root, _fileSystem.GetValidFilename(name) + " [boxset]");
        Guid? rebind = null;
        var seen = new HashSet<Guid>();
        foreach (var box in boxSets)
        {
            var samePath = string.Equals(box.Path, folder, StringComparison.OrdinalIgnoreCase);
            var sameName = string.Equals((box.Name ?? string.Empty).Trim(), name, StringComparison.OrdinalIgnoreCase);
            if (!samePath && !sameName)
            {
                continue;
            }

            seen.Add(box.Id);
            if (HasTether(box, tether))
            {
                rebind ??= box.Id;
                continue;
            }

            conflicts.Add(new CollectionNameConflict(CollectionNameConflictKind.ExistingCollection, box.Id, box.Name ?? string.Empty));
        }

        var onFolder = boxSets.Any(b => string.Equals(b.Path, folder, StringComparison.OrdinalIgnoreCase));
        // A file of the folder's name is in the way too: the server couldn't make the folder, and the create would fail.
        if (!onFolder && (Directory.Exists(folder) || File.Exists(folder)))
        {
            conflicts.Add(new CollectionNameConflict(CollectionNameConflictKind.FolderExists, null, Path.GetFileName(folder)));
        }

        var id = _library.GetNewItemId(folder, typeof(BoxSet));
        if (!seen.Contains(id) && _library.GetItemById(id) is { } item)
        {
            if (HasTether(item, tether))
            {
                rebind ??= item.Id;
            }
            else
            {
                conflicts.Add(new CollectionNameConflict(CollectionNameConflictKind.ItemIdExists, item.Id, item.Name ?? string.Empty));
            }
        }

        // Another definition that has no collection yet holds the name too; one with a collection is covered above.
        foreach (var other in _store.Current.Definitions)
        {
            if (!other.Id.Equals(definitionId)
                && other.CollectionId is null
                && string.Equals(other.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                conflicts.Add(new CollectionNameConflict(CollectionNameConflictKind.AutoCollection, other.Id, other.Name));
            }
        }

        return (conflicts, rebind, folder);
    }

    private List<string> Suggest(string name, string root, IReadOnlyList<BoxSet> boxSets, string? tether, Guid? definitionId)
    {
        var suggestions = new List<string>();
        bool Free(string candidate) => Conflicts(candidate, root, boxSets, tether, definitionId).Conflicts.Count == 0;
        string Fit(string suffix) => (name.Length + suffix.Length > MaxNameLength ? name[..(MaxNameLength - suffix.Length)].TrimEnd() : name) + suffix;

        for (var n = 2; n <= 20; n++)
        {
            var candidate = Fit(" (" + n.ToString(CultureInfo.InvariantCulture) + ")");
            if (Free(candidate))
            {
                suggestions.Add(candidate);
                break;
            }
        }

        var auto = Fit(" (Auto)");
        if (!suggestions.Contains(auto, StringComparer.OrdinalIgnoreCase) && Free(auto))
        {
            suggestions.Add(auto);
        }

        return suggestions;
    }

    private async Task<string> CollectionsRootAsync()
    {
        var folder = await _collections.GetCollectionsFolder(false).ConfigureAwait(false);
        return folder?.Path is { Length: > 0 } path ? path : Path.Combine(_paths.DataPath, "collections");
    }

    private IReadOnlyList<BoxSet> BoxSets()
        => _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Recursive = true,
            DtoOptions = new DtoOptions(false) { Fields = [ItemFields.ProviderIds] }
        }).OfType<BoxSet>().ToList();

    /// <summary>
    /// The TMDb collection names of movies in libraries that add movies to collections automatically, as the server's
    /// post-scan task matches them (exactly); kept for a few minutes, since loading the movies is the expensive part.
    /// </summary>
    private IReadOnlySet<string> CollectionNamesOfAutoAdd()
    {
        var now = _time.GetUtcNow();
        lock (_cacheLock)
        {
            if (_collectionNames is { } cached && now - cached.Taken < _collectionNamesLifetime)
            {
                return cached.Names;
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var library in _library.GetVirtualFolders())
        {
            if (library.LibraryOptions?.AutomaticallyAddToCollection != true || !Guid.TryParse(library.ItemId, out var libraryId))
            {
                continue;
            }

            for (var start = 0; ; start += MoviePage)
            {
                var movies = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Movie],
                    IsVirtualItem = false,
                    AncestorIds = [libraryId],
                    Recursive = true,
                    StartIndex = start,
                    Limit = MoviePage
                });
                foreach (var movie in movies.OfType<Movie>())
                {
                    if (!string.IsNullOrEmpty(movie.CollectionName) && KitItemQueries.PrimaryVersionText(movie) is null)
                    {
                        names.Add(movie.CollectionName);
                    }
                }

                if (movies.Count < MoviePage)
                {
                    break;
                }
            }
        }

        lock (_cacheLock)
        {
            _collectionNames = (now, names);
        }

        return names;
    }
}
