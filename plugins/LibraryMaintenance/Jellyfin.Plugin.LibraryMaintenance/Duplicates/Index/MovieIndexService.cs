using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Api.Models;
using Jellyfin.Plugin.LibraryMaintenance.Configuration;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using ItemVisibility = Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups.ItemVisibility;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

/// <summary>
/// Holds the immutable movie index and keeps it fresh, single-flight: never two builds at once, and a rebuild
/// asked for during a build runs once after it. A failed build keeps the index before it.
/// </summary>
/// <remarks>
/// <para>What builds it: an administrator's <c>POST /Index/Rebuild</c>; a read
/// (<see cref="GetSnapshotAsync"/>) that finds no index, or an out-of-date one, and waits up to <c>RequestWaitSeconds</c>
/// for it; a batch of library changes (<see cref="DuplicatesChangeConsumer"/>, after the kit's 60 s quiet period); the
/// post-scan task's full pass; and a save of the settings that changes what the index reads or turns Duplicates on. A
/// batch, a full pass or a save marks the index out of date (<c>Dirty</c>) and builds it only when the page was used in
/// the last 24 hours or <c>WarmIndexAfterScan</c> is on; otherwise the next read builds it. With Duplicates turned off a
/// batch only marks it out of date, so turning it on never serves the old index as current.</para>
/// <para>The scan rule: while a library scan runs, an index that exists is never rebuilt but by the post-scan task's full
/// pass, which comes once the scan has read the libraries; a server with no index builds one anyway, marked
/// <c>BuiltDuringScan</c>. A build the rule put off and that the post-scan build didn't cover (asked for after it started)
/// runs when the scan ends.</para>
/// <para>A failed build: <c>LastError</c> says so in a fixed sentence (the log has the exception); for
/// <see cref="FailureBackoff"/> after it a read doesn't start another build, so a page polling a server whose builds fail
/// doesn't start one per request; <see cref="Rebuild"/> retries at once.</para>
/// <para>Our own actions (1.1) rebuild through <see cref="Rebuild"/>: there is no snapshot patching (decided
/// 2026-11-01).</para>
/// </remarks>
internal sealed class MovieIndexService : IDuplicatesIndex, IDisposable
{
    /// <summary>The longest <c>RequestWaitSeconds</c> (0 to 30).</summary>
    public const int MaxRequestWaitSeconds = 30;

    /// <summary>How long the page counts as in use after a request: its batches build the index meanwhile.</summary>
    public static readonly TimeSpan PageUseWindow = TimeSpan.FromHours(24);

    /// <summary>How often a build the scan rule put off checks whether the scan has ended.</summary>
    public static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long after a failed build a read doesn't start another; a rebuild always does.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(30);

    private readonly MovieIndexBuilder _builder;
    private readonly ILibraryManager _library;
    private readonly IPluginConfigurationProvider _configuration;
    private readonly ILogger<MovieIndexService> _logger;
    private readonly TimeProvider _time;
    private readonly LibraryAccess? _access;
    private readonly ItemVisibility? _visibility;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stoppingToken;
    private readonly object _gate = new();
    private readonly Dictionary<BuildReason, int> _builds = [];
    private MovieIndexSnapshot? _current;
    private Task? _running;
    private BuildReason _reason;
    private bool _dirty;
    private bool _followUp;
    private BuildReason _followUpReason;
    private bool _waitingForScan;
    private BuildReason _deferredReason;
    private int _deferredByScan;
    private DateTimeOffset? _pageUsedUtc;
    private BuildReason? _lastReason;
    private double _progress;
    private long _version;
    private int _failures;
    private int _backedOff;
    private string? _lastError;
    private DateTime? _lastErrorUtc;
    private DateTimeOffset? _failedAt;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MovieIndexService"/> class.
    /// </summary>
    /// <param name="builder">The builder.</param>
    /// <param name="library">The library manager, for <c>IsScanRunning</c>.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock; the system's by default.</param>
    /// <param name="access">Which libraries a caller can open, for <see cref="GetStatus(User)"/>; without it every caller
    /// counts every library (tests of the index alone).</param>
    /// <param name="visibility">The movies a caller's parental rating and tags hide, for <see cref="GetStatus(User)"/>; without
    /// it none is.</param>
    public MovieIndexService(MovieIndexBuilder builder, ILibraryManager library, IPluginConfigurationProvider configuration, ILogger<MovieIndexService> logger, TimeProvider? time = null, LibraryAccess? access = null, ItemVisibility? visibility = null)
    {
        _builder = builder;
        _library = library;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _access = access;
        _visibility = visibility;
        _stoppingToken = _stopping.Token;
        _configuration.Changed += OnSettingsChanged;
    }

