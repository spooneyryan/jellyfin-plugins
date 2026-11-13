using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.SmartSearch.Query.Evaluation;
using Jellyfin.Plugin.SmartSearch.Query.Execution;
using Jellyfin.Plugin.SmartSearch.Query.Planning;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.SmartSearch.Search;

/// <summary>
/// The value index: per library folder and <see cref="ValueProfile">parental profile</see>, the distinct Genres, Tags,
/// Studios, parental ratings and container tokens with how many items of each kind carry each, the series, and the series
/// names the episodes store. It serves the rule builder's suggestions, the series picker and the planner's scoped lookups
/// (<see cref="IScopedLookups"/>), so a user is never offered, or planned by, a value from an item they can't see.
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> A folder's index is built from the items <c>/Items</c> would show a user of that profile there:
/// the movie, series, episode, music video and video rows under the folder (<c>TopParentIds</c>, so never an extra or a
/// local alternate), not missing, loaded with the user's access query (<c>SetUser</c>'s parental limit, unrated kinds and
/// tags in SQL, the unrated <c>Other</c> kind as <c>/Items</c> adds it), and each through <c>IsVisible(user)</c>, which
/// <c>GetItemList</c> applies with a user. Both read only the profile, so the index is shared by the users who have it.
/// Folders are the unit (not libraries) because an item has one top folder while a folder can be in two libraries:
/// summing a user's folders counts each item once.</para>
/// <para><b>Builds.</b> One build per (folder, profile) at a time; a second caller of the same key waits for it with its
/// own cancellation token, and other keys build alongside, up to <see cref="DefaultBuildSlots"/> at once. A build that
/// waits longer than its slot wait for a slot, or runs past its timeout, ends, and its callers get 503 <c>Busy</c>. A
/// build isn't tied to the request that started it: a caller that goes away leaves it to finish for the next one. After a
/// build ran past its timeout, its key isn't built again for <see cref="BuildBackOff"/>: a caller without an index of
/// that key gets <c>Busy</c> at once, and one with a stale index is served it.</para>
/// <para><b>Freshness.</b> Items of the indexed kinds added, updated or removed make every index stale, except updates of
/// images alone (chapter images are saved that way too); other kinds (people, seasons, folders) never do. The staleness
/// waits until no such event came for <see cref="Debounce"/> (at most <see cref="MaxDebounce"/> after the first), so a
/// burst rebuilds once. A stale index is still served to suggestions while its one rebuild runs in the background; only a
/// key with no index yet makes its caller wait for the build. While a library scan runs the last index is served (a first
/// one is built), so suggestions can lag the scan; the first use after it rebuilds once. At most
/// <see cref="MaxEntries"/> indexes are kept, the least recently used dropped first.</para>
/// <para><b>The planner never waits for a build.</b> It takes an index only when every folder's is built and no event has
/// touched it since, outside a scan. Otherwise it reads the episodes' series names or the studio for its plan itself
/// (<see cref="LoadedLookups"/>), and starts the missing or stale builds in the background without awaiting them, so a
/// later plan finds them.</para>
/// </remarks>
internal sealed class ValueIndexService : IScopedLookups, IDisposable
{
    /// <summary>The most (folder, profile) indexes kept.</summary>
    public const int MaxEntries = 256;

    /// <summary>The items loaded per query while building.</summary>
    public const int BatchSize = 1_000;

    /// <summary>How many builds run at once by default.</summary>
    public const int DefaultBuildSlots = 2;

    /// <summary>The fields the index serves, by registry id.</summary>
    public static readonly IReadOnlyList<string> Fields = ["Genres", "Tags", "Studios", "OfficialRating", "Container"];

    /// <summary>How long a build waits for a slot by default.</summary>
    public static readonly TimeSpan DefaultSlotWait = TimeSpan.FromSeconds(10);

