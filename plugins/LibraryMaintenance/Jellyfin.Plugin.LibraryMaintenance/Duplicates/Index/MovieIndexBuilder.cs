using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// Builds the movie index: the ids of every movie in the included libraries, then the items in chunks
/// with minimal loading, projected to <see cref="MovieRecord"/>s and grouped into version sets with the kit's
/// <see cref="VersionSets.Group"/>.
/// </summary>
/// <remarks>
/// The library query is <c>IncludeItemTypes = [Movie]</c>, <c>Recursive</c>, <c>IsVirtualItem = false</c>,
/// <c>GroupByPresentationUniqueKey = false</c>, no user, and explicit, never empty, <c>TopParentIds</c>: owned items (local
/// alternates, extras) have no top parent, so it leaves them out, and an item with an <c>OwnerId</c> is dropped again in
/// memory. It never sets <c>HasOwnerId</c>: on 10.11.5 <c>HasOwnerId = false</c> means <c>OwnerId IS NULL</c>, while every
/// item saved on 10.11 stores the zero GUID, so it would drop nearly every movie.
/// </remarks>
internal sealed class MovieIndexBuilder
{
    /// <summary>How many ids one <c>GetItemList</c> carries.</summary>
    public const int ChunkSize = 500;

    private readonly ILibraryManager _library;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly Func<IReadOnlyList<CollectionFolder>> _libraries;
    private readonly Func<CollectionFolder, bool> _isEnabled;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieIndexBuilder"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="configuration">The settings: the included libraries and the excluded folders.</param>
    public MovieIndexBuilder(ILibraryManager library, IPluginConfigurationProvider configuration)
        : this(library, configuration, null, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieIndexBuilder"/> class.
    /// </summary>
    /// <param name="library">The library manager.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="libraries">The server's libraries; by default the root folder's <see cref="CollectionFolder"/>
    /// children. Tests give their own.</param>
    /// <param name="isEnabled">Whether a library is enabled; by default <see cref="IsEnabled"/>. Tests give their own.</param>
    internal MovieIndexBuilder(ILibraryManager library, IPluginConfigurationProvider configuration, Func<IReadOnlyList<CollectionFolder>>? libraries, Func<CollectionFolder, bool>? isEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(configuration);
        _library = library;
        _configuration = configuration;
        _libraries = libraries ?? (() => library.GetUserRootFolder().Children.OfType<CollectionFolder>().ToList());
        _isEnabled = isEnabled ?? IsEnabled;
    }

    /// <summary>
    /// Gets the items' minimal loading: no images, no user data, and the provider ids, which stay empty unless
    /// asked for.
    /// </summary>
    /// <returns>The options.</returns>
    public static DtoOptions MinimalDtoOptions() => new(false)
    {
        EnableImages = false,
        EnableUserData = false,
        Fields = [ItemFields.ProviderIds]
    };

    /// <summary>
    /// Gets the query for a library's movie ids.
    /// </summary>
    /// <param name="topParentIds">The library's folders; never empty (an empty list would mean every library).</param>
    /// <returns>The query.</returns>
    public static InternalItemsQuery LibraryQuery(Guid[] topParentIds)
    {
        ArgumentNullException.ThrowIfNull(topParentIds);
        if (topParentIds.Length == 0)
        {
            throw new ArgumentException("A library query needs its folders; none would read every library.", nameof(topParentIds));
        }

        return new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            Recursive = true,
            IsVirtualItem = false,
            TopParentIds = topParentIds,
            GroupByPresentationUniqueKey = false,
            DtoOptions = MinimalDtoOptions()
        };
    }

