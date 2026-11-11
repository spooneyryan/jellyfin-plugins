using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SmartSearch.Query.Planning;

/// <summary>
/// <see cref="IPushdownFacts"/> over the server's <c>IDbContextFactory&lt;JellyfinDbContext&gt;</c> (read only,
/// <c>AsNoTracking</c>, a context of its own that saves nothing, as Access Roles reads it). Tied to the 10.11 schema; a
/// read that throws is logged and answers "unknown", so nothing is pushed on what it would have decided.
/// </summary>
/// <remarks>
/// <para><b>The clean-form probe</b> (<see cref="Refresh"/>, at startup and after each library scan):
/// one pass over the names of the six kinds' items and of Studio items, and one over the genre, studio and tag
/// <c>ItemValues</c>; the rows whose stored clean form differs are kept, at most <see cref="MaxStaleRows"/> of each (more
/// means the snapshot is dropped and those fields aren't pushed). It logs what it found. Stale rows only get fewer while the
/// server runs (a save rewrites <c>CleanName</c>; new <c>ItemValues</c> rows get today's form), so a snapshot stays
/// conservative until the next one; a failed refresh keeps the last one.</para>
/// <para><b>User data</b>: whether a user's rows of each item agree on a flag is read on demand, one grouped query per user
/// and flag, and cached until that user's data is saved, an item is added (core then re-attaches old rows to it), a library
/// scan ends or <see cref="AgreementLifetime"/> passes (for a change no event reports).</para>
/// </remarks>
internal sealed class DatabasePushdownFacts : IPushdownFacts, IDisposable
{
    /// <summary>The most stale rows of each table a snapshot keeps.</summary>
    public const int MaxStaleRows = 10_000;

    /// <summary>The most <c>|</c> rows a plan reads.</summary>
    public const int MaxPipeRows = 10_000;

    /// <summary>The most <c>(SeriesId, SeriesName)</c> pairs the series resolver reads.</summary>
    public const int MaxEpisodeSeries = 50_000;

    /// <summary>The longest a user-data agreement is trusted without an event.</summary>
    public static readonly TimeSpan AgreementLifetime = TimeSpan.FromMinutes(5);

    // BaseItemRepository.PlaceholderId: the owner of user data rows whose item is gone.
    private static readonly Guid PlaceholderId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly string[] NameTypes =
    [
        typeof(Movie).FullName!, typeof(Series).FullName!, typeof(Episode).FullName!, typeof(MusicVideo).FullName!,
        typeof(Video).FullName!, typeof(BoxSet).FullName!, typeof(Studio).FullName!
    ];

    private static readonly string EpisodeType = typeof(Episode).FullName!;

