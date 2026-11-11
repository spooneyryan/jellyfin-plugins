using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Api;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Grouping;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Normalization;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;

/// <summary>
/// <c>GET /Groups</c> and <c>GET /Groups/{groupId}</c>: reads the index for the request
/// (<see cref="MovieIndexService.GetSnapshotAsync"/>), works out the effective libraries, groups them through
/// <see cref="GroupResultCache"/> in the mode asked for and the other one, then filters, sorts and pages.
/// </summary>
/// <remarks>
/// <para><b>Effective libraries</b>: those asked for (every one when none) that the caller can open, through the kit's
/// <see cref="LibraryAccess.ResolveLibraries"/> (a view's id names its library; an id the caller can't open is dropped),
/// and that the index read (the settings' included libraries). Administrators get no bypass: an admin
/// whose access is limited to some libraries sees only their groups, and of each entry only its versions in those
/// libraries (<see cref="GroupedEntry"/>): no path, size, library, id, count or flag of another
/// version shows. An API key has no user and sees every library the index read.</para>
/// <para><b>Item visibility</b>: within those libraries, a movie the caller's parental rating or tags
/// hide (<see cref="ItemVisibility"/>) is in no group, count, size or keep suggestion, so every id the groups show is one
/// <c>GetItemById(id, user)</c> returns them. A caller whose policy limits no item (and an API key) costs nothing more;
/// a limited one shares cached results only with callers whose hidden movies are the same.</para>
/// <para><b>Cost</b>: the entries' projections are kept per snapshot and title rules
/// (<see cref="EntryProjectionCache"/>); a grouping narrows them to the caller's libraries, buckets them once, groups the
/// buckets in the mode asked for and counts the other mode's groups (<c>AlternateModeGroupCount</c>).</para>
/// <para><b>Filters</b>, after grouping and never changing the groups: the confidences asked for, and the text filter,
/// which keeps a group when its title key contains the filter's own key (the same title rules, so <c>amelie</c> finds
/// <i>Amélie</i>) or an entry's name contains the text, ignoring case.</para>
/// </remarks>
internal sealed class DuplicateGroupsService : IDuplicateGroups, IDisposable
{
    private readonly MovieIndexService _index;
    private readonly LibraryAccess _access;
    private readonly ItemVisibility _visibility;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly TimeProvider _time;
    private readonly Queue<IDictionary<string, object?>> _recent = new();
    private long _lastGroupingTicks = -1;
    private long _groupings;

    /// <summary>
    /// Initializes a new instance of the <see cref="DuplicateGroupsService"/> class.
    /// </summary>
    /// <param name="index">The movie index.</param>
    /// <param name="access">Which libraries a caller can open.</param>
    /// <param name="visibility">The movies a caller's parental rating and tags hide.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="time">The clock, for N10's last year; the system's by default.</param>
    public DuplicateGroupsService(MovieIndexService index, LibraryAccess access, ItemVisibility visibility, IPluginConfigurationProvider configuration, TimeProvider? time = null)
    {
        _index = index;
        _access = access;
        _visibility = visibility;
        _configuration = configuration;
        _time = time ?? TimeProvider.System;
        _index.Published += OnPublished;
        _configuration.Changed += OnSettingsChanged;
    }

    /// <summary>Gets how many groupings Diagnostics lists, the newest last.</summary>
    public static int RecentCapacity => 16;

    /// <summary>Gets the group results kept.</summary>
    public GroupResultCache Cache { get; } = new();

    /// <summary>Gets the entries' projections kept.</summary>
    public EntryProjectionCache Projections { get; } = new();