    /// <summary>How long a build may run by default.</summary>
    public static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long after the last item event the indexes go stale.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    /// <summary>The longest a burst of item events holds the staleness back.</summary>
    public static readonly TimeSpan MaxDebounce = TimeSpan.FromSeconds(30);

    /// <summary>How long a key isn't built again after a build of it ran past its timeout.</summary>
    public static readonly TimeSpan BuildBackOff = TimeSpan.FromSeconds(60);

    /// <summary>The kinds the index holds.</summary>
    public static readonly IReadOnlySet<BaseItemKind> Kinds = new HashSet<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.MusicVideo, BaseItemKind.Video };

    private readonly ILibraryManager _library;
    private readonly LibraryAccess _access;
    private readonly TimeProvider _time;
    private readonly TimeSpan _slotWait;
    private readonly TimeSpan _buildTimeout;
    private readonly SemaphoreSlim _slots;
    private readonly LoadedLookups _loaded;
    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid Folder, string Profile), Entry> _entries = [];
    private readonly Dictionary<(Guid Folder, string Profile), Task<Entry>> _building = [];
    private readonly Dictionary<(Guid Folder, string Profile), DateTimeOffset> _backOffUntil = [];
    private long _events;
    private DateTimeOffset _lastEvent = DateTimeOffset.MinValue;
    private DateTimeOffset _burstStart = DateTimeOffset.MinValue;
    private long _uses;
    private int _builds;

    /// <summary>
    /// Initializes a new instance of the <see cref="ValueIndexService"/> class.
    /// </summary>
    /// <param name="library">The library manager: its item events make the indexes stale.</param>
    /// <param name="access">The kit's library access.</param>
    /// <param name="time">The clock (the debounce, the slot wait and the build timeout).</param>
    /// <param name="buildSlots">How many builds run at once.</param>
    /// <param name="slotWait">How long a build waits for a slot; <c>null</c> for <see cref="DefaultSlotWait"/>.</param>
    /// <param name="buildTimeout">How long a build may run; <c>null</c> for <see cref="DefaultBuildTimeout"/>.</param>
    public ValueIndexService(ILibraryManager library, LibraryAccess access, TimeProvider time, int buildSlots = DefaultBuildSlots, TimeSpan? slotWait = null, TimeSpan? buildTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(buildSlots, 1);
        _library = library;
        _access = access;
        _time = time;
        _slotWait = slotWait ?? DefaultSlotWait;
        _buildTimeout = buildTimeout ?? DefaultBuildTimeout;
        _slots = new SemaphoreSlim(buildSlots, buildSlots);
        _loaded = new LoadedLookups(library);
        _library.ItemAdded += OnItemAdded;
        _library.ItemUpdated += OnItemUpdated;
        _library.ItemRemoved += OnItemRemoved;
    }

    /// <summary>Gets how many indexes have been built (for tests and diagnostics).</summary>
    public int Builds => Volatile.Read(ref _builds);

    /// <summary>Gets how many builds are running now, including those no caller waits for (for tests).</summary>
    internal int RunningBuilds
    {
        get
        {
            lock (_gate)
            {
                return _building.Count;
            }
        }
    }

    /// <summary>Gets the values of a field in the libraries a user asks for (within theirs), with their counts.</summary>
    /// <param name="user">The user.</param>
    /// <param name="fieldId">One of <see cref="Fields"/>.</param>
    /// <param name="libraryIds">Library ids; none for every library the user can open.</param>
    /// <param name="kinds">The item kinds counted (the server's allowed kinds).</param>
    /// <param name="cancellationToken">The request's token, for its waits.</param>
    /// <returns>Each value with the number of the user's visible items of those kinds that carry it, summed over the folders.</returns>
    /// <exception cref="SmartSearchRefusedException">A build waited too long for a slot or ran too long (<c>Busy</c>).</exception>
    public async Task<IReadOnlyDictionary<string, int>> ValuesAsync(User user, string fieldId, IReadOnlyCollection<Guid>? libraryIds, IReadOnlyCollection<BaseItemKind> kinds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(kinds);
        if (!Fields.Contains(fieldId, StringComparer.Ordinal))
        {
            throw new ArgumentException("The value index has no field " + fieldId + ".", nameof(fieldId));
        }

        var libraries = (libraryIds is null || libraryIds.Count == 0 ? _access.VisibleLibraries(user) : _access.ResolveLibraries(libraryIds, user))
            .Where(l => l.CollectionType != CollectionType.boxsets);
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in await EntriesAsync(LibraryAccess.TopParentIds(libraries), user, cancellationToken).ConfigureAwait(false))
        {
            foreach (var (kind, values) in entry.Values[fieldId])
            {
                if (!kinds.Contains(kind))
                {
                    continue;
                }

                foreach (var (value, count) in values)
                {
                    totals[value] = totals.GetValueOrDefault(value) + count;
                }
            }
        }

        return totals;
    }

    /// <summary>Gets the series a user can see in a library's folders.</summary>
    /// <param name="user">The user.</param>
    /// <param name="folders">The library's physical folders.</param>
    /// <param name="cancellationToken">The request's token, for its waits.</param>
    /// <returns>The series, each once.</returns>
    /// <exception cref="SmartSearchRefusedException">A build waited too long for a slot or ran too long (<c>Busy</c>).</exception>
    public async Task<IReadOnlyList<IndexedSeries>> SeriesAsync(User user, IReadOnlyList<Guid> folders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(folders);
        var entries = await EntriesAsync(folders, user, cancellationToken).ConfigureAwait(false);
        return entries.SelectMany(e => e.Series).DistinctBy(s => s.Id).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<EpisodeSeries>? EpisodeSeries(User user, IReadOnlyList<Guid> folders, bool includeMissing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(folders);
        cancellationToken.ThrowIfCancellationRequested();

        // The index holds no missing episode: then the episodes are read for the plan.
        if (includeMissing)
        {
            return _loaded.EpisodeSeries(user, folders, includeMissing, cancellationToken);
        }

        // No current index (a scan runs, an item just changed, a build runs or isn't there yet): read them for this plan.
        return CurrentEntries(folders, user) is { } entries
            ? entries.SelectMany(e => e.EpisodeSeries).Distinct().ToList()
            : _loaded.EpisodeSeries(user, folders, includeMissing, cancellationToken);
    }

    /// <inheritdoc />
    public bool HasStudio(User user, IReadOnlyList<Guid> folders, IReadOnlyCollection<BaseItemKind> kinds, bool includeMissing, string name, Guid studioId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(kinds);
        cancellationToken.ThrowIfCancellationRequested();
        if (!includeMissing && CurrentEntries(folders, user) is { } entries)
        {
            var folded = RuleValues.Fold(name);
            return entries.Any(e => e.StudioFolds.Contains(folded));
        }

        // No current index (a scan runs, an item just changed, a build runs or isn't there yet), or missing items in scope:
        // look it up.
        return _loaded.HasStudio(user, folders, kinds, includeMissing, name, studioId, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemUpdated -= OnItemUpdated;
        _library.ItemRemoved -= OnItemRemoved;
        _slots.Dispose();
    }

    /// <summary>
    /// Tells whether an item event can change what an index holds: an item of the indexed kinds added, removed, or updated
    /// other than in its images alone (core reports the removed descendants of a folder one by one).
    /// </summary>
    /// <param name="e">The event.</param>
    /// <param name="update">Whether it is an update.</param>
    /// <returns>Whether it makes the indexes stale.</returns>
    internal static bool Matters(ItemChangeEventArgs e, bool update)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (update && e.UpdateReason == ItemUpdateType.ImageUpdate)
        {
            return false;
        }

        return e.Item is not { } item || Kinds.Contains(item.GetBaseItemKind());
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => OnItemChanged(e, update: false);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => OnItemChanged(e, update: true);

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e) => OnItemChanged(e, update: false);

    private void OnItemChanged(ItemChangeEventArgs e, bool update)
    {
        if (!Matters(e, update))
        {
            return;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (now - _lastEvent >= Debounce)
            {
                _burstStart = now;
            }

            _lastEvent = now;
            _events++;
        }
    }

    // The folders' entries for the planner, when each is built and current (no event since its build) and no scan runs;
    // else null. Never waits: a missing or stale entry's build is started in the background (unless one runs, or its key
    // backs off after a timeout) for a later plan.
    private List<Entry>? CurrentEntries(IReadOnlyList<Guid> folders, User user)
    {
        if (_library.IsScanRunning)
        {
            return null;
        }

        var profile = ValueProfile.KeyOf(user);
        var entries = new List<Entry>();
        var complete = true;
        lock (_gate)
        {
            foreach (var folder in folders.Distinct())
            {
                var key = (folder, profile);
                var found = _entries.TryGetValue(key, out var entry);
                if (found && entry!.Generation == _events)
                {
                    entry.LastUse = ++_uses;
                    entries.Add(entry);
                    continue;
                }

                complete = false;
                if (!found || !IsServed(entry!))
                {
                    StartBuildLocked(key, user);
                }
            }
        }

        return complete ? entries : null;
    }

    private async Task<List<Entry>> EntriesAsync(IEnumerable<Guid> folders, User user, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = ValueProfile.KeyOf(user);
        var tasks = folders.Distinct().Select(folder => GetAsync((folder, profile), user, cancellationToken)).ToList();
        return [.. await Task.WhenAll(tasks).ConfigureAwait(false)];
    }

    private Task<Entry> GetAsync((Guid Folder, string Profile) key, User user, CancellationToken cancellationToken)
    {
        Task<Entry>? build;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                // Current, or stale and still served (a scan runs, or its events are still coming); or stale and served
                // while its one rebuild runs in the background.
                if (!IsServed(entry))
                {
                    StartBuildLocked(key, user);
                }

                entry.LastUse = ++_uses;
                return Task.FromResult(entry);
            }

            build = StartBuildLocked(key, user) ?? throw SmartSearchRefusedException.Busy(SmartSearchRefusedException.IndexBusyMessage);
        }

        return build.WaitAsync(cancellationToken);
    }

    // Under _gate: the key's running build, or a new one; null while the key backs off after a build that timed out.
    private Task<Entry>? StartBuildLocked((Guid Folder, string Profile) key, User user)
    {
        if (_building.TryGetValue(key, out var running))
        {
            return running;
        }

        if (_backOffUntil.TryGetValue(key, out var until))
        {
            if (_time.GetUtcNow() < until)
            {
                return null;
            }

            _backOffUntil.Remove(key);
        }

        var build = Task.Run(() => BuildAsync(key, user), CancellationToken.None);
        _building[key] = build;

        // A build nobody waits for (one started for the planner, or the rebuild of a stale index) has its failure
        // observed all the same.
        _ = build.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return build;
    }

    // Under _gate: current, or stale but still served (a scan runs, or the events that staled it are still coming).
    private bool IsServed(Entry entry)
    {
        if (entry.Generation == _events || _library.IsScanRunning)
        {
            return true;
        }

        var now = _time.GetUtcNow();
        return now - _lastEvent < Debounce && now - _burstStart < MaxDebounce;
    }

    private async Task<Entry> BuildAsync((Guid Folder, string Profile) key, User user)
    {
        try
        {
            using (var wait = new CancellationTokenSource(_slotWait, _time))
            {
                try
                {
                    await _slots.WaitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw SmartSearchRefusedException.Busy(SmartSearchRefusedException.IndexBusyMessage);
                }
            }

            try
            {
                using var timeout = new CancellationTokenSource(_buildTimeout, _time);
                long generation;
                lock (_gate)
                {
                    generation = _events;
                }

                Entry entry;
                try
                {
                    entry = Build(key.Folder, user, generation, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        _backOffUntil[key] = _time.GetUtcNow() + BuildBackOff;
                    }

                    throw SmartSearchRefusedException.Busy(SmartSearchRefusedException.IndexBusyMessage);
                }

                lock (_gate)
                {
                    entry.LastUse = ++_uses;
                    _entries[key] = entry;
                    while (_entries.Count > MaxEntries)
                    {
                        _entries.Remove(_entries.MinBy(e => e.Value.LastUse).Key);
                    }
                }

                Interlocked.Increment(ref _builds);
                return entry;
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            lock (_gate)
            {
                _building.Remove(key);
            }
        }
    }

    private Entry Build(Guid folder, User user, long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var query = QueryPlanner.CreateAccessQuery(user, PlanPurpose.Search);
        query.TopParentIds = [folder];
        query.IncludeItemTypes = [.. Kinds];
        query.IsVirtualItem = false;
        query.DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false };
        var ids = _library.GetItemIds(query);
        cancellationToken.ThrowIfCancellationRequested();
        var values = Fields.ToDictionary(f => f, _ => new Dictionary<BaseItemKind, Dictionary<string, int>>(), StringComparer.Ordinal);
        var series = new List<IndexedSeries>();
        var episodeSeries = new HashSet<EpisodeSeries>();
        foreach (var batch in ids.Chunk(BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            query.ItemIds = batch;
            foreach (var item in _library.GetItemList(query))
            {
                if (LibraryAccess.IsOwned(item))
                {
                    continue;
                }

                var kind = item.GetBaseItemKind();
                Add(values["Genres"], kind, item.Genres);
                Add(values["Tags"], kind, item.Tags);
                Add(values["Studios"], kind, item.Studios);
                Add(values["OfficialRating"], kind, [item.OfficialRating]);
                Add(values["Container"], kind, string.IsNullOrEmpty(item.Container) ? [] : ChoiceEvaluator.Tokens(item.Container));
                if (item is Series)
                {
                    series.Add(new IndexedSeries(item.Id, item.Name, item.SortName, item.ProductionYear, RuleValues.Fold(item.Name ?? string.Empty)));
                }
                else if (item is Episode episode)
                {
                    episodeSeries.Add(new EpisodeSeries(episode.SeriesId, episode.SeriesName));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        var studioFolds = values["Studios"].Values.SelectMany(v => v.Keys).Select(RuleValues.Fold).ToHashSet(StringComparer.Ordinal);
        return new Entry(generation, values, series, [.. episodeSeries], studioFolds);
    }

    // Each distinct non-blank value of one item counts once, under the item's kind.
    private static void Add(Dictionary<BaseItemKind, Dictionary<string, int>> byKind, BaseItemKind kind, IEnumerable<string?>? values)
    {
        if (!byKind.TryGetValue(kind, out var counts))
        {
            counts = new Dictionary<string, int>(StringComparer.Ordinal);
            byKind[kind] = counts;
        }

        foreach (var value in (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal))
        {
            counts[value!] = counts.GetValueOrDefault(value!) + 1;
        }
    }

    private sealed class Entry(
        long generation,
        Dictionary<string, Dictionary<BaseItemKind, Dictionary<string, int>>> values,
        IReadOnlyList<IndexedSeries> series,
        IReadOnlyList<EpisodeSeries> episodeSeries,
        IReadOnlySet<string> studioFolds)
    {
        public long Generation { get; } = generation;

        public Dictionary<string, Dictionary<BaseItemKind, Dictionary<string, int>>> Values { get; } = values;

        public IReadOnlyList<IndexedSeries> Series { get; } = series;

        public IReadOnlyList<EpisodeSeries> EpisodeSeries { get; } = episodeSeries;

        public IReadOnlySet<string> StudioFolds { get; } = studioFolds;

        public long LastUse { get; set; }
    }
}
