using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The engine's result cache: evaluated results by <c>ResultToken</c> and by
/// <c>(user, QueryHash, now-bucket)</c>, with a sliding lifetime (<c>ResultCacheSeconds</c>), at most
/// <see cref="PerUser"/> entries per user, <see cref="Total"/> in all and <c>MaxCachedResultIds</c> ids in all (the least
/// recently used go first).
/// </summary>
/// <remarks>
/// <para><b>Per user</b>: an entry is only ever returned to the user it was evaluated for, and only while that user's
/// access is what it was (<see cref="Fingerprint"/>: the libraries they can open, their parental limit, blocked and
/// allowed tags and blocked unrated kinds; core raises no event when a policy changes, <c>OnUserUpdated</c> fires on a
/// rename only), so a token passed between users, or kept across a policy change, evaluates again.</para>
/// <para><b>Invalidation</b>: an item added, updated or removed ends every entry (a new match would be missing, a removed
/// one counted); an update of the user ends that user's entries, and their saved user data (a playback report, a
/// favourite) the entries that read it (<see cref="CachedResult.ReadsUserData"/>). The engine then evaluates again and
/// says so (<c>Warnings: ["Reevaluated"]</c>). An entry carries the generations read before its evaluation started
/// (<see cref="Snapshot"/>), so an event during the evaluation ends it too.</para>
/// <para><b>Memory</b>: the ids of all entries together stay within the id budget; expired entries are removed on every
/// read and store, and by a timer every <see cref="SweepInterval"/>, so an idle server doesn't keep them.</para>
/// </remarks>
internal sealed class ResultCache : IDisposable
{
    /// <summary>The most entries a user keeps.</summary>
    public const int PerUser = 20;

    /// <summary>The most entries in all.</summary>
    public const int Total = 200;