    /// <inheritdoc />
    public async Task<LibraryMaintenanceDuplicateGroupsResultDto?> GetGroupsAsync(
        string? mode,
        string? unknownYear,
        string? libraryIds,
        string? confidence,
        string? search,
        string? sort,
        int? startIndex,
        int? limit,
        User? user,
        CancellationToken cancellationToken)
    {
        var query = Parse(mode, unknownYear, libraryIds, confidence, search, sort, startIndex, limit);
        var read = await _index.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (Snapshot(read) is not { } snapshot)
        {
            return null;
        }

        var libraries = EffectiveLibraries(snapshot, query.LibraryIds, user);
        var result = Grouped(snapshot, query.Grouping, libraries, _visibility.Hidden(snapshot, user, libraries));

        var kept = Filter(result.Groups, query).ToList();
        kept.Sort(Order(query.Sort));
        var names = LibraryNames(snapshot);
        return new LibraryMaintenanceDuplicateGroupsResultDto
        {
            Index = new LibraryMaintenanceDuplicateGroupsIndexDto
            {
                Version = snapshot.Version,
                GeneratedUtc = snapshot.GeneratedUtc,
                Stale = read.Stale,
                Rebuilding = read.Rebuilding,
                ScanInProgress = _index.ScanInProgress,
                BuiltDuringScan = snapshot.BuiltDuringScan,
                LastError = read.LastError,
                LastErrorUtc = read.LastErrorUtc,
            },
            Mode = query.Grouping.Mode.ToString(),
            UnknownYear = query.Grouping.UnknownYear.ToString(),
            OptionsHash = query.Grouping.Hash(),
            EffectiveLibraryIds = libraries,
            TotalMovies = result.Movies,
            TotalEntries = result.Entries,
            GroupCount = result.Groups.Count,
            AlternateModeGroupCount = result.AlternateModeGroupCount,
            ReclaimableBytes = result.Groups.Sum(g => g.ReclaimableBytes),
            ReclaimableIsLowerBound = result.Groups.Any(g => g.ReclaimableIsLowerBound),
            StartIndex = query.StartIndex,
            TotalRecordCount = kept.Count,
            Items = kept.Skip(query.StartIndex).Take(query.Limit).Select(g => ToDto(g, names)).ToList(),
        };
    }

    /// <inheritdoc />
    public async Task<LibraryMaintenanceDuplicateGroupDto?> GetGroupAsync(
        string groupId,
        string? mode,
        string? unknownYear,
        string? libraryIds,
        User? user,
        CancellationToken cancellationToken)
    {
        var query = Parse(mode, unknownYear, libraryIds, null, null, null, null, null);
        var read = await _index.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (Snapshot(read) is not { } snapshot)
        {
            return null;
        }

        var libraries = EffectiveLibraries(snapshot, query.LibraryIds, user);
        var result = Grouped(snapshot, query.Grouping, libraries, _visibility.Hidden(snapshot, user, libraries));
        var group = result.Groups.FirstOrDefault(g => string.Equals(g.Id, groupId?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw PluginException.NotFound("This duplicate group is gone: its movies changed, or the options or libraries differ.");
        return ToDto(group, LibraryNames(snapshot));
    }

    /// <inheritdoc />
    public LibraryMaintenanceDuplicateGroupsPendingDto GetPending()
    {
        var status = _index.GetStatus();
        return new LibraryMaintenanceDuplicateGroupsPendingDto
        {
            Rebuilding = status.Rebuilding,
            Progress = status.Progress,
            ScanInProgress = status.ScanInProgress,
            LastError = status.LastError,
        };
    }

    /// <summary>
    /// Describes the groups for Diagnostics: the results kept, the cache's hits and misses, the last grouping's time, and the
    /// last <see cref="RecentCapacity"/> groupings (when, how long, the projection's part of it, the snapshot, the mode, the
    /// entries, the groups and the other mode's count), the evidence for the groups' "under 100 ms after warm-up". They
    /// are every caller's groupings, so Diagnostics shows them only to an administrator who sees every library.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        List<IDictionary<string, object?>> recent;
        lock (_recent)
        {
            recent = [.. _recent];
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Scope"] = "Server-wide: every caller's groupings",
            ["CachedResults"] = Cache.Count,
            ["CacheHits"] = Cache.Hits,
            ["CacheMisses"] = Cache.Misses,
            ["Groupings"] = Interlocked.Read(ref _groupings),
            ["LastGroupingMs"] = Interlocked.Read(ref _lastGroupingTicks) is var ticks and >= 0 ? Milliseconds(ticks) : null,
            ["Projections"] = Projections.Projections,
            ["ProjectionKept"] = Projections.HasProjection,
            ["LastProjectionMs"] = Projections.LastProjection is { } projection ? Milliseconds(projection.Ticks) : null,
            ["VisibilityChecks"] = _visibility.Checks,
            ["LastVisibilityCheckMs"] = _visibility.LastCheck is { } check ? Milliseconds(check.Ticks) : null,
            ["Recent"] = recent,
        };
    }

    /// <summary>
    /// Stops listening to the index and the settings.
    /// </summary>
    public void Dispose()
    {
        _index.Published -= OnPublished;
        _configuration.Changed -= OnSettingsChanged;
    }

    /// <summary>
    /// Gets the libraries a request groups: those asked for (every one when none) that the
    /// caller can open and the index read, in the index's order.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="requested">The libraries asked for, or null.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <returns>The library ids.</returns>
    internal IReadOnlyList<Guid> EffectiveLibraries(MovieIndexSnapshot snapshot, IReadOnlyList<Guid>? requested, User? user)
    {
        var allowed = _access.ResolveLibraries(requested, user).Select(l => l.Id).ToHashSet();
        return snapshot.Libraries.Select(l => l.Id).Where(allowed.Contains).Distinct().ToList();
    }

    private static MatchMode Other(MatchMode mode) => mode == MatchMode.Title ? MatchMode.TitleAndYear : MatchMode.Title;

    // The snapshot to answer from; none while the first build runs (the route's 202), and a 503 when the last build failed
    // and none runs (a 202 then would never end).
    private static MovieIndexSnapshot? Snapshot(IndexRead read)
    {
        if (read.Failed)
        {
            throw PluginException.Unavailable((read.LastError ?? "The movie index couldn't be built.") + " Rebuild the index to try again.");
        }

        return read.Snapshot;
    }

    private static Dictionary<Guid, string> LibraryNames(MovieIndexSnapshot snapshot)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var library in snapshot.Libraries)
        {
            names.TryAdd(library.Id, library.Name);
        }