    private readonly IDbContextFactory<JellyfinDbContext> _factory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IUserDataManager? _userData;
    private readonly ILibraryManager? _library;
    private readonly ConcurrentDictionary<Guid, long> _userVersions = new();
    private readonly ConcurrentDictionary<(Guid User, UserDataFlag Flag), (long Version, bool Agrees, DateTime AtUtc)> _agreement = new();
    private readonly Lock _refreshLock = new();
    private CleanFormSnapshot? _snapshot;
    private long _itemsVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabasePushdownFacts"/> class.
    /// </summary>
    /// <param name="factory">The server's database context factory.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="time">The clock.</param>
    /// <param name="userData">The user data manager, whose saves end a user's cached agreement; <c>null</c> caches nothing.</param>
    /// <param name="library">The library manager, whose added items end every cached agreement; <c>null</c> caches nothing.</param>
    public DatabasePushdownFacts(IDbContextFactory<JellyfinDbContext> factory, ILogger logger, TimeProvider time, IUserDataManager? userData, ILibraryManager? library)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        _factory = factory;
        _logger = logger;
        _time = time;
        _userData = userData;
        _library = library;
        if (_userData is not null && _library is not null)
        {
            _userData.UserDataSaved += OnUserDataSaved;
            _library.ItemAdded += OnItemAdded;
        }
    }

    /// <inheritdoc />
    public CleanFormSnapshot? CleanForms => Volatile.Read(ref _snapshot);

    /// <summary>Runs the clean-form probe and keeps its snapshot (or keeps the last one when it fails).</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The snapshot now in use, or <c>null</c>.</returns>
    public CleanFormSnapshot? Refresh(CancellationToken cancellationToken)
    {
        lock (_refreshLock)
        {
            var watch = Stopwatch.StartNew();

            // A scan can move user data between items without an event this cache hears.
            _agreement.Clear();
            try
            {
                // Past MaxStaleRows the snapshot is refused, so rows past the cap aren't kept, only counted for the warning.
                using var context = _factory.CreateDbContext();
                var names = new List<StaleName>();
                var staleNames = 0;
                var itemsRead = 0;
                foreach (var row in context.BaseItems.AsNoTracking().Where(e => NameTypes.Contains(e.Type)).Select(e => new { e.Id, e.Name, e.CleanName }).AsEnumerable())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    itemsRead++;
                    if (CleanFormRisk.NameDiffers(row.Name, row.CleanName) && ++staleNames <= MaxStaleRows)
                    {
                        names.Add(new StaleName(row.Id, row.Name, row.CleanName));
                    }
                }

                var values = new List<ItemValueRow>();
                var staleValues = 0;
                var valuesRead = 0;
                var types = CleanFormRisk.ValueTypes.ToArray();
                foreach (var row in context.ItemValues.AsNoTracking().Where(v => types.Contains(v.Type)).Select(v => new { v.Type, v.Value, v.CleanValue }).AsEnumerable())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    valuesRead++;
                    if (CleanFormRisk.Differs(row.Value, row.CleanValue) && ++staleValues <= MaxStaleRows)
                    {
                        values.Add(new ItemValueRow(row.Type, row.Value, row.CleanValue));
                    }
                }

                if (staleNames > MaxStaleRows || staleValues > MaxStaleRows)
                {
                    _logger.LogWarning(
                        "Smart Search's clean-form probe found {Names} item names and {Values} genre, studio and tag values stored with an older clean form (more than {Max}); title, genre, tag and studio rules are evaluated in memory until a refresh or a rescan rewrites them",
                        staleNames,
                        staleValues,
                        MaxStaleRows);
                    Volatile.Write(ref _snapshot, null);
                    return null;
                }

                var snapshot = new CleanFormSnapshot(_time.GetUtcNow().UtcDateTime, itemsRead, valuesRead, names, values);
                Volatile.Write(ref _snapshot, snapshot);
                if (names.Count > 0 || values.Count > 0)
                {
                    _logger.LogInformation(
                        "Smart Search's clean-form probe read {Items} items and {ValuesRead} values in {Elapsed} ms: {Names} names and {Values} values have a stored clean form the evaluator doesn't compare (an older version's, or '|' in a value), so rules that could miss them aren't pushed down",
                        itemsRead,
                        valuesRead,
                        watch.ElapsedMilliseconds,
                        names.Count,
                        values.Count);
                }
                else
                {
                    _logger.LogDebug("Smart Search's clean-form probe read {Items} items and {ValuesRead} values in {Elapsed} ms; none differs", itemsRead, valuesRead, watch.ElapsedMilliseconds);
                }

                return snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or System.Data.Common.DbException)
            {
                _logger.LogWarning(ex, "Smart Search's clean-form probe couldn't read the database; the last snapshot stays in use");
                return CleanForms;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ItemValueRow>? PipeValues(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var context = _factory.CreateDbContext();
            var types = CleanFormRisk.ValueTypes.ToArray();
            var rows = new List<ItemValueRow>();
            foreach (var row in context.ItemValues.AsNoTracking()
                // string.Contains(char) has no SQLite translation in EF Core 9 (found by a live check against 10.11.5); LIKE has, and '|' isn't a wildcard.
                .Where(v => types.Contains(v.Type) && EF.Functions.Like(v.Value, "%|%"))
                .Select(v => new ItemValueRow(v.Type, v.Value, v.CleanValue))
                .Take(MaxPipeRows + 1)
                .AsEnumerable())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(row);
            }

            return rows.Count > MaxPipeRows ? null : rows;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            _logger.LogWarning(ex, "Smart Search couldn't read the values holding '|'; genre, tag and studio rules are evaluated in memory");
            return null;
        }
    }

    /// <inheritdoc />
    public bool UserDataAgrees(Guid userId, UserDataFlag flag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var version = _userVersions.GetValueOrDefault(userId) + (Interlocked.Read(ref _itemsVersion) << 32);
        var now = _time.GetUtcNow().UtcDateTime;
        if (_agreement.TryGetValue((userId, flag), out var cached) && cached.Version == version && now - cached.AtUtc < AgreementLifetime)
        {
            return cached.Agrees;
        }

        try
        {
            using var context = _factory.CreateDbContext();
            var rows = context.UserData.AsNoTracking().Where(u => u.UserId == userId && u.ItemId != PlaceholderId).GroupBy(u => u.ItemId);
            var disagrees = flag switch
            {
                UserDataFlag.Played => rows.Any(g => g.Max(u => u.Played ? 1 : 0) != g.Min(u => u.Played ? 1 : 0)),
                UserDataFlag.Favorite => rows.Any(g => g.Max(u => u.IsFavorite ? 1 : 0) != g.Min(u => u.IsFavorite ? 1 : 0)),
                _ => rows.Any(g => g.Max(u => u.PlaybackPositionTicks > 0 ? 1 : 0) != g.Min(u => u.PlaybackPositionTicks > 0 ? 1 : 0))
            };
            if (_userData is not null && _library is not null)
            {
                _agreement[(userId, flag)] = (version, !disagrees, now);
            }

            return !disagrees;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            _logger.LogWarning(ex, "Smart Search couldn't read the user data rows; {Flag} rules are evaluated in memory", flag);
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<EpisodeSeries>? EpisodeSeries(IReadOnlyCollection<Guid> topParentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topParentIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (topParentIds.Count == 0)
        {
            return [];
        }

        try
        {
            var folders = topParentIds.ToArray();
            using var context = _factory.CreateDbContext();
            var pairs = new List<EpisodeSeries>();
            foreach (var pair in context.BaseItems.AsNoTracking()
                .Where(e => e.Type == EpisodeType && e.TopParentId != null && folders.Contains(e.TopParentId.Value))
                .Select(e => new { e.SeriesId, e.SeriesName })
                .Distinct()
                .Take(MaxEpisodeSeries + 1)
                .AsEnumerable())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pairs.Add(new EpisodeSeries(pair.SeriesId ?? Guid.Empty, pair.SeriesName));
            }

            return pairs.Count > MaxEpisodeSeries ? null : pairs;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            _logger.LogWarning(ex, "Smart Search couldn't read the episodes' series names; series name rules are evaluated in memory");
            return null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_userData is not null && _library is not null)
        {
            _userData.UserDataSaved -= OnUserDataSaved;
            _library.ItemAdded -= OnItemAdded;
        }
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e) => _userVersions.AddOrUpdate(e.UserId, 1, (_, v) => v + 1);

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Interlocked.Increment(ref _itemsVersion);
}