    /// <summary>
    /// Raised after a build made a new snapshot the index (outside the service's lock): what was made from an older one,
    /// such as the group results, is old (nothing keeps an old snapshot alive until the next read).
    /// </summary>
    public event EventHandler<IndexPublishedEventArgs>? Published;

    /// <inheritdoc />
    public bool IsEnabled => _configuration.GetConfiguration().DuplicatesEnabled;

    /// <summary>Gets the current index, or null before the first build.</summary>
    public MovieIndexSnapshot? Current => Volatile.Read(ref _current);

    /// <summary>Gets a value indicating whether a library scan is running.</summary>
    public bool ScanInProgress => _library.IsScanRunning;

    /// <summary>Gets when the page last read the index (the warm rule's 24 hours), or null since the server started.</summary>
    public DateTimeOffset? PageUsedUtc
    {
        get
        {
            lock (_gate)
            {
                return _pageUsedUtc;
            }
        }
    }

    /// <summary>
    /// Gets the index's status over every library it read (Diagnostics' numbers, and tests); it counts as a use of the page.
    /// </summary>
    /// <returns>The status.</returns>
    public LibraryMaintenanceDuplicateIndexDto GetStatus() => GetStatus(null);

    /// <inheritdoc />
    /// <remarks>It counts as a use of the page, as the routes call it; Diagnostics doesn't.</remarks>
    public LibraryMaintenanceDuplicateIndexDto GetStatus(User? user)
    {
        NoteUse();
        var snapshot = Current;
        var scope = Scope(snapshot, user);
        return Status(snapshot, scope, Hidden(snapshot, user, scope));
    }

    /// <summary>
    /// Starts a rebuild, as <see cref="RequestRebuild(User)"/> for a caller who sees every library.
    /// </summary>
    /// <returns>The status right after asking.</returns>
    public LibraryMaintenanceDuplicateIndexDto RequestRebuild() => RequestRebuild(null);

    /// <inheritdoc />
    public LibraryMaintenanceDuplicateIndexDto RequestRebuild(User? user)
    {
        NoteUse();
        Rebuild();
        var snapshot = Current;
        var scope = Scope(snapshot, user);
        return Status(snapshot, scope, Hidden(snapshot, user, scope));
    }

    /// <inheritdoc />
    public bool SeesEveryLibrary(User? user) => Scope(Current, user) is null && (user is null || !ItemVisibility.HasItemRestrictions(user));