    /// <summary>How often the timer removes expired entries.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager? _library;
    private readonly IUserDataManager? _userData;
    private readonly IUserManager? _users;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _lifetime;
    private readonly Func<long> _idBudget;
    private readonly ITimer _sweep;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CachedResult> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<(Guid User, string Hash, long Bucket), string> _byQuery = [];
    private readonly Dictionary<Guid, long> _userGenerations = [];
    private readonly Dictionary<Guid, long> _userDataGenerations = [];
    private long _generation;
    private long _uses;
    private long _ids;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResultCache"/> class.
    /// </summary>
    /// <param name="library">The library manager, whose item events end every entry; <c>null</c> to hear none.</param>
    /// <param name="userData">The user data manager, whose saves end a user's entries; <c>null</c> to hear none.</param>
    /// <param name="users">The user manager, whose updates end a user's entries; <c>null</c> to hear none.</param>
    /// <param name="time">The clock (the lifetime and the sweep timer).</param>
    /// <param name="lifetime">The sliding lifetime (<c>ResultCacheSeconds</c>, read on each use).</param>
    /// <param name="idBudget">The most ids all entries hold together (<c>MaxCachedResultIds</c>, read on each store);
    /// <c>null</c> for the default.</param>
    public ResultCache(ILibraryManager? library, IUserDataManager? userData, IUserManager? users, TimeProvider time, Func<TimeSpan> lifetime, Func<long>? idBudget = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(lifetime);
        _library = library;
        _userData = userData;
        _users = users;
        _time = time;
        _lifetime = lifetime;
        _idBudget = idBudget ?? (() => EngineSettings.DefaultMaxCachedResultIds);
        if (_library is not null)
        {
            _library.ItemAdded += OnItemChanged;
            _library.ItemUpdated += OnItemChanged;
            _library.ItemRemoved += OnItemChanged;
        }

        if (_userData is not null)
        {
            _userData.UserDataSaved += OnUserDataSaved;
        }

        if (_users is not null)
        {
            _users.OnUserUpdated += OnUserUpdated;
        }

        _sweep = time.CreateTimer(_ => Sweep(), null, SweepInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gets the number of entries.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _byToken.Count;
            }
        }
    }

    /// <summary>Gets the number of ids all entries hold together.</summary>
    public long IdCount
    {
        get
        {
            lock (_lock)
            {
                return _ids;
            }
        }
    }

    /// <summary>
    /// Gets a user's access as a result depends on it: the libraries they can open, their parental limit, blocked and
    /// allowed tags and blocked unrated kinds.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="visibleLibraries">The ids of the libraries they can open.</param>
    /// <returns>The fingerprint.</returns>
    public static string Fingerprint(User user, IEnumerable<Guid> visibleLibraries)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(visibleLibraries);
        static string List(IEnumerable<string> values) => string.Join(",", values.Order(StringComparer.Ordinal));
        return string.Join(
            "|",
            List(visibleLibraries.Select(id => id.ToString("N"))),
            user.MaxParentalRatingScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            user.MaxParentalRatingSubScore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            List(user.GetPreference(PreferenceKind.BlockedTags)),
            List(user.GetPreference(PreferenceKind.AllowedTags)),
            List(user.GetPreference(PreferenceKind.BlockUnratedItems)));
    }

    /// <summary>Gets the minute a time falls in (the cache's now-bucket).</summary>
    /// <param name="utc">The time.</param>
    /// <returns>The bucket.</returns>
    public static long Bucket(DateTime utc) => utc.Ticks / TimeSpan.TicksPerMinute;

    /// <summary>
    /// Reads the generations a result evaluated for the user depends on. Read before the evaluation starts and passed to
    /// <see cref="Store(CachedResult, Generations)"/>, so an item or user event during the evaluation ends the entry.
    /// </summary>
    /// <param name="userId">The user.</param>
    /// <returns>The generations.</returns>
    public Generations Snapshot(Guid userId)
    {
        lock (_lock)
        {
            return new Generations(_generation, _userGenerations.GetValueOrDefault(userId), _userDataGenerations.GetValueOrDefault(userId));
        }
    }

    /// <summary>Stores a result with the current generations and gives it its token.</summary>
    /// <param name="entry">The result (its <see cref="CachedResult.Token"/> is set here).</param>
    /// <returns>The token.</returns>
    public string Store(CachedResult entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Store(entry, Snapshot(entry.UserId));
    }

    /// <summary>Stores a result and gives it its token.</summary>
    /// <param name="entry">The result (its <see cref="CachedResult.Token"/> is set here).</param>
    /// <param name="generations">The generations read before its evaluation started (<see cref="Snapshot"/>). An entry an
    /// event has already ended gets its token, but isn't kept.</param>
    /// <returns>The token.</returns>
    public string Store(CachedResult entry, Generations generations)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_lock)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            Expire(now);
            entry.Token = NewToken();
            entry.Generation = generations.Item;
            entry.UserGeneration = generations.User;
            entry.UserDataGeneration = generations.UserData;
            entry.LastUsedUtc = now;
            entry.LastUse = ++_uses;
            if (Stale(entry, now))
            {
                return entry.Token;
            }

            _byToken[entry.Token] = entry;
            _byQuery[(entry.UserId, entry.QueryHash, entry.Bucket)] = entry.Token;
            _ids += entry.OrderedIds.Count;

            foreach (var old in _byToken.Values.Where(e => e.UserId == entry.UserId).OrderBy(e => e.LastUse).Take(Math.Max(0, _byToken.Values.Count(e => e.UserId == entry.UserId) - PerUser)).ToList())
            {
                Remove(old);
            }

            foreach (var old in _byToken.Values.OrderBy(e => e.LastUse).Take(Math.Max(0, _byToken.Count - Total)).ToList())
            {
                Remove(old);
            }

            // The id budget: the least recently used go first (the new entry last, when it alone is past the budget).
            var budget = _idBudget();
            if (_ids > budget)
            {
                foreach (var old in _byToken.Values.OrderBy(e => e.LastUse).ToList())
                {
                    Remove(old);
                    if (_ids <= budget)
                    {
                        break;
                    }
                }
            }

            return entry.Token;
        }
    }

    /// <summary>Gets a result by its token, for the user it was evaluated for, while it is valid.</summary>
    /// <param name="token">The token.</param>
    /// <param name="userId">The asking user.</param>
    /// <param name="queryHash">The query's hash (a token of another query is no hit).</param>
    /// <param name="fingerprint">The user's access now (<see cref="Fingerprint"/>).</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public CachedResult? TryGet(string token, Guid userId, string queryHash, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(token);
        lock (_lock)
        {
            Expire(_time.GetUtcNow().UtcDateTime);
            return _byToken.TryGetValue(token, out var entry) ? Use(entry, userId, queryHash, fingerprint) : null;
        }
    }

    /// <summary>Gets the result a user had for the query in the same now-bucket, while it is valid.</summary>
    /// <param name="userId">The asking user.</param>
    /// <param name="queryHash">The query's hash.</param>
    /// <param name="bucket">The now-bucket.</param>
    /// <param name="fingerprint">The user's access now.</param>
    /// <returns>The result, or <c>null</c>.</returns>
    public CachedResult? TryFind(Guid userId, string queryHash, long bucket, string fingerprint)
    {
        lock (_lock)
        {
            Expire(_time.GetUtcNow().UtcDateTime);
            return _byQuery.TryGetValue((userId, queryHash, bucket), out var token) && _byToken.TryGetValue(token, out var entry)
                ? Use(entry, userId, queryHash, fingerprint)
                : null;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }

        _sweep.Dispose();
        if (_library is not null)
        {
            _library.ItemAdded -= OnItemChanged;
            _library.ItemUpdated -= OnItemChanged;
            _library.ItemRemoved -= OnItemChanged;
        }

        if (_userData is not null)
        {
            _userData.UserDataSaved -= OnUserDataSaved;
        }

        if (_users is not null)
        {
            _users.OnUserUpdated -= OnUserUpdated;
        }
    }

    /// <summary>Ends every entry (an item changed).</summary>
    public void InvalidateAll()
    {
        lock (_lock)
        {
            _generation++;
        }
    }

    /// <summary>Ends a user's entries (their account changed).</summary>
    /// <param name="userId">The user.</param>
    public void InvalidateUser(Guid userId)
    {
        lock (_lock)
        {
            _userGenerations[userId] = _userGenerations.GetValueOrDefault(userId) + 1;
        }
    }

    /// <summary>Ends a user's entries that read their user data (it was saved).</summary>
    /// <param name="userId">The user.</param>
    public void InvalidateUserData(Guid userId)
    {
        lock (_lock)
        {
            _userDataGenerations[userId] = _userDataGenerations.GetValueOrDefault(userId) + 1;
        }
    }

    private static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private CachedResult? Use(CachedResult entry, Guid userId, string queryHash, string fingerprint)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        if (entry.UserId != userId || !string.Equals(entry.QueryHash, queryHash, StringComparison.Ordinal))
        {
            // Another user's token, or another query's, is no hit, and leaves the entry as it is.
            return null;
        }

        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal) || Stale(entry, now))
        {
            Remove(entry);
            return null;
        }

        entry.LastUsedUtc = now;
        entry.LastUse = ++_uses;
        return entry;
    }

    // The timer's pass: removes the expired entries, then sets itself for the next pass (a one-shot timer).
    private void Sweep()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            Expire(_time.GetUtcNow().UtcDateTime);
        }

        try
        {
            _sweep.Change(SweepInterval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed in between: no next pass.
        }
    }

    private void Expire(DateTime now)
    {
        var lifetime = _lifetime();
        foreach (var stale in _byToken.Values.Where(e => Stale(e, now, lifetime)).ToList())
        {
            Remove(stale);
        }
    }

    private bool Stale(CachedResult entry, DateTime now) => Stale(entry, now, _lifetime());

    private bool Stale(CachedResult entry, DateTime now, TimeSpan lifetime)
        => now - entry.LastUsedUtc >= lifetime
            || entry.Generation != _generation
            || entry.UserGeneration != _userGenerations.GetValueOrDefault(entry.UserId)
            || (entry.ReadsUserData && entry.UserDataGeneration != _userDataGenerations.GetValueOrDefault(entry.UserId));

    private void Remove(CachedResult entry)
    {
        if (_byToken.Remove(entry.Token))
        {
            _ids -= entry.OrderedIds.Count;
        }

        var key = (entry.UserId, entry.QueryHash, entry.Bucket);
        if (_byQuery.TryGetValue(key, out var token) && token == entry.Token)
        {
            _byQuery.Remove(key);
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e) => InvalidateAll();

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e) => InvalidateUserData(e.UserId);

    private void OnUserUpdated(object? sender, GenericEventArgs<User> e)
    {
        if (e.Argument is { } user)
        {
            InvalidateUser(user.Id);
        }
    }

    /// <summary>The generations a result depends on, read before it is evaluated.</summary>
    /// <param name="Item">The item generation (any item added, updated or removed).</param>
    /// <param name="User">The user's generation (their account).</param>
    /// <param name="UserData">The user's user-data generation.</param>
    internal readonly record struct Generations(long Item, long User, long UserData);
}
