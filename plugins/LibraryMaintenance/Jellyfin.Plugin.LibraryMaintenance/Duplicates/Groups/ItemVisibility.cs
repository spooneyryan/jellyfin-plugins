using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kit.Library;
using Jellyfin.Plugin.LibraryMaintenance.Duplicates.Index;

namespace Jellyfin.Plugin.LibraryMaintenance.Duplicates.Groups;

/// <summary>
/// The indexed movies a caller's parental rating and tags hide: the index is built
/// with no user, and a library a caller can open can still hold movies their policy hides, so every id the groups and the
/// index's counts show must be one <c>GetItemById(id, user)</c> returns them. The check is the kit's
/// <see cref="LibraryAccess.EligibleIds"/>: core's <c>SetUser</c> filters (the maximum parental rating and its sub-score,
/// unrated items, blocked and allowed tags) and each item's and parent's <c>IsVisible</c>, as <c>IsVisibleStandalone</c>
/// asks.
/// </summary>
/// <remarks>
/// <para><b>Fast path</b>: a caller whose policy sets none of these (<see cref="HasItemRestrictions"/>), or an API key,
/// has nothing hidden and costs nothing: no query, no set, and the same cached results as before.</para>
/// <para><b>Cost</b>: one <c>GetItemList</c> per 500 movies of the caller's libraries, once per snapshot, caller and policy
/// (<see cref="Capacity"/> answers kept, all dropped by a newer snapshot). A change to the caller's policy is seen at once
/// (it is in the key); a movie's rating or tags changing is a library change, seen when the index is built again after
/// its batch, as fresh as the index itself.</para>
/// </remarks>
internal sealed class ItemVisibility
{
    /// <summary>How many callers' answers are kept per snapshot.</summary>
    public const int Capacity = 8;

    private readonly LibraryAccess _access;
    private readonly object _gate = new();
    private readonly Dictionary<string, HiddenItems> _kept = new(StringComparer.Ordinal);
    private long _version = -1;
    private long _checks;
    private long _lastTicks = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemVisibility"/> class.
    /// </summary>
    /// <param name="access">Which libraries and items a user can reach.</param>
    public ItemVisibility(LibraryAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        _access = access;
    }

    /// <summary>Gets how many callers' items were checked (the slow path).</summary>
    public long Checks => Interlocked.Read(ref _checks);

    /// <summary>Gets how long the last check took, or null before the first.</summary>
    public TimeSpan? LastCheck => Interlocked.Read(ref _lastTicks) is var ticks and >= 0 ? TimeSpan.FromTicks(ticks) : null;

    /// <summary>
    /// Gets whether a user's policy limits items and not only libraries: the parental rating, unrated items, blocked or
    /// allowed tags (what core's <c>IsParentalAllowed</c> reads; the libraries are <see cref="LibraryAccess"/>'s).
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>Whether their items have to be checked.</returns>
    public static bool HasItemRestrictions(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.MaxParentalRatingScore.HasValue
            || user.GetPreference(PreferenceKind.BlockUnratedItems).Any(v => !string.IsNullOrWhiteSpace(v))
            || user.GetPreference(PreferenceKind.BlockedTags).Any(t => !string.IsNullOrWhiteSpace(t))
            || user.GetPreference(PreferenceKind.AllowedTags).Any(t => !string.IsNullOrWhiteSpace(t));
    }

    /// <summary>
    /// Gets the indexed movies of the caller's libraries that the caller can't see, with the ones the build left out (no
    /// name, an excluded folder) that they can't see.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="user">The caller, or null for an API key.</param>
    /// <param name="libraryIds">The caller's effective libraries.</param>
    /// <returns>The hidden movies, or null when none is (always for the fast path).</returns>
    public HiddenItems? Hidden(MovieIndexSnapshot snapshot, User? user, IReadOnlyCollection<Guid> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(libraryIds);
        if (user is null || !HasItemRestrictions(user) || libraryIds.Count == 0)
        {
            return null;
        }

        var key = Key(user, libraryIds);
        lock (_gate)
        {
            Advance(snapshot.Version);
            if (_version == snapshot.Version && _kept.TryGetValue(key, out var kept))
            {
                return kept.Ids.Count == 0 ? null : kept;
            }
        }

        var watch = Stopwatch.StartNew();
        var scope = libraryIds.ToHashSet();
        // The members, and the movies the build left out (no name, an excluded folder), whose counts GET /Index scopes too
        // (the 1.0 verifier: Diagnostics' "Left out by the excluded folders" counted the server's for this caller).
        var leftOut = snapshot.Stats.SkippedMovies.Concat(snapshot.Stats.ExcludedMovies).Where(m => m.LibraryIds.Any(scope.Contains)).Select(m => m.Id);
        var ids = snapshot.Entries.SelectMany(e => e.Members).Where(m => m.LibraryIds.Any(scope.Contains)).Select(m => m.Id).Concat(leftOut).Distinct().ToList();
        var visible = _access.EligibleIds(user, _access.ResolveLibraries(libraryIds, user), ids);
        var hidden = ids.Where(id => !visible.Contains(id)).ToFrozenSet();
        var result = new HiddenItems(hidden, Digest(hidden));
        Interlocked.Exchange(ref _lastTicks, watch.Elapsed.Ticks);
        Interlocked.Increment(ref _checks);
        lock (_gate)
        {
            Advance(snapshot.Version);
            if (_version == snapshot.Version)
            {
                if (_kept.Count >= Capacity)
                {
                    _kept.Clear();
                }

                _kept[key] = result;
            }
        }

        return hidden.Count == 0 ? null : result;
    }

    /// <summary>
    /// Drops the answers of every snapshot older than <paramref name="version"/>.
    /// </summary>
    /// <param name="version">The new snapshot's version.</param>
    public void Retire(long version)
    {
        lock (_gate)
        {
            Advance(version);
        }
    }

    // The caller, their policy's item limits and their libraries: an answer is never served to another policy.
    private static string Key(User user, IEnumerable<Guid> libraryIds)
    {
        static string List(User user, PreferenceKind kind)
            => string.Join('\u001f', user.GetPreference(kind).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Order(StringComparer.Ordinal));

        return string.Join(
            '\n',
            user.Id.ToString("N", CultureInfo.InvariantCulture),
            user.MaxParentalRatingScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            user.MaxParentalRatingSubScore?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            List(user, PreferenceKind.BlockUnratedItems),
            List(user, PreferenceKind.BlockedTags),
            List(user, PreferenceKind.AllowedTags),
            string.Join(',', libraryIds.Distinct().Select(id => id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)));
    }

    // The hidden set's part of a cached result's key: callers who can't see the same movies share results.
    private static string Digest(IReadOnlySet<Guid> hidden)
    {
        if (hidden.Count == 0)
        {
            return string.Empty;
        }

        var text = string.Join(',', hidden.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal));
        return hidden.Count.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8);
    }

    private void Advance(long version)
    {
        if (version > _version)
        {
            _kept.Clear();
            _version = version;
        }
    }
}