        return names;
    }

    private static IEnumerable<DuplicateGroup> Filter(IEnumerable<DuplicateGroup> groups, GroupsQuery query)
    {
        var key = query.Search is { } search ? TitleNormalizer.Normalize(search, query.Grouping.Normalization).Key : null;
        return groups.Where(g => (query.Confidence is null || query.Confidence.Contains(g.Confidence))
            && (query.Search is null || Matches(g, query.Search, key)));
    }

    private static bool Matches(DuplicateGroup group, string search, string? key)
        => (key is { Length: > 0 } && group.Entries[0].Title.Key.Contains(key, StringComparison.Ordinal))
            || group.Entries.Any(e => e.Representative.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

    private static Comparison<DuplicateGroup> Order(GroupSort sort) => sort switch
    {
        GroupSort.Title => ByTitle,
        GroupSort.Reclaimable => static (a, b) => Then(b.ReclaimableBytes.CompareTo(a.ReclaimableBytes), a, b),
        GroupSort.Size => static (a, b) => Then(b.Entries.Count.CompareTo(a.Entries.Count), a, b),
        GroupSort.Newest => static (a, b) => Then(Newest(b).CompareTo(Newest(a)), a, b),
        _ => static (a, b) => Then(((int)a.Confidence).CompareTo((int)b.Confidence), a, b),
    };

    private static int Then(int first, DuplicateGroup a, DuplicateGroup b) => first != 0 ? first : ByTitle(a, b);

    // The title key (the title rules' form: case, accents and a leading article set aside, so "The Matrix" sorts under M),
    // then the group key (the year), the display title and the id: a total order, the same on every server.
    private static int ByTitle(DuplicateGroup a, DuplicateGroup b)
    {
        var c = string.CompareOrdinal(a.Entries[0].Title.Key, b.Entries[0].Title.Key);
        if (c == 0)
        {
            c = string.CompareOrdinal(a.Key, b.Key);
        }

        if (c == 0)
        {
            c = string.CompareOrdinal(a.DisplayTitle, b.DisplayTitle);
        }

        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    private static DateTime Newest(DuplicateGroup group) => group.Entries.SelectMany(e => e.Members).Max(m => m.DateCreated);

    private static LibraryMaintenanceDuplicateGroupDto ToDto(DuplicateGroup group, Dictionary<Guid, string> names) => new()
    {
        GroupId = group.Id,
        DisplayTitle = group.DisplayTitle,
        Key = group.Key,
        Confidence = group.Confidence.ToString(),
        Reasons = Names(group.Reasons),
        Flags = Names(group.Flags),
        Years = group.Years,
        SuggestedKeepId = group.SuggestedKeep.Id,
        ReclaimableBytes = group.ReclaimableBytes,
        ReclaimableIsLowerBound = group.ReclaimableIsLowerBound,
        Entries = group.Entries.Select(e => ToDto(e, names)).ToList(),
    };

    private static LibraryMaintenanceDuplicateGroupEntryDto ToDto(GroupedEntry entry, Dictionary<Guid, string> names)
    {
        var main = entry.Representative;
        return new LibraryMaintenanceDuplicateGroupEntryDto
        {
            ItemId = main.Id,
            Name = main.Name,
            OriginalTitle = main.OriginalTitle,
            Edition = entry.Title.Edition,
            Year = entry.Year.Year,
            YearSource = entry.Year.Source.ToString(),
            Libraries = entry.LibraryIds
                .Select(id => new LibraryMaintenanceDuplicateGroupLibraryDto { Id = id, Name = names.GetValueOrDefault(id, string.Empty) })
                .ToList(),
            Path = main.Path,
            IsInMixedFolder = main.IsInMixedFolder,
            Width = main.Width,
            Height = main.Height,
            ResolutionLabel = ResolutionLabel(main.Width),
            SizeBytes = main.Size,
            TotalBitrate = main.TotalBitrate,
            RunTimeTicks = main.RunTimeTicks,
            Container = main.Container,
            VideoType = main.VideoType.ToString(),
            SourceCount = entry.SourceCount,
            LinkedVersionIds = entry.Members.Where(m => m.Id != main.Id).Select(m => m.Id).ToList(),
            LocalAlternateCount = main.LocalAlternateCount,
            PartCount = main.PartCount,
            VersionState = entry.State.ToString(),
            ProviderIds = new LibraryMaintenanceDuplicateProviderIdsDto
            {
                Tmdb = FirstId(entry.Members.Select(m => m.Tmdb)),
                Imdb = FirstId(entry.Members.Select(m => m.Imdb)),
            },
            DateCreated = main.DateCreated.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(main.DateCreated, DateTimeKind.Utc) : main.DateCreated.ToUniversalTime(),
            Flags = EntryFlags(entry),
        };
    }

    /// <summary>
    /// Gets the resolution label by width: 3800 and up 2160p, 2500 1440p, 1900 1080p, 1200 720p,
    /// else SD; scope-width films such as 1920×800 are still 1080p.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <returns>The label, or null when the width is unknown.</returns>
    internal static string? ResolutionLabel(int? width) => width switch
    {
        null or <= 0 => null,
        >= 3800 => "2160p",
        >= 2500 => "1440p",
        >= 1900 => "1080p",
        >= 1200 => "720p",
        _ => "SD",
    };

    private static List<string> EntryFlags(GroupedEntry entry)
    {
        var flags = new List<string>();
        void Add(bool on, GroupFlags flag)
        {
            if (on)
            {
                flags.Add(flag.ToString());
            }
        }

        Add(entry.Year.Year is null, GroupFlags.YearUnknown);
        Add(entry.State != VersionSetState.None, GroupFlags.BrokenMerge);
        Add(entry.Members.Any(m => m.VideoType != VideoType.VideoFile), GroupFlags.ContainsDiscRip);
        Add(entry.Members.Any(m => m.IsShortcut), GroupFlags.ContainsShortcut);
        Add(entry.Members.Any(m => m.IsPlaceHolder), GroupFlags.ContainsPlaceholder);
        return flags;
    }

    private static string? FirstId(IEnumerable<string?> ids) => ids.Select(id => id?.Trim()).FirstOrDefault(id => !string.IsNullOrEmpty(id));

    // A [Flags] value's names, in the enum's order, None left out.
    private static List<string> Names<T>(T value)
        where T : struct, Enum
        => Enum.GetValues<T>().Where(v => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0 && value.HasFlag(v)).Select(v => v.ToString()).ToList();

    private GroupsQuery Parse(string? mode, string? unknownYear, string? libraryIds, string? confidence, string? search, string? sort, int? startIndex, int? limit)
        => GroupsQuery.Parse(_configuration.GetConfiguration(), _time.GetUtcNow().UtcDateTime, mode, unknownYear, libraryIds, confidence, search, sort, startIndex, limit);

    private static double Milliseconds(long ticks) => Math.Round(TimeSpan.FromTicks(ticks).TotalMilliseconds, 1);

    // A new snapshot: drop what the old one made at once, so it isn't kept alive until the next read.
    private void OnPublished(object? sender, IndexPublishedEventArgs e)
    {
        Cache.Retire(e.Version);
        Projections.Retire(e.Version);
        _visibility.Retire(e.Version);
    }

    // New title rules: the projections and results made with the old ones can't be asked for any more.
    private void OnSettingsChanged(object? sender, PluginConfigurationChangedEventArgs e)
    {
        var today = _time.GetUtcNow().UtcDateTime;
        if (!string.Equals(GroupingOptions.FromConfiguration(e.Previous, today).NormalizationHash(), GroupingOptions.FromConfiguration(e.Current, today).NormalizationHash(), StringComparison.Ordinal))
        {
            Projections.Clear();
            Cache.Clear();
        }
    }

    // One grouping (a cache miss): the snapshot's projections (kept), narrowed to the libraries unless they are every one
    // the index read and to the movies the caller can see, bucketed once, grouped in the mode and counted in the other.
    // Callers who can't see the same movies never share a result (the hidden set's digest is part of the key).
    private GroupingResult Grouped(MovieIndexSnapshot snapshot, GroupingOptions options, IReadOnlyList<Guid> libraries, HiddenItems? hidden)
        => Cache.GetOrAdd(snapshot.Version, options.Hash(), libraries, () => GroupNow(snapshot, options, libraries, hidden), hidden?.Key);

    private GroupingResult GroupNow(MovieIndexSnapshot snapshot, GroupingOptions options, IReadOnlyList<Guid> libraries, HiddenItems? hidden)
    {
        var watch = Stopwatch.StartNew();
        var projected = Projections.GetOrAdd(snapshot, options.Normalization, options.NormalizationHash(), out var projection);
        var scope = libraries.Count == snapshot.Libraries.Select(l => l.Id).Distinct().Count() ? null : libraries.ToHashSet();
        var buckets = DuplicateGrouper.Bucket(projected, scope, options.Normalization, hidden?.Ids);
        var result = DuplicateGrouper.Group(buckets, options) with
        {
            AlternateModeGroupCount = DuplicateGrouper.Count(buckets, options with { Mode = Other(options.Mode) })
        };
        var ticks = watch.Elapsed.Ticks;
        Interlocked.Exchange(ref _lastGroupingTicks, ticks);
        Interlocked.Increment(ref _groupings);
        var grouping = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Utc"] = _time.GetUtcNow().UtcDateTime,
            ["Ms"] = Milliseconds(ticks),
            ["ProjectionMs"] = projection is { } took ? Milliseconds(took.Ticks) : null,
            ["Version"] = snapshot.Version,
            ["Mode"] = options.Mode.ToString(),
            ["UnknownYear"] = options.UnknownYear.ToString(),
            ["Entries"] = result.Entries,
            ["Groups"] = result.Groups.Count,
            ["AlternateModeGroups"] = result.AlternateModeGroupCount,
        };
        lock (_recent)
        {
            _recent.Enqueue(grouping);
            while (_recent.Count > RecentCapacity)
            {
                _recent.Dequeue();
            }
        }

        return result;
    }
}