    /// <summary>
    /// Gets whether a path is inside one of the excluded folders: compared ignoring case, with <c>/</c> and
    /// <c>\</c> alike, and only at a separator, so <c>D:\Clips</c> doesn't exclude <c>D:\Clips2</c>. Blank and relative
    /// entries exclude nothing.
    /// </summary>
    /// <param name="path">The video's path.</param>
    /// <param name="prefixes">The excluded folders.</param>
    /// <returns>Whether the path is excluded.</returns>
    public static bool IsExcluded(string? path, IReadOnlyList<string>? prefixes)
    {
        if (string.IsNullOrEmpty(path) || prefixes is null || prefixes.Count == 0)
        {
            return false;
        }

        var normalized = path.Replace('/', '\\');
        foreach (var prefix in prefixes)
        {
            if (string.IsNullOrWhiteSpace(prefix) || !System.IO.Path.IsPathFullyQualified(prefix.Trim()))
            {
                continue;
            }

            var folder = prefix.Trim().Replace('/', '\\').TrimEnd('\\');
            if (normalized.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether a library is enabled: its <c>LibraryOptions.Enabled</c>, the first thing 10.11.5's
    /// <c>CollectionFolder.IsVisible(user)</c> asks, so a disabled library is hidden from every user, administrators
    /// included. Per-user access is the requests'. A library with no path (none on a server) is enabled.
    /// </summary>
    /// <param name="library">The library.</param>
    /// <returns>Whether the index may read it.</returns>
    public static bool IsEnabled(CollectionFolder library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return string.IsNullOrEmpty(library.Path) || library.GetLibraryOptions().Enabled;
    }

    /// <summary>
    /// Gets the server's libraries the index can read: every <c>movies</c> and mixed (no content type) library, enabled or
    /// not, each once, in the server's order (the Settings tab's list).
    /// </summary>
    /// <returns>The libraries.</returns>
    public IReadOnlyList<CollectionFolder> MovieLibraries()
        => (_libraries() ?? [])
            .Where(l => l is not null && l.CollectionType is null or CollectionType.movies)
            .GroupBy(l => l.Id)
            .Select(g => g.First())
            .ToList();

    /// <summary>
    /// Gets whether a library is enabled in Jellyfin (<see cref="IsEnabled"/>, or the tests' own rule).
    /// </summary>
    /// <param name="library">The library.</param>
    /// <returns>Whether the index may read it.</returns>
    public bool IsLibraryEnabled(CollectionFolder library) => _isEnabled(library);

    /// <summary>
    /// Gets the libraries the index reads: the enabled <c>movies</c> and mixed (no content type) libraries, limited to
    /// the settings' included libraries when it names any.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The libraries, in the server's order.</returns>
    public IReadOnlyList<CollectionFolder> IncludedLibraries(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var included = (configuration.IncludedLibraryIds ?? []).Where(id => id != Guid.Empty).ToHashSet();
        return MovieLibraries()
            .Where(l => included.Count == 0 || included.Contains(l.Id))
            .Where(_isEnabled)
            .ToList();
    }

    /// <summary>
    /// Builds the index's content with the saved settings.
    /// </summary>
    /// <param name="cancellationToken">Stops the build between queries.</param>
    /// <param name="progress">Told the share of chunks read, from 0 to 1.</param>
    /// <returns>The content.</returns>
    public MovieIndexContent Build(CancellationToken cancellationToken, Action<double>? progress = null)
        => Build(_configuration.GetConfiguration(), cancellationToken, progress);

    /// <summary>
    /// Builds the index's content with the given settings: the saved ones, or settings not saved yet, for the Settings
    /// tab's preview of what a change to the libraries or the excluded folders does. It only reads the library.
    /// </summary>
    /// <param name="configuration">The settings: the included libraries and the excluded folders.</param>
    /// <param name="cancellationToken">Stops the build between queries.</param>
    /// <param name="progress">Told the share of chunks read, from 0 to 1.</param>
    /// <returns>The content.</returns>
    public MovieIndexContent Build(PluginConfiguration configuration, CancellationToken cancellationToken, Action<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var libraries = IncludedLibraries(configuration);

        // 1 and 2: the ids per library, each id once with every library it is in.
        var libraryQueries = 0;
        var librariesOf = new Dictionary<Guid, List<Guid>>();
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = LibraryAccess.TopParentIds([library]);
            if (folders.Length == 0)
            {
                continue;
            }

            libraryQueries++;
            foreach (var id in _library.GetItemIds(LibraryQuery(folders)) ?? [])
            {
                if (id == Guid.Empty)
                {
                    continue;
                }

                if (!librariesOf.TryGetValue(id, out var of))
                {
                    librariesOf[id] = of = [];
                }

                if (!of.Contains(library.Id))
                {
                    of.Add(library.Id);
                }
            }
        }

        // 3 and 4: the items in chunks; owned items dropped; projected.
        var ids = librariesOf.Keys.ToList();
        var chunks = (ids.Count + ChunkSize - 1) / ChunkSize;
        var fetched = new Dictionary<Guid, MovieRecord>(ids.Count);
        var input = new List<MovieRecord>(ids.Count);
        var owned = 0;
        var skipped = new List<IndexLeftOut>();
        var excluded = new List<IndexLeftOut>();
        var excludedFolders = configuration.ExcludedPathPrefixes ?? [];
        for (var chunk = 0; chunk < chunks; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new InternalItemsQuery
            {
                ItemIds = ids.Skip(chunk * ChunkSize).Take(ChunkSize).ToArray(),
                GroupByPresentationUniqueKey = false,
                DtoOptions = MinimalDtoOptions()
            };
            foreach (var item in _library.GetItemList(query) ?? [])
            {
                if (item is not Video video || !librariesOf.TryGetValue(item.Id, out var of) || fetched.ContainsKey(item.Id))
                {
                    continue;
                }

                if (LibraryAccess.IsOwned(item))
                {
                    owned++;
                    continue;
                }

                var record = MovieRecord.From(video, of);
                fetched[record.Id] = record;
                if (string.IsNullOrWhiteSpace(record.Name))
                {
                    skipped.Add(new IndexLeftOut(record.Id, record.LibraryIds));
                }
                else if (IsExcluded(record.Path, excludedFolders))
                {
                    excluded.Add(new IndexLeftOut(record.Id, record.LibraryIds));
                }
                else
                {
                    input.Add(record);
                }
            }

            progress?.Invoke((chunk + 1) / (double)chunks);
        }

        // 5: version sets. The fetched items answer first (an excluded or nameless member stays in its set), then the
        // library, for a primary in a library the index doesn't read.
        var libraryLookup = VersionSets.LibraryLookup(_library);
        var sets = VersionSets.Group(
            input.Select(r => r.Version),
            id => fetched.TryGetValue(id, out var record) ? record.Version : libraryLookup(id),
            VersionSets.LibraryPaths(_library));

        var indexed = input.ToDictionary(r => r.Id);
        var entries = sets.Select(set => ToEntry(set, indexed, fetched)).ToList();
        var states = Enum.GetValues<VersionSetState>()
            .Where(s => s != VersionSetState.None)
            .ToDictionary(s => s, s => entries.Count(e => e.State.HasFlag(s)));
        var stats = new IndexStats
        {
            Movies = input.Count,
            Entries = entries.Count,
            MergedEntries = entries.Count(e => e.AllIds.Count > 1),
            BrokenEntries = entries.Count(e => e.State != VersionSetState.None),
            Skipped = skipped.Count,
            Excluded = excluded.Count,
            Owned = owned,
            States = states,
            SkippedMovies = skipped,
            ExcludedMovies = excluded
        };
        var read = libraries
            .Select(l => new IndexLibrary(l.Id, l.Name ?? string.Empty, l.CollectionType is null ? "mixed" : "movies", input.Count(r => r.LibraryIds.Contains(l.Id))))
            .ToList();
        progress?.Invoke(1);
        return new MovieIndexContent(entries, read, stats, libraryQueries, chunks);
    }

    private static VersionEntry ToEntry(VersionSet set, Dictionary<Guid, MovieRecord> indexed, Dictionary<Guid, MovieRecord> fetched)
    {
        // The representative is the root when it is indexed, else the indexed member with the lowest id (a root in a
        // library the index doesn't read, excluded or nameless), never one the libraries' order picked; the others follow
        // by id (nothing depends on the input's order).
        var indexedMembers = set.MemberIds.Where(indexed.ContainsKey).Select(id => indexed[id]).ToList();
        var representative = indexedMembers.FirstOrDefault(m => m.Id == set.RootId) ?? indexedMembers.MinBy(m => m.Id)!;
        List<MovieRecord> members = [representative, .. indexedMembers.Where(m => m.Id != representative.Id).OrderBy(m => m.Id)];
        var localAlternates = fetched.TryGetValue(set.RootId, out var rootRecord) ? rootRecord.LocalAlternateCount : 0;
        return new VersionEntry(
            representative,
            members,
            set.MemberIds.Count + localAlternates,
            set.State,
            set.StaleLinkIds,
            set.ChainedIds.Concat(set.UnlistedIds).Concat(set.ListedNotPointingIds).Distinct().ToList(),
            members.SelectMany(m => m.LibraryIds).Distinct().ToList(),
            set.MemberIds.ToHashSet(),
            set.RootId,
            set.MemberIds.Where(id => !indexed.ContainsKey(id)).ToList())
        {
            ChainedIds = set.ChainedIds,
            UnlistedIds = set.UnlistedIds,
            ListedNotPointingIds = set.ListedNotPointingIds
        };
    }
}
