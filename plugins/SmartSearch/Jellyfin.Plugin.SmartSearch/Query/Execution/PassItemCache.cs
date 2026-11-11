using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SmartSearch.Query.Execution;

/// <summary>
/// The candidates loaded during one pass over the auto-collections, shared by the definitions that read the same items
/// the same way: the same user (or none), the same libraries and the same fields. An item the server didn't return for
/// such a load is remembered as absent too. At most <see cref="MaxItems"/> entries are kept; past that, loads still work
/// but nothing more is kept. It lives only as long as the pass, so a change in the library shows in the next one.
/// </summary>
internal sealed class PassItemCache
{
    private readonly Dictionary<string, Dictionary<Guid, BaseItem?>> _scopes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<Guid>> _candidates = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private int _count;

    /// <summary>
    /// Initializes a new instance of the <see cref="PassItemCache"/> class.
    /// </summary>
    /// <param name="maxItems">The most entries kept; 0 keeps none.</param>
    public PassItemCache(int maxItems)
    {
        MaxItems = Math.Max(0, maxItems);
    }

    /// <summary>Gets the most entries kept: items, and the ids of the candidate lists.</summary>
    public int MaxItems { get; }

    /// <summary>Gets how many entries are kept.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    /// <summary>Gets how many items were answered from the cache.</summary>
    public int Hits { get; private set; }

    /// <summary>Gets how many items had to be loaded.</summary>
    public int Loads { get; private set; }

    /// <summary>Loads a batch of candidates: those kept for this way of reading from the cache, the others through the loader.</summary>
    /// <param name="scope">What the items are read with: the user, the libraries and the fields (<see cref="ScopeOf"/>).</param>
    /// <param name="ids">The batch.</param>
    /// <param name="load">Loads the items of the ids given (those the server returns).</param>
    /// <returns>The items the server returns for the batch, in the batch's order.</returns>
    public IReadOnlyList<BaseItem> Load(string scope, IReadOnlyList<Guid> ids, Func<IReadOnlyList<Guid>, IEnumerable<BaseItem>> load)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(load);
        Dictionary<Guid, BaseItem?> known;
        List<Guid> missing;
        lock (_lock)
        {
            if (!_scopes.TryGetValue(scope, out known!))
            {
                known = [];
                _scopes[scope] = known;
            }

            missing = ids.Where(id => !known.ContainsKey(id)).Distinct().ToList();
            Hits += ids.Count - missing.Count;
            Loads += missing.Count;
        }

        var loaded = new Dictionary<Guid, BaseItem>();
        if (missing.Count > 0)
        {
            foreach (var item in load(missing))
            {
                loaded[item.Id] = item;
            }
        }

        var result = new List<BaseItem>(ids.Count);
        lock (_lock)
        {
            foreach (var id in missing)
            {
                if (_count >= MaxItems)
                {
                    break;
                }

                known[id] = loaded.GetValueOrDefault(id);
                _count++;
            }

            foreach (var id in ids)
            {
                var item = loaded.TryGetValue(id, out var fresh) ? fresh : known.GetValueOrDefault(id);
                if (item is not null)
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets a scope's candidate ids (its kinds, libraries and access, before any rule), loaded once per pass: the
    /// definitions over the same scope then check their rules in memory over the same items.
    /// </summary>
    /// <param name="key">The scope (<see cref="CandidatesOf"/>).</param>
    /// <param name="load">Loads the ids.</param>
    /// <returns>The ids.</returns>
    public IReadOnlyList<Guid> Candidates(string key, Func<IReadOnlyList<Guid>> load)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(load);
        lock (_lock)
        {
            if (_candidates.TryGetValue(key, out var known))
            {
                return known;
            }
        }

        // The ids count against the same limit as the items: a list that doesn't fit isn't kept.
        var ids = load();
        lock (_lock)
        {
            if (!_candidates.ContainsKey(key) && ids.Count <= MaxItems - _count)
            {
                _candidates[key] = ids;
                _count += ids.Count;
            }
        }

        return ids;
    }

    /// <summary>Tells whether every one of the ids is kept for a way of reading items, or can still be.</summary>
    /// <param name="scope">The way of reading (<see cref="ScopeOf"/>).</param>
    /// <param name="ids">The ids.</param>
    /// <returns>Whether loading them through <see cref="Load"/> keeps them all.</returns>
    public bool CanHold(string scope, IReadOnlyCollection<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            var known = _scopes.GetValueOrDefault(scope);
            var missing = known is null ? ids.Count : ids.Count(id => !known.ContainsKey(id));
            return missing <= MaxItems - _count;
        }
    }

    /// <summary>The key of a scope's candidates: the user, the libraries, the kinds and whether missing items count.</summary>
    /// <param name="userId">The user whose access applies, or <c>null</c>.</param>
    /// <param name="libraries">The libraries' folder ids.</param>
    /// <param name="kinds">The kinds.</param>
    /// <param name="isVirtualItem">The query's missing-item filter.</param>
    /// <returns>The key.</returns>
    public static string CandidatesOf(Guid? userId, IEnumerable<Guid>? libraries, IEnumerable<BaseItemKind>? kinds, bool? isVirtualItem)
        => ScopeOf(userId, libraries, false, null) + "|" + string.Join(',', (kinds ?? []).Select(k => k.ToString()).Order(StringComparer.Ordinal)) + "|" + (isVirtualItem?.ToString() ?? "-");

    /// <summary>The key of a way of reading items: the user, the libraries, and whether user data and which fields are read.</summary>
    /// <param name="userId">The user the items are read for, or <c>null</c>.</param>
    /// <param name="libraries">The libraries' folder ids.</param>
    /// <param name="userData">Whether user data is read.</param>
    /// <param name="fields">The extra fields read.</param>
    /// <returns>The key.</returns>
    public static string ScopeOf(Guid? userId, IEnumerable<Guid>? libraries, bool userData, IEnumerable<string>? fields)
        => string.Join(
            '|',
            (userId ?? Guid.Empty).ToString("N"),
            string.Join(',', (libraries ?? []).Select(l => l.ToString("N")).Order(StringComparer.Ordinal)),
            userData ? "1" : "0",
            string.Join(',', (fields ?? []).Order(StringComparer.Ordinal)));
}