    /// <summary>
    /// Marks the index out of date without building it: a change Duplicates saw while it was turned off,
    /// so the first read after it is turned on builds.
    /// </summary>
    public void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
        }
    }

    /// <summary>
    /// Builds the index (an administrator's request, or our own action in 1.1), or asks for one more after the running
    /// build. While a library scan runs and an index exists, it waits for the post-scan build (the scan rule).
    /// </summary>
    /// <returns>The running builds' task: it ends when no build runs any more (at once when none started).</returns>
    public Task Rebuild() => Trigger(BuildReason.Rebuild);

    /// <summary>
    /// A batch of library changes: the index is out of date, and is built when the page is in use or
    /// <c>WarmIndexAfterScan</c> is on, and no scan runs.
    /// </summary>
    /// <returns>The running builds' task, or a completed one.</returns>
    public Task OnLibraryChanged() => Trigger(BuildReason.Changes);

    /// <summary>
    /// The post-scan task's full pass: the scan has read the libraries, so the index is built now, once, when the
    /// page is in use or <c>WarmIndexAfterScan</c> is on. Its snapshot isn't <c>BuiltDuringScan</c>.
    /// </summary>
    /// <returns>The running builds' task, or a completed one.</returns>
    public Task OnLibraryScanned() => Trigger(BuildReason.Scan);

    /// <summary>
    /// Reads the index for a request (the groups): a current index at once; with none, an
    /// out-of-date one or a build running, a build (subject to the scan rule and <see cref="FailureBackoff"/>; a running
    /// one is waited for) and up to <c>RequestWaitSeconds</c> of waiting for it, then the index there is, <c>Stale</c>
    /// when a build still runs or the scan rule put it off, or none (the route's 202, or its 503 when the last build failed
    /// and none runs).
    /// </summary>
    /// <param name="cancellationToken">The request's token.</param>
    /// <returns>The read.</returns>
    public async Task<IndexRead> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        NoteUse();
        lock (_gate)
        {
            // Current: built since the last change, and no build running (one runs only because the index is out of date;
            // its start clears the flag, not the index).
            if (_current is { } current && !_dirty && _running is null)
            {
                return new IndexRead(current, false, false) { LastError = _lastError, LastErrorUtc = _lastErrorUtc };
            }
        }

        var build = Trigger(BuildReason.Request);
        var seconds = Math.Clamp(_configuration.GetConfiguration().RequestWaitSeconds, 0, MaxRequestWaitSeconds);
        if (!build.IsCompleted && seconds > 0)
        {
            try
            {
                await build.WaitAsync(TimeSpan.FromSeconds(seconds), _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Answered from the index there is; the build goes on.
            }
        }

        lock (_gate)
        {
            var rebuilding = _running is not null;
            return new IndexRead(_current, _current is not null && (_dirty || rebuilding), rebuilding) { LastError = _lastError, LastErrorUtc = _lastErrorUtc };
        }
    }

    /// <summary>
    /// Describes the index for Diagnostics (the cost numbers measured before 1.0): the last build's time, counts and cost, and
    /// the builds since the server started. Its counts cover every library the index read (<c>Scope</c>), so Diagnostics
    /// shows them only to an administrator who can open every one.
    /// </summary>
    /// <returns>The description.</returns>
    public IDictionary<string, object?> Describe()
    {
        var snapshot = Current;
        var status = Status(snapshot, null, null);
        int failures, deferred, backedOff;
        DateTimeOffset? used;
        BuildReason? lastReason;
        Dictionary<string, int> builds;
        lock (_gate)
        {
            failures = _failures;
            deferred = _deferredByScan;
            backedOff = _backedOff;
            used = _pageUsedUtc;
            lastReason = _lastReason;
            builds = Enum.GetValues<BuildReason>().ToDictionary(r => r.ToString(), r => _builds.GetValueOrDefault(r));
        }

        return new Dictionary<string, object?>
        {
            ["Scope"] = "Server-wide: every library the index read",
            ["Version"] = status.Version,
            ["GeneratedUtc"] = status.GeneratedUtc,
            ["BuildMs"] = status.BuildMs,
            ["BuiltDuringScan"] = status.BuiltDuringScan,
            ["Movies"] = status.Movies,
            ["Entries"] = status.Entries,
            ["MergedEntries"] = status.MergedEntries,
            ["BrokenEntries"] = status.BrokenEntries,
            ["Skipped"] = status.Skipped,
            ["Excluded"] = status.Excluded,
            ["Owned"] = snapshot?.Stats.Owned ?? 0,
            ["States"] = snapshot is null ? null : Enum.GetValues<VersionSetState>().Where(s => s != VersionSetState.None).ToDictionary(s => s.ToString(), s => snapshot.Stats.States.GetValueOrDefault(s)),
            ["Libraries"] = status.Libraries.Count,
            ["LibraryQueries"] = snapshot?.Metrics.LibraryQueries,
            ["ItemQueries"] = snapshot?.Metrics.ItemQueries,
            ["AllocatedMB"] = snapshot is null ? null : Math.Round(snapshot.Metrics.AllocatedBytes / 1048576.0, 1),
            ["WorkingSetMB"] = snapshot is null ? null : Math.Round(snapshot.Metrics.WorkingSetBytes / 1048576.0, 1),
            ["PeakWorkingSetMB"] = snapshot is null ? null : Math.Round(snapshot.Metrics.PeakWorkingSetBytes / 1048576.0, 1),
            ["Rebuilding"] = status.Rebuilding,
            ["Dirty"] = status.Dirty,
            ["LastBuildReason"] = lastReason?.ToString(),
            ["Builds"] = builds,
            ["DeferredByScan"] = deferred,
            ["PageUsedUtc"] = used?.UtcDateTime,
            ["WarmIndexAfterScan"] = _configuration.GetConfiguration().WarmIndexAfterScan,
            ["FailedBuilds"] = failures,
            ["ReadsBackedOff"] = backedOff,
            ["LastError"] = status.LastError,
            ["LastErrorUtc"] = status.LastErrorUtc
        };
    }

    /// <summary>
    /// Stops a running build at its next query. Idempotent: the container disposes the service once for each of its
    /// registrations (<see cref="MovieIndexService"/> and <see cref="IDuplicatesIndex"/>).
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping.Cancel();
        }

        _configuration.Changed -= OnSettingsChanged;
        _stopping.Dispose();
    }

    /// <summary>
    /// Gets what the settings make the index read (the included libraries and the excluded folders), as one string: a
    /// change to it marks the index out of date. The title rules and the mode aren't in it: the
    /// group results are keyed by them (<c>OptionsHash</c>), so a change to them groups again without a build.
    /// </summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>The key.</returns>
    internal static string IndexKey(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var libraries = (configuration.IncludedLibraryIds ?? []).Where(id => id != Guid.Empty).Distinct()
            .Select(id => id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal);
        var folders = (configuration.ExcludedPathPrefixes ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return string.Join(',', libraries) + "\n" + string.Join('\u001f', folders);
    }

    /// <summary>
    /// Counts the index as a caller sees it: the entries with a member
    /// in the caller's libraries, and of each only those members (<see cref="EntryScope"/>), none their parental rating or
    /// tags hide; and of the movies the build left out, those in the caller's libraries that aren't hidden from them.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="scope">The caller's libraries, or null for every library the index read.</param>
    /// <param name="hidden">The movies the caller can't see, or null for none.</param>
    /// <returns>The counts.</returns>
    internal static IndexStats ScopedStats(MovieIndexSnapshot snapshot, IReadOnlySet<Guid>? scope, IReadOnlySet<Guid>? hidden = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int movies = 0, entries = 0, merged = 0, broken = 0;
        var states = Enum.GetValues<VersionSetState>().Where(s => s != VersionSetState.None).ToDictionary(s => s, _ => 0);
        foreach (var entry in snapshot.Entries)
        {
            if (EntryScope.View(entry, scope, hidden) is not { } view)
            {
                continue;
            }

            entries++;
            movies += view.Members.Count;
            if (view.IsWhole ? entry.AllIds.Count > 1 : view.Members.Count > 1)
            {
                merged++;
            }

            if (view.State == VersionSetState.None)
            {
                continue;
            }

            broken++;
            foreach (var state in states.Keys.Where(s => view.State.HasFlag(s)).ToList())
            {
                states[state]++;
            }
        }

        return new IndexStats
        {
            Movies = movies,
            Entries = entries,
            MergedEntries = merged,
            BrokenEntries = broken,
            Skipped = scope is null && hidden is null ? snapshot.Stats.Skipped : snapshot.Stats.SkippedMovies.Count(m => Sees(m, scope, hidden)),
            Excluded = scope is null && hidden is null ? snapshot.Stats.Excluded : snapshot.Stats.ExcludedMovies.Count(m => Sees(m, scope, hidden)),
            States = states
        };
    }

    // Whether a caller sees a movie the build left out (the 1.0 verifier): in one of their libraries, and not one their
    // parental rating or tags hide, so "Skipped: no name" and "Left out by the excluded folders" count only those.
    private static bool Sees(IndexLeftOut movie, IReadOnlySet<Guid>? scope, IReadOnlySet<Guid>? hidden)
        => (scope is null || movie.LibraryIds.Any(scope.Contains)) && (hidden is null || !hidden.Contains(movie.Id));

    /// <summary>
    /// Counts each library's movies without the ones the caller's parental rating or tags hide: what
    /// the snapshot's own per-library counts are for a caller with no item limits.
    /// </summary>
    /// <param name="snapshot">The index.</param>
    /// <param name="hidden">The movies the caller can't see.</param>
    /// <returns>The movies of each library the caller can see.</returns>
    internal static Dictionary<Guid, int> LibraryMovies(MovieIndexSnapshot snapshot, IReadOnlySet<Guid> hidden)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(hidden);
        var counts = new Dictionary<Guid, int>();
        foreach (var member in snapshot.Entries.SelectMany(e => e.Members).Where(m => !hidden.Contains(m.Id)))
        {
            foreach (var id in member.LibraryIds.Distinct())
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }

    // The log line's reason, a fixed phrase.
    private static string ReasonText(BuildReason reason, bool duringScan)
    {
        var what = reason switch
        {
            BuildReason.Rebuild => "a rebuild was asked for",
            BuildReason.Request => "read with no index or an out-of-date one",
            BuildReason.Changes => "after library changes",
            BuildReason.Settings => "after the settings changed",
            _ => "after a library scan"
        };
        return duringScan ? what + ", during a library scan" : what;
    }

    // A reason that builds only when the page is in use or the warm-up is on.
    private static bool IsBackground(BuildReason reason) => reason is BuildReason.Changes or BuildReason.Scan or BuildReason.Settings;

    // The caller's libraries, or null when they can open every library the index read (an API key, an administrator with
    // access to all), whose numbers are then the server-wide ones. Before the first build, every library the server has.
    private IReadOnlySet<Guid>? Scope(MovieIndexSnapshot? snapshot, User? user)
    {
        if (_access is null || user is null)
        {
            return null;
        }

        var visible = _access.VisibleLibraries(user).Select(l => l.Id).ToHashSet();
        var read = snapshot?.Libraries.Select(l => l.Id) ?? _access.VisibleLibraries(null).Select(l => l.Id);
        return read.All(visible.Contains) ? null : visible;
    }

    // The movies of the caller's libraries their parental rating and tags hide, or null (none, an API key, no index).
    private IReadOnlySet<Guid>? Hidden(MovieIndexSnapshot? snapshot, User? user, IReadOnlySet<Guid>? scope)
    {
        if (_visibility is null || snapshot is null || user is null)
        {
            return null;
        }

        var libraries = snapshot.Libraries.Select(l => l.Id).Where(id => scope is null || scope.Contains(id)).Distinct().ToList();
        return _visibility.Hidden(snapshot, user, libraries)?.Ids;
    }

    private LibraryMaintenanceDuplicateIndexDto Status(MovieIndexSnapshot? snapshot, IReadOnlySet<Guid>? scope, IReadOnlySet<Guid>? hidden)
    {
        bool rebuilding, dirty;
        double progress;
        string? lastError;
        DateTime? lastErrorUtc;
        lock (_gate)
        {
            rebuilding = _running is not null;
            dirty = _dirty;
            progress = _progress;
            lastError = _lastError;
            lastErrorUtc = _lastErrorUtc;
        }

        var stats = snapshot is null ? null : scope is null && hidden is null ? snapshot.Stats : ScopedStats(snapshot, scope, hidden);
        var counts = snapshot is null || hidden is null ? null : LibraryMovies(snapshot, hidden);
        var states = stats?.States ?? new Dictionary<VersionSetState, int>();
        return new LibraryMaintenanceDuplicateIndexDto
        {
            Version = snapshot?.Version ?? 0,
            GeneratedUtc = snapshot?.GeneratedUtc,
            BuildMs = snapshot is null ? null : (long)snapshot.BuildTime.TotalMilliseconds,
            BuiltDuringScan = snapshot?.BuiltDuringScan ?? false,
            Movies = stats?.Movies ?? 0,
            Entries = stats?.Entries ?? 0,
            MergedEntries = stats?.MergedEntries ?? 0,
            BrokenEntries = stats?.BrokenEntries ?? 0,
            Libraries = snapshot?.Libraries
                .Where(l => scope is null || scope.Contains(l.Id))
                .Select(l => new LibraryMaintenanceDuplicateIndexLibraryDto { Id = l.Id, Name = l.Name, Kind = l.Kind, Movies = counts is null ? l.Movies : counts.GetValueOrDefault(l.Id) })
                .ToList() ?? [],
            Dirty = dirty,
            Rebuilding = rebuilding,
            Progress = rebuilding ? progress : null,
            ScanInProgress = _library.IsScanRunning,
            States = new LibraryMaintenanceDuplicateIndexStatesDto
            {
                Orphaned = states.GetValueOrDefault(VersionSetState.Orphaned),
                StaleLinks = states.GetValueOrDefault(VersionSetState.StaleLinks),
                Chained = states.GetValueOrDefault(VersionSetState.Chained),
                Unlisted = states.GetValueOrDefault(VersionSetState.Unlisted),
                ListedNotPointing = states.GetValueOrDefault(VersionSetState.ListedNotPointing),
                Cycle = states.GetValueOrDefault(VersionSetState.Cycle)
            },
            Skipped = stats?.Skipped ?? 0,
            Excluded = stats?.Excluded ?? 0,
            LastError = lastError,
            LastErrorUtc = lastErrorUtc
        };
    }

    private void NoteUse()
    {
        lock (_gate)
        {
            _pageUsedUtc = _time.GetUtcNow();
        }
    }

    // A save of the settings: turning Duplicates on, or changing the libraries or folders the
    // index reads, makes the index out of date; it builds as a batch would (when the page is in use or the warm-up is on).
    // While Duplicates is off it is only marked.
    private void OnSettingsChanged(object? sender, PluginConfigurationChangedEventArgs e)
    {
        var turnedOn = e.Current.DuplicatesEnabled && !e.Previous.DuplicatesEnabled;
        if (!turnedOn && string.Equals(IndexKey(e.Previous), IndexKey(e.Current), StringComparison.Ordinal))
        {
            return;
        }

        if (!e.Current.DuplicatesEnabled)
        {
            MarkDirty();
            return;
        }

        _ = Trigger(BuildReason.Settings);
    }

    // Under the lock: whether a batch or a full pass builds the index now (the warm rule).
    private bool IsWarm()
        => _configuration.GetConfiguration().WarmIndexAfterScan
            || (_pageUsedUtc is { } used && _time.GetUtcNow() - used <= PageUseWindow);

    // Under the lock: the scan rule. An index that exists is rebuilt during a scan only by the post-scan build.
    private bool ScanForbids(BuildReason reason) => reason != BuildReason.Scan && _current is not null && _library.IsScanRunning;

    private Task Trigger(BuildReason reason)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (reason == BuildReason.Request && _running is not null)
            {
                // A read waits for the build that runs; it has nothing new for it to read.
                return _running;
            }

            if (reason == BuildReason.Request && _failedAt is { } failed && _time.GetUtcNow() - failed < FailureBackoff)
            {
                // A build failed moments ago: a read doesn't start another (the index is as out of date as it was).
                _backedOff++;
                return Task.CompletedTask;
            }

            _dirty = true;
            if (IsBackground(reason) && !IsWarm())
            {
                // Nobody used the page lately and the warm-up is off: the next read builds it.
                return _running ?? Task.CompletedTask;
            }

            if (_running is not null)
            {
                _followUp = true;
                _followUpReason = _followUpReason == BuildReason.Scan ? BuildReason.Scan : reason;
                return _running;
            }

            if (ScanForbids(reason))
            {
                DeferUntilScanEnds(reason);
                return Task.CompletedTask;
            }

            _reason = reason;
            _running = Task.Run(Run);
            return _running;
        }
    }

    // Under the lock: one waiter for the scan's end, which builds then if the post-scan build didn't cover the change, for
    // the strongest reason put off (a rebuild or a read builds whatever the warm rule says; changes and settings only when
    // it allows).
    private void DeferUntilScanEnds(BuildReason reason)
    {
        _deferredByScan++;
        if (_waitingForScan)
        {
            if (IsBackground(_deferredReason))
            {
                _deferredReason = reason;
            }

            return;
        }

        _waitingForScan = true;
        _deferredReason = reason;
        _ = Task.Run(WaitForScanEndAsync);
    }

    private async Task WaitForScanEndAsync()
    {
        try
        {
            while (_library.IsScanRunning)
            {
                await Task.Delay(ScanPollInterval, _time, _stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            lock (_gate)
            {
                _waitingForScan = false;
            }
        }

        bool due;
        BuildReason reason;
        lock (_gate)
        {
            due = _dirty && !_disposed;
            reason = _deferredReason;
        }

        if (due)
        {
            await Trigger(reason).ConfigureAwait(false);
        }
    }

    private void Run()
    {
        while (true)
        {
            BuildReason reason;
            lock (_gate)
            {
                reason = _reason;
                _followUp = false;
                _followUpReason = BuildReason.Changes;
                _dirty = false;
                _progress = 0;
            }

            var built = BuildOnce(reason);
            lock (_gate)
            {
                if (!built)
                {
                    // The index before it stays, and is as out of date as it was.
                    _dirty = true;
                }

                if (!_followUp || _disposed)
                {
                    _running = null;
                    _followUp = false;
                    return;
                }

                var next = _followUpReason;
                if (ScanForbids(next))
                {
                    _running = null;
                    _followUp = false;
                    DeferUntilScanEnds(next);
                    return;
                }

                _reason = next;
            }
        }
    }

    private bool BuildOnce(BuildReason reason)
    {
        // The post-scan build starts once the scan has read the libraries, though IsScanRunning is still true then.
        var duringScan = reason != BuildReason.Scan && _library.IsScanRunning;
        var started = _time.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        MovieIndexSnapshot snapshot;
        try
        {
            var content = _builder.Build(_stoppingToken, p => Volatile.Write(ref _progress, p));
            var elapsed = _time.GetElapsedTime(started);
            using var process = Process.GetCurrentProcess();
            var metrics = new BuildMetrics(content.LibraryQueries, content.ItemQueries, GC.GetAllocatedBytesForCurrentThread() - allocated, process.WorkingSet64, process.PeakWorkingSet64);
            var byId = new Dictionary<Guid, VersionEntry>();
            foreach (var entry in content.Entries)
            {
                foreach (var id in entry.AllIds)
                {
                    byId.TryAdd(id, entry);
                }
            }

            snapshot = new MovieIndexSnapshot(Interlocked.Increment(ref _version), _time.GetUtcNow().UtcDateTime, elapsed, duringScan, content.Entries, byId, content.Libraries, content.Stats, metrics);
            Volatile.Write(ref _current, snapshot);
            lock (_gate)
            {
                _lastError = null;
                _lastErrorUtc = null;
                _failedAt = null;
                _lastReason = reason;
                _builds[reason] = _builds.GetValueOrDefault(reason) + 1;
            }

            _logger.LogInformation(
                "Library Maintenance indexed {Movies} movies in {Entries} entries ({Broken} with a broken merge) from {Libraries} libraries in {Milliseconds} ms ({Reason:l})",
                content.Stats.Movies,
                content.Stats.Entries,
                content.Stats.BrokenEntries,
                content.Libraries.Count,
                (long)elapsed.TotalMilliseconds,
                ReasonText(reason, duringScan));
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            // The server is stopping.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library Maintenance couldn't build its movie index; the index before it stays in use");
            lock (_gate)
            {
                _failures++;

                // A fixed sentence: an exception's message can name a path or a title (LastError has neither).
                _lastError = "The movie index couldn't be built (" + ex.GetType().Name + "); the server's log has the details.";
                _failedAt = _time.GetUtcNow();
                _lastErrorUtc = _failedAt.Value.UtcDateTime;
            }

            return false;
        }

        try
        {
            Published?.Invoke(this, new IndexPublishedEventArgs(snapshot.Version));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Library Maintenance couldn't drop what it made from its old movie index");
        }

        return true;
    }
}
